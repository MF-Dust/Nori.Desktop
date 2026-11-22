using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Nori.Core.Voice.Audio;
using Nori.Desktop.Bridge;
using Nori.Desktop.Chat;
using Nori.Desktop.Runtime;
using Nori.Desktop.Settings;
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
	public async Task RealtimeVoiceOptionsPersistAndAppearInSnapshot()
	{
		var main = new FakeBridgeSource(WindowLabels.Main);
		BridgeCommands commands = CreateCommands();
		await commands.InvokeAsync(main, "settings_update_voice", Args(new
		{
			realtimeSttMode = "realtime", realtimeSttModel = "gpt-live-transcribe", realtimeBatchModel = "whisper-1",
			realtimeLanguage = "", realtimeOutputDevice = " speaker-2 ",
		}));
		Assert.Equal("realtime", _config.GetStringOr(RealtimeVoiceSettings.SttModeKey, ""));
		Assert.Equal("gpt-live-transcribe", _config.GetStringOr(RealtimeVoiceSettings.StreamingSttModelKey, ""));
		Assert.Equal("whisper-1", _config.GetStringOr(RealtimeVoiceSettings.BatchSttModelKey, ""));
		Assert.Equal(new ConfigValue.Text(""), _config.Get(RealtimeVoiceSettings.LanguageKey));
		Assert.Equal("speaker-2", _config.GetStringOr(RealtimeVoiceSettings.OutputDeviceKey, ""));
		JsonElement voice = JsonSerializer.SerializeToElement(_runtime.BuildSnapshot(), BridgeJson.Options).GetProperty("voice");
		Assert.Equal("realtime", voice.GetProperty("realtimeSttMode").GetString());
		Assert.Equal("gpt-live-transcribe", voice.GetProperty("realtimeSttModel").GetString());
		Assert.Equal("whisper-1", voice.GetProperty("realtimeBatchModel").GetString());
		Assert.Equal("", voice.GetProperty("realtimeLanguage").GetString());
		Assert.Equal("speaker-2", voice.GetProperty("realtimeOutputDevice").GetString());
		await Assert.ThrowsAsync<InvalidOperationException>(() => commands.InvokeAsync(main, "settings_update_voice",
			Args(new { realtimeSttMode = "local", realtimeOutputDevice = "speaker\n" })));
		Assert.Equal("realtime", _config.GetStringOr(RealtimeVoiceSettings.SttModeKey, ""));
		await Assert.ThrowsAsync<InvalidOperationException>(() => commands.InvokeAsync(main, "settings_update_voice",
			Args(new { realtimeSttMode = "unknown" })));
	}

	[Fact]
	public Task RealtimeVoiceChatControlsRespectSurfaceVisibilityAndSafeMode() => WithSettingsUiAsync(async () =>
	{
		using BridgeCommandsTests safeFixture = new(true);
		Window owner = new();
		using NativeChatContext chat = new(owner);
		using NativeChatContext quick = new(owner, NativeChatSurface.QuickChat);
		owner.Show();
		try
		{
			BridgeCommands commands = safeFixture.CreateCommands();
			foreach (string command in new[] { "realtime_voice_start", "realtime_voice_stop", "realtime_voice_interrupt", "realtime_voice_status", "tts_stop" })
			{
				NativeBridgePolicy.Validate(chat, command);
				Assert.Throws<InvalidOperationException>(() => NativeBridgePolicy.Validate(quick, command));
			}
			var blocked = await Assert.ThrowsAsync<InvalidOperationException>(() => commands.InvokeAsync(chat, "realtime_voice_start", Args(new { })));
			Assert.Contains("安全模式", blocked.Message);
			Assert.NotNull(await commands.InvokeAsync(chat, "realtime_voice_status", Args(new { })));
			Assert.Throws<InvalidOperationException>(() => NativeBridgePolicy.Validate(chat, "voice_mic_test"));
			Assert.Throws<InvalidOperationException>(() => NativeBridgePolicy.Validate(new FakeBridgeSource(WindowLabels.Main), "voice_mic_test"));
			var settings = new WindowContext(owner, WindowLabels.Settings);
			safeFixture._config.Set(RealtimeVoiceSettings.InputDeviceKey, new ConfigValue.Text("not-default"));
			var localTest = await Assert.ThrowsAsync<InvalidOperationException>(() => commands.InvokeAsync(settings, "voice_mic_test", Args(new { })));
			Assert.Contains("系统默认输入", localTest.Message);
			owner.Hide();
			Assert.Throws<InvalidOperationException>(() => NativeBridgePolicy.Validate(chat, "realtime_voice_start"));
		}
		finally { owner.Close(); }
	});

	[Fact]
	public void VoiceMicTestUsesExclusiveCaptureAndClearsLevelOnCancellation()
	{
		CaptureOwnership ownership = new();
		Assert.True(ownership.TryAcquire());
		Assert.Throws<InvalidOperationException>(() => AppRuntime.CaptureMicrophoneLevel(ownership,
			() => throw new Xunit.Sdk.XunitException("占用时不得打开设备"), _ => { }, CancellationToken.None));
		Assert.True(ownership.IsHeld);
		ownership.Release();
		using CancellationTokenSource cancellation = new();
		MeterCapture capture = new();
		List<double> levels = [];
		AppRuntime.CaptureMicrophoneLevel(ownership, () => capture, level =>
		{
			levels.Add(level);
			if (level > 0) cancellation.Cancel();
		}, cancellation.Token);
		Assert.Equal(2, levels.Count);
		Assert.True(levels[0] > 0);
		Assert.Equal(0, levels[1]);
		Assert.True(capture.Stopped && capture.Disposed);
		Assert.False(ownership.IsHeld);
	}

	private sealed class MeterCapture : IAudioCaptureDevice
	{
		public bool Stopped { get; private set; }
		public bool Disposed { get; private set; }
		public AudioFormat Open() => new(16000, 1);
		public int Read(Span<float> buffer, CancellationToken cancellationToken)
		{
			buffer[0] = 0.5f;
			return 1;
		}
		public void Stop() => Stopped = true;
		public void Dispose() => Disposed = true;
	}

	[Fact]
	public Task VoiceSettingsPageAdvancedFieldsAreCollapsedAndLocalized() => WithSettingsUiAsync(() =>
	{
		Window owner = new();
		using SettingsService service = new(_services, owner);
		using VoiceSettingsPage page = new(service);
		SettingsPagePresenter presenter = new() { DataContext = page };
		owner.Content = presenter;
		owner.Show();
		try
		{
			Expander advanced = Assert.Single(presenter.GetVisualDescendants().OfType<Expander>());
			Assert.False(advanced.IsExpanded);
			Assert.Equal("高级语音设置", AutomationProperties.GetName(advanced));
			var advancedSection = Assert.Single(page.Sections, section => section.IsCollapsible);
			Assert.Contains(advancedSection.Fields, field => field.Key == "realtimeLanguage");
			page.SetLanguage("en-US");
			owner.UpdateLayout();
			advanced = Assert.Single(presenter.GetVisualDescendants().OfType<Expander>());
			Assert.Equal("Advanced voice settings", AutomationProperties.GetName(advanced));
			Assert.False(advanced.IsExpanded);
			Assert.Contains(presenter.GetVisualDescendants().OfType<ProgressBar>(), level => AutomationProperties.GetName(level) == "Microphone level");
		}
		finally { owner.Close(); }
		return Task.CompletedTask;
	});

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
