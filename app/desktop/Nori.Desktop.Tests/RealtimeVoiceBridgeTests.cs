using System.Text.Json;
using Avalonia.Input;
using Nori.Core.Configuration;
using Nori.Core.Voice.Realtime;
using Nori.Desktop.Windows;

namespace Nori.Desktop.Tests;

/// <summary>实时语音的设置、安全和窗口快捷键。不打开真实麦克风。</summary>
public partial class BridgeCommandsTests
{
	[Fact]
	public async Task 实时语音默认关闭且非法设备不会写入()
	{
		JsonElement status = JsonSerializer.SerializeToElement(await CreateCommands().InvokeAsync(
			new FakeBridgeSource(WindowLabels.Main), "realtime_voice_status", Args(new { })));
		Assert.Equal("idle", status.GetProperty("phase").GetString());
		Assert.False(status.GetProperty("streaming").GetBoolean());
		Assert.False(status.GetProperty("bargeIn").GetBoolean());
		Assert.False(status.GetProperty("enabled").GetBoolean());
		Assert.False(status.TryGetProperty("text", out _));

		InvalidOperationException disabled = await Assert.ThrowsAsync<InvalidOperationException>(() =>
			CreateCommands().InvokeAsync(new FakeBridgeSource(WindowLabels.Main), "realtime_voice_start", Args(new { })));
		Assert.Contains("请先打开", disabled.Message, StringComparison.Ordinal);

		await CreateCommands().InvokeAsync(new FakeBridgeSource(WindowLabels.Main), "settings_update_voice", Args(new
		{
			realtimeEnabled = true,
			realtimeContinuous = true,
			realtimeSensitivity = "0.25",
			realtimeSilenceMs = "1200",
			realtimeInterruptEnabled = false,
			realtimeInputDevice = "",
		}));
		Assert.True(_config.GetBoolOr(RealtimeVoiceSettings.EnabledKey, false));
		Assert.Equal(1200, RealtimeVoiceSettings.Read(_config).SilenceMilliseconds);
		Assert.Equal(0.25, RealtimeVoiceSettings.Read(_config).Sensitivity);
		Assert.False(RealtimeVoiceSettings.Read(_config).InterruptEnabled);

		InvalidOperationException invalid = await Assert.ThrowsAsync<InvalidOperationException>(() =>
			CreateCommands().InvokeAsync(new FakeBridgeSource(WindowLabels.Main), "settings_update_voice", Args(new
			{
				realtimeInputDevice = "mic\r\nsecret",
			})));
		Assert.Contains("无效", invalid.Message, StringComparison.Ordinal);
		Assert.DoesNotContain("secret", invalid.Message, StringComparison.Ordinal);
		Assert.Equal("", _config.GetStringOr(RealtimeVoiceSettings.InputDeviceKey, ""));

		await CreateCommands().InvokeAsync(new FakeBridgeSource(WindowLabels.Main), "settings_update_voice", Args(new
		{
			realtimeInputDevice = "hw:1",
		}));
		InvalidOperationException device = await Assert.ThrowsAsync<InvalidOperationException>(() =>
			CreateCommands().InvokeAsync(new FakeBridgeSource(WindowLabels.Main), "realtime_voice_start", Args(new { })));
		Assert.Contains("默认", device.Message, StringComparison.Ordinal);
		Assert.DoesNotContain("hw:1", device.Message, StringComparison.Ordinal);

		_config.Set(RealtimeVoiceSettings.EnabledKey, new ConfigValue.Boolean(true));
		InvalidOperationException partial = await Assert.ThrowsAsync<InvalidOperationException>(() =>
			CreateCommands().InvokeAsync(new FakeBridgeSource(WindowLabels.Main), "settings_update_voice", Args(new
			{
				realtimeEnabled = false,
				realtimeInputDevice = "mic\nsecret",
			})));
		Assert.Contains("无效", partial.Message, StringComparison.Ordinal);
		Assert.True(_config.GetBoolOr(RealtimeVoiceSettings.EnabledKey, false));
		Assert.Equal("hw:1", _config.GetStringOr(RealtimeVoiceSettings.InputDeviceKey, ""));

		JsonElement idle = JsonSerializer.SerializeToElement(await CreateCommands().InvokeAsync(
			new FakeBridgeSource(WindowLabels.Main), "realtime_voice_interrupt", Args(new { })));
		Assert.False(idle.GetProperty("interrupted").GetBoolean());
	}

	[Fact]
	public void 打断快捷键区分平台且不是单独的Esc()
	{
		KeyModifiers primary = System.OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;
		Assert.True(WindowManager.IsRealtimeInterruptGesture(Key.OemPeriod, primary | KeyModifiers.Shift));
		Assert.False(WindowManager.IsRealtimeInterruptGesture(Key.Escape, primary | KeyModifiers.Shift));
		Assert.False(WindowManager.IsRealtimeInterruptGesture(Key.OemPeriod, KeyModifiers.None));
		Assert.False(WindowManager.IsRealtimeInterruptGesture(Key.K, primary));
	}
}
