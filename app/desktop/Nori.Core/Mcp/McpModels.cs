using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Nori.Core.Mcp;

/// <summary>
/// MCP 服务器传输协议类型
/// </summary>
public static class McpTransportType
{
	public const string Stdio = "stdio";

	/// <summary>先尝试 Streamable HTTP，失败后再回退 SSE。</summary>
	public const string Http = "http";

	/// <summary>显式 SSE。已保存的 sse 配置继续按 SSE 连接，不改走自动探测。</summary>
	public const string Sse = "sse";
}

/// <summary>
/// MCP 服务器配置模型
/// </summary>
public sealed record McpServerConfig
{
	[JsonPropertyName("id")]
	public required string Id { get; init; }

	[JsonPropertyName("name")]
	public required string Name { get; init; }

	[JsonPropertyName("transport")]
	public string Transport { get; init; } = McpTransportType.Stdio;

	[JsonPropertyName("command")]
	public string? Command { get; init; }

	[JsonPropertyName("args")]
	public string[]? Args { get; init; }

	/// <summary>
	/// 仅用于后端连接输入; 持久化时由 McpEnvironmentStore 拆分到独立密文,
	/// 不会出现在服务器状态 DTO 中。null 表示保留已保存的值，空字典表示清除。
	/// </summary>
	[JsonPropertyName("env")]
	[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
	public Dictionary<string, string>? Env { get; init; }

	/// <summary>
	/// 仅用于 HTTP / SSE 连接输入。持久化时拆到独立密文，状态 DTO 不回传值。
	/// null 表示保留已保存的请求头，空字典表示清除。
	/// </summary>
	[JsonPropertyName("headers")]
	[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
	public Dictionary<string, string>? Headers { get; init; }

	[JsonPropertyName("url")]
	public string? Url { get; init; }

	[JsonPropertyName("enabled")]
	public bool Enabled { get; init; } = false;

	[JsonPropertyName("autoConnect")]
	public bool AutoConnect { get; init; } = false;
}

/// <summary>
/// MCP 工具定义 (对应 tools/list)
/// </summary>
public sealed record McpToolDefinition
{
	[JsonPropertyName("name")]
	public required string Name { get; init; }

	[JsonPropertyName("description")]
	public string? Description { get; init; }

	[JsonPropertyName("inputSchema")]
	public JsonObject? InputSchema { get; init; }
}

/// <summary>
/// MCP 资源定义 (对应 resources/list)
/// </summary>
public sealed record McpResourceDefinition
{
	[JsonPropertyName("uri")]
	public required string Uri { get; init; }

	[JsonPropertyName("name")]
	public required string Name { get; init; }

	[JsonPropertyName("description")]
	public string? Description { get; init; }

	[JsonPropertyName("mimeType")]
	public string? MimeType { get; init; }
}

/// <summary>
/// MCP 工具调用内容项
/// </summary>
public sealed record McpContentItem
{
	[JsonPropertyName("type")]
	public string Type { get; init; } = "text";

	[JsonPropertyName("text")]
	public string? Text { get; init; }

	[JsonPropertyName("data")]
	public string? Data { get; init; }

	[JsonPropertyName("mimeType")]
	public string? MimeType { get; init; }
}

/// <summary>
/// MCP 工具执行结果
/// </summary>
public sealed record McpToolResult
{
	[JsonPropertyName("content")]
	public List<McpContentItem> Content { get; init; } = [];

	[JsonPropertyName("isError")]
	public bool IsError { get; init; }

	/// <summary>
	/// 提取所有文本内容为一个可读字符串
	/// </summary>
	public string AsText()
	{
		List<string> texts = [];
		foreach (McpContentItem item in Content)
		{
			if (!string.IsNullOrEmpty(item.Text))
			{
				texts.Add(item.Text);
			}
		}
		string result = texts.Count > 0
			? string.Join("\n", texts)
			: (IsError ? "MCP 工具执行失败 (无详细输出)" : "MCP 工具执行成功");
		return result.Length <= McpConfigValidator.MaxResultCharacters
			? result
			: result[..McpConfigValidator.MaxResultCharacters];
	}
}

/// <summary>
/// MCP 服务器运行时状态信息
/// </summary>
public sealed record McpServerStatusInfo
{
	[JsonPropertyName("serverId")]
	public required string ServerId { get; init; }

	[JsonPropertyName("name")]
	public required string Name { get; init; }

	[JsonPropertyName("status")]
	public required string Status { get; init; } // "disconnected" | "connecting" | "connected" | "error"

	[JsonPropertyName("errorMessage")]
	public string? ErrorMessage { get; init; }

	[JsonPropertyName("tools")]
	public IReadOnlyList<McpToolDefinition> Tools { get; init; } = [];

	[JsonPropertyName("resources")]
	public IReadOnlyList<McpResourceDefinition> Resources { get; init; } = [];

	/// <summary>是否存在可用的环境变量, 不返回环境变量本身。</summary>
	[JsonPropertyName("hasEnvironment")]
	public bool HasEnvironment { get; init; }

	/// <summary>环境变量密钥问题分类, 不包含值。</summary>
	[JsonPropertyName("secretIssue")]
	public string? SecretIssue { get; init; }

	/// <summary>是否存在可用的请求头, 不返回请求头本身。</summary>
	[JsonPropertyName("hasHeaders")]
	public bool HasHeaders { get; init; }

	/// <summary>请求头密钥问题分类, 不包含值。</summary>
	[JsonPropertyName("headerSecretIssue")]
	public string? HeaderSecretIssue { get; init; }
}

/// <summary>导入预览里的一台服务器。只含名称和键名，不含环境变量或请求头的值。</summary>
public sealed record McpImportServerPreview
{
	[JsonPropertyName("name")]
	public required string Name { get; init; }

	[JsonPropertyName("transport")]
	public required string Transport { get; init; }

	[JsonPropertyName("command")]
	public string? Command { get; init; }

	[JsonPropertyName("url")]
	public string? Url { get; init; }

	[JsonPropertyName("argumentCount")]
	public int ArgumentCount { get; init; }

	[JsonPropertyName("environmentNames")]
	public IReadOnlyList<string> EnvironmentNames { get; init; } = [];

	[JsonPropertyName("headerNames")]
	public IReadOnlyList<string> HeaderNames { get; init; } = [];
}

/// <summary>一次 MCP 导入预览。确认时只提交令牌，不把秘密值交回客户端。</summary>
public sealed record McpImportPreview
{
	[JsonPropertyName("token")]
	public required string Token { get; init; }

	[JsonPropertyName("servers")]
	public IReadOnlyList<McpImportServerPreview> Servers { get; init; } = [];
}
