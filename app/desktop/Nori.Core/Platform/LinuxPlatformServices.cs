using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Nori.Core.Platform;

/// <summary>
/// Linux 实现
///
/// X11 会话下能力齐全:
/// - 全局光标: XQueryPointer
/// - 窗口拖动: 向根窗口发 _NET_WM_MOVERESIZE ClientMessage (EWMH)
/// - 点击穿透: XShapeCombineRectangles(ShapeInput) —— 空输入形状即整窗穿透
///
/// Wayland 会话下协议不提供这些能力, 由 Create() 直接返回能力全 false 的实例,
/// 前端据此把伴侣视窗降级为「整窗可点 + 拖动手柄 + 免打扰开关」。
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class LinuxPlatformServices : IPlatformServices
{
	private const string LibX11 = "libX11.so.6";
	private const string LibXext = "libXext.so.6";

	/// <summary>ShapeInput: 决定哪些区域接收输入事件</summary>
	private const int ShapeInput = 2;

	/// <summary>ShapeSet: 用给定矩形集合整体替换形状</summary>
	private const int ShapeSet = 0;

	/// <summary>_NET_WM_MOVERESIZE_MOVE_KEYBOARD 之外的「用鼠标移动」</summary>
	private const int MoveResizeMove = 8;

	private const long SubstructureNotifyMask = 1L << 19;
	private const long SubstructureRedirectMask = 1L << 20;

	[StructLayout(LayoutKind.Sequential)]
	private struct XRectangle
	{
		public short X;
		public short Y;
		public ushort Width;
		public ushort Height;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct XClientMessageEvent
	{
		public int Type;
		public nuint Serial;
		public int SendEvent;
		public nint Display;
		public nint Window;
		public nint MessageType;
		public int Format;
		public nint Data0;
		public nint Data1;
		public nint Data2;
		public nint Data3;
		public nint Data4;
		// XEvent 是一个 union, 总长度按 24 个 long 对齐; 多留一段避免栈越界
		public nint Pad0;
		public nint Pad1;
		public nint Pad2;
		public nint Pad3;
		public nint Pad4;
		public nint Pad5;
		public nint Pad6;
		public nint Pad7;
		public nint Pad8;
		public nint Pad9;
		public nint Pad10;
		public nint Pad11;
	}

	[DllImport(LibX11, EntryPoint = "XOpenDisplay")]
	private static extern nint OpenDisplay(nint name);

	[DllImport(LibX11, EntryPoint = "XCloseDisplay")]
	private static extern int CloseDisplay(nint display);

	[DllImport(LibX11, EntryPoint = "XDefaultRootWindow")]
	private static extern nint DefaultRootWindow(nint display);

	[DllImport(LibX11, EntryPoint = "XQueryPointer")]
	private static extern int QueryPointer(
		nint display, nint window,
		out nint rootReturn, out nint childReturn,
		out int rootX, out int rootY,
		out int winX, out int winY,
		out uint mask);

	[DllImport(LibX11, EntryPoint = "XInternAtom")]
	private static extern nint InternAtom(nint display, [MarshalAs(UnmanagedType.LPStr)] string name, int onlyIfExists);

	[DllImport(LibX11, EntryPoint = "XSendEvent")]
	private static extern int SendEvent(nint display, nint window, int propagate, long mask, ref XClientMessageEvent evt);

	[DllImport(LibX11, EntryPoint = "XFlush")]
	private static extern int Flush(nint display);

	[DllImport(LibX11, EntryPoint = "XUngrabPointer")]
	private static extern int UngrabPointer(nint display, nint time);

	[DllImport(LibXext, EntryPoint = "XShapeCombineRectangles")]
	private static extern void ShapeCombineRectangles(
		nint display, nint window, int kind, int xOffset, int yOffset,
		[In] XRectangle[] rectangles, int count, int operation, int ordering);

	private readonly nint _display;

	private LinuxPlatformServices(nint display, SessionType session, PlatformCapabilities capabilities)
	{
		_display = display;
		Session = session;
		Capabilities = capabilities;
	}

	/// <summary>
	/// 按会话类型创建实现; X11 不可用或 Wayland 会话下返回能力全 false 的降级实例
	/// </summary>
	public static IPlatformServices Create()
	{
		SessionType session = PlatformServices.DetectSession();
		if (session == SessionType.Wayland) return new UnsupportedPlatformServices();

		nint display;
		try
		{
			display = OpenDisplay(0);
		}
		catch (DllNotFoundException)
		{
			// 没有 libX11 (纯 Wayland 容器) → 全部降级
			return new UnsupportedPlatformServices();
		}
		if (display == 0) return new UnsupportedPlatformServices();

		return new LinuxPlatformServices(display, SessionType.X11, new PlatformCapabilities
		{
			SupportsGlobalCursor = true,
			SupportsWindowDrag = true,
			SupportsHitThrough = true,
			SupportsTopmost = true,
			// 托盘由 Avalonia 的 TrayIcon 决定, 部分桌面环境没有 StatusNotifier; 由宿主实测后覆盖
			SupportsTray = true,
		});
	}

	/// <inheritdoc />
	public SessionType Session { get; }

	/// <inheritdoc />
	public PlatformCapabilities Capabilities { get; }

	/// <inheritdoc />
	public (double X, double Y) GetCursorPosition()
	{
		nint root = DefaultRootWindow(_display);
		if (QueryPointer(_display, root, out _, out _, out int rootX, out int rootY, out _, out _, out _) == 0)
		{
			throw new InvalidOperationException("XQueryPointer 调用失败");
		}
		return (rootX, rootY);
	}

	/// <inheritdoc />
	public void StartWindowDrag(nint windowHandle)
	{
		if (windowHandle == 0) throw new ArgumentException("窗口句柄无效", nameof(windowHandle));

		(double x, double y) = GetCursorPosition();
		nint atom = InternAtom(_display, "_NET_WM_MOVERESIZE", 0);
		if (atom == 0) throw new InvalidOperationException("窗口管理器不支持 _NET_WM_MOVERESIZE");

		// 交出指针抓取, 否则窗口管理器接不到后续移动
		UngrabPointer(_display, 0);

		XClientMessageEvent evt = new()
		{
			Type = 33, // ClientMessage
			Display = _display,
			Window = windowHandle,
			MessageType = atom,
			Format = 32,
			Data0 = (nint)(long)x,
			Data1 = (nint)(long)y,
			Data2 = MoveResizeMove,
			Data3 = 1, // 左键
			Data4 = 1, // 来源: 应用程序
		};
		nint root = DefaultRootWindow(_display);
		SendEvent(_display, root, 0, SubstructureNotifyMask | SubstructureRedirectMask, ref evt);
		Flush(_display);
	}

	/// <inheritdoc />
	public void SetClickThrough(nint windowHandle, bool through)
	{
		if (windowHandle == 0) return;
		// 空矩形集合 = 输入形状为空 = 整窗穿透; 一个大矩形 = 恢复接收输入
		XRectangle[] rectangles = through
			? []
			: [new XRectangle {X = 0, Y = 0, Width = ushort.MaxValue, Height = ushort.MaxValue}];
		try
		{
			ShapeCombineRectangles(_display, windowHandle, ShapeInput, 0, 0, rectangles, rectangles.Length, ShapeSet, 0);
			Flush(_display);
		}
		catch (DllNotFoundException)
		{
			// 没有 Xext: 保持整窗可点, 交由前端提示降级
		}
	}

	/// <summary>
	/// 用模型外接矩形设置输入形状
	///
	/// 当前调用方只传一个连续矩形；接口保留集合形式以匹配 XShape API。
	/// </summary>
	public void SetInputShape(nint windowHandle, IReadOnlyList<(int X, int Y, int Width, int Height)> regions)
	{
		if (windowHandle == 0) return;
		XRectangle[] rectangles = new XRectangle[regions.Count];
		for (int index = 0; index < regions.Count; index++)
		{
			(int x, int y, int width, int height) = regions[index];
			rectangles[index] = new XRectangle
			{
				X = (short)Math.Clamp(x, short.MinValue, short.MaxValue),
				Y = (short)Math.Clamp(y, short.MinValue, short.MaxValue),
				Width = (ushort)Math.Clamp(width, 0, ushort.MaxValue),
				Height = (ushort)Math.Clamp(height, 0, ushort.MaxValue),
			};
		}
		try
		{
			ShapeCombineRectangles(_display, windowHandle, ShapeInput, 0, 0, rectangles, rectangles.Length, ShapeSet, 0);
			Flush(_display);
		}
		catch (DllNotFoundException)
		{
			// 没有 Xext: 忽略, 伴侣视窗保持整窗可点
		}
	}
}
