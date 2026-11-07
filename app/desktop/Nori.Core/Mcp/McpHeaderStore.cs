using System.Text.Json;
using System.Text.Json.Nodes;
using Nori.Core.Configuration;

namespace Nori.Core.Mcp;

/// <summary>
/// MCP HTTP / SSE 请求头的独立敏感存储。
///
/// 与环境变量一样，从 mcp_servers 元数据拆出后整体加密。状态只报告是否已配置和密钥问题。
/// </summary>
public sealed class McpHeaderStore(ConfigStore config)
{
	private readonly ConfigStore _config = config;

	/// <summary>根据服务器 ID 生成请求头敏感配置键。</summary>
	public static string KeyFor(string serverId)
	{
		if (!ConfigValidation.IsValidMcpServerId(serverId)) throw new ArgumentException("MCP 服务器 ID 无效", nameof(serverId));
		return ConfigStore.McpHeaderKeyPrefix + serverId;
	}

	/// <summary>读取给连接使用的请求头；失败时按空集合处理。</summary>
	public IReadOnlyDictionary<string, string> Read(string serverId)
	{
		string key = KeyFor(serverId);
		SecretReadResult result = _config.ReadSecret(key);
		if (!result.IsConfigured || string.IsNullOrWhiteSpace(result.Value)) return new Dictionary<string, string>();

		try
		{
			Dictionary<string, string>? values = JsonSerializer.Deserialize<Dictionary<string, string>>(result.Value);
			if (values is null) return new Dictionary<string, string>();
			return Normalize(values);
		}
		catch (JsonException)
		{
			_config.RecordSecretIssue(key, SecretIssueCategory.CorruptCiphertext);
			return new Dictionary<string, string>();
		}
	}

	/// <summary>保存请求头；加密失败会抛出且不会写入明文。</summary>
	public void Save(string serverId, IReadOnlyDictionary<string, string>? values)
	{
		string key = KeyFor(serverId);
		Dictionary<string, string> normalized = Normalize(values ?? new Dictionary<string, string>());
		if (normalized.Count == 0)
		{
			_config.Delete(key);
			return;
		}

		string json = JsonSerializer.Serialize(normalized);
		JsonNode node = JsonNode.Parse(json) ?? new JsonObject();
		_config.Set(key, new ConfigValue.Json(node));
	}

	/// <summary>删除一个服务器的请求头密文。</summary>
	public void Delete(string serverId) => _config.Delete(KeyFor(serverId));

	/// <summary>当前请求头是否有可用值，不返回具体内容。</summary>
	public bool HasConfiguredValues(string serverId) => Read(serverId).Count > 0;

	/// <summary>当前请求头是否存在需要用户处理的问题。</summary>
	public SecretIssue? GetIssue(string serverId) => _config.GetSecretIssue(KeyFor(serverId));

	private static Dictionary<string, string> Normalize(IReadOnlyDictionary<string, string> values)
	{
		Dictionary<string, string> normalized = new(StringComparer.Ordinal);
		foreach ((string key, string value) in values)
		{
			if (!ConfigValidation.IsValidHeaderName(key)) continue;
			normalized[key] = value;
		}
		return normalized;
	}
}
