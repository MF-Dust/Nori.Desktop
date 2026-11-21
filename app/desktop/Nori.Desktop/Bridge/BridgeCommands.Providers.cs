using System.Text.Json;
using System.Text.Json.Nodes;
using Nori.Core.Agent;
using Nori.Core.Chat;
using Nori.Core.Configuration;
using Nori.Core.Mcp;
using Nori.Core.Resources;
using Nori.Core.Skills;
using Nori.Core.Tools;
using Nori.Desktop.Windows;

namespace Nori.Desktop.Bridge;

public sealed partial class BridgeCommands
{
	/// <summary>
	/// 清空聊天记录。
	///
	/// 对话交给 LuoLiCore 时，上下文记在对端，本地表只是一份副本。**先重置远端、成功了才删
	/// 本地**：反过来的话，删完本地远端却没清，她下一句仍然接得上前面聊过的内容，而界面上
	/// 什么都没有了，用户只会以为清空没生效 —— 这种不一致比清不掉难查得多。
	///
	/// 因此远端重置失败时整条命令失败，本地记录原样留着，两边仍然一致。连不上对端又确实要
	/// 清本地的话，关掉 LuoLiCore 再清。
	/// </summary>
	private async Task<object?> ClearChatAsync(CancellationToken cancellationToken)
	{
		// 与 AgentEngine 的生成及最终落库共用闸门，含尚未进入后台执行的已接受请求。
		using AgentSessionLease lease = Runtime.Engine.ReserveSession("chat-clear", cancellationToken);
		bool hadRemoteSession = Runtime.Engine.HasRemoteContext;
		bool remoteReset = false;
		if (_services.SafeMode)
		{
			Runtime.Engine.ForgetRemoteSession();
		}
		else
		{
			remoteReset = await Runtime.Engine.ResetRemoteContextAsync(cancellationToken);
		}

		_services.Chat.ClearHistory();
		Runtime.NotifyChatHistoryChanged();
		return new ClearChatResult(
			remoteReset,
			_services.SafeMode && hadRemoteSession
				? "安全模式下未联系外部服务；本地会话已作废，重新启用后会开一段新的对话"
				: null);
	}

	/// <summary>
	/// 清空聊天记录的结果。
	///
	/// <paramref name="RemoteReset"/> 为 false 有两种原因：没接外部后端（无事可做），或者安全
	/// 模式跳过了那次调用（<paramref name="Note"/> 会说明）。两者对界面的意义不同。
	/// </summary>
	private sealed record ClearChatResult(bool RemoteReset, string? Note);

	private async Task<object?> SkillsInstallUrlAsync(JsonElement args)
	{
		object skill = await Runtime.Skills.InstallFromUrlAsync(Str(args, "url"));
		Runtime.InvalidateSnapshot();
		return skill;
	}

	private object SkillsSaveCustom(JsonElement args)
	{
		SkillRecord skill = args.GetProperty("skill").Deserialize<SkillRecord>(BridgeJson.Options)
			?? throw new InvalidOperationException("技能数据不能为空");
		object saved = Runtime.Skills.SaveCustom(skill);
		Runtime.InvalidateSnapshot();
		return saved;
	}

	private async Task<object?> ToolsExecuteManualAsync(JsonElement args)
	{
		string name = Str(args, "name");
		RegisteredTool? tool = Runtime.Tools.Get(name)
			?? throw new InvalidOperationException($"未找到工具: {name}");
		if (tool.PermissionLevel != "safe")
		{
			throw new InvalidOperationException($"{name} 标记为 {tool.PermissionLevel}, 手动测试仅支持 safe 工具");
		}
		JsonNode? toolArgs = null;
		if (args.TryGetProperty("arguments", out JsonElement argElem) && argElem.ValueKind == JsonValueKind.Object)
		{
			toolArgs = JsonNode.Parse(argElem.GetRawText());
		}
		ToolResult result = await Runtime.Tools.ExecuteAsync(name, toolArgs);
		if (result.Error is not null) throw new InvalidOperationException(result.Error);
		return result.Result;
	}

	// ===================================================================
	// 聊天历史
	// ===================================================================

	/// <summary>
	/// 分页读取聊天历史 (服务端规范化旧协议 JSON, 前端不再解析业务内容)
	/// </summary>
	private object GetHistoryPage(int limit, long beforeId) => _services.Chat
		.GetHistory(limit, beforeId, excludeToolFeedback: true)
		.Select(row => new
		{
			id = row.Id,
			role = row.Role,
			content = row.Role == "assistant" ? AgentHistory.ExtractDisplayText(row.Content) : row.Content,
			createdAt = row.CreatedAt,
		})
		.ToArray();

