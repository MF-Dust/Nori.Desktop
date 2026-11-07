using System.Text.Json.Nodes;

namespace Nori.Core.Mcp;

/// <summary>
/// 解析 Claude / Cursor 的 MCP 配置文本。
///
/// 接受 mcpServers 对象、单个服务器对象和数组。只生成待写入的配置，不保存、不连接。
/// </summary>
public static class McpConfigImporter
{
	public const int MaxDocumentCharacters = 256 * 1024;
	public const int MaxServers = 32;

	/// <summary>解析导入文本。结果一律停用且不自动连接。</summary>
	public static IReadOnlyList<McpServerConfig> Parse(string? text)
	{
		if (string.IsNullOrWhiteSpace(text)) throw new InvalidOperationException("MCP 配置不能为空");
		if (text.Length > MaxDocumentCharacters) throw new InvalidOperationException("MCP 配置超过长度上限");
		JsonNode? node;
		try
		{
			node = JsonNode.Parse(text);
		}
		catch (System.Text.Json.JsonException exception)
		{
			throw new InvalidOperationException($"未识别的 MCP 配置文件结构: {exception.Message}");
		}

		List<(string? Name, JsonObject Body)> servers = Extract(node);
		if (servers.Count == 0) throw new InvalidOperationException("没有可导入的 MCP 服务器");
		if (servers.Count > MaxServers) throw new InvalidOperationException($"一次最多导入 {MaxServers} 个 MCP 服务器");
		List<McpServerConfig> configs = [];
		foreach ((string? name, JsonObject body) in servers)
		{
			configs.Add(McpConfigValidator.Validate(Build(name, body)));
		}
		return configs;
	}

	private static List<(string? Name, JsonObject Body)> Extract(JsonNode? node)
	{
		if (node is JsonArray array)
		{
			List<(string?, JsonObject)> items = [];
			foreach (JsonNode? item in array)
			{
				if (item is not JsonObject body) throw new InvalidOperationException("MCP 服务器配置必须是对象");
				items.Add((ReadString(body, "name") ?? ReadString(body, "id"), body));
			}
			return items;
		}

		if (node is not JsonObject root) throw new InvalidOperationException("未识别的 MCP 配置文件结构");
		if (root["mcpServers"] is JsonObject servers) return NamedServers(servers);
		if (LooksLikeServer(root)) return [(ReadString(root, "name") ?? ReadString(root, "id"), root)];
		throw new InvalidOperationException("找不到 mcpServers 或服务器定义");
	}

	private static List<(string? Name, JsonObject Body)> NamedServers(JsonObject servers)
	{
		List<(string?, JsonObject)> items = [];
		foreach ((string key, JsonNode? value) in servers)
		{
			if (value is not JsonObject body) throw new InvalidOperationException($"MCP 服务器 {key} 的配置必须是对象");
			items.Add((ReadString(body, "name") ?? key, body));
		}
		return items;
	}

	private static bool LooksLikeServer(JsonObject root) =>
		root.ContainsKey("command") || root.ContainsKey("url") || root.ContainsKey("type") || root.ContainsKey("transport");

	private static McpServerConfig Build(string? name, JsonObject body)
	{
		string? url = ReadString(body, "url");
		string? command = ReadString(body, "command");
		string transport = NormalizeTransport(ReadString(body, "type") ?? ReadString(body, "transport"), url is not null);
		bool remote = transport is McpTransportType.Http or McpTransportType.Sse;
		Dictionary<string, string>? headers = remote ? ReadMap(body, "headers") : null;
		return new McpServerConfig
		{
			Id = $"mcp_import_{Guid.NewGuid().ToString("N")[..8]}",
			Name = string.IsNullOrWhiteSpace(name) ? "导入的 MCP 服务" : name.Trim(),
			Transport = transport,
			Command = remote ? null : command ?? "npx",
			Args = remote ? [] : ReadArgs(body),
			Env = ReadMap(body, "env"),
			Headers = headers,
			Url = remote ? url : null,
			Enabled = false,
			AutoConnect = false,
		};
	}

	private static string NormalizeTransport(string? type, bool hasUrl)
	{
		string value = type?.Trim().ToLowerInvariant().Replace("_", "-", StringComparison.Ordinal) ?? "";
		return value switch
		{
			"" => hasUrl ? McpTransportType.Http : McpTransportType.Stdio,
			"stdio" => McpTransportType.Stdio,
			"sse" => McpTransportType.Sse,
			"http" or "streamable-http" or "streamablehttp" => McpTransportType.Http,
			_ => throw new InvalidOperationException($"不支持的 MCP 传输类型: {type}"),
		};
	}

	private static string[] ReadArgs(JsonObject body)
	{
		if (body["args"] is null) return [];
		if (body["args"] is not JsonArray array) throw new InvalidOperationException("MCP 参数必须是字符串数组");
		List<string> args = [];
		foreach (JsonNode? item in array)
		{
			if (item is not JsonValue value || !value.TryGetValue<string>(out string? text) || text is null)
				throw new InvalidOperationException("MCP 参数必须是字符串");
			args.Add(text);
		}
		return args.ToArray();
	}

	private static Dictionary<string, string>? ReadMap(JsonObject body, string name)
	{
		if (body[name] is null) return null;
		if (body[name] is not JsonObject map) throw new InvalidOperationException($"MCP {name} 必须是对象");
		if (map.Count == 0) return null;
		Dictionary<string, string> values = new(StringComparer.Ordinal);
		foreach ((string key, JsonNode? node) in map)
		{
			if (node is not JsonValue value || !value.TryGetValue<string>(out string? text) || text is null)
				throw new InvalidOperationException($"MCP {name} 的值必须是字符串");
			values[key] = text;
		}
		return values;
	}

	private static string? ReadString(JsonObject body, string name)
	{
		if (body[name] is not JsonValue value || !value.TryGetValue<string>(out string? text)) return null;
		return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
	}
}
