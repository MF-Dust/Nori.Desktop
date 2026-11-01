using Avalonia;

namespace Nori.Startup;

/// <summary>有 XWayland 时保留桌宠定位与穿透能力；无 DISPLAY 时直接使用原生 Wayland。</summary>
internal static class LinuxWindowing
{
	internal static AppBuilder UseNoriLinuxBackend(this AppBuilder builder)
	{
		if (OperatingSystem.IsLinux() && UsesWayland(Environment.GetEnvironmentVariable("DISPLAY"),
			Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"), Environment.GetEnvironmentVariable("XDG_SESSION_TYPE")))
			return builder.With(CreateWaylandOptions()).UseWayland();
		return builder;
	}

	/// <summary>宿主自带窗口外观，禁止重建 Wayland 表面后重新协商出系统标题栏。</summary>
	internal static WaylandPlatformOptions CreateWaylandOptions()
	{
#pragma warning disable AVALONIA_WAYLAND_FORCE_CSD
		return new WaylandPlatformOptions { ForceDrawnDecorations = true };
#pragma warning restore AVALONIA_WAYLAND_FORCE_CSD
	}

	internal static bool UsesWayland(string? display, string? waylandDisplay, string? sessionType) =>
		string.IsNullOrWhiteSpace(display) && (!string.IsNullOrWhiteSpace(waylandDisplay)
			|| string.Equals(sessionType, "wayland", StringComparison.OrdinalIgnoreCase));
}
