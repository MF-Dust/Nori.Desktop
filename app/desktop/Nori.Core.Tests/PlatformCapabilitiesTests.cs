using System.Runtime.InteropServices;
using Nori.Core.Platform;

namespace Nori.Core.Tests;

/// <summary>
/// 平台能力探测: 会话类型判定与降级契约
///
/// 真正的 P/Invoke 只能在对应平台上验证, 这里锁住「能力标志与会话类型的关系」,
/// 保证 Wayland 一类不支持的场景不会被误判成支持。
/// </summary>
public class PlatformCapabilitiesTests
{
	private const int GwlExStyle = -20;
	private const long WsExTopmost = 0x00000008;
	private const long WsExTransparent = 0x00000020;
	private const long WsExLayered = 0x00080000;
	private const uint WsPopup = 0x80000000;

	[DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern nint CreateWindowEx(
		uint exStyle, string className, string? windowName, uint style,
		int x, int y, int width, int height,
		nint parent, nint menu, nint instance, nint parameter);

	[DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
	private static extern nint GetWindowLongPtr(nint window, int index);

	[DllImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool DestroyWindow(nint window);

	[Fact]
	public void 会话类型与当前操作系统一致()
	{
		SessionType session = PlatformServices.DetectSession();
		if (OperatingSystem.IsWindows()) Assert.Equal(SessionType.Windows, session);
		else if (OperatingSystem.IsMacOS()) Assert.Equal(SessionType.MacOS, session);
		else if (OperatingSystem.IsLinux()) Assert.True(session is SessionType.X11 or SessionType.Wayland);
	}

	[Fact]
	public void Wayland会话下探测为Wayland()
	{
		if (!OperatingSystem.IsLinux()) return;

		string? sessionType = Environment.GetEnvironmentVariable("XDG_SESSION_TYPE");
		string? wayland = Environment.GetEnvironmentVariable("WAYLAND_DISPLAY");
		string? display = Environment.GetEnvironmentVariable("DISPLAY");
		try
		{
			// 纯 Wayland: 有 WAYLAND_DISPLAY 且没有 X 显示 (即没有 XWayland 兜底)
			Environment.SetEnvironmentVariable("XDG_SESSION_TYPE", "wayland");
			Environment.SetEnvironmentVariable("WAYLAND_DISPLAY", "wayland-0");
			Environment.SetEnvironmentVariable("DISPLAY", null);
			Assert.Equal(SessionType.Wayland, PlatformServices.DetectSession());

			// XWayland 可用时按 X11 处理 (逐像素穿透等能力仍然拿得到)
			Environment.SetEnvironmentVariable("DISPLAY", ":0");
			Assert.Equal(SessionType.X11, PlatformServices.DetectSession());
		}
		finally
		{
			Environment.SetEnvironmentVariable("XDG_SESSION_TYPE", sessionType);
			Environment.SetEnvironmentVariable("WAYLAND_DISPLAY", wayland);
			Environment.SetEnvironmentVariable("DISPLAY", display);
		}
	}

	[Fact]
	public void 不支持的平台能力标志全为false且穿透调用不抛()
	{
		UnsupportedPlatformServices services = new();
		PlatformCapabilities capabilities = services.Capabilities;

		Assert.False(capabilities.SupportsGlobalCursor);
		Assert.False(capabilities.SupportsWindowDrag);
		Assert.False(capabilities.SupportsHitThrough);
		Assert.False(capabilities.SupportsTopmost);
		Assert.False(capabilities.SupportsTray);
		Assert.Equal(SessionType.Unknown, services.Session);

		// 伴侣视窗每 ~10Hz 会调一次穿透切换: 不支持时必须静默忽略, 不能打断渲染循环
		services.SetClickThrough(0, true);
		services.SetClickThrough(1234, false);

		// 光标与拖动被前端按能力标志禁用; 万一调到要能明确报错便于定位
		Assert.Throws<PlatformNotSupportedException>(() => services.GetCursorPosition());
		Assert.Throws<PlatformNotSupportedException>(() => services.StartWindowDrag(1234));
	}

	[Fact]
	public void Windows能力齐全()
	{
		if (!OperatingSystem.IsWindows()) return;
		PlatformCapabilities capabilities = PlatformServices.Current.Capabilities;
		Assert.True(capabilities.SupportsGlobalCursor);
		Assert.True(capabilities.SupportsWindowDrag);
		Assert.True(capabilities.SupportsHitThrough);
		Assert.True(capabilities.SupportsTopmost);
		Assert.Equal(SessionType.Windows, PlatformServices.Current.Session);
	}

	[Fact]
	public void Windows穿透叠加分层透明样式且保持置顶()
	{
		if (!OperatingSystem.IsWindows()) return;

		nint window = CreateWindowEx((uint)WsExTopmost, "STATIC", null, WsPopup, 0, 0, 1, 1, 0, 0, 0, 0);
		Assert.NotEqual(0, window);
		try
		{
			WindowsPlatformServices services = new();
			services.SetClickThrough(window, true);
			long throughStyle = GetWindowLongPtr(window, GwlExStyle).ToInt64();
			Assert.NotEqual(0, throughStyle & WsExTransparent);
			Assert.NotEqual(0, throughStyle & WsExLayered);
			Assert.NotEqual(0, throughStyle & WsExTopmost);

			services.SetClickThrough(window, false);
			long clickableStyle = GetWindowLongPtr(window, GwlExStyle).ToInt64();
			Assert.Equal(0, clickableStyle & WsExTransparent);
			Assert.Equal(0, clickableStyle & WsExLayered);
			Assert.NotEqual(0, clickableStyle & WsExTopmost);
		}
		finally
		{
			DestroyWindow(window);
		}
	}

	[Fact]
	public void 当前平台能读到全局光标时坐标为有限值()
	{
		if (!PlatformServices.Current.Capabilities.SupportsGlobalCursor) return;
		(double x, double y) = PlatformServices.Current.GetCursorPosition();
		Assert.True(double.IsFinite(x));
		Assert.True(double.IsFinite(y));
	}
}
