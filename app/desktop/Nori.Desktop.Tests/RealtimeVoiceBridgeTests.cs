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
using Nori.Core.Voice.Stt;
using Nori.Desktop.Audio;
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
			safeFixture._config.Set(RealtimeVoiceSettings.InputDeviceKey, new ConfigValue.Text("bad\nmic"));
			var localTest = await Assert.ThrowsAsync<InvalidOperationException>(() => commands.InvokeAsync(settings, "voice_mic_test", Args(new { })));
			Assert.Contains("麦克风标识无效", localTest.Message);
			owner.Hide();
			Assert.Throws<InvalidOperationException>(() => NativeBridgePolicy.Validate(chat, "realtime_voice_start"));
		}
		finally { owner.Close(); }
	});

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public Task AudioListDevices_设置窗口列出设备且安全模式不选云端(bool safeMode) => WithSettingsUiAsync(async () =>
	{
		NativeAudioDeviceManager devices = new(direction => direction == AudioDeviceDirection.Input
			? [new("mic-bt", "蓝牙麦克风", direction, true, true, true, null, null, "免提双向模式")]
			: [new("speaker-usb", "USB 扬声器", direction, true, false, true, null, null, null)]);
		using BridgeCommandsTests fixture = new(safeMode, null, audioDevices: devices,
			openCapture: _ => throw new InvalidOperationException("设备枚举不应打开麦克风"));
		Window owner = new();
		try
		{
			WindowContext settings = new(owner, WindowLabels.Settings);
			BridgeCommands commands = fixture.CreateCommands();
			JsonElement result = JsonSerializer.SerializeToElement(await commands.InvokeAsync(settings, "audio_list_devices", Args(new { })));
			JsonElement input = Assert.Single(result.GetProperty("inputs").EnumerateArray());
			Assert.Equal("mic-bt", input.GetProperty("id").GetString());
			Assert.Equal("蓝牙麦克风", input.GetProperty("name").GetString());
			Assert.True(input.GetProperty("isDefault").GetBoolean());
			Assert.True(input.GetProperty("isBluetooth").GetBoolean());
			Assert.Equal("免提双向模式", input.GetProperty("note").GetString());
			Assert.Equal("speaker-usb", Assert.Single(result.GetProperty("outputs").EnumerateArray()).GetProperty("id").GetString());
			Assert.Throws<InvalidOperationException>(() => NativeBridgePolicy.Validate(new FakeBridgeSource(WindowLabels.Main), "audio_list_devices"));

			fixture._config.Set(RealtimeVoiceSettings.SttModeKey, new ConfigValue.Text("realtime"));
			fixture._config.Set(RealtimeVoiceSettings.InputDeviceKey, new ConfigValue.Text("mic-bt"));
			await fixture._runtime.RealtimeVoice.ApplySettingsAsync(RealtimeVoiceSettings.Read(fixture._config));
			Assert.Equal(!safeMode, fixture._runtime.RealtimeVoice.TranscriberSupportsStreaming);
			Assert.Equal(safeMode ? 1 : 4, fixture._runtime.SttProviders.List().Count);
			Assert.Equal(!safeMode, fixture._runtime.SttProviders.List().Any(entry => entry.Descriptor.RequiresNetwork));
			Assert.Null(fixture._runtime.SttProviders.SelectBest(new() { AllowNetwork = false, RequireBatch = true }));
			Assert.IsType<LocalOfflineSttProvider>(fixture._runtime.SttProviders.SelectBestProvider(new() { AllowNetwork = false, RequireLocalOffline = true }));

			using SettingsService service = new(fixture._services, owner);
			using VoiceSettingsPage page = new(service);
			page.ApplySnapshot(JsonSerializer.SerializeToElement(fixture._runtime.BuildSnapshot()));
			var fields = page.Sections.SelectMany(section => section.Fields).ToArray();
			var picker = Assert.Single(fields, field => field.Key == "realtimeInputDeviceChoice");
			Assert.Equal("mic-bt", picker.Selected);
			Assert.Contains(picker.Options, option => option.Value == "mic-bt" && option.DisplayText.Contains("免提"));
			Assert.Contains(picker.Options, option => option.Value == "");
			Assert.Contains(fields, field => field.Key == "realtimeInputDevice" && field.EditorKind == SettingsEditorKind.Text);
		}
		finally { owner.Close(); }
	});

	[Fact]
	public async Task RealtimeVoice_打开指定麦克风且切换后必须重新开始()
	{
		List<string?> openedIds = [];
		using BridgeCommandsTests fixture = new(false, null, audioDevices: new NativeAudioDeviceManager(_ => []), openCapture: id =>
		{
			openedIds.Add(id);
			return new WaitingCapture();
		});
		BridgeCommands commands = fixture.CreateCommands();
		FakeBridgeSource main = new(WindowLabels.Main);
		await commands.InvokeAsync(main, "settings_update_voice", Args(new { realtimeEnabled = true, realtimeInputDevice = "mic-usb" }));
		await commands.InvokeAsync(main, "realtime_voice_start", Args(new { }));
		await commands.InvokeAsync(main, "settings_update_voice", Args(new { realtimeInputDevice = "mic-bt" }));
		Assert.Equal(RealtimeVoicePhase.Idle, fixture._runtime.RealtimeVoice.Phase);
		Assert.Equal(["mic-usb"], openedIds);
		await commands.InvokeAsync(main, "realtime_voice_start", Args(new { }));
		await commands.InvokeAsync(main, "realtime_voice_stop", Args(new { }));
		Assert.Equal(["mic-usb", "mic-bt"], openedIds);
	}

	private sealed class WaitingCapture : IAudioCaptureDevice
	{
		public AudioFormat Open() => new(16000, 1);
		public int Read(Span<float> buffer, CancellationToken cancellationToken)
		{
			cancellationToken.WaitHandle.WaitOne();
			return 0;
		}
		public void Stop() { }
		public void Dispose() { }
	}

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
