namespace Nori.Core.Voice.Audio;

/// <summary>
/// 回声消除（AEC）与双讲检测能力声明。
/// 只有在全部必要硬件/算法能力具备且经过端到端验证（<see cref="IsVerifiedForBargeIn"/> 为 true）时，
/// 上层才允许在播放期间保持麦克风采集并启用语音打断（barge-in）。
/// </summary>
public sealed record AecCapabilities
{
	/// <summary>默认无 AEC 能力（全部为 false）。</summary>
	public static AecCapabilities None { get; } = new();

	/// <summary>是否具备真实声学回声消除。</summary>
	public bool HasAcousticEchoCancellation { get; init; }

	/// <summary>是否具备背景噪声抑制。</summary>
	public bool HasNoiseSuppression { get; init; }

	/// <summary>是否具备双讲（Double-Talk）检测能力，可区分远端播放回声与近端用户说话。</summary>
	public bool HasDoubleTalkDetection { get; init; }

	/// <summary>是否支持播放参考信号与采集帧的时间/采样率同步。</summary>
	public bool SupportsPlaybackReferenceSync { get; init; }

	/// <summary>是否已通过真实设备端到端语音打断安全验证。</summary>
	public bool IsVerifiedForBargeIn { get; init; }

	/// <summary>是否满足开启语音 barge-in 的全部安全前提。</summary>
	public bool CanEnableVoiceBargeIn =>
		HasAcousticEchoCancellation
		&& HasDoubleTalkDetection
		&& SupportsPlaybackReferenceSync
		&& IsVerifiedForBargeIn;
}

/// <summary>
/// 声学回声消除与双讲检测处理器抽象。
/// </summary>
public interface IAecProcessor : IDisposable
{
	/// <summary>当前处理器的能力声明。</summary>
	AecCapabilities Capabilities { get; }

	/// <summary>向处理器喂入即将或正在送往扬声器的播放参考帧（交错浮点样本）。</summary>
	void FeedPlaybackReference(ReadOnlySpan<float> interleaved, int channels, int sampleRate);

	/// <summary>处理一帧采集到的麦克风音频，向输出缓冲写入消除回声后的样本，并返回实际写入长度。</summary>
	int ProcessCapture(ReadOnlySpan<float> captured, Span<float> cleaned, int channels, int sampleRate);

	/// <summary>判断当前处理后的采集帧是否包含近端用户双讲语音（非扬声器回声）。</summary>
	bool DetectDoubleTalk(ReadOnlySpan<float> cleaned, int channels, int sampleRate);
}

/// <summary>
/// 默认安全占位实现：不宣称任何 AEC 或双讲能力，确保未接通真实 AEC 前严禁语音 barge-in。
/// </summary>
public sealed class NullAecProcessor : IAecProcessor
{
	/// <summary>全局共享只读实例。</summary>
	public static NullAecProcessor Instance { get; } = new();

	private NullAecProcessor()
	{
	}

	/// <inheritdoc />
	public AecCapabilities Capabilities => AecCapabilities.None;

	/// <inheritdoc />
	public void FeedPlaybackReference(ReadOnlySpan<float> interleaved, int channels, int sampleRate)
	{
	}

	/// <inheritdoc />
	public int ProcessCapture(ReadOnlySpan<float> captured, Span<float> cleaned, int channels, int sampleRate)
	{
		if (captured.IsEmpty) return 0;
		int count = Math.Min(captured.Length, cleaned.Length);
		captured[..count].CopyTo(cleaned[..count]);
		return count;
	}

	/// <inheritdoc />
	public bool DetectDoubleTalk(ReadOnlySpan<float> cleaned, int channels, int sampleRate) => false;

	/// <inheritdoc />
	public void Dispose()
	{
	}
}
