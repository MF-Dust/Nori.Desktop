using Nori.Core.Platform;

namespace Nori.Desktop.Automation.Desktop;

internal sealed record DesktopAutomationPlatform(SessionType Session, string? UnavailableReason)
{
	internal const string UnsupportedReason = "桌面自动化仅支持 Windows 和 Linux X11 会话";
	internal bool IsSupported => UnavailableReason is null;

	internal static DesktopAutomationPlatform Detect(bool safeMode)
	{
		if (OperatingSystem.IsWindows()) return new(SessionType.Windows, null);
		if (!OperatingSystem.IsLinux()) return new(SessionType.Unknown, UnsupportedReason);
		return DetectLinux(
			Environment.GetEnvironmentVariable("XDG_SESSION_TYPE"),
			Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"),
			safeMode ? static () => "安全模式已禁用自动化" : LinuxX11Input.ProbeUnavailableReason);
	}

	internal static DesktopAutomationPlatform DetectLinux(string? sessionType, string? waylandDisplay, Func<string?> probeX11)
	{
		// 即使 DISPLAY 指向 XWayland，也不能把整个 Wayland 桌面声明为可操作。
		if (string.Equals(sessionType, "wayland", StringComparison.OrdinalIgnoreCase)
			|| (!string.Equals(sessionType, "x11", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(waylandDisplay)))
			return new(SessionType.Wayland, "Wayland 桌面环境受安全模型限制暂不支持键鼠接管；切换到 X11 会话即可启用");
		return new(SessionType.X11, probeX11());
	}
}
