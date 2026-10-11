using Nori.Core.Chat;

namespace Nori.Core.Agent;

/// <summary>Agent 上下文预算参数。预算使用稳定的字符近似，不依赖某个供应商的 tokenizer。</summary>
public sealed record ContextBudgetOptions
{
	/// <summary>输入上下文上限 (不含保留输出)。</summary>
	public int MaxInputTokens { get; init; } = 12_000;

	/// <summary>为模型输出预留的 token 数。</summary>
	public int ReservedOutputTokens { get; init; } = 2_000;

	/// <summary>系统提示词独立上限。</summary>
	public int MaxSystemTokens { get; init; } = 7_000;

	/// <summary>历史消息独立上限。</summary>
	public int MaxHistoryTokens { get; init; } = 4_000;

	/// <summary>人格 / 基础指令段上限。</summary>
	public int MaxPersonaCharacters { get; init; } = 12_000;

	/// <summary>记忆段上限。</summary>
	public int MaxMemoryCharacters { get; init; } = 8_000;

	/// <summary>知识段上限。</summary>
	public int MaxKnowledgeCharacters { get; init; } = 10_000;

	/// <summary>技能段上限。</summary>
	public int MaxSkillsCharacters { get; init; } = 24_000;

	/// <summary>工具段上限。</summary>
	public int MaxToolsCharacters { get; init; } = 32_000;

	/// <summary>单条历史消息上限。</summary>
	public int MaxMessageCharacters { get; init; } = 12_000;

	internal ContextBudgetOptions Normalize() => this with
	{
		MaxInputTokens = Math.Clamp(MaxInputTokens, 256, ContextWindowPlanner.MaxWindow),
		ReservedOutputTokens = Math.Clamp(ReservedOutputTokens, 128, 128_000),
		MaxSystemTokens = Math.Clamp(MaxSystemTokens, 128, ContextWindowPlanner.MaxWindow),
		MaxHistoryTokens = Math.Clamp(MaxHistoryTokens, 0, ContextWindowPlanner.MaxWindow),
		MaxPersonaCharacters = Math.Clamp(MaxPersonaCharacters, 256, 512_000),
		MaxMemoryCharacters = Math.Clamp(MaxMemoryCharacters, 0, 512_000),
		MaxKnowledgeCharacters = Math.Clamp(MaxKnowledgeCharacters, 0, 512_000),
		MaxSkillsCharacters = Math.Clamp(MaxSkillsCharacters, 0, 512_000),
		MaxToolsCharacters = Math.Clamp(MaxToolsCharacters, 0, 512_000),
		MaxMessageCharacters = Math.Clamp(MaxMessageCharacters, 256, 512_000),
	};
}

/// <summary>预算后的系统提示词分段，测试和诊断可以确认各段没有互相吞噬预算。</summary>
public sealed record ContextBudgetSections(
	string Persona,
	string Memory,
	string Knowledge,
	string Skills,
	string Tools,
	string Protocol,
	string Other);

/// <summary>PromptBuilder 与预算器之间的独立提示词分段。</summary>
public sealed record PromptSections(
	string Persona,
	string Memory,
	string Knowledge,
	string Skills,
	string Tools,
	string Protocol,
	string Other);

/// <summary>确定性上下文预算结果。</summary>
public sealed record ContextBudgetResult
{
	/// <summary>预算后的系统提示词。</summary>
	public required string SystemPrompt { get; init; }

	/// <summary>预算后的消息序列。</summary>
	public required IReadOnlyList<ChatMessageInput> Messages { get; init; }

	/// <summary>各系统提示词分段。</summary>
	public required ContextBudgetSections Sections { get; init; }

	/// <summary>字符近似 token 数 (仅用于诊断和测试，不宣称是供应商精确计数)。</summary>
	public required int EstimatedInputTokens { get; init; }

	/// <summary>保留给输出的 token 数。</summary>
	public required int ReservedOutputTokens { get; init; }

	/// <summary>最新用户消息是否被保留。</summary>
	public required bool PreservedLatestUserMessage { get; init; }
}

/// <summary>
/// Agent 上下文确定性裁剪器。
///
/// 先按段限制系统提示词，再从历史尾部向前选择消息；最新用户消息是硬性保留项，即使它本身超过预算
/// 也不会被截断。不会调用 LLM 做摘要，也不会依赖运行时随机排序。
/// </summary>
public static class ContextBudgeter
{
	private const int CharactersPerToken = 4;

