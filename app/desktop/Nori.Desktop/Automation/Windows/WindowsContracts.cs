using Nori.Core.Automation;

namespace Nori.Desktop.Automation.Windows;

/// <summary>Windows 自动化能力状态。</summary>
public sealed record WindowsAutomationAvailability(bool IsAvailable, string Reason)
{
	public static WindowsAutomationAvailability Current { get; } = OperatingSystem.IsWindows()
		? new(true, "") : new(false, "Windows 桌面自动化仅支持 Windows");
}

/// <summary>可见顶层窗口信息。</summary>
public sealed record WindowsTopLevelWindow(nint Handle, string Title, int ProcessId, AutomationBounds Bounds, uint Dpi, bool IsForeground);

/// <summary>目标拒绝原因。</summary>
public enum WindowsTargetRejection { None, UnsupportedPlatform, InvalidHandle, NotTopLevel, NotVisible, NotForeground, SecureDesktop, UipiBlocked, InvalidBounds }

/// <summary>目标校验结果。</summary>
public sealed record WindowsTargetValidationResult(bool IsValid, WindowsTargetRejection Rejection, string Reason)
{
	public static WindowsTargetValidationResult Valid { get; } = new(true, WindowsTargetRejection.None, "");
}

/// <summary>截图格式。</summary>
public enum WindowsScreenshotFormat { Png, Jpeg }

/// <summary>截图请求。</summary>
/// <summary>
/// 截屏请求。
///
/// <c>RequireForeground</c> 默认为 true，与自动化的既有判据一致。读屏要截的是被 Nori 自己的
/// 窗口挡住的那一个，按定义不是前台，因此需要能关掉这条要求。
/// </summary>
public sealed record WindowsScreenshotRequest(
	WindowsScreenshotFormat Format = WindowsScreenshotFormat.Png,
	int Quality = 90,
	bool RequireForeground = true);

/// <summary>内存中的窗口截图。</summary>
public sealed record WindowsScreenshot(byte[] Data, int Width, int Height, uint Dpi, WindowsScreenshotFormat Format);

/// <summary>输入结果。</summary>
public sealed record WindowsAutomationResult(bool Succeeded, string? Error)
{
	public static WindowsAutomationResult Success { get; } = new(true, null);
}

/// <summary>Win32 矩形，单位为物理像素。</summary>
public readonly record struct WindowsNativeRect(int Left, int Top, int Right, int Bottom)
{
	public int Width => Right - Left;
	public int Height => Bottom - Top;
}

/// <summary>SendInput 抽象事件。</summary>
public readonly record struct WindowsInputPacket(WindowsInputPacketKind Kind, ushort VirtualKey = 0, ushort ScanCode = 0, uint Flags = 0, int MouseData = 0, int AbsoluteX = 0, int AbsoluteY = 0);
public enum WindowsInputPacketKind { MouseMove, MouseDown, MouseUp, MouseWheel, Keyboard }
public readonly record struct WindowsInputSendFailure(int ErrorCode, string Reason);

/// <summary>窗口相关 Win32 最小接口，便于 fake 测试。</summary>
public interface IWindowsWindowNativeApi
{
	bool TryEnumerateTopLevelWindows(Func<nint, bool> callback);
	bool IsWindow(nint handle);
	bool IsWindowVisible(nint handle);
	nint GetRootWindow(nint handle);
	string GetWindowTitle(nint handle);
	int GetProcessId(nint handle);
	nint GetForegroundWindow();
	bool TryGetWindowRect(nint handle, out WindowsNativeRect rect);
	uint GetDpiForWindow(nint handle);
	bool IsSecureDesktop();
	bool IsUipiAllowed(nint handle);
	nint SetThreadDpiAwarenessContext(nint context);
}

/// <summary>输入相关 Win32 最小接口。</summary>
public interface IWindowsInputNativeApi
{
	bool TryGetVirtualScreenBounds(out AutomationBounds bounds);
	bool TrySendInput(nint target, IReadOnlyList<WindowsInputPacket> packets, out WindowsInputSendFailure failure);
}

/// <summary>截图相关 Win32 最小接口。</summary>
public interface IWindowsScreenCaptureNativeApi
{
	bool TryCaptureWindow(nint handle, WindowsNativeRect rect, out byte[]? bgra32, out string? error);
}
