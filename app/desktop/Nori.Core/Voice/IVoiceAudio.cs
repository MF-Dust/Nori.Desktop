namespace Nori.Core.Voice;

/// <summary>
/// TTS 合成选项
/// </summary>
public sealed record TtsSynthesizeOptions
{
	/// <summary>朗读音色</summary>
	public string? Voice { get; init; }

	/// <summary>语速 (1.0 为常速)</summary>
	public double Speed { get; init; } = 1.0;

	/// <summary>
	/// 合成时的情绪倾向 (如 happy/sad/angry 或短描述)。
	///
	/// 可选：由 AI 回复或全局情绪状态自动推断，不为 null 时支持的
	/// Provider (如 IndexTTS-2) 会映射为情感控制参数；其余 Provider 忽略。
	/// </summary>
	public string? EmotionText { get; init; }
}

/// <summary>
/// TTS 提供商接口: 云端合成返回音频字节 (mp3/wav 由端点决定)
///
/// 后端化后仅保留云端/HTTP 路径; 浏览器 Web Speech 与浏览器 Edge-TTS 已移除。
/// </summary>
public interface ITtsProvider
{
	/// <summary>提供商名 (对应配置键 tts_provider 的取值)</summary>
	string Name { get; }

	/// <summary>合成文本并返回带 MIME 的音频数据</summary>
	Task<EncodedAudio> SynthesizeAsync(string text, TtsSynthesizeOptions options, CancellationToken cancellationToken);
}

/// <summary>
/// 原生音频播放接口
///
/// 播放期间通过 VolumeSampled 输出 0~1 音量采样驱动口型,
/// PlayingChanged 通知说话状态变化。
///
/// 实现已从 NAudio 换成 WebView 内的 WebAudio (三平台一套代码),
/// 因此这里的语义是“把音频交给播放宿主并等待其播完”。
/// </summary>
public interface IAudioPlayback : IDisposable
{
	/// <summary>是否正在播放</summary>
	bool IsPlaying { get; }

	/// <summary>播放状态变化</summary>
	event Action<bool>? PlayingChanged;

	/// <summary>音量采样 (0.0 ~ 1.0)</summary>
	event Action<double>? VolumeSampled;

	/// <summary>阻塞式播放一段带 MIME 的音频</summary>
	Task PlayAsync(EncodedAudio audio, CancellationToken cancellationToken) =>
		PlayAsync(audio.Bytes, audio.Mime, cancellationToken);

	/// <summary>旧字节播放兼容入口；新实现应覆盖带 MIME 的重载。</summary>
	Task PlayAsync(byte[] data, string? mime, CancellationToken cancellationToken) =>
		throw new NotSupportedException("音频播放后端未实现带 MIME 的播放接口");

	/// <summary>停止当前播放并清空队列</summary>
	void Stop();
}

/// <summary>
/// 麦克风录音接口
///
/// 全部异步: WebView 录音需要等前端回传音频, 绝不能在 UI 线程上同步阻塞。
/// </summary>
public interface IMicrophoneRecorder : IDisposable
{
	/// <summary>开始录制</summary>
	Task StartAsync(CancellationToken cancellationToken = default);

	/// <summary>停止录制并返回 MediaRecorder 的实际 MIME 与文件名</summary>
	Task<RecordedAudio> StopAsync(CancellationToken cancellationToken = default);

	/// <summary>是否正在录制</summary>
	bool IsRecording { get; }
}
