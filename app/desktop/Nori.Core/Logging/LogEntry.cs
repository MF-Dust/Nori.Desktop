using System.Globalization;
using System.Diagnostics;
using Nori.Core.Security;

namespace Nori.Core.Logging;

/// <summary>
/// 单条日志 (调试页内存缓冲与文件共用同一格式)
///
/// Time 用已格式化的文本: 跨桥接序列化给前端时无需再约定时区与格式.
/// </summary>
public sealed record LogEntry(string Time, string Level, LogSource Source, string Message)
{
	public DateTimeOffset Timestamp { get; init; }
	public string SessionId { get; init; } = "";
	public long Sequence { get; init; }
	public string? Category { get; init; }
	public string? EventId { get; init; }
	public string? WindowLabel { get; init; }
	public string? OperationId { get; init; }
	public string? ExceptionType { get; init; }
	public string? ExceptionSite { get; init; }
	/// <summary>
	/// 从写入参数构造一条日志, 时间取当前时刻
	/// </summary>
	public static LogEntry Create(LogSource source, string level, string message)
	{
		DateTimeOffset now = DateTimeOffset.UtcNow;
		string safe = SensitiveDataRedactor.Redact(message.Length > 16_384 ? message[..16_384] : message);
		return new(now.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture), level, source, safe)
		{ Timestamp = now };
	}

	/// <summary>元数据只允许有限标识符，不接受路径或自由正文。</summary>
	public static string? SafeIdentifier(string? value)
	{
		if (string.IsNullOrEmpty(value)) return null;
		if (value.Length > 160 || value.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('.' or '_' or '-' or '+' or '`')))
			return "unknown";
		return value;
	}

	internal static string? ExceptionLocation(Exception? exception)
	{
		if (exception is null) return null;
		foreach (StackFrame frame in new StackTrace(exception, false).GetFrames().Take(32))
		{
			System.Reflection.MethodBase? method = frame.GetMethod();
			string? type = method?.DeclaringType?.FullName;
			if (type?.StartsWith("Nori.", StringComparison.Ordinal) == true)
				return SafeIdentifier($"{type}.{method!.Name}");
		}
		return null;
	}
}
