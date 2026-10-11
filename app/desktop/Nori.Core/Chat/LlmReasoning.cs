using Nori.Core.Chat.Catalog;

namespace Nori.Core.Chat;

/// <summary>
/// 用户选择的思考档位。
///
/// <see cref="Auto"/> 表示不发送任何思考参数，由服务商按模型默认处理 —— 这是唯一对所有
/// OpenAI 兼容服务都安全的取值，因此是默认值。
/// </summary>
public enum LlmReasoningLevel
{
	/// <summary>不发送思考参数，使用模型默认行为。</summary>
	Auto,

	/// <summary>尽量关闭思考。</summary>
	Off,

	/// <summary>最低。</summary>
	Minimal,

	/// <summary>低。</summary>
	Low,

	/// <summary>中。</summary>
	Medium,

	/// <summary>高。</summary>
	High,

	/// <summary>超高。</summary>
	XHigh,

	/// <summary>最大。</summary>
	Max,
}

/// <summary>思考档位的解析与序列化。</summary>
public static class LlmReasoningLevels
{
	/// <summary>全部档位的配置字符串，按强度升序。</summary>
	public static IReadOnlyList<string> All { get; } = ["auto", "off", "minimal", "low", "medium", "high", "xhigh", "max"];

	/// <summary>解析配置字符串；未知取值按 <see cref="LlmReasoningLevel.Auto"/> 处理。</summary>
	public static LlmReasoningLevel Parse(string? value) => value?.Trim().ToLowerInvariant() switch
	{
		"off" or "none" or "disabled" => LlmReasoningLevel.Off,
		"minimal" => LlmReasoningLevel.Minimal,
		"low" => LlmReasoningLevel.Low,
		"medium" => LlmReasoningLevel.Medium,
		"high" => LlmReasoningLevel.High,
		"xhigh" or "extra_high" => LlmReasoningLevel.XHigh,
		"max" => LlmReasoningLevel.Max,
		_ => LlmReasoningLevel.Auto,
	};

	/// <summary>转为配置字符串。</summary>
	public static string AsString(this LlmReasoningLevel level) => level switch
	{
		LlmReasoningLevel.Off => "off",
		LlmReasoningLevel.Minimal => "minimal",
		LlmReasoningLevel.Low => "low",
		LlmReasoningLevel.Medium => "medium",
		LlmReasoningLevel.High => "high",
		LlmReasoningLevel.XHigh => "xhigh",
		LlmReasoningLevel.Max => "max",
		_ => "auto",
	};
}

/// <summary>单次模型调用的生成参数。缺省值与旧行为完全一致。</summary>
public sealed record LlmGenerationOptions
{
	/// <summary>无任何附加参数。</summary>
	public static LlmGenerationOptions Default { get; } = new();

	/// <summary>思考档位。</summary>
	public LlmReasoningLevel Reasoning { get; init; } = LlmReasoningLevel.Auto;

	/// <summary>模型能力 (来自 models.dev)；null 表示未知。</summary>
	public ModelCapabilities? Capabilities { get; init; }
}

/// <summary>一次请求最终要发送的思考参数。</summary>
/// <param name="Effort">思考强度 (none / minimal / low / medium / high / xhigh / max)；null 表示不发送。</param>
/// <param name="BudgetTokens">思考 token 预算；null 表示不发送，0 表示关闭。</param>
/// <param name="DisableThinking">OpenAI 兼容协议下用 <c>thinking: {type: disabled}</c> 关闭思考。</param>
public sealed record ReasoningRequest(string? Effort, int? BudgetTokens, bool DisableThinking);

/// <summary>
/// 把用户档位换算成具体协议的思考参数。
///
/// 有模型能力时只发模型声明支持的取值 (就近取档)，非思考模型一律不发 —— 多数服务对未知参数
/// 直接返回 400。没有能力信息时按协议惯例发送，用户显式选择了档位，报错也比静默无效更好排查。
/// </summary>
public static class ReasoningPlanner
{
	/// <summary>Anthropic 要求 max_tokens 大于思考预算；为正文保留的最小输出。</summary>
	public const int MinVisibleOutputTokens = 4096;

	private const int AnthropicMinBudget = 1024;

	/// <summary>计算思考参数；不需要发送任何参数时返回 null。</summary>
	public static ReasoningRequest? Plan(LlmProvider protocol, string model, LlmReasoningLevel level, ModelCapabilities? capabilities)
	{
		if (level == LlmReasoningLevel.Auto) return null;
		if (capabilities is {Reasoning: false}) return null;
		return protocol switch
		{
			LlmProvider.Anthropic => PlanAnthropic(level, capabilities),
			LlmProvider.Google => PlanGoogle(model, level, capabilities),
			_ => PlanOpenAi(protocol, level, capabilities),
		};
	}

