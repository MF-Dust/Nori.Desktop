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

	/// <summary>保存输入设备标识。非法值直接拒绝，不把原文写进异常。</summary>
	private void UpdateRealtimeInputDevice(JsonElement args)
	{
		if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty("realtimeInputDevice", out JsonElement value)) return;
		if (value.ValueKind != JsonValueKind.String) return;
		string text = value.GetString() ?? "";
		if (RealtimeVoiceSettings.IsInvalidDevice(text)) throw new InvalidOperationException("麦克风标识无效");
		UpdateConfigDirect(RealtimeVoiceSettings.InputDeviceKey, text.Trim());
	}
}
