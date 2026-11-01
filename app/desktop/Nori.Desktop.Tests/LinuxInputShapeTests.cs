using System.Reflection;
using System.Runtime.InteropServices;
using Avalonia;
using Nori.Core.Platform;
using Nori.Desktop.Windows;

namespace Nori.Desktop.Tests;

/// <summary>只创建未映射的测试窗口，不移动真实鼠标、不覆盖用户桌面。</summary>
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
	[DllImport("libXext.so.6")]
	private static extern nint XShapeGetRectangles(nint display, nint window, int kind, out int count, out int ordering);
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
