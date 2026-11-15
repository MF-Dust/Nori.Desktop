using Nori.Core.Configuration;

namespace Nori.Core.Voice.Realtime;

/// <summary>
/// 把现有 Whisper 整段识别接进实时会话。
///
/// <see cref="WhisperSttProvider"/> 只向 <c>/audio/transcriptions</c> 提交完整录音并读取一个 text 字段，
/// 协议里没有临时结果。这里因此不报告流式能力，调用方不能把界面分段当成流式识别。
/// </summary>
public static class WhisperUtteranceTranscriber
{
	/// <summary>当前 Whisper 路径没有流式结果。</summary>
	public const bool SupportsStreaming = false;

	/// <summary>识别一整段已经截好的录音。</summary>
	public static Task<string> TranscribeAsync(
		HttpClient httpClient,
		ConfigStore config,
		RecordedAudio audio,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(httpClient);
		ArgumentNullException.ThrowIfNull(config);
		return new WhisperSttProvider(httpClient, config).TranscribeAsync(audio, cancellationToken);
	}
}
