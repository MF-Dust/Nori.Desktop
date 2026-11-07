using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Nori.Core.Mcp;

/// <summary>MCP 配置、命令行和工具调用的集中校验。</summary>
public static partial class McpConfigValidator
{
	public const int MaxIdCharacters = 128;
	public const int MaxNameCharacters = 256;
	public const int MaxCommandCharacters = 1_024;
	public const int MaxArgumentCharacters = 4_096;
	public const int MaxArguments = 128;
	public const int MaxTotalArgumentCharacters = 32_000;
	public const int MaxEnvironmentEntries = 64;
	public const int MaxEnvironmentValueCharacters = 8_192;
	public const int MaxHeaderEntries = 64;
	public const int MaxHeaderValueCharacters = 8_192;
	public const int MaxToolNameCharacters = 256;
	public const int MaxToolArgumentsCharacters = 32_000;
	public const int MaxToolDescriptionCharacters = 2_000;
	public const int MaxToolSchemaCharacters = 12_000;
	public const int MaxResultCharacters = 48_000;

	/// <summary>校验并复制配置，避免调用方之后修改可变数组/字典影响运行时状态。</summary>
	public static McpServerConfig Validate(McpServerConfig config)
	{
		ArgumentNullException.ThrowIfNull(config);
		string id = Required(config.Id, "MCP 服务器 ID", MaxIdCharacters);
		if (!IdRegex().IsMatch(id)) throw new InvalidOperationException("MCP 服务器 ID 只能包含字母、数字、点、下划线和连字符");
		string name = Required(config.Name, "MCP 服务器名称", MaxNameCharacters);
		string transport = config.Transport.Trim().ToLowerInvariant();
		if (transport is not (McpTransportType.Stdio or McpTransportType.Http or McpTransportType.Sse))
		{
			throw new InvalidOperationException($"不支持的 MCP 传输类型: {config.Transport}");
		}

		string? command = null;
		if (transport == McpTransportType.Stdio)
		{
			command = Required(config.Command, "MCP 启动命令", MaxCommandCharacters);
			RejectControl(command, "MCP 启动命令");
		}

		string? url = null;
		if (transport is McpTransportType.Http or McpTransportType.Sse)
		{
			url = RequireHttpUrl(config.Url, transport == McpTransportType.Sse ? "MCP SSE URL" : "MCP HTTP URL");
		}

		string[] args = ValidateArguments(config.Args);
		Dictionary<string, string>? environment = ValidateMap(
			config.Env, MaxEnvironmentEntries, MaxEnvironmentValueCharacters, "环境变量", EnvironmentKeyRegex());
		if (transport == McpTransportType.Stdio && config.Headers is {Count: > 0})
		{
			throw new InvalidOperationException("stdio 传输不能携带请求头");
		}
		Dictionary<string, string>? headers = transport == McpTransportType.Stdio
			? null
			: ValidateHeaders(config.Headers);

		return config with
		{
			Id = id,
			Name = name,
			Transport = transport,
			Command = command,
			Args = args,
			Env = environment,
			Headers = headers,
			Url = url,
		};
	}

	/// <summary>校验 stdio 命令参数并复制数组。</summary>
	public static string[] ValidateArguments(IReadOnlyList<string>? arguments)
	{
		if (arguments is null || arguments.Count == 0) return [];
		if (arguments.Count > MaxArguments) throw new InvalidOperationException("MCP 命令参数数量超过上限");
		List<string> result = [];
		int total = 0;
		foreach (string? argument in arguments)
		{
			if (argument is null || argument.Length > MaxArgumentCharacters)
			{
				throw new InvalidOperationException("MCP 命令参数为空或超过长度上限");
			}
			RejectControl(argument, "MCP 命令参数");
			total += argument.Length;
			if (total > MaxTotalArgumentCharacters) throw new InvalidOperationException("MCP 命令参数总长度超过上限");
			result.Add(argument);
		}
		return result.ToArray();
	}

