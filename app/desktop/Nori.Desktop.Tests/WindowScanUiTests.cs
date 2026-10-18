using System.Reflection;
using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Nori.Core.Configuration;
using Nori.Desktop.Chat;
using Nori.Desktop.QuickChat;
using Nori.Desktop.Vision;
using Nori.Desktop.Windows;

namespace Nori.Desktop.Tests;

public partial class BridgeCommandsTests
{
	[Fact]
	public Task ScanButtonInSafeModeShowsReasonWithoutOpeningOverlayOrConsumingCharge() => WithSettingsUiAsync(async () =>
	{
		using BridgeCommandsTests fixture = new(safeMode: true);
		QuickChatWindow window = new(fixture._services);
		try
		{
			window.Show();
			await window.Body.RefreshAsync();
			window.UpdateLayout();
			Button scan = window.Body.GetVisualDescendants().OfType<Button>().Single(control => control.Name == "QuickChatScan");
			scan.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
			Border readout = window.Body.GetVisualDescendants().OfType<Border>().Single(control => control.Name == "QuickChatScanReadout");
			Assert.True(readout.IsVisible);
			Assert.Contains("安全模式", AutomationProperties.GetName(readout));
			Assert.Equal(5, fixture._services.ScanChip.Charges(DateTimeOffset.UtcNow));
			Assert.Single(window.InteractiveRects(), rect => rect.Height > 0 && rect.Width > 0 && rect.Height < 60);
		}
		finally { window.AllowClose = true; window.Close(); }
	});

	[Fact]
	public Task ScanReadoutTypesUnicodeExpiresAndHonorsReducedMotion() => WithSettingsUiAsync(() =>
	{
		using BridgeCommandsTests fixture = new(safeMode: true);
		Window host = new();
		using NativeChatService service = new(fixture._services, host, NativeChatSurface.QuickChat);
		DateTimeOffset now = DateTimeOffset.UtcNow;
		bool reduced = false;
		using QuickChatView view = new(service, (_, _, _) => Task.FromResult(default(JsonElement)), () => now, () => reduced);
		view.ShowScanReadout("A🌊中");
		var render = typeof(QuickChatView).GetMethod("RenderScan", BindingFlags.Instance | BindingFlags.NonPublic)!;
		var textField = typeof(QuickChatView).GetField("_scanText", BindingFlags.Instance | BindingFlags.NonPublic)!;
		TextBlock text = (TextBlock)textField.GetValue(view)!;
		Assert.Equal("▌", text.Text);
		now += TimeSpan.FromMilliseconds(56);
		render.Invoke(view, [now, false]);
		Assert.Equal("A🌊▌", text.Text);
		reduced = true;
		render.Invoke(view, [now, false]);
		Assert.Equal("A🌊中", text.Text);
		now += TimeSpan.FromSeconds(30);
		render.Invoke(view, [now, false]);
		Assert.Equal("", text.Text);
		return Task.CompletedTask;
	});

	[Theory]
	[InlineData(720, 480, false)]
	[InlineData(1920, 1080, false)]
	[InlineData(720, 480, true)]
	[InlineData(1920, 1080, true)]
	public Task ScanPresentationMatchesReferenceAtDesktopSizes(int width, int height, bool english) => WithSettingsUiAsync(async () =>
	{
		using BridgeCommandsTests fixture = new(safeMode: true);
		fixture._config.Set(ConfigStore.KeyLanguage, new ConfigValue.Text(english ? "en-US" : "zh-CN"));
		fixture._runtime.InvalidateSnapshot();
		QuickChatWindow chat = new(fixture._services);
		WindowScanOverlay? overlay = null;
		try
		{
			chat.Show();
			await chat.Body.RefreshAsync();
			chat.Body.SetHostVisible(false);
			chat.Body.ShowScanReadout(english ? "A code editor is open. The build completed successfully." : "当前窗口是代码编辑器，构建已成功完成。\n可以继续运行测试验证改动。");
			typeof(QuickChatView).GetField("_scanReceivedAt", BindingFlags.Instance | BindingFlags.NonPublic)!
				.SetValue(chat.Body, DateTimeOffset.UtcNow.AddSeconds(-5));
			typeof(QuickChatView).GetMethod("RenderScan", BindingFlags.Instance | BindingFlags.NonPublic)!
				.Invoke(chat.Body, [DateTimeOffset.UtcNow, true]);
			chat.Width = 308;
			chat.Height = chat.MeasureContentHeight(height);
			chat.UpdateLayout();
			Assert.InRange(chat.Height, 74, height);
			AvaloniaHeadlessPlatform.ForceRenderTimerTick();
			CaptureUi(chat, $"scan-readout-{width}x{height}-{english}");
			int selected = 0, cancelled = 0, cycled = 0, confirmed = 0;
			overlay = new(new PixelRect(0, 0, width, height), _ => { }, _ => selected++, () => cancelled++, direction => cycled += direction, () => confirmed++) { English = english };
			overlay.Show();
			overlay.SetTarget(new(42, english ? "Editor — project.cs" : "编辑器 — 项目.cs", 4242, new(60, 40, width - 120, height - 160), 96, false), scanning: false);
			overlay.UpdateLayout();
			AvaloniaHeadlessPlatform.ForceRenderTimerTick();
			CaptureUi(overlay, $"scan-picking-{width}x{height}-{english}");
			overlay.SetTarget(new(42, english ? "Editor — project.cs" : "编辑器 — 项目.cs", 4242, new(60, 40, width - 120, height - 160), 96, false), scanning: true);
			AvaloniaHeadlessPlatform.ForceRenderTimerTick();
			CaptureUi(overlay, $"scan-scanning-{width}x{height}-{english}");
			overlay.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Tab });
			overlay.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
			overlay.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
			Assert.Equal(1, cycled); Assert.Equal(1, confirmed); Assert.Equal(1, cancelled); Assert.Equal(0, selected);
		}
		finally { overlay?.Close(); chat.AllowClose = true; chat.Close(); }
	});
}