	// ===================================================================
	// LLM / MCP / 技能辅助
	// ===================================================================

	private async Task<object?> FetchModelsAsync(JsonElement args)
	{
		AiChatSettings chat = _services.AiSettings.Read().Chat;
		string apiKey = OptionalStr(args, "apiKey") ?? chat.ApiKey;
		return await _services.Llm.FetchModelsAsync(
			OptionalStr(args, "provider") ?? chat.ProviderId, Str(args, "baseUrl"), apiKey);
	}

	private async Task<object?> GetModelInfoAsync(JsonElement args, CancellationToken cancellationToken)
	{
		AiChatSettings chat = _services.AiSettings.Read().Chat;
		string providerId = OptionalStr(args, "provider") ?? chat.ProviderId;
		// 插件提供商的模型能力未知，按保守默认值返回，与 AgentEngine 运行时一致，不查内置协议目录。
		bool customProvider = LlmProviderExtensions.IsCustomProvider(providerId);
		string baseUrl = OptionalStr(args, "baseUrl") ?? chat.BaseUrl;
		string model = (OptionalStr(args, "model") ?? chat.Model).Trim();
		Nori.Core.Chat.Catalog.ModelCapabilities? capabilities = model.Length == 0 || customProvider
			? null
			: await _services.ModelCatalog.FindAsync(LlmProviderExtensions.ParseProvider(providerId), baseUrl, model, cancellationToken);
		ContextWindowPlan plan = ContextWindowPlanner.Resolve(
			ConfigText(ContextWindowPlanner.KeyContextLimit), ConfigText(ContextWindowPlanner.KeyReservedOutput), capabilities);
		return new
		{
			found = capabilities is not null,
			providerId = capabilities?.ProviderId,
			modelId = capabilities?.ModelId,
			name = capabilities?.Name,
			contextWindow = capabilities?.ContextWindow ?? 0,
			inputLimit = capabilities?.InputLimit ?? 0,
			outputLimit = capabilities?.OutputLimit ?? 0,
			reasoning = capabilities?.Reasoning ?? false,
			reasoningEfforts = capabilities?.ReasoningEfforts ?? [],
			reasoningToggle = capabilities?.ReasoningToggle ?? false,
			reasoningBudget = capabilities?.ReasoningBudget ?? false,
			toolCall = capabilities?.ToolCall ?? false,
			imageInput = capabilities?.ImageInput ?? false,
			imageOutput = capabilities?.ImageOutput ?? false,
			catalogFetchedAt = _services.ModelCatalog.FetchedAt,
			context = new
			{
				limit = plan.MaxInputTokens,
				reserved = plan.ReservedOutputTokens,
				source = plan.Source,
			},
		};
	}

	/// <summary>读取配置的原始文本；整数推断后的值也按文本返回。</summary>
	private string? ConfigText(string key) => _services.Config.Get(key) switch
	{
		ConfigValue.Integer integer => integer.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
		ConfigValue.Text text => text.Value,
		_ => null,
	};

	private async Task<object?> ReadGeneratedImageAsync(string name, CancellationToken cancellationToken)
	{
		if (!Nori.Core.Chat.Images.GeneratedImageStore.IsValidName(name)) throw new InvalidOperationException("图片名称无效");
		byte[]? bytes = await _services.ImageStore.ReadAsync(name, cancellationToken);
		return bytes is null ? null : new {name, base64 = Convert.ToBase64String(bytes)};
	}

	private object? OpenGeneratedImage(string name)
	{
		string path = _services.ImageStore.Resolve(name) ?? throw new InvalidOperationException("图片不存在或已被删除");
		Nori.Desktop.Runtime.ShellOpen.OpenDataFile(path, _services.Paths.DataRoot);
		return null;
	}

	private async Task<object?> TestAiConnectionAsync(JsonElement args, CancellationToken cancellationToken)
	{
		string target = OptionalStr(args, "target") ?? "chat";
		if (string.Equals(target, "embedding", StringComparison.OrdinalIgnoreCase))
		{
			return await TestEmbeddingConnectionAsync(args, cancellationToken);
		}
		return await TestLlmConnectionAsync(args, cancellationToken);
	}

	private async Task<ProviderConnectionTestResult> TestLlmConnectionAsync(JsonElement args, CancellationToken cancellationToken)
	{
		AiChatSettings chat = _services.AiSettings.Read().Chat;
		string provider = OptionalStr(args, "provider") ?? chat.ProviderId;
		string baseUrl = OptionalStr(args, "baseUrl") ?? chat.BaseUrl;
		string apiKey = OptionalStr(args, "apiKey") ?? chat.ApiKey;
		string model = OptionalStr(args, "model") ?? chat.Model;
		ProviderConnectionTester tester = new(_services.Http, _services.Embedding, _services.Chat.CustomProviders);
		return await tester.TestLlmAsync(provider, baseUrl, apiKey, model, cancellationToken);
	}