	/// <summary>按预算生成系统提示词和消息列表。</summary>
	public static ContextBudgetResult Build(
		PromptBuildOptions promptOptions,
		IReadOnlyList<(string Role, string Content)> history,
		string latestUserMessage,
		ContextBudgetOptions? options = null)
	{
		ArgumentNullException.ThrowIfNull(promptOptions);
		ArgumentNullException.ThrowIfNull(history);
		ArgumentNullException.ThrowIfNull(latestUserMessage);

		ContextBudgetOptions normalized = (options ?? new ContextBudgetOptions()).Normalize();
		PromptSections rawSections = PromptBuilder.BuildSections(promptOptions);
		PromptSections bounded = BoundSections(rawSections, normalized);
		string systemPrompt = PromptBuilder.BuildSectionsText(bounded);

		int inputBudget = Math.Max(0, normalized.MaxInputTokens - normalized.ReservedOutputTokens);
		int systemBudget = Math.Min(normalized.MaxSystemTokens, inputBudget);
		if (EstimateTokens(systemPrompt) > systemBudget)
		{
			bounded = TrimToTokenBudget(bounded, systemBudget);
			systemPrompt = PromptBuilder.BuildSectionsText(bounded);
		}

		int historyBudget = Math.Min(normalized.MaxHistoryTokens,
			Math.Max(0, inputBudget - EstimateTokens(systemPrompt)));
		IReadOnlyList<ChatMessageInput> messages = BudgetMessages(history, latestUserMessage, historyBudget, normalized.MaxMessageCharacters);
		int estimated = EstimateTokens(systemPrompt) + messages.Sum(message => EstimateTokens(message.Content));

		return new ContextBudgetResult
		{
			SystemPrompt = systemPrompt,
			Messages = messages,
			Sections = new ContextBudgetSections(
				bounded.Persona,
				bounded.Memory,
				bounded.Knowledge,
				bounded.Skills,
				bounded.Tools,
				bounded.Protocol,
				bounded.Other),
			EstimatedInputTokens = estimated,
			ReservedOutputTokens = normalized.ReservedOutputTokens,
			PreservedLatestUserMessage = messages.Any(message =>
				message.Role == "user" && message.Content == latestUserMessage),
		};
	}

	/// <summary>字符近似 token 计数，公开用于稳定测试。</summary>
	public static int EstimateTokens(string text) =>
		Math.Max(1, ((text?.Length ?? 0) + CharactersPerToken - 1) / CharactersPerToken);

	private static PromptSections BoundSections(PromptSections source, ContextBudgetOptions options) => new(
		Cap(source.Persona, options.MaxPersonaCharacters),
		Cap(source.Memory, options.MaxMemoryCharacters),
		Cap(source.Knowledge, options.MaxKnowledgeCharacters),
		Cap(source.Skills, options.MaxSkillsCharacters),
		Cap(source.Tools, options.MaxToolsCharacters),
		Cap(source.Protocol, Math.Max(256, options.MaxPersonaCharacters / 2)),
		Cap(source.Other, Math.Max(0, options.MaxPersonaCharacters / 2)));

	private static PromptSections TrimToTokenBudget(PromptSections source, int tokenBudget)
	{
		int maxCharacters = Math.Max(0, tokenBudget * CharactersPerToken);
		PromptSections current = source;
		int currentLength = PromptBuilder.BuildSectionsText(current).Length;
		if (currentLength <= maxCharacters) return current;

		// 先丢弃可重建的低优先级数据段，保留人格与协议骨架。
		string[] optional = [source.Other, source.Skills, source.Knowledge, source.Memory];
		int[] lengths = optional.Select(item => item.Length).ToArray();
		for (int index = 0; index < optional.Length && currentLength > maxCharacters; index++)
		{
			if (lengths[index] == 0) continue;
			current = index switch
			{
				0 => current with {Other = ""},
				1 => current with {Skills = ""},
				2 => current with {Knowledge = ""},
				_ => current with {Memory = ""},
			};
			currentLength = PromptBuilder.BuildSectionsText(current).Length;
		}

		if (currentLength > maxCharacters)
		{
			int keep = Math.Max(0, maxCharacters - current.Persona.Length - current.Protocol.Length - current.Other.Length);
			int toolsKeep = Math.Min(current.Tools.Length, Math.Max(0, keep));
			current = current with {Tools = Cap(current.Tools, toolsKeep)};
			currentLength = PromptBuilder.BuildSectionsText(current).Length;
		}
		if (currentLength > maxCharacters)
		{
			int keep = Math.Max(0, maxCharacters - current.Protocol.Length - current.Other.Length - current.Tools.Length);
			current = current with {Persona = Cap(current.Persona, keep)};
		}
		if (PromptBuilder.BuildSectionsText(current).Length > maxCharacters)
		{
			current = current with {Protocol = Cap(current.Protocol, Math.Max(0, maxCharacters - current.Other.Length))};
		}
		return current;
	}

	private static IReadOnlyList<ChatMessageInput> BudgetMessages(
		IReadOnlyList<(string Role, string Content)> history,
		string latestUserMessage,
		int historyBudgetTokens,
		int maxMessageCharacters)
	{
		List<(int Index, ChatMessageInput Message)> selected = [];
		int latestIndex = -1;
		for (int index = history.Count - 1; index >= 0; index--)
		{
			(string role, string content) = history[index];
			if (role == "user" && content == latestUserMessage)
			{
				latestIndex = index;
				break;
			}
		}

		int used = 0;
		for (int index = history.Count - 1; index >= 0; index--)
		{
			if (index == latestIndex) continue;
			(string role, string content) = history[index];
			string bounded = Cap(content, maxMessageCharacters);
			int cost = EstimateTokens(bounded);
			if (used + cost > historyBudgetTokens) continue;
			selected.Add((index, new ChatMessageInput {Role = role, Content = bounded}));
			used += cost;
		}

		if (latestIndex >= 0)
		{
			selected.Add((latestIndex, new ChatMessageInput {Role = "user", Content = latestUserMessage}));
		}
		else
		{
			// 即使调用方传入的历史不含最后一条，也强制加入最新用户消息。
			selected.Add((history.Count, new ChatMessageInput {Role = "user", Content = latestUserMessage}));
		}

		return selected.OrderBy(item => item.Index).Select(item => item.Message).ToList();
	}

	private static string Cap(string value, int limit)
	{
		if (limit <= 0 || value.Length <= limit) return limit <= 0 ? "" : value;
		return value[..limit];
	}

}
