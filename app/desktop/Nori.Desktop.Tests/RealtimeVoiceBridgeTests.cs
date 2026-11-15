using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Nori.Core.Configuration;
using Nori.Core.Voice.Realtime;
using Nori.Desktop.Settings;
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

		JsonElement idle = JsonSerializer.SerializeToElement(await CreateCommands().InvokeAsync(
			new FakeBridgeSource(WindowLabels.Main), "realtime_voice_interrupt", Args(new { })));
		Assert.False(idle.GetProperty("interrupted").GetBoolean());
	}

	[Theory]
	[InlineData("zh-CN", 720, 480)]
	[InlineData("zh-CN", 1920, 1080)]
	[InlineData("en-US", 720, 480)]
	[InlineData("en-US", 1920, 1080)]
	public Task 实时语音设置在两种语言和两种分辨率下可聚焦(string language, int width, int height) => WithSettingsUiAsync(async () =>
	{
		using BridgeCommandsTests fixture = new();
		fixture._config.Set(ConfigStore.KeyLanguage, new ConfigValue.Text(language));
		SettingsWindow window = new() { Width = width, Height = height };
		using SettingsService service = new(fixture._services, window);
		using SettingsViewModel viewModel = new(service);
		window.DataContext = viewModel;
		try
		{
			window.Show();
			viewModel.Navigate("voice");
			await viewModel.RefreshSnapshotAsync();
			await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
			SettingsPageBase page = viewModel.Groups.SelectMany(group => group.Pages).Single(item => item.Key == "voice").Page;
			Assert.Contains(page.Sections, section => section.Fields.Any(field => field.Key == "realtimeEnabled" && field.IsVisible));
			SettingsFieldViewModel status = page.Sections.SelectMany(section => section.Fields).Single(field => field.Key == "realtimeStatus");
			SettingsFieldViewModel interrupt = page.Sections.SelectMany(section => section.Fields).Single(field => field.Key == "realtimeInterrupt");
			Assert.True(status.IsReadOnly);
			Assert.Contains(language == "en-US" ? "Not listening" : "未监听", status.Text, StringComparison.Ordinal);
			Assert.Contains(language == "en-US" ? "Command+Shift+Esc" : "Ctrl+Shift+Esc", interrupt.Description, StringComparison.Ordinal);

			SettingsPagePresenter presenter = Assert.IsType<SettingsPagePresenter>(window.FindControl<SettingsPagePresenter>("PagePresenter"));
			Button button = presenter.GetVisualDescendants().OfType<Button>().Single(item =>
				item.Content as string == (language == "en-US" ? "Interrupt speech" : "打断朗读"));
			Assert.True(button.Focus());
			Assert.True(button.IsFocused);
			Assert.True(button.Bounds.Height > 0);
			Assert.True(presenter.Bounds.Width > 0);
			Assert.True(presenter.Bounds.Height > 0);
			CaptureVoiceUi(window, $"realtime-voice-{language}-{width}x{height}");
		}
		finally
		{
			window.DataContext = null;
			window.Close();
			SettingsLocalization.SetLanguage("zh-CN");
		}
	});

	[Fact]
	public void 打断快捷键区分平台且不是单独的Esc()
	{
		KeyModifiers primary = System.OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;
		Assert.True(WindowManager.IsRealtimeInterruptGesture(Key.Escape, primary | KeyModifiers.Shift));
		Assert.False(WindowManager.IsRealtimeInterruptGesture(Key.Escape, KeyModifiers.None));
		Assert.False(WindowManager.IsRealtimeInterruptGesture(Key.K, primary));
	}

	private static void CaptureVoiceUi(TopLevel surface, string name)
	{
		string? directory = Environment.GetEnvironmentVariable("NORI_UI_CAPTURE_DIR");
		if (string.IsNullOrWhiteSpace(directory)) return;
		AvaloniaHeadlessPlatform.ForceRenderTimerTick();
		using Bitmap? frame = surface.CaptureRenderedFrame();
		// 默认 headless drawing 不产出像素。只有换上 Skia 时才留下截图，布局断言不依赖它。
		if (frame is null) return;
		Directory.CreateDirectory(directory);
		frame.Save(Path.Combine(directory, name + ".png"), PngBitmapEncoderOptions.Default);
	}
}