	private async Task<ProviderConnectionTestResult> TestEmbeddingConnectionAsync(JsonElement args, CancellationToken cancellationToken)
	{
		AiEmbeddingSettings embedding = _services.AiSettings.Read().Embedding;
		string baseUrl = OptionalStr(args, "baseUrl") ?? embedding.BaseUrl;
		string apiKey = OptionalStr(args, "apiKey") ?? embedding.ApiKey;
		string model = OptionalStr(args, "model") ?? embedding.Model;
		int? dimensions = OptionalInt(args, "dimensions") ?? embedding.Dimensions;
		if (dimensions is null && int.TryParse(OptionalStr(args, "dimensions"), out int supplied))
			dimensions = supplied > 0 ? supplied : null;
		ProviderConnectionTester tester = new(_services.Http, _services.Embedding);
		return await tester.TestEmbeddingAsync(baseUrl, apiKey, model, dimensions, cancellationToken);
	}

	private object SkillServiceMarketplace() => Nori.Core.Skills.SkillPresets.All.Select(skill => new
	{
		id = skill.Id,
		name = skill.Name,
		description = skill.Description,
		author = skill.Author,
		version = skill.Version,
		icon = skill.Icon,
		tags = skill.Tags,
		category = skill.Category,
		instructions = skill.Instructions,
		tools = skill.Tools,
		source = skill.Source,
	}).ToArray();

	/// <summary>构建不含技能指令正文和远程地址的脱敏 DTO。</summary>
	private static object RedactedSkillDto(SkillRecord skill) => new
	{
		id = skill.Id,
		name = skill.Name,
		description = skill.Description,
		author = skill.Author,
		version = skill.Version,
		icon = skill.Icon,
		tags = skill.Tags.ToArray(),
		category = skill.Category,
		instructions = "",
		enabled = skill.Enabled,
		source = skill.Source,
	};

	private bool SkillsToggle(string id, bool enabled) => Runtime.Skills.Toggle(id, enabled);

	private async Task<object?> McpImportUrlAsync(
		JsonElement args,
		CancellationToken cancellationToken)
	{
		string url = Str(args, "url");
		Nori.Core.Network.UrlAccessPolicy.EnsurePublicHttp(new Uri(url));
		using HttpResponseMessage response = await Nori.Core.Network.UrlAccessPolicy.GetWithSafeRedirectsAsync(
			_services.PublicHttp, new Uri(url), allowPrivate: false, cancellationToken: cancellationToken);
		if (!response.IsSuccessStatusCode)
		{
			throw new HttpRequestException($"HTTP {(int)response.StatusCode}: {response.ReasonPhrase}");
		}
		string text = await Nori.Core.Network.UrlAccessPolicy.ReadCappedTextAsync(
			response.Content, Nori.Core.Network.UrlAccessPolicy.MaxResponseBytes, cancellationToken);
		return _services.Mcp.PreviewImport(text);
	}

	private async Task<object?> McpImportCommitAsync(JsonElement args)
	{
		IReadOnlyList<McpServerStatusInfo> saved = await _services.Mcp.CommitImportAsync(Str(args, "token"));
		await Runtime.RefreshMcpToolsAsync();
		InvalidateMcpSnapshot();
		return saved;
	}

	private async Task<object?> McpSaveServerAsync(JsonElement args)
	{
		object result = await _services.Mcp.SaveServerAsync(ParseMcpConfig(args));
		await Runtime.RefreshMcpToolsAsync();
		InvalidateMcpSnapshot();
		return result;
	}

	private async Task<object?> McpDeleteServerAsync(JsonElement args)
	{
		bool deleted = await _services.Mcp.DeleteServerAsync(Str(args, "id"));
		await Runtime.RefreshMcpToolsAsync();
		InvalidateMcpSnapshot();
		return deleted;
	}

	private async Task<object?> McpConnectServerAsync(JsonElement args)
	{
		object result = await _services.Mcp.ConnectServerAsync(Str(args, "id"));
		await Runtime.RefreshMcpToolsAsync();
		InvalidateMcpSnapshot();
		return result;
	}

	private async Task<object?> McpDisconnectServerAsync(JsonElement args)
	{
		object result = await _services.Mcp.DisconnectServerAsync(Str(args, "id"));
		await Runtime.RefreshMcpToolsAsync();
		InvalidateMcpSnapshot();
		return result;
	}

	private void InvalidateMcpSnapshot() => Runtime.InvalidateSnapshot();
}
