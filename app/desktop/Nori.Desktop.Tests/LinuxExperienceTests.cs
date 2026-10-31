using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Nori.Core.Configuration;
using Nori.Core.Platform;
using Nori.Desktop.Windows;
using Nori.Startup;

namespace Nori.Desktop.Tests;

public partial class BridgeCommandsTests
{
	[Theory]
	[InlineData(":0", "wayland-0", "wayland", false)]
	[InlineData(null, "wayland-0", "wayland", true)]
	[InlineData(null, null, "Wayland", true)]
	[InlineData(" ", "wayland-0", null, true)]
	[InlineData(":0", null, "x11", false)]
	[InlineData(null, null, null, false)]
	public void Linux窗口后端与可用显示服务器一致(string? display, string? wayland, string? session, bool nativeWayland) =>
		Assert.Equal(nativeWayland, LinuxWindowing.UsesWayland(display, wayland, session));

	[Theory]
	[InlineData("zh-CN", 720, 480)]
	[InlineData("en-US", 720, 480)]
	[InlineData("zh-CN", 1920, 1080)]
	[InlineData("en-US", 1920, 1080)]
	public Task Linux无托盘入口与平台提示可呈现(string language, int width, int height) => WithSettingsUiAsync(async () =>
	{
		using BridgeCommandsTests fixture = new(safeMode: true);
		fixture._config.Set("language", new ConfigValue.Text(language));
		fixture.ConfigureDesktop();
		fixture._services.Runtime!.TrayAvailable = false;
		MainWindow main = new(WindowDefinition.All.Single(item => item.Label == WindowLabels.Main), fixture._services);
		QuickChatWindow quick = new(fixture._services);
		try
		{
			main.Width = width; main.Height = height; main.Show(); main.UpdateLayout();
			await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
			Assert.True(main.ShowInTaskbar);
			Assert.False(quick.ShowInTaskbar);
			Assert.Equal(WindowDecorations.None, quick.WindowDecorations);
			Assert.DoesNotContain(main.GetVisualDescendants().OfType<TextBlock>(), text =>
				text.Text?.Contains("Wayland", StringComparison.Ordinal) == true);
			Assert.Equal(PlatformServices.Current.Capabilities.SupportsTopmost, quick.Topmost);
			CaptureUi(main, $"linux-main-{PlatformServices.Current.Session}-{language}-{width}x{height}");
		}
		finally { main.AllowClose = true; main.Close(); await quick.PrepareShutdownAsync(); quick.AllowClose = true; quick.Close(); }
	});
}
