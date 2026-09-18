namespace Nori.Core.Voice.Audio;

/// <summary>
/// 解码后的音频。
///
/// 样本一律归一到 <c>float</c> 的 −1..1，声道**交错**存放（L R L R …）—— 这是
/// WASAPI / CoreAudio / ALSA 共同接受的形状，送进设备不必再排一次。
///
/// 定成 float 而不是 short：混音、增益、淡入淡出都要在浮点上做，而 RMS 取电平
/// 本来就要除以满量程。存 short 的话每一步都要来回转换。
/// </summary>
public sealed record PcmAudio
{
	/// <summary>交错样本，范围 −1..1。</summary>
	public required float[] Samples { get; init; }

	/// <summary>采样率，例如 44100。</summary>
	public required int SampleRate { get; init; }

	/// <summary>声道数。</summary>
	public required int Channels { get; init; }

	/// <summary>每个声道有多少帧。</summary>
	public int FrameCount => Channels == 0 ? 0 : Samples.Length / Channels;

	/// <summary>时长。</summary>
	public TimeSpan Duration => SampleRate == 0
		? TimeSpan.Zero
		: TimeSpan.FromSeconds((double) FrameCount / SampleRate);
}

/// <summary>解不动某段音频时抛这个，调用方据此给出可读的失败原因而不是崩掉。</summary>
public sealed class AudioDecodeException(string message) : Exception(message);
