using System.Text.RegularExpressions;

namespace Nori.Core.Security;

/// <summary>
/// 日志、诊断和宿主错误出口共用的敏感数据脱敏边界。
///
/// 脱敏器只作为最后一道保险; 请求正文、聊天正文和 MCP stderr 本身不应进入日志。
/// 对异常正文默认只使用 ExceptionType, 不把用户输入当成诊断数据。
/// </summary>
public static partial class SensitiveDataRedactor
{
	private const int MaxTextLength = 1000;

	/// <summary>移除常见凭据、查询参数、认证头和本机绝对路径。</summary>
	public static string Redact(string? value)
	{
		if (string.IsNullOrEmpty(value)) return string.Empty;
		string scrubbed = value;
		scrubbed = CredentialUrlRegex().Replace(scrubbed, "$1[redacted]@");
		scrubbed = JsonSecretRegex().Replace(scrubbed, "$1[redacted]$3");
		scrubbed = QuerySecretRegex().Replace(scrubbed, "$1[redacted]");
		scrubbed = AssignmentSecretRegex().Replace(scrubbed, "$1[redacted]");
		scrubbed = PathRegex().Replace(scrubbed, "[path]");
		return scrubbed.Length > MaxTextLength ? scrubbed[..MaxTextLength] : scrubbed;
	}

	/// <summary>异常对外摘要只保留异常类型和固定分类, 不包含消息或堆栈正文。</summary>
	public static string ExceptionType(Exception? exception) => exception is null
		? "Exception"
		: exception.GetType().FullName ?? exception.GetType().Name;

	/// <summary>构造可写入本地日志的异常摘要。</summary>
	public static string ExceptionSummary(Exception? exception) => ExceptionType(exception);

	[GeneratedRegex(@"(?i)(https?://)[^\s/@:]+(?::[^\s/@]*)?@", RegexOptions.CultureInvariant)]
	private static partial Regex CredentialUrlRegex();

	[GeneratedRegex(@"(?i)([""']?(?:api[_-]?key|authorization|bearer|token|password|secret|cookie)[""']?\s*[:=]\s*[""']?)([^""'\s,;&}]+)([""']?)", RegexOptions.CultureInvariant)]
	private static partial Regex JsonSecretRegex();

	[GeneratedRegex(@"(?i)([?&](?:api[_-]?key|authorization|bearer|token|password|secret|cookie)=)[^&#\s]*", RegexOptions.CultureInvariant)]
	private static partial Regex QuerySecretRegex();

	[GeneratedRegex(@"(?i)((?:api[_-]?key|authorization|bearer|token|password|secret|cookie)\s*[:=]\s*)[^\s,;]+", RegexOptions.CultureInvariant)]
	private static partial Regex AssignmentSecretRegex();

	[GeneratedRegex(@"(?i)(?:[A-Za-z]:\\|/Users/|/home/|/tmp/|/var/folders/)[^\r\n\s]+", RegexOptions.CultureInvariant)]
	private static partial Regex PathRegex();
}
