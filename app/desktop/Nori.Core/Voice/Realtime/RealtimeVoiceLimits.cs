namespace Nori.Core.Voice.Realtime;

/// <summary>实时语音的固定边界。缓冲、静音和设备标识都落在这些上限里。</summary>
public static class RealtimeVoiceLimits
{
	/// <summary>短于此时长的能量脉冲不当成一句话，用来滤掉咔哒声。</summary>
	public const int MinimumSpeechMilliseconds = 200;

	/// <summary>开口前保留的音频，避免把句首切掉。</summary>
	public const int PrerollMilliseconds = 300;

	/// <summary>单轮采集上限。到点就结束这一轮，不让缓冲无限长大。</summary>
	public const int MaximumUtteranceMilliseconds = 30_000;

	/// <summary>静音结束阈值的下限。</summary>
	public const int MinimumSilenceMilliseconds = 200;

	/// <summary>静音结束阈值的上限。</summary>
	public const int MaximumSilenceMilliseconds = 3_000;

	/// <summary>默认静音结束时间。</summary>
	public const int DefaultSilenceMilliseconds = 700;

	/// <summary>默认灵敏度，0 最钝，1 最灵。</summary>
	public const double DefaultSensitivity = 0.5;

	/// <summary>输入设备标识的最大长度。空字符串表示系统默认设备。</summary>
	public const int MaximumInputDeviceLength = 128;
}
