using Nori.Core.Security;

namespace Nori.Core.Voice.Stt;

/// <summary>
/// STT 错误与配置安全清理工具。
///
/// 保证 API Key 不会出现在 URL、异常消息或事件错误文本中，
/// 且不把音频字节或识别正文写入日志。
/// </summary>
internal static class SttSanitizer
{
	private const int MaxErrorLength = 300;

	/// <summary>校验 API Key 不含换行或控制字符。</summary>
	public static string NormalizeApiKey(string? apiKey)
	{
		if (string.IsNullOrWhiteSpace(apiKey)) return string.Empty;
		string trimmed = apiKey.Trim();
		foreach (char character in trimmed)
		{
			if (char.IsControl(character))
			{
				throw new InvalidOperationException("语音识别 API Key 包含无效控制字符");
			}
		}
		return trimmed;
	}

	/// <summary>对错误文本做脱敏、单行化与长度截断，确保不包含 API Key。</summary>
	public static string RedactError(string? message, string? apiKey)
	{
		if (string.IsNullOrWhiteSpace(message)) return string.Empty;

		string scrubbed = message;
		if (!string.IsNullOrWhiteSpace(apiKey))
		{
			string trimmedKey = apiKey.Trim();
			scrubbed = scrubbed.Replace(apiKey, "[redacted]", StringComparison.Ordinal);
			if (trimmedKey.Length > 0 && !string.Equals(trimmedKey, apiKey, StringComparison.Ordinal))
			{
				scrubbed = scrubbed.Replace(trimmedKey, "[redacted]", StringComparison.Ordinal);
			}
		}

		scrubbed = SensitiveDataRedactor.Redact(scrubbed)
			.Replace('\r', ' ')
			.Replace('\n', ' ')
			.Trim();

		return scrubbed.Length > MaxErrorLength ? scrubbed[..MaxErrorLength] : scrubbed;
	}
}
