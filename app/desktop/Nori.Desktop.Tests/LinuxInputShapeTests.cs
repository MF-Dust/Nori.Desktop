using System.Reflection;
using System.Runtime.InteropServices;
using Avalonia;
using Nori.Core.Platform;
using Nori.Desktop.Windows;

namespace Nori.Desktop.Tests;

/// <summary>输入形状回读不映射窗口；指针投递测试须另行显式开启，仅在隔离 X11 桌面运行。</summary>
[Collection("Native settings")]
public sealed class LinuxInputShapeTests
{
	[NativeX11Fact]
	public void X11服务器保留分数缩放输入区域并可切换整窗穿透()
	{
		if (!System.OperatingSystem.IsLinux()) return;
		LinuxPlatformServices platform = Assert.IsType<LinuxPlatformServices>(PlatformServices.Current);
		// 查询与生产调用共用连接，避免跨 X11 客户端的请求顺序竞争；连接归平台所有，不在测试中关闭。
		nint display = (nint)typeof(LinuxPlatformServices).GetField("_display", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(platform)!;
		nint window = XCreateSimpleWindow(display, XDefaultRootWindow(display), 0, 0, 400, 200, 0, 0, 0);
		Assert.NotEqual(0, window);
		try
		{
			var region = QuickChatWindow.PhysicalInputRegion(new Rect(14, 14, 280, 46), 1.25);
			platform.SetInputShape(window, [region]);
			Assert.Equal((17, 17, 351, 58), Assert.Single(ReadInputShape(display, window)));

			platform.SetClickThrough(window, true);
			Assert.Empty(ReadInputShape(display, window));

			platform.SetClickThrough(window, false);
			var restored = Assert.Single(ReadInputShape(display, window));
			Assert.True(restored.X <= 0 && restored.Y <= 0);
			Assert.True(restored.X + restored.Width >= 400 && restored.Y + restored.Height >= 200);
		}
		finally { XDestroyWindow(display, window); XSync(display, 0); }
	}

	[NativeX11PointerFact]
	public void X11实际点击按输入形状投递到上层或下层窗口()
	{
		if (!System.OperatingSystem.IsLinux()) return;
		LinuxPlatformServices platform = Assert.IsType<LinuxPlatformServices>(PlatformServices.Current);
		nint display = (nint)typeof(LinuxPlatformServices).GetField("_display", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(platform)!;
		nint parent = XCreateSimpleWindow(display, XDefaultRootWindow(display), 0, 0, 400, 200, 0, 0, 0);
		Assert.NotEqual(0, parent);
		nint buffer = Marshal.AllocHGlobal(24 * IntPtr.Size);
		try
		{
			// 不让窗口管理器的激活点击或装饰抓取影响 SHAPE 的投递断言。
			var attributes = new XSetWindowAttributes { OverrideRedirect = 1 };
			XChangeWindowAttributes(display, parent, 1 << 9, ref attributes);
			nint lower = XCreateSimpleWindow(display, parent, 0, 0, 400, 200, 0, 0, 0);
			nint upper = XCreateSimpleWindow(display, parent, 0, 0, 400, 200, 0, 0, 0);
			Assert.NotEqual(0, lower);
			Assert.NotEqual(0, upper);
			XSelectInput(display, lower, (1 << 2) | (1 << 3));
			XSelectInput(display, upper, (1 << 2) | (1 << 3));
			XMapWindow(display, lower);
			XMapWindow(display, upper);
			XMapWindow(display, parent);

			platform.SetInputShape(upper, [(100, 50, 200, 100)]);
			Click(200, 100, upper, lower);
			Click(20, 20, lower, upper);
			platform.SetClickThrough(upper, true);
			Click(200, 100, lower, upper);
			platform.SetClickThrough(upper, false);
			Click(20, 20, upper, lower);

			void Click(int x, int y, nint expected, nint excluded)
			{
				// 等 X 服务器映射完成，且指针已落在目标子窗口上，再发送按键。
				Assert.True(SpinWait.SpinUntil(() =>
				{
					Assert.NotEqual(0, XTestFakeMotionEvent(display, -1, x, y, 0));
					XSync(display, 0);
					return XQueryPointer(display, parent, out _, out nint child, out _, out _, out _, out _, out _) != 0
						&& child == expected;
				}, TimeSpan.FromSeconds(3)), "隔离 X11 桌面未能将指针定位到预期窗口");
				Assert.NotEqual(0, XTestFakeButtonEvent(display, 1, 1, 0));
				Assert.NotEqual(0, XTestFakeButtonEvent(display, 1, 0, 0));
				XSync(display, 0);
				Assert.True(SpinWait.SpinUntil(() => XCheckTypedWindowEvent(display, expected, 4, buffer) != 0,
					TimeSpan.FromSeconds(3)), "目标窗口未收到 ButtonPress");
				Assert.True(SpinWait.SpinUntil(() => XCheckTypedWindowEvent(display, expected, 5, buffer) != 0,
					TimeSpan.FromSeconds(3)), "目标窗口未收到 ButtonRelease");
				Assert.Equal(0, XCheckTypedWindowEvent(display, excluded, 4, buffer));
				Assert.Equal(0, XCheckTypedWindowEvent(display, excluded, 5, buffer));
			}
		}
		finally
		{
			Marshal.FreeHGlobal(buffer);
			XDestroyWindow(display, parent);
			XSync(display, 0);
		}
	}

	private static (int X, int Y, int Width, int Height)[] ReadInputShape(nint display, nint window)
	{
		nint rectangles = XShapeGetRectangles(display, window, 2, out int count, out _);
		try
		{
			var result = new (int X, int Y, int Width, int Height)[count];
			for (int index = 0; index < count; index++)
			{
				XRectangle rect = Marshal.PtrToStructure<XRectangle>(rectangles + index * Marshal.SizeOf<XRectangle>());
				result[index] = (rect.X, rect.Y, rect.Width, rect.Height);
			}
			return result;
		}
		finally { if (rectangles != 0) XFree(rectangles); }
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct XSetWindowAttributes
	{
		public nuint BackgroundPixmap, BackgroundPixel, BorderPixmap, BorderPixel;
		public int BitGravity, WinGravity, BackingStore;
		public nuint BackingPlanes, BackingPixel;
		public int SaveUnder;
		public nint EventMask, DoNotPropagateMask;
		public int OverrideRedirect;
		public nuint Colormap, Cursor;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct XRectangle
	{
		public short X;
		public short Y;
		public ushort Width;
		public ushort Height;
	}

	[DllImport("libX11.so.6")]
	private static extern nint XDefaultRootWindow(nint display);
	[DllImport("libX11.so.6")]
	private static extern nint XCreateSimpleWindow(nint display, nint parent, int x, int y,
		uint width, uint height, uint borderWidth, nuint border, nuint background);
	[DllImport("libX11.so.6")]
	private static extern int XDestroyWindow(nint display, nint window);
	[DllImport("libX11.so.6")]
	private static extern int XSync(nint display, int discard);
	[DllImport("libX11.so.6")]
	private static extern int XFree(nint data);
	[DllImport("libX11.so.6")]
	private static extern int XChangeWindowAttributes(nint display, nint window, nuint mask, ref XSetWindowAttributes attributes);
	[DllImport("libX11.so.6")]
	private static extern int XMapWindow(nint display, nint window);
	[DllImport("libX11.so.6")]
	private static extern int XSelectInput(nint display, nint window, nint mask);
	[DllImport("libXtst.so.6")]
	private static extern int XTestFakeMotionEvent(nint display, int screen, int x, int y, nuint delay);
	[DllImport("libX11.so.6")]
	private static extern int XQueryPointer(nint display, nint window, out nint root, out nint child,
		out int rootX, out int rootY, out int windowX, out int windowY, out uint mask);
	[DllImport("libX11.so.6")]
	private static extern int XCheckTypedWindowEvent(nint display, nint window, int type, nint eventReturn);
	[DllImport("libXtst.so.6")]
	private static extern int XTestFakeButtonEvent(nint display, uint button, int pressed, nuint delay);
	[DllImport("libXext.so.6")]
	private static extern nint XShapeGetRectangles(nint display, nint window, int kind, out int count, out int ordering);
}

internal sealed class NativeX11PointerFactAttribute : FactAttribute
{
	public NativeX11PointerFactAttribute()
	{
		if (!System.OperatingSystem.IsLinux() || Environment.GetEnvironmentVariable("NORI_TEST_X11_POINTER") != "1"
			|| PlatformServices.Current.Session != SessionType.X11 || !PlatformServices.Current.Capabilities.SupportsHitThrough)
			Skip = "仅在隔离 X11 桌面设置 NORI_TEST_X11_POINTER=1；测试会移动指针并注入点击，需要 libXtst。";
	}
}

internal sealed class NativeX11FactAttribute : FactAttribute
{
	public NativeX11FactAttribute()
	{
		if (!System.OperatingSystem.IsLinux() || Environment.GetEnvironmentVariable("NORI_TEST_X11") != "1"
			|| PlatformServices.Current.Session != SessionType.X11 || !PlatformServices.Current.Capabilities.SupportsHitThrough)
			Skip = "在支持 SHAPE 1.1 的 Linux X11/XWayland 会话下设置 NORI_TEST_X11=1。";
	}
}
