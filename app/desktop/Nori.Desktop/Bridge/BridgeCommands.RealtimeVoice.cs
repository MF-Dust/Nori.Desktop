using System.Text.Json;
using Nori.Core.Voice.Realtime;

namespace Nori.Desktop.Bridge;

public sealed partial class BridgeCommands
{
	private async Task<object?> UpdateVoiceSettingsAsync(JsonElement args)
	{
		ValidateRealtimeInputDevice(args);
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
		UpdateRealtimeInputDevice(args);
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
		RealtimeVoiceStatus status = Runtime.RealtimeVoice.Status();
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
			bargeIn = status.VoiceBargeInSupported,
		};
	}

	/// <summary>在写入任何语音配置之前拒绝非法设备标识，避免半截保存。</summary>
	private static void ValidateRealtimeInputDevice(JsonElement args)
	{
		if (!TryReadRealtimeInputDevice(args, out string text)) return;
		if (RealtimeVoiceSettings.IsInvalidDevice(text)) throw new InvalidOperationException("麦克风标识无效");
	}

	/// <summary>保存输入设备标识。调用前须先通过 <see cref="ValidateRealtimeInputDevice"/>。</summary>
	private void UpdateRealtimeInputDevice(JsonElement args)
	{
		if (!TryReadRealtimeInputDevice(args, out string text)) return;
		if (RealtimeVoiceSettings.IsInvalidDevice(text)) throw new InvalidOperationException("麦克风标识无效");
		UpdateConfigDirect(RealtimeVoiceSettings.InputDeviceKey, text.Trim());
	}

	private static bool TryReadRealtimeInputDevice(JsonElement args, out string text)
	{
		text = "";
		if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty("realtimeInputDevice", out JsonElement value)) return false;
		if (value.ValueKind != JsonValueKind.String) return false;
		text = value.GetString() ?? "";
		return true;
	}
}
