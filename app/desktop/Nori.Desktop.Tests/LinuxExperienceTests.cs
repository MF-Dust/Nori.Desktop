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
	[InlineData(null, null, null, null, true, false, "@im=fcitx")]
	[InlineData(null, null, null, null, false, true, "@im=ibus")]
	[InlineData(null, null, null, null, true, true, "@im=fcitx")]
	[InlineData(null, null, null, null, false, false, null)]
	[InlineData("fcitx", null, null, null, true, false, null)]
	[InlineData(null, "fcitx5", null, null, true, false, null)]
	[InlineData(null, null, "ibus", null, false, true, null)]
	[InlineData("none", null, null, null, true, false, null)]
	[InlineData(null, "none", null, null, true, false, null)]
	[InlineData(null, null, "none", null, false, true, null)]
	[InlineData(null, null, null, "@im=ibus", true, false, null)]
	[InlineData(null, "wayland", null, null, true, false, "@im=fcitx")]
	[InlineData("xim", null, null, null, true, false, "@im=fcitx")]
	[InlineData(null, null, null, "", true, false, "@im=fcitx")]
	public void X11在没有可识别输入法变量时选择会话里的Fcitx或IBus(
		string? avaloniaIm, string? gtkIm, string? qtIm, string? xmodifiers, bool fcitxReady, bool ibusReady, string? expected) =>
		Assert.Equal(expected, LinuxWindowing.SelectX11InputMethodModifiers(avaloniaIm, gtkIm, qtIm, xmodifiers, fcitxReady, ibusReady));

	[Theory]
	[InlineData(null, null, null, null, true)]
	[InlineData("none", null, null, null, false)]
	[InlineData(null, "fcitx", null, null, false)]
	[InlineData(null, null, null, "@im=fcitx", false)]
	[InlineData(null, "wayland", null, null, true)]
	public void X11只在Avalonia不会自己连上输入法时探测会话总线(
		string? avaloniaIm, string? gtkIm, string? qtIm, string? xmodifiers, bool expected) =>
		Assert.Equal(expected, LinuxWindowing.NeedsX11InputMethodProbe(avaloniaIm, gtkIm, qtIm, xmodifiers));

	[Fact]
	public void 会话里已有Fcitx时X11启动为本进程补上XMODIFIERS()
	{
		if (!System.OperatingSystem.IsLinux()) return;
		if (LinuxWindowing.UsesWayland(Environment.GetEnvironmentVariable("DISPLAY"),
			Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"), Environment.GetEnvironmentVariable("XDG_SESSION_TYPE"))) return;
		if (!LinuxWindowing.SessionOwnsFcitx()) return;
		string? avaloniaIm = Environment.GetEnvironmentVariable("AVALONIA_IM_MODULE");
		string? gtkIm = Environment.GetEnvironmentVariable("GTK_IM_MODULE");
		string? qtIm = Environment.GetEnvironmentVariable("QT_IM_MODULE");
		string? xmodifiers = Environment.GetEnvironmentVariable("XMODIFIERS");
		try
		{
			Environment.SetEnvironmentVariable("AVALONIA_IM_MODULE", null);
			Environment.SetEnvironmentVariable("GTK_IM_MODULE", null);
			Environment.SetEnvironmentVariable("QT_IM_MODULE", null);
			Environment.SetEnvironmentVariable("XMODIFIERS", null);
			LinuxWindowing.ApplyX11InputMethod();
			Assert.Equal("@im=fcitx", Environment.GetEnvironmentVariable("XMODIFIERS"));
		}
		finally
		{
			Environment.SetEnvironmentVariable("AVALONIA_IM_MODULE", avaloniaIm);
			Environment.SetEnvironmentVariable("GTK_IM_MODULE", gtkIm);
			Environment.SetEnvironmentVariable("QT_IM_MODULE", qtIm);
			Environment.SetEnvironmentVariable("XMODIFIERS", xmodifiers);
		}
	}

	[Fact]
	public void 原生Wayland禁用服务端装饰以保持重显后无边框()
	{
#pragma warning disable AVALONIA_WAYLAND_FORCE_CSD
		Assert.True(LinuxWindowing.CreateWaylandOptions().ForceDrawnDecorations);
#pragma warning restore AVALONIA_WAYLAND_FORCE_CSD
	}

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
				text.Text?.Contains("Wayland", StringComparison.Ordinal) == true
				|| text.Text?.Contains("系统托盘不可用", StringComparison.Ordinal) == true
				|| text.Text?.Contains("System tray unavailable", StringComparison.Ordinal) == true);
			Assert.Equal(PlatformServices.Current.Capabilities.SupportsTopmost, quick.Topmost);
			CaptureUi(main, $"linux-main-{PlatformServices.Current.Session}-{language}-{width}x{height}");
		}
		finally { main.AllowClose = true; main.Close(); await quick.PrepareShutdownAsync(); quick.AllowClose = true; quick.Close(); }
	});
}
