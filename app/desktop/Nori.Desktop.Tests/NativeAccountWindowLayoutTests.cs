using Avalonia;
using Avalonia.Controls;
using Avalonia.Automation;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using Nori.Core.Cloud;
using Nori.Core.Platform;
using Nori.Desktop.Account;
using Nori.Desktop.Appearance;
using Nori.Desktop.Settings;
using Nori.Desktop.Windows;

namespace Nori.Desktop.Tests;

public partial class BridgeCommandsTests
{
	[Theory]
	[InlineData(false, 720, 480, "zh-CN")]
	[InlineData(true, 720, 480, "zh-CN")]
	[InlineData(false, 1920, 1080, "zh-CN")]
	[InlineData(true, 1920, 1080, "zh-CN")]
	[InlineData(false, 720, 480, "en-US")]
	[InlineData(true, 720, 480, "en-US")]
	[InlineData(false, 1920, 1080, "en-US")]
	[InlineData(true, 1920, 1080, "en-US")]
	public Task NativeAccountWindowsKeepChromeAndBadgeOutsideScroll(bool cloud, int width, int height, string language) => WithSettingsUiAsync(async () =>
	{
		using BridgeCommandsTests fixture = new(safeMode: true);
		SettingsLocalization.SetLanguage(language);
		Window window = cloud
			? new CloudSyncWindow(fixture._services.CloudSync)
			: new AccountWindow(_ => Task.FromResult(new SignInOutcome {Ok = false}), _ => { }, SignInMethod.Password);
		Size size = NativeWindowSizing.FitToWorkArea(new Size(window.Width, window.Height),
			new PixelRect(0, 0, width, height), 1, default, default);
		window.Width = size.Width;
		window.Height = size.Height;
		try
		{
			window.Show();
			window.UpdateLayout();
			Assert.Equal(size, window.ClientSize);
			Grid root = Assert.IsType<Grid>(window.Content);
			ScrollViewer scroll = window.GetLogicalDescendants().OfType<ScrollViewer>().First();
			if (cloud)
			{
				// 操作结果可能带多行未包含项；增长正文不能把标题栏与归属标识挤出窗口。
				StackPanel inner = Assert.IsType<StackPanel>(scroll.Content);
				inner.Children.Add(new TextBlock {Text = string.Join("\n", Enumerable.Repeat("未包含：本机资源路径", 30))});
				window.UpdateLayout();
			}
			Assert.Equal(3, root.Children.Count);
			NativeWindowChrome chrome = Assert.IsType<NativeWindowChrome>(root.Children[0]);
			Control badge = root.Children[2];
			Assert.Equal(40, chrome.Bounds.Height);
			Assert.Equal(NoriThemeTokens.Color("bg-sidebar"), Assert.IsAssignableFrom<ISolidColorBrush>(chrome.Background).Color);
			Assert.Equal(PlatformServices.Current.Capabilities.SupportsWindowDrag ? WindowDecorations.None : WindowDecorations.Full, window.WindowDecorations);
			Button close = Assert.Single(chrome.GetLogicalDescendants().OfType<Button>(), button => button.IsVisible);
			Assert.Equal(language == "en-US" ? "Close window" : "关闭窗口", AutomationProperties.GetName(close));
			Assert.True(badge.Bounds.Bottom <= window.ClientSize.Height);
			Assert.True(badge.Bounds.Height > 0);
			Assert.DoesNotContain(badge.GetVisualAncestors(), ancestor => ancestor is ScrollViewer);
			Assert.True(scroll.Viewport.Height > 0);
			if (cloud || height == 480) Assert.True(scroll.Extent.Height > scroll.Viewport.Height);
			string captureName = $"{(cloud ? "cloud" : "account")}-{width}x{height}-{language}";
			CaptureUi(window, captureName);
			ToolTip closeHint = new() { Content = ToolTip.GetTip(close) };
			ToolTip.SetTip(close, closeHint);
			ToolTip.SetIsOpen(close, true);
			await WaitForTooltipAsync(closeHint);
			CaptureUi(window, captureName + "-close-hint");
			ToolTip.SetIsOpen(close, false);
			bool closed = false;
			window.Closed += (_, _) => closed = true;
			close.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
			Assert.True(closed);
		}
		finally { window.Close(); SettingsLocalization.SetLanguage("zh-CN"); }
	});

	// 按需导出真实 Skia headless 帧，输出目录由本地验证指定，默认测试不写截图。
	private static void CaptureUi(TopLevel surface, string name)
	{
		string? directory = Environment.GetEnvironmentVariable("NORI_UI_CAPTURE_DIR");
		if (string.IsNullOrWhiteSpace(directory)) return;
		using var frame = surface.CaptureRenderedFrame();
		Assert.NotNull(frame);
		Directory.CreateDirectory(directory);
		frame.Save(Path.Combine(directory, name + ".png"), PngBitmapEncoderOptions.Default);
	}
}
