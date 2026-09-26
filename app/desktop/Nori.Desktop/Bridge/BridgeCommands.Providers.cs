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
	private async Task<object?> ClearChatAsync(IBridgeSource source, CancellationToken cancellationToken) =>
		await RequireMainAsync(source, async () =>
		{
			// 与 AgentEngine 的生成及最终落库共用闸门，含尚未进入后台执行的已接受请求。
			using AgentSessionLease lease = Runtime.Engine.ReserveSession("chat-clear", cancellationToken);
			// 安全模式禁用一切外部调用（AGENTS.md §1「Safe Mode」），重置远端会话是一次
			// 真正的出网请求，所以这里跳过它。
			//
			// 不把 chat_clear 整条加进 IsNetworkCommand：清空本地聊天记录本身是纯本地操作，
			// 安全模式没有理由连它一起禁掉 —— 那会让人在排障时连清个记录都做不到。
			//
			// 代价是两边会不一致：安全模式下对话本来就不走远端（chat_start 已被挡），远端的
			// 上下文停在进入安全模式那一刻，清完本地它还记着。因此把这件事**报给调用方**，
			// 由界面提示一句，而不是让用户以为清干净了 —— 这种不一致查起来比清不掉贵得多。
			// 判据要在动手之前取：作废之后再问「有没有远端会话」恒为否。
			bool hadRemoteSession = Runtime.Engine.HasRemoteContext;
			bool remoteReset = false;
			if (_services.SafeMode)
			{
				// 安全模式不出网，但本地记着的会话必须作废 —— 否则退出安全模式之后那段「已经
				// 清掉」的上下文会原样回来。这一步不联网。
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
					// 用户这一侧已经干净了：本地空了，那段会话也不会再被用到。留着说一句是因为
					// 旧会话的内容仍留在对端服务器上 —— 删它需要联网，而安全模式的前提是不联网。
					? "安全模式下未联系外部服务；本地会话已作废，重新启用后会开一段新的对话"
					: null);
		});

	/// <summary>
	/// 清空聊天记录的结果。
	///
	/// <paramref name="RemoteReset"/> 为 false 有两种原因：没接外部后端（无事可做），或者安全
	/// 模式跳过了那次调用（<paramref name="Note"/> 会说明）。两者对界面的意义不同。
	/// </summary>
	private sealed record ClearChatResult(bool RemoteReset, string? Note);

	private async Task<object?> SkillsInstallUrlAsync(IBridgeSource source, JsonElement args)
	{
		RequireMainVoid(source);
		object skill = await Runtime.Skills.InstallFromUrlAsync(Str(args, "url"));
		Runtime.InvalidateSnapshot();
		return skill;
	}

	private async Task<object?> SkillsSaveCustomAsync(IBridgeSource source, JsonElement args)
	{
		RequireMainVoid(source);
		SkillRecord skill = args.GetProperty("skill").Deserialize<SkillRecord>(BridgeJson.Options)
			?? throw new InvalidOperationException("技能数据不能为空");
		object saved = Runtime.Skills.SaveCustom(skill);
		Runtime.InvalidateSnapshot();
		return saved;
	}

	private async Task<object?> McpGetServersAsync(IBridgeSource source)
	{
		RequireMainVoid(source);
		IReadOnlyList<McpServerStatusInfo> servers = await _services.Mcp.GetServersAsync();
		// 原生设置已订阅状态通知，读取列表不能再触发同一页面的刷新。
		// 连接、断开与配置写入命令仍负责更新工具注册表并广播状态。
		if (source is not INativeSettingsSource)
		{
			await Runtime.RefreshMcpToolsAsync();
			Runtime.InvalidateSnapshot();
		}
		return servers;
	}

	private async Task<object?> ToolsExecuteManualAsync(IBridgeSource source, JsonElement args)
	{
		RequireMainVoid(source);
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

	private async Task<object?> FetchModelsWithSourceCheckAsync(IBridgeSource source, JsonElement args)
	{
		RequireLabel(source, WindowLabels.FirstRun, WindowLabels.Main, () => (object?)true);
		AiChatSettings chat = _services.AiSettings.Read().Chat;
		string apiKey = OptionalStr(args, "apiKey") ?? chat.ApiKey;
		return await _services.Llm.FetchModelsAsync(
			OptionalStr(args, "provider") ?? chat.Provider.AsString(), Str(args, "baseUrl"), apiKey);
	}

	private async Task<object?> TestAiConnectionAsync(IBridgeSource source, JsonElement args, CancellationToken cancellationToken)
	{
		string target = OptionalStr(args, "target") ?? "chat";
		if (string.Equals(target, "embedding", StringComparison.OrdinalIgnoreCase))
		{
			return await TestEmbeddingConnectionAsync(source, args, cancellationToken);
		}
		return await TestLlmConnectionAsync(source, args, cancellationToken);
	}

	private async Task<ProviderConnectionTestResult> TestLlmConnectionAsync(IBridgeSource source, JsonElement args, CancellationToken cancellationToken)
	{
		RequireMainVoid(source);
		AiChatSettings chat = _services.AiSettings.Read().Chat;
		string provider = OptionalStr(args, "provider") ?? chat.Provider.AsString();
		string baseUrl = OptionalStr(args, "baseUrl") ?? chat.BaseUrl;
		string apiKey = OptionalStr(args, "apiKey") ?? chat.ApiKey;
		string model = OptionalStr(args, "model") ?? chat.Model;
		ProviderConnectionTester tester = new(_services.Http, _services.Embedding);
		return await tester.TestLlmAsync(provider, baseUrl, apiKey, model, cancellationToken);
	}

	private async Task<ProviderConnectionTestResult> TestEmbeddingConnectionAsync(IBridgeSource source, JsonElement args, CancellationToken cancellationToken)
	{
		RequireMainVoid(source);
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
		IBridgeSource source,
		JsonElement args,
		CancellationToken cancellationToken)
	{
		RequireMainVoid(source);
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

		List<McpServerConfig> imported = [];
		try
		{
			using JsonDocument document = JsonDocument.Parse(text);
			JsonElement root = document.RootElement.Clone();

			if (root.TryGetProperty("mcpServers", out JsonElement serversElem) && serversElem.ValueKind == JsonValueKind.Object)
			{
				foreach (JsonProperty server in serversElem.EnumerateObject())
				{
					imported.Add(BuildImportedConfig(server.Name, server.Value));
				}
			}
			else if (root.ValueKind == JsonValueKind.Array)
			{
				foreach (JsonElement item in root.EnumerateArray())
				{
					imported.Add(BuildImportedConfig(OptionalGetString(item, "name") ?? "导入的 MCP 服务", item));
				}
			}
			else
			{
				imported.Add(BuildImportedConfig(
					OptionalGetString(root, "name") ?? OptionalGetString(root, "id") ?? "导入的 MCP 服务", root));
			}
		}
		catch (JsonException exception)
		{
			throw new InvalidOperationException($"未识别的 MCP 配置文件结构: {exception.Message}");
		}

		List<McpServerStatusInfo> results = [];
		foreach (McpServerConfig config in imported)
		{
			cancellationToken.ThrowIfCancellationRequested();
			results.Add(await _services.Mcp.SaveServerAsync(config));
		}

		InvalidateMcpSnapshot();
		return results;
	}

	private static string? OptionalGetString(JsonElement element, string name) =>
		element.ValueKind == JsonValueKind.Object
			&& element.TryGetProperty(name, out JsonElement value)
			&& value.ValueKind == JsonValueKind.String
				? value.GetString()
				: null;

	private static McpServerConfig BuildImportedConfig(string name, JsonElement source)
	{
		static string[] ReadArgs(JsonElement element) =>
			element.ValueKind == JsonValueKind.Object
				&& element.TryGetProperty("args", out JsonElement argsElem)
				&& argsElem.ValueKind == JsonValueKind.Array
					? argsElem.EnumerateArray().OfType<JsonElement>().Where(a => a.ValueKind == JsonValueKind.String).Select(a => a.GetString()!).ToArray()
					: [];

		return new McpServerConfig
		{
			Id = $"mcp_import_{Guid.NewGuid().ToString("N")[..8]}",
			Name = name,
			Transport = OptionalGetString(source, "url") is not null ? McpTransportType.Sse : McpTransportType.Stdio,
			Command = OptionalGetString(source, "command") ?? "npx",
			Args = ReadArgs(source),
			Url = OptionalGetString(source, "url"),
			Enabled = false,
			AutoConnect = false,
		};
	}

	private async Task<object?> McpSaveServerAsync(IBridgeSource source, JsonElement args)
	{
		RequireMainVoid(source);
		object result = await _services.Mcp.SaveServerAsync(ParseMcpConfig(args));
		await Runtime.RefreshMcpToolsAsync();
		InvalidateMcpSnapshot();
		return result;
	}

	private async Task<object?> McpDeleteServerAsync(IBridgeSource source, JsonElement args)
	{
		RequireMainVoid(source);
		bool deleted = await _services.Mcp.DeleteServerAsync(Str(args, "id"));
		await Runtime.RefreshMcpToolsAsync();
		InvalidateMcpSnapshot();
		return deleted;
	}

	private async Task<object?> McpConnectServerAsync(IBridgeSource source, JsonElement args)
	{
		RequireMainVoid(source);
		object result = await _services.Mcp.ConnectServerAsync(Str(args, "id"));
		await Runtime.RefreshMcpToolsAsync();
		InvalidateMcpSnapshot();
		return result;
	}

	private async Task<object?> McpDisconnectServerAsync(IBridgeSource source, JsonElement args)
	{
		RequireMainVoid(source);
		object result = await _services.Mcp.DisconnectServerAsync(Str(args, "id"));
		await Runtime.RefreshMcpToolsAsync();
		InvalidateMcpSnapshot();
		return result;
	}

	private async Task<object?> McpTestServerAsync(IBridgeSource source, JsonElement args)
	{
		RequireMainVoid(source);
		return await _services.Mcp.TestServerAsync(ParseMcpConfig(args));
	}

	private async Task<object?> McpCallToolAsync(
		IBridgeSource source,
		JsonElement args,
		CancellationToken cancellationToken)
	{
		RequireMainVoid(source);
		return await CallMcpToolCoreAsync(source, args, cancellationToken);
	}
	private void InvalidateMcpSnapshot() => Runtime.InvalidateSnapshot();
}
