using System.Text.Json;
using Nori.Core.Voice.Realtime;

namespace Nori.Desktop.Bridge;

public sealed partial class BridgeCommands
{
	/// <summary>
	/// 开始实时监听。
	/// 前端调用：invoke("realtime_voice_start")
	/// </summary>
	private async Task<object?> RealtimeVoiceStartAsync(IBridgeSource source, CancellationToken cancellationToken)
	{
		RequireMainVoid(source);
		await Runtime.RealtimeVoice.StartAsync(cancellationToken).ConfigureAwait(false);
		Runtime.InvalidateSnapshot();
		return RealtimeVoiceStatusPayload();
	}

	/// <summary>
	/// 停止实时监听。
	/// 前端调用：invoke("realtime_voice_stop")
	/// </summary>
	private async Task<object?> RealtimeVoiceStopAsync(IBridgeSource source)
	{
		RequireMainVoid(source);
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
