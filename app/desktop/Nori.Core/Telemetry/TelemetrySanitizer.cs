using Nori.Core.Security;

namespace Nori.Core.Telemetry;

/// <summary>
/// 遥测发送前的纯函数脱敏器。
///
/// 事件只允许携带固定操作名与运行环境标签; 异常正文不跨出本机。这里的文本脱敏仍作为
/// 最后一层保险, 防止 SDK 或未来调用方把凭据放进了错误摘要、URL 或路径。
/// </summary>
public static class TelemetrySanitizer
{
	private const int MaxOperationLength = 80;
	private const int MaxTagLength = 64;

	/// <summary>允许随异常上传的标签键; 白名单外的键在发送边界一律丢弃。</summary>
	private static readonly HashSet<string> SafeTagKeys = new(StringComparer.Ordinal)
	{
		"operation",
		"provider",
		"failure_kind",
		"plugin_id",
		"plugin_version",
		"host_api",
		"exception_kind",
		"hresult",
		"gl_error",
		"assembly",
		"type_name",
	};

	/// <summary>把操作名压缩为不含用户输入的 ASCII 标识。</summary>
	public static string NormalizeOperation(string? operation)
	{
		if (string.IsNullOrWhiteSpace(operation)) return "operation";

		string normalized = new string(operation.Trim().Select(character =>
			char.IsAsciiLetterOrDigit(character) || character is '_' or '.' or '-' ? character : '_').ToArray()).Trim('_');
		if (normalized.Length == 0) return "operation";
		return normalized.Length > MaxOperationLength ? normalized[..MaxOperationLength] : normalized;
	}

	/// <summary>
	/// 把标签值压缩为小写稳定标识, 不保留空白与用户正文。
	///
	/// 标签值可能来自插件 ID 等外部输入, 只允许固定的安全字符, 供 tag 白名单过滤使用。
	/// </summary>
	public static string NormalizeTag(string? value)
	{
		if (string.IsNullOrWhiteSpace(value)) return string.Empty;

		string normalized = new string(value.Trim().Select(character =>
			char.IsAsciiLetterOrDigit(character) || character is '_' or '.' or '-' ? char.ToLowerInvariant(character) : '_').ToArray()).Trim('_');
		return normalized.Length > MaxTagLength ? normalized[..MaxTagLength] : normalized;
	}

	/// <summary>按白名单过滤并归一化标签; 键或值非法的条目直接丢弃。</summary>
	public static IReadOnlyDictionary<string, string> NormalizeTags(IReadOnlyDictionary<string, string>? tags)
	{
		if (tags is null || tags.Count == 0) return new Dictionary<string, string>();

		Dictionary<string, string> safe = new(StringComparer.Ordinal);
		foreach ((string rawKey, string rawValue) in tags)
		{
			string key = NormalizeTag(rawKey);
			string value = NormalizeTag(rawValue);
			if (key.Length == 0 || value.Length == 0 || !SafeTagKeys.Contains(key) || safe.ContainsKey(key)) continue;
			safe[key] = value;
		}
		return safe;
	}

	/// <summary>
	/// 脱敏普通诊断文本。
	///
	/// 不把它用于上传聊天正文; 对异常值应使用 SanitizeExceptionValue, 只保留类型。
	/// </summary>
	public static string ScrubText(string? value)
	{
		if (string.IsNullOrEmpty(value)) return string.Empty;

		string scrubbed = SensitiveDataRedactor.Redact(value);
		return scrubbed.Length > 240 ? scrubbed[..240] : scrubbed;
	}

	/// <summary>异常正文只保留类型, 不上传消息、提示词、聊天内容或请求结果。</summary>
	public static string SanitizeExceptionValue(Exception? exception) =>
		exception is null ? "Exception" : exception.GetType().FullName ?? exception.GetType().Name;

}
