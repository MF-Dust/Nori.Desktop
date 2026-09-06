using System.Runtime.Versioning;

namespace Nori.Core.Platform;

/// <summary>
/// 运行时会话类型
/// </summary>
public enum SessionType
{
	/// <summary>Windows 桌面</summary>
	Windows,

	/// <summary>macOS (Cocoa)</summary>
	MacOS,

	/// <summary>Linux + X11 (含 XWayland)</summary>
	X11,

	/// <summary>Linux + 原生 Wayland</summary>
	Wayland,

	/// <summary>其他/未知</summary>
	Unknown,
}

/// <summary>
/// 平台能力标志
///
/// 前端一切与平台相关的 UI 都由这些标志驱动 —— 不支持就明确禁用并给出说明,
/// 而不是靠 try/catch 静默吞掉 PlatformNotSupportedException。
/// </summary>
public sealed record PlatformCapabilities
{
	/// <summary>能否读取窗口外的全局光标 (眼神跟随)</summary>
	public required bool SupportsGlobalCursor { get; init; }

	/// <summary>能否从 HTML 标题栏发起原生窗口拖动</summary>
	public required bool SupportsWindowDrag { get; init; }

	/// <summary>能否按模型交互区域做点击穿透</summary>
	public required bool SupportsHitThrough { get; init; }

	/// <summary>能否置顶窗口</summary>
	public required bool SupportsTopmost { get; init; }

	/// <summary>系统托盘是否可用</summary>
	public required bool SupportsTray { get; init; }
}

/// <summary>
/// 平台相关能力
///
/// 浏览器拿不到窗口外的光标, 也无法从 WebView 内部发起原生窗口拖动或设置交互区域穿透,
/// 这几件事必须由宿主用系统 API 完成. 各平台实现:
/// - Windows: user32 (GetCursorPos / WM_NCLBUTTONDOWN / WM_NCHITTEST)
/// - macOS:   ObjC runtime (NSEvent.mouseLocation / performWindowDragWithEvent: / setIgnoresMouseEvents:)
/// - Linux X11: libX11 + XShape (XQueryPointer / _NET_WM_MOVERESIZE / ShapeInput)
/// - Wayland: 协议不允许全局光标与输入形状, 相关能力标志为 false, 由前端降级
/// </summary>
public interface IPlatformServices
{
	/// <summary>当前会话类型</summary>
	SessionType Session { get; }

	/// <summary>当前平台的能力标志</summary>
	PlatformCapabilities Capabilities { get; }

	/// <summary>
	/// 获取全局光标位置 (物理像素, 相对屏幕左上角)
	/// </summary>
	(double X, double Y) GetCursorPosition();

	/// <summary>
	/// 从当前鼠标按下状态发起窗口拖动
	///
	/// WebView 会吞掉指针事件, 因此 HTML 标题栏的拖动要回调到宿主由系统接管
	/// </summary>
	void StartWindowDrag(nint windowHandle);

	/// <summary>
	/// 设置窗口是否整体穿透点击
	///
	/// Windows 上通过 WS_EX_LAYERED + WS_EX_TRANSPARENT 让系统 hit-test 无视 Z 序跳过窗口, 保持置顶;
	/// macOS / Linux 则按 alpha 采样结果在「整窗可点」与「整窗穿透」之间切换。
	/// </summary>
	void SetClickThrough(nint windowHandle, bool through);
}

/// <summary>
/// 平台能力入口
/// </summary>
public static class PlatformServices
{
	/// <summary>
	/// 当前平台的实现
	/// </summary>
	public static IPlatformServices Current { get; } = Create();

	private static IPlatformServices Create()
	{
		if (OperatingSystem.IsWindows()) return new WindowsPlatformServices();
		if (OperatingSystem.IsMacOS()) return new MacPlatformServices();
		if (OperatingSystem.IsLinux()) return LinuxPlatformServices.Create();
		return new UnsupportedPlatformServices();
	}

	/// <summary>
	/// 探测当前会话类型 (Linux 上区分 X11 与 Wayland)
	/// </summary>
	[UnsupportedOSPlatform("browser")]
	public static SessionType DetectSession()
	{
		if (OperatingSystem.IsWindows()) return SessionType.Windows;
		if (OperatingSystem.IsMacOS()) return SessionType.MacOS;
		if (!OperatingSystem.IsLinux()) return SessionType.Unknown;

		// XWayland 下 DISPLAY 有值且 X11 API 可用, 按 X11 处理即可
		string sessionType = Environment.GetEnvironmentVariable("XDG_SESSION_TYPE") ?? "";
		bool hasWayland = (Environment.GetEnvironmentVariable("WAYLAND_DISPLAY") ?? "").Length > 0;
		bool hasDisplay = (Environment.GetEnvironmentVariable("DISPLAY") ?? "").Length > 0;
		if (sessionType.Equals("wayland", StringComparison.OrdinalIgnoreCase) && !hasDisplay) return SessionType.Wayland;
		if (hasWayland && !hasDisplay) return SessionType.Wayland;
		return SessionType.X11;
	}
}
