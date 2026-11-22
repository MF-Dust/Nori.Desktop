using Nori.Core.Configuration;
using Nori.Core.Voice.Stt;

namespace Nori.Core.Voice.Realtime;

/// <summary>
/// 实时语音的已保存选项。
///
/// 总开关只表示允许用户之后去听，本身不会打开麦克风。
/// 输入设备留空表示系统默认；当前三平台采集都只打开默认输入。
/// </summary>
public sealed record RealtimeVoiceSettings
{
	/// <summary>总开关。默认关闭。</summary>
	public const string EnabledKey = "realtime_voice_enabled";

	/// <summary>说完并朗读之后是否继续听下一轮。</summary>
	public const string ContinuousKey = "realtime_voice_continuous";

	/// <summary>能量检测灵敏度，0 到 1。</summary>
	public const string SensitivityKey = "realtime_voice_vad_sensitivity";

	/// <summary>静音多久算这句话说完，单位毫秒。</summary>
	public const string SilenceKey = "realtime_voice_silence_ms";

	/// <summary>是否允许主动打断朗读。</summary>
	public const string InterruptKey = "realtime_voice_interrupt_enabled";

	/// <summary>输入设备标识。空字符串是系统默认麦克风。</summary>
	public const string InputDeviceKey = "realtime_voice_input_device";

	public const string OutputDeviceKey = "realtime_voice_output_device";

	public const string SttModeKey = "realtime_voice_stt_mode";
	public const string StreamingSttModelKey = "realtime_voice_stt_model";
	public const string BatchSttModelKey = "realtime_voice_batch_model";
	public const string LanguageKey = "realtime_voice_language";

	/// <summary>未写入数据库时的默认值：关闭、不连续、可打断、默认麦克风。</summary>
	public static RealtimeVoiceSettings Defaults { get; } = new(
		false,
		false,
		RealtimeVoiceLimits.DefaultSensitivity,
		RealtimeVoiceLimits.DefaultSilenceMilliseconds,
		true,
		"",
		false);

	/// <summary>创建一组已经夹紧的选项。</summary>
	public RealtimeVoiceSettings(
		bool enabled,
		bool continuous,
		double sensitivity,
		int silenceMilliseconds,
		bool interruptEnabled,
		string? inputDeviceId,
		bool inputDeviceInvalid)
	{
		Enabled = enabled;
		Continuous = continuous;
		double finiteSensitivity = double.IsFinite(sensitivity) ? sensitivity : RealtimeVoiceLimits.DefaultSensitivity;
		Sensitivity = Math.Clamp(finiteSensitivity, 0, 1);
		SilenceMilliseconds = Math.Clamp(
			silenceMilliseconds,
			RealtimeVoiceLimits.MinimumSilenceMilliseconds,
			RealtimeVoiceLimits.MaximumSilenceMilliseconds);
		InterruptEnabled = interruptEnabled;
		InputDeviceId = inputDeviceId?.Trim() ?? "";
		InputDeviceInvalid = inputDeviceInvalid;
	}

	/// <summary>用户是否允许实时语音。不会因此自动开始监听。</summary>
	public bool Enabled { get; init; }

	/// <summary>一轮朗读结束后是否回到监听。</summary>
	public bool Continuous { get; init; }

	/// <summary>0 最钝，1 最灵。</summary>
	public double Sensitivity { get; init; }

	/// <summary>静音结束阈值。</summary>
	public int SilenceMilliseconds { get; init; }

	/// <summary>主动打断是否可用。关掉之后，按钮和快捷键都不会停掉朗读。</summary>
	public bool InterruptEnabled { get; init; }

	/// <summary>修剪后的设备标识。空字符串表示默认设备。</summary>
	public string InputDeviceId { get; init; }

	/// <summary>设备标识含控制字符或超长。这种值不能当成默认设备悄悄用掉。</summary>
	public bool InputDeviceInvalid { get; init; }

	/// <summary>修剪后的输出设备标识。空字符串表示默认输出设备。</summary>
	public string OutputDeviceId { get; init; } = "";

	/// <summary>配置中使用的识别模式标识（realtime / batch / local）。</summary>
	public string SttModeId => SttMode switch
	{
		SttMode.RealtimeCloud => "realtime",
		SttMode.Local => "local",
		_ => "batch",
	};

	/// <summary>当前语音识别模式；默认只在说完后上传整段录音。</summary>
	public SttMode SttMode { get; init; } = SttMode.BatchCloud;

	/// <summary>流式识别模型名称。</summary>
	public string StreamingSttModel { get; init; } = OpenAiRealtimeSttProvider.DefaultModel;

	/// <summary>整段文件识别模型名称。</summary>
	public string BatchSttModel { get; init; } = GptTranscribeProvider.DefaultModel;

	/// <summary>空字符串交由服务端自动检测。</summary>
	public string Language { get; init; } = "zh";

	/// <summary>从配置库读取。缺项用默认值，非法数字会被夹紧。</summary>
	public static RealtimeVoiceSettings Read(ConfigStore config)
	{
		ArgumentNullException.ThrowIfNull(config);
		string rawDevice = config.GetStringOr(InputDeviceKey, "");
		bool invalid = IsInvalidDevice(rawDevice);
		return new RealtimeVoiceSettings(
			config.GetBoolOr(EnabledKey, false),
			config.GetBoolOr(ContinuousKey, false),
			config.GetClampedDouble(SensitivityKey, RealtimeVoiceLimits.DefaultSensitivity, 0, 1),
			(int)Math.Round(config.GetClampedDouble(
				SilenceKey,
				RealtimeVoiceLimits.DefaultSilenceMilliseconds,
				RealtimeVoiceLimits.MinimumSilenceMilliseconds,
				RealtimeVoiceLimits.MaximumSilenceMilliseconds)),
			config.GetBoolOr(InterruptKey, true),
			invalid ? "" : rawDevice,
			invalid)
		{
			OutputDeviceId = ReadOutputDeviceId(config),
			SttMode = config.GetStringOr(SttModeKey, "batch").Trim().ToLowerInvariant() switch
			{
				"realtime" => SttMode.RealtimeCloud,
				"local" => SttMode.Local,
				_ => SttMode.BatchCloud,
			},
			StreamingSttModel = ReadModel(config, StreamingSttModelKey, Defaults.StreamingSttModel),
			BatchSttModel = ReadModel(config, BatchSttModelKey, Defaults.BatchSttModel),
			Language = config.Get(LanguageKey) is ConfigValue.Text language ? language.Value.Trim() : Defaults.Language,
		};
	}

	private static string ReadModel(ConfigStore config, string key, string fallback) =>
		config.GetStringOr(key, fallback).Trim() is { Length: > 0 } model ? model : fallback;

	/// <summary>读取播放设备标识；留空或无效值使用系统默认设备。</summary>
	public static string ReadOutputDeviceId(ConfigStore config)
	{
		string device = config.GetStringOr(OutputDeviceKey, "");
		return IsInvalidDevice(device) ? "" : device.Trim();
	}

	/// <summary>设备标识是否不能安全保存。不把原文放进异常。</summary>
	public static bool IsInvalidDevice(string? value)
	{
		if (string.IsNullOrEmpty(value)) return false;
		string trimmed = value.Trim();
		if (trimmed.Length > RealtimeVoiceLimits.MaximumInputDeviceLength) return true;
		foreach (char character in value)
		{
			if (char.IsControl(character)) return true;
		}
		return false;
	}
}