	/// <summary>档位对应的默认思考预算。</summary>
	public static int BudgetFor(LlmReasoningLevel level) => level switch
	{
		LlmReasoningLevel.Minimal => 1024,
		LlmReasoningLevel.Low => 2048,
		LlmReasoningLevel.Medium => 8192,
		LlmReasoningLevel.High => 16384,
		LlmReasoningLevel.XHigh => 32768,
		LlmReasoningLevel.Max => 65536,
		_ => 0,
	};

	private static ReasoningRequest? PlanAnthropic(LlmReasoningLevel level, ModelCapabilities? capabilities)
	{
		// Anthropic 的思考默认关闭，「关闭」不需要发送任何参数。
		if (level == LlmReasoningLevel.Off) return null;
		int min = Math.Max(AnthropicMinBudget, capabilities?.ReasoningBudgetMin ?? AnthropicMinBudget);
		int max = capabilities?.ReasoningBudgetMax ?? int.MaxValue;
		if (capabilities is {OutputLimit: > 0} known) max = Math.Min(max, known.OutputLimit - MinVisibleOutputTokens);
		if (max < min) return null;
		return new ReasoningRequest(null, Math.Clamp(BudgetFor(level), min, max), false);
	}

	private static ReasoningRequest? PlanGoogle(string model, LlmReasoningLevel level, ModelCapabilities? capabilities)
	{
		// Gemini 3 起改用 thinkingLevel；2.5 系列只认 thinkingBudget，发错会被拒绝。
		IReadOnlyList<string> efforts = capabilities?.ReasoningEfforts is {Count: > 0} declared
			? declared
			: capabilities is null && IsGemini3OrLater(model) ? ["minimal", "low", "medium", "high"] : [];
		if (efforts.Count > 0)
		{
			string? effort = Snap(level, efforts);
			return effort is null ? null : new ReasoningRequest(effort, null, false);
		}

		if (capabilities is {ReasoningBudget: false}) return null;
		int min = capabilities?.ReasoningBudgetMin ?? 0;
		int max = capabilities?.ReasoningBudgetMax ?? 32768;
		if (level == LlmReasoningLevel.Off)
		{
			// 2.5 Pro 不能完全关闭思考，只能降到下限。
			bool canDisable = capabilities is null || capabilities.ReasoningToggle || min == 0;
			return new ReasoningRequest(null, canDisable ? 0 : min, false);
		}
		return new ReasoningRequest(null, Math.Clamp(BudgetFor(level), Math.Max(min, 1), Math.Max(max, 1)), false);
	}

	private static ReasoningRequest? PlanOpenAi(LlmProvider protocol, LlmReasoningLevel level, ModelCapabilities? capabilities)
	{
		if (capabilities is null)
			return new ReasoningRequest(level == LlmReasoningLevel.Off ? "none" : level.AsString(), null, false);

		IReadOnlyList<string> efforts = capabilities.ReasoningEfforts;
		if (level == LlmReasoningLevel.Off)
		{
			if (efforts.Contains("none")) return new ReasoningRequest("none", null, false);
			// 只声明了开关的模型 (DeepSeek、GLM 等) 用 thinking.type 关闭；Responses 协议没有这个字段。
			if (capabilities.ReasoningToggle && protocol == LlmProvider.OpenAi) return new ReasoningRequest(null, null, true);
			return efforts.Count > 0 ? new ReasoningRequest(efforts.First(value => value != "none"), null, false) : null;
		}
		if (efforts.Count == 0) return null;
		string? snapped = Snap(level, efforts);
		return snapped is null ? null : new ReasoningRequest(snapped, null, false);
	}

	/// <summary>在模型支持的取值中选择离目标最近的一档；距离相同取更高的一档。</summary>
	internal static string? Snap(LlmReasoningLevel level, IReadOnlyList<string> supported)
	{
		string target = level == LlmReasoningLevel.Off ? "none" : level.AsString();
		if (supported.Contains(target)) return target;
		int targetIndex = Array.IndexOf(Catalog.ModelsDevIndex.EffortOrder, target);
		string? best = null;
		int bestDistance = int.MaxValue;
		foreach (string value in supported)
		{
			// 非「关闭」档位不能被就近换成 none，那等于替用户关掉了思考。
			if (level != LlmReasoningLevel.Off && value == "none") continue;
			int index = Array.IndexOf(Catalog.ModelsDevIndex.EffortOrder, value);
			if (index < 0) continue;
			int distance = Math.Abs(index - targetIndex);
			if (distance < bestDistance || (distance == bestDistance && best is not null
				&& index > Array.IndexOf(Catalog.ModelsDevIndex.EffortOrder, best)))
			{
				best = value;
				bestDistance = distance;
			}
		}
		return best;
	}

	private static bool IsGemini3OrLater(string model)
	{
		string id = model.Trim().ToLowerInvariant();
		if (id.StartsWith("models/", StringComparison.Ordinal)) id = id["models/".Length..];
		if (!id.StartsWith("gemini-", StringComparison.Ordinal)) return false;
		string version = new(id["gemini-".Length..].TakeWhile(character => char.IsAsciiDigit(character)).ToArray());
		return int.TryParse(version, out int major) && major >= 3;
	}
}