	/// <summary>校验工具名和参数对象，拒绝过大的参数而不是静默截断。</summary>
	public static JsonObject ValidateToolCall(string toolName, JsonObject? arguments)
	{
		string name = Required(toolName, "MCP 工具名称", MaxToolNameCharacters);
		RejectControl(name, "MCP 工具名称");
		JsonObject copy = arguments?.DeepClone().AsObject() ?? new JsonObject();
		if (copy.ToJsonString().Length > MaxToolArgumentsCharacters)
		{
			throw new InvalidOperationException($"MCP 工具 {name} 的参数超过安全长度上限 ({MaxToolArgumentsCharacters} 字符)");
		}
		return copy;
	}

	/// <summary>外部 MCP 工具描述和 Schema 的安全展示文本。</summary>
	public static string CapDescription(string? description) => Cap(description, MaxToolDescriptionCharacters);

	public static JsonObject CapSchema(JsonObject? schema)
	{
		if (schema is null) return new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() };
		if (schema.ToJsonString().Length <= MaxToolSchemaCharacters) return schema.DeepClone().AsObject();
		return new JsonObject
		{
			["type"] = "object",
			["properties"] = new JsonObject(),
			["description"] = "MCP 工具参数 Schema 已达到安全长度上限。",
		};
	}


	private static string RequireHttpUrl(string? value, string label)
	{
		if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? endpoint)
			|| (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps)
			|| endpoint.UserInfo.Length > 0)
		{
			throw new InvalidOperationException($"{label}必须是无用户信息的 http/https 地址");
		}
		return endpoint.ToString();
	}

	private static Dictionary<string, string>? ValidateMap(
		IReadOnlyDictionary<string, string>? values,
		int maxEntries,
		int maxValueCharacters,
		string label,
		Regex pattern)
	{
		if (values is null) return null;
		if (values.Count > maxEntries) throw new InvalidOperationException($"MCP {label}数量超过上限");
		Dictionary<string, string> copy = new(StringComparer.Ordinal);
		foreach ((string key, string value) in values)
		{
			if (!pattern.IsMatch(key)) throw new InvalidOperationException($"MCP {label}名无效: {key}");
			if (value.Length > maxValueCharacters) throw new InvalidOperationException($"MCP {label}过长: {key}");
			RejectControl(value, $"MCP {label} {key}");
			copy[key] = value;
		}
		return copy;
	}

	private static Dictionary<string, string>? ValidateHeaders(IReadOnlyDictionary<string, string>? values)
	{
		if (values is null) return null;
		if (values.Count > MaxHeaderEntries) throw new InvalidOperationException("MCP 请求头数量超过上限");
		Dictionary<string, string> copy = new(StringComparer.Ordinal);
		foreach ((string key, string value) in values)
		{
			if (!Nori.Core.Configuration.ConfigValidation.IsValidHeaderName(key))
				throw new InvalidOperationException($"MCP 请求头名无效: {key}");
			if (value.Length > MaxHeaderValueCharacters) throw new InvalidOperationException($"MCP 请求头过长: {key}");
			RejectControl(value, $"MCP 请求头 {key}");
			copy[key] = value;
		}
		return copy;
	}

	private static string Required(string? value, string label, int max)
	{
		if (string.IsNullOrWhiteSpace(value)) throw new InvalidOperationException($"{label}不能为空");
		string trimmed = value.Trim();
		if (trimmed.Length > max) throw new InvalidOperationException($"{label}超过长度上限 ({max})");
		RejectControl(trimmed, label);
		return trimmed;
	}

	private static string Cap(string? value, int max) =>
		string.IsNullOrEmpty(value) ? "" : value.Length <= max ? value : value[..max];

	private static void RejectControl(string value, string label)
	{
		if (value.Any(character => char.IsControl(character))) throw new InvalidOperationException($"{label}包含控制字符");
	}

	[GeneratedRegex("^[A-Za-z0-9._-]+$")]
	private static partial Regex IdRegex();

	[GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$")]
	private static partial Regex EnvironmentKeyRegex();
}
