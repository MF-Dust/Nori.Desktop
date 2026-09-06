using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Nori.Core.Platform;

/// <summary>
/// macOS 实现 (ObjC runtime P/Invoke)
///
/// 纪律:
/// - objc_msgSend 是变参函数, 不同返回类型必须分别声明入口点 (EntryPoint 相同, 签名不同),
///   否则在 arm64 上会取错寄存器。
/// - 所有调用都必须在 UI 线程 (AppKit 非线程安全); 调用方保证。
/// - NSEvent.mouseLocation 用的是左下原点坐标系, 要按主屏高度翻转成 Avalonia 的左上原点。
/// </summary>
[SupportedOSPlatform("macos")]
public sealed class MacPlatformServices : IPlatformServices
{
	private const string ObjC = "/usr/lib/libobjc.dylib";
	private const string AppKit = "/System/Library/Frameworks/AppKit.framework/AppKit";

	[StructLayout(LayoutKind.Sequential)]
	private struct CGPoint
	{
		public double X;
		public double Y;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct CGRect
	{
		public double X;
		public double Y;
		public double Width;
		public double Height;
	}

	[DllImport(ObjC, EntryPoint = "objc_getClass")]
	private static extern nint GetClass([MarshalAs(UnmanagedType.LPStr)] string name);

	[DllImport(ObjC, EntryPoint = "sel_registerName")]
	private static extern nint GetSelector([MarshalAs(UnmanagedType.LPStr)] string name);

	[DllImport(ObjC, EntryPoint = "objc_msgSend")]
	private static extern nint SendPtr(nint receiver, nint selector);

	[DllImport(ObjC, EntryPoint = "objc_msgSend")]
	private static extern void SendVoidBool(nint receiver, nint selector, [MarshalAs(UnmanagedType.I1)] bool value);

	[DllImport(ObjC, EntryPoint = "objc_msgSend")]
	private static extern void SendVoidPtr(nint receiver, nint selector, nint value);

	[DllImport(ObjC, EntryPoint = "objc_msgSend")]
	private static extern void SendVoidLong(nint receiver, nint selector, long value);

	// 返回结构体的 msgSend 在 arm64 上走 objc_msgSend (小结构走寄存器), 单独声明保证签名正确
	[DllImport(ObjC, EntryPoint = "objc_msgSend")]
	private static extern CGPoint SendPoint(nint receiver, nint selector);

	[DllImport(ObjC, EntryPoint = "objc_msgSend")]
	private static extern CGRect SendRect(nint receiver, nint selector);

	[DllImport(AppKit, EntryPoint = "NSApplicationLoad")]
	private static extern void EnsureAppKitLoaded();

	/// <summary>NSFloatingWindowLevel</summary>
	private const long FloatingWindowLevel = 3;

	/// <inheritdoc />
	public SessionType Session => SessionType.MacOS;

	/// <inheritdoc />
	public PlatformCapabilities Capabilities { get; } = new()
	{
		SupportsGlobalCursor = true,
		// performWindowDragWithEvent: 需要一个当前事件; 拿不到时退化为不支持, 前端会显示拖动手柄
		SupportsWindowDrag = true,
		// 按光标是否位于模型交互矩形内，在「整窗可点」与「整窗穿透」之间切换
		SupportsHitThrough = true,
		SupportsTopmost = true,
		SupportsTray = true,
	};

	/// <inheritdoc />
	public (double X, double Y) GetCursorPosition()
	{
		EnsureAppKitLoaded();
		CGPoint location = SendPoint(GetClass("NSEvent"), GetSelector("mouseLocation"));

		// NSEvent 是左下原点, Avalonia 用左上原点: 按主屏高度翻转 Y
		nint mainScreen = SendPtr(GetClass("NSScreen"), GetSelector("mainScreen"));
		if (mainScreen == 0) return (location.X, location.Y);
		CGRect frame = SendRect(mainScreen, GetSelector("frame"));
		return (location.X, frame.Height - location.Y);
	}

	/// <inheritdoc />
	public void StartWindowDrag(nint windowHandle)
	{
		if (windowHandle == 0) throw new ArgumentException("窗口句柄无效", nameof(windowHandle));
		nint window = ResolveWindow(windowHandle);
		if (window == 0) throw new InvalidOperationException("无法解析 NSWindow");

		// 用当前事件发起系统拖动; 没有当前事件 (例如事件已被 WebView 吞掉) 时抛错由调用方降级
		nint app = SendPtr(GetClass("NSApplication"), GetSelector("sharedApplication"));
		nint currentEvent = app == 0 ? 0 : SendPtr(app, GetSelector("currentEvent"));
		if (currentEvent == 0) throw new InvalidOperationException("没有可用的当前事件, 无法发起窗口拖动");
		SendVoidPtr(window, GetSelector("performWindowDragWithEvent:"), currentEvent);
	}

	/// <inheritdoc />
	public void SetClickThrough(nint windowHandle, bool through)
	{
		if (windowHandle == 0) return;
		nint window = ResolveWindow(windowHandle);
		if (window == 0) return;
		SendVoidBool(window, GetSelector("setIgnoresMouseEvents:"), through);
	}

	/// <summary>把窗口提到浮动层级 (伴侣窗口置顶)</summary>
	public void SetFloatingLevel(nint windowHandle)
	{
		if (windowHandle == 0) return;
		nint window = ResolveWindow(windowHandle);
		if (window == 0) return;
		SendVoidLong(window, GetSelector("setLevel:"), FloatingWindowLevel);
	}

	/// <summary>
	/// Avalonia 给出的句柄可能是 NSView 也可能是 NSWindow, 统一解析成 NSWindow
	/// </summary>
	private static nint ResolveWindow(nint handle)
	{
		nint windowSelector = GetSelector("window");
		nint respondsSelector = GetSelector("respondsToSelector:");
		// NSWindow 自己不响应 -window, NSView 响应
		nint responds = SendPtrWithPtr(handle, respondsSelector, windowSelector);
		return responds != 0 ? SendPtr(handle, windowSelector) : handle;
	}

	[DllImport(ObjC, EntryPoint = "objc_msgSend")]
	private static extern nint SendPtrWithPtr(nint receiver, nint selector, nint arg);
}
