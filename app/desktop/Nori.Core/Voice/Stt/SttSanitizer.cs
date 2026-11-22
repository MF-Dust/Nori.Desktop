using System.Text.Json;
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

	/// <summary>只在有效成功响应中读取转写；错误不回显上游内容。</summary>
	public static async Task<string> ReadTranscriptAsync(HttpContent content, string provider, CancellationToken cancellationToken)
	{
		try
		{
			byte[] bytes = await VoiceHttpContent.ReadBytesAsync(content, cancellationToken, allowEmpty: true).ConfigureAwait(false);
			using JsonDocument document = JsonDocument.Parse(bytes);
			if (document.RootElement.ValueKind == JsonValueKind.Object
				&& document.RootElement.TryGetProperty("text", out JsonElement text) && text.ValueKind == JsonValueKind.String)
				return text.GetString()!;
		}
		catch (JsonException)
		{
			throw new VoiceProviderException(provider, VoiceFailureKind.InvalidResponse, "语音识别响应不是有效的 JSON");
		}
		catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
		{
			throw new VoiceProviderException(provider, VoiceFailureKind.Timeout, "读取语音识别响应超时");
		}
		catch (Exception exception) when (exception is IOException or HttpRequestException)
		{
			throw new VoiceProviderException(provider, VoiceFailureKind.Network, "读取语音识别响应失败");
		}
		throw new VoiceProviderException(provider, VoiceFailureKind.InvalidResponse, "语音识别响应缺少 text 字段");
	}

	/// <summary>为非转写语音请求清理并截断传输错误；转写请求不回显此文本。</summary>
	public static string RedactError(string? message)
	{
		string scrubbed = SensitiveDataRedactor.Redact(message)
			.Replace('\r', ' ')
			.Replace('\n', ' ')
			.Trim();
		return scrubbed.Length > MaxErrorLength ? scrubbed[..MaxErrorLength] : scrubbed;
	}
}
