using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Nori.Core.Platform;

///
/// Windows 实现
///
/// 跨进程点击穿透配方: WS_EX_TRANSPARENT 必须叠加 WS_EX_LAYERED 并用
/// SetLayeredWindowAttributes 激活分层, 系统 hit-test 才会无视 Z 序跳过本窗口;
/// 裸 WS_EX_TRANSPARENT / HTTRANSPARENT 只在同一线程内生效, 点不到其他程序的窗口。
/// 伴侣视窗是 WS_EX_NOREDIRECTIONBITMAP 的 DirectComposition 窗口, alpha=255 的分层
/// 属性不参与合成, 画面不变; 窗口全程保持 Topmost。
///
[SupportedOSPlatform("windows")]
public sealed class WindowsPlatformServices : IPlatformServices
{
	private const uint WmNcLButtonDown = 0x00A1;
	private const nint HtCaption = 2;
	private const int GwlExStyle = -20;
	private const int WsExTransparent = 0x00000020;
	private const int WsExLayered = 0x00080000;
	private const uint LwaAlpha = 0x00000002;
	private static readonly nint HwndTopmost = new(-1);
	private const uint SwpNoSize = 0x0001;
	private const uint SwpNoMove = 0x0002;
	private const uint SwpNoActivate = 0x0010;
	private const uint SwpFrameChanged = 0x0020;

	[DllImport("user32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool SetWindowPos(
		nint hWnd, nint hWndInsertAfter, int x, int y, int width, int height, uint flags);

	[DllImport("user32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool SetLayeredWindowAttributes(nint hWnd, uint crKey, byte bAlpha, uint dwFlags);

	[StructLayout(LayoutKind.Sequential)]
	private struct Point
	{
		public int X;
		public int Y;
	}

	[DllImport("user32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool GetCursorPos(out Point point);

	[DllImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool ReleaseCapture();

	[DllImport("user32.dll", EntryPoint = "SendMessageW")]
	private static extern nint SendMessage(nint hWnd, uint message, nint wParam, nint lParam);

	[DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
	private static extern nint GetWindowLongPtr(nint hWnd, int index);

	[DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
	private static extern nint SetWindowLongPtr(nint hWnd, int index, nint value);

	/// <inheritdoc />
	public SessionType Session => SessionType.Windows;

	/// <inheritdoc />
	public PlatformCapabilities Capabilities { get; } = new()
	{
		SupportsGlobalCursor = true,
		SupportsWindowDrag = true,
		SupportsHitThrough = true,
		SupportsTopmost = true,
		SupportsTray = true,
	};

	/// <inheritdoc />
	public (double X, double Y) GetCursorPosition()
	{
		if (!GetCursorPos(out Point point)) return (0, 0);
		return (point.X, point.Y);
	}

	/// <inheritdoc />
	public void StartWindowDrag(nint windowHandle)
	{
		if (windowHandle == 0) throw new ArgumentException("窗口句柄无效", nameof(windowHandle));
		// 先释放鼠标捕获, 再让系统把这次按下当作拖标题栏处理
		ReleaseCapture();
		SendMessage(windowHandle, WmNcLButtonDown, HtCaption, 0);
	}

	/// <inheritdoc />
	public void SetClickThrough(nint windowHandle, bool through)
	{
		if (windowHandle == 0) return;
		Marshal.SetLastPInvokeError(0);
		nint style = GetWindowLongPtr(windowHandle, GwlExStyle);
		int error = Marshal.GetLastPInvokeError();
		if (style == 0 && error != 0) throw new InvalidOperationException($"读取 Windows 伴侣窗口样式失败: {error}");

		// WS_EX_TRANSPARENT 只有在 WS_EX_LAYERED + SetLayeredWindowAttributes 激活分层后,
		// 才能让系统 hit-test 无视 Z 序跨进程跳过本窗口; 裸 WS_EX_TRANSPARENT 与
		// HTTRANSPARENT 都只在同一线程内生效。伴侣视窗是 WS_EX_NOREDIRECTIONBITMAP 的
		// DirectComposition 窗口, alpha=255 的分层属性不参与合成, 画面不受影响。
		nint next = through ? style | WsExTransparent | WsExLayered : style & ~(WsExTransparent | WsExLayered);
		if (next != style)
		{
			Marshal.SetLastPInvokeError(0);
			nint previous = SetWindowLongPtr(windowHandle, GwlExStyle, next);
			error = Marshal.GetLastPInvokeError();
			if (previous == 0 && error != 0) throw new InvalidOperationException($"设置 Windows 伴侣窗口样式失败: {error}");
		}

		if (through && !SetLayeredWindowAttributes(windowHandle, 0, 255, LwaAlpha))
		{
			throw new InvalidOperationException($"激活 Windows 伴侣分层窗口失败: {Marshal.GetLastWin32Error()}");
		}

		// 穿透由分层 hit-test 跳过实现, 与 Z 序无关: 伴侣视窗保持置顶。
		// SetWindowPos 同时让 SetWindowLongPtr 的样式改动立即生效。
		if (!SetWindowPos(windowHandle, HwndTopmost, 0, 0, 0, 0,
			SwpNoSize | SwpNoMove | SwpNoActivate | SwpFrameChanged))
		{
			throw new InvalidOperationException($"设置 Windows 伴侣窗口层级失败: {Marshal.GetLastWin32Error()}");
		}
	}
}
