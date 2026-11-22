using System.Text.Json;
using Nori.Core.Voice.Realtime;

namespace Nori.Desktop.Bridge;

public sealed partial class BridgeCommands
{
	private async Task<object?> UpdateVoiceSettingsAsync(JsonElement args)
	{
		var voiceOptions = ReadRealtimeVoiceOptions(args);
		UpdateOptionalConfig(args, "volume", "audio_volume");
		UpdateOptionalConfig(args, "ttsProvider", "tts_provider");
		UpdateOptionalConfig(args, "ttsBaseUrl", "tts_base_url");
		UpdateOptionalConfig(args, "ttsModel", "tts_model");
		UpdateSecretConfig(args, "ttsApiKey", "tts_api_key");
		UpdateOptionalConfig(args, "ttsVoice", "tts_voice");
		UpdateOptionalConfig(args, "ttsSpeed", "tts_speed");
		UpdateBoolConfig(args, "ttsAutoPlay", "tts_auto_play");
		UpdateOptionalConfig(args, "gptsovitsBaseUrl", "gptsovits_base_url");
		UpdateOptionalConfig(args, "gptsovitsRefAudio", "gptsovits_ref_audio");
		UpdateOptionalConfig(args, "gptsovitsPromptText", "gptsovits_prompt_text");
		UpdateOptionalConfig(args, "gptsovitsPromptLang", "gptsovits_prompt_lang");
		UpdateOptionalConfig(args, "indexttsTemplateAudio", "indextts_template_audio");
		UpdateOptionalConfig(args, "indexttsEmoAlpha", "indextts_emo_alpha");
		UpdateOptionalConfig(args, "sttProvider", "stt_provider");
		UpdateOptionalConfig(args, "sttBaseUrl", "stt_base_url");
		UpdateSecretConfig(args, "sttApiKey", "stt_api_key");
		UpdateBoolConfig(args, "realtimeEnabled", RealtimeVoiceSettings.EnabledKey);
		UpdateBoolConfig(args, "realtimeContinuous", RealtimeVoiceSettings.ContinuousKey);
		UpdateOptionalConfig(args, "realtimeSensitivity", RealtimeVoiceSettings.SensitivityKey);
		UpdateOptionalConfig(args, "realtimeSilenceMs", RealtimeVoiceSettings.SilenceKey);
		UpdateBoolConfig(args, "realtimeInterruptEnabled", RealtimeVoiceSettings.InterruptKey);
		foreach (var (key, value) in voiceOptions) UpdateConfigDirect(key, value);
		if (_services.Runtime is { } runtime)
		{
			string raw = _services.Config.GetStringOr("audio_volume", "1");
			if (double.TryParse(raw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double vol))
			{
				runtime.Voice.SetVolume(vol);
			}
			if (HasTtsConfigurationChange(args) || HasString(args, "ttsModel")) runtime.Voice.NotifyConfigurationChanged();
			await runtime.RealtimeVoice.ApplySettingsAsync(RealtimeVoiceSettings.Read(_services.Config)).ConfigureAwait(false);
		}
		Runtime.InvalidateSnapshot();
		return null;
	}

	/// <summary>
	/// 开始实时监听。
	/// 前端调用：invoke("realtime_voice_start")
	/// </summary>
	private async Task<object?> RealtimeVoiceStartAsync(CancellationToken cancellationToken)
	{
		await Runtime.RealtimeVoice.StartAsync(cancellationToken).ConfigureAwait(false);
		Runtime.InvalidateSnapshot();
		return RealtimeVoiceStatusPayload();
	}

	/// <summary>
	/// 停止实时监听。
	/// 前端调用：invoke("realtime_voice_stop")
	/// </summary>
	private async Task<object?> RealtimeVoiceStopAsync()
	{
		await Runtime.RealtimeVoice.StopAsync().ConfigureAwait(false);
		Runtime.InvalidateSnapshot();
		return RealtimeVoiceStatusPayload();
	}

	/// <summary>
	/// 主动打断。播放期间麦克风检测暂停，这个命令仍然可用。
	/// 前端调用：invoke("realtime_voice_interrupt") → {interrupted}
	/// </summary>
	private object RealtimeVoiceInterrupt()
	{
		bool interrupted = Runtime.RealtimeVoice.Interrupt();
		Runtime.InvalidateSnapshot();
		return new {interrupted};
	}

	/// <summary>
	/// 读取实时语音状态。不返回录音、识别正文、密钥或设备原文。
	/// 前端调用：invoke("realtime_voice_status")
	/// </summary>
	private object RealtimeVoiceStatusPayload()
	{
		var (status, inputLevel, _) = Runtime.RealtimeVoice.ReadPresentation();
		return new
		{
			phase = status.Phase.ToString().ToLowerInvariant(),
			generation = status.Generation,
			error = status.Error,
			capturePaused = status.CapturePaused,
			interruptEnabled = status.InterruptEnabled,
			enabled = status.Enabled,
			continuous = status.Continuous,
			streaming = status.Streaming,
			speaking = Runtime.Voice.IsSpeaking,
			inputLevel,
			fallbackReason = status.FallbackReason,
			bargeIn = status.VoiceBargeInSupported,
			aecVerified = status.Aec.CanEnableVoiceBargeIn,
		};
	}

	/// <summary>在写入语音配置前校验所有新选项，避免部分保存。</summary>
	private static List<(string Key, string Value)> ReadRealtimeVoiceOptions(JsonElement args)
	{
		List<(string, string)> values = [];
		Add("realtimeInputDevice", RealtimeVoiceSettings.InputDeviceKey, (_, raw) => !RealtimeVoiceSettings.IsInvalidDevice(raw), "麦克风标识无效");
		Add("realtimeOutputDevice", RealtimeVoiceSettings.OutputDeviceKey, (_, raw) => !RealtimeVoiceSettings.IsInvalidDevice(raw), "输出设备标识无效");
		Add("realtimeSttMode", RealtimeVoiceSettings.SttModeKey, (text, _) => text is "batch" or "realtime" or "local", "语音识别模式无效");
		Add("realtimeSttModel", RealtimeVoiceSettings.StreamingSttModelKey, IsModel, "流式识别模型标识无效");
		Add("realtimeBatchModel", RealtimeVoiceSettings.BatchSttModelKey, IsModel, "整段识别模型标识无效");
		Add("realtimeLanguage", RealtimeVoiceSettings.LanguageKey,
			(text, raw) => text.Length <= 35 && !raw.Any(char.IsControl) && text.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'), "识别语言代码无效");
		return values;

		void Add(string argument, string key, Func<string, string, bool> isValid, string error)
		{
			if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(argument, out JsonElement value)) return;
			if (value.ValueKind != JsonValueKind.String || value.GetString() is not { } raw || !isValid(raw.Trim(), raw))
				throw new InvalidOperationException(error);
			values.Add((key, raw.Trim()));
		}

		static bool IsModel(string text, string raw) => text.Length <= 256 && !raw.Any(char.IsControl);
	}

	/// <summary>
	/// 进行限时本地麦克风测试，只计算电平，不录制或上传。
	/// 前端调用：invoke("voice_mic_test")，取消调用可提前结束。
	/// </summary>
	private async Task<object?> VoiceMicTestAsync(CancellationToken cancellationToken)
	{
		await Runtime.TestMicrophoneAsync(cancellationToken).ConfigureAwait(false);
		return null;
	}
}
