using System.Runtime.InteropServices;
using System.Text;
using Nori.Core.Automation;
using Nori.Core.Vision;

namespace Nori.Core.Platform;

internal static class LinuxX11WindowProvider
{
	private const string LibX11 = "libX11.so.6";
	private const int IsViewable = 2;
	private const int ZPixmap = 2;
	private const nint CardinalAtom = 6;
	private const nint StringAtom = 31;
	private const nint WindowAtom = 33;
	private const nint WmNameAtom = 39;
	private static readonly Lock ErrorHandlerGate = new();
	private static readonly XErrorHandler ErrorHandler = HandleError;
	private static nint _activeDisplay;
	private static nint _previousErrorHandler;

	internal sealed record Frame(byte[] Bgra, int Width, int Height);

	internal static IReadOnlyList<VisionWindow> Enumerate() => WithDisplay(display =>
	{
		nint root = XDefaultRootWindow(display);
		nint[] windows = ReadProperty(display, root, XInternAtom(display, "_NET_CLIENT_LIST_STACKING", 1), WindowAtom, 32, 65536, ReadWindows)
			?? ReadFallbackStacking(display, root);
		nint pidAtom = XInternAtom(display, "_NET_WM_PID", 1);
		nint titleAtom = XInternAtom(display, "_NET_WM_NAME", 1);
		nint utf8Atom = XInternAtom(display, "UTF8_STRING", 1);
		List<VisionWindow> result = [];
		// EWMH 堆叠顺序从底到顶，读屏选择最靠前的目标。
		for (int index = windows.Length - 1; index >= 0; index--)
		{
			nint window = windows[index];
			if (GetBounds(display, window) is not { } bounds) continue;
			int pid = GetProcessId(display, window, pidAtom);
			if (pid == Environment.ProcessId) continue;
			string? title = utf8Atom != 0 ? ReadTitle(display, window, titleAtom, utf8Atom, Encoding.UTF8) : null;
			if (string.IsNullOrWhiteSpace(title)) title = ReadLegacyTitle(display, window);
			result.Add(new(window, string.IsNullOrWhiteSpace(title) ? "未命名窗口" : title, pid, bounds));
		}
		return result;
	});

	// _NET_CLIENT_LIST 是首次映射顺序，不是 z 序；按根窗口的子窗口（可能是装饰框）重新排序。
	private static nint[] ReadFallbackStacking(nint display, nint root)
	{
		nint[]? clients = ReadProperty(display, root, XInternAtom(display, "_NET_CLIENT_LIST", 1), WindowAtom, 32, 65536, ReadWindows);
		if (clients is not { Length: > 0 }) return [];
		if (XQueryTree(display, root, out _, out _, out nint children, out uint count) == 0)
			throw new InvalidOperationException("无法读取 X11 窗口堆叠顺序");
		nint[] stack;
		try { stack = ReadWindows(children, (int)count); }
		finally { XFree(children); }
		Dictionary<nint, int> ranks = new(stack.Length);
		for (int index = 0; index < stack.Length; index++) ranks[stack[index]] = index;
		return [.. clients.Select(window => (Window: window, Rank: Rank(window)))
			.Where(item => item.Rank >= 0).OrderBy(item => item.Rank).Select(item => item.Window)];

		int Rank(nint window)
		{
			while (window != root)
			{
				if (ranks.TryGetValue(window, out int rank)) return rank;
				if (XQueryTree(display, window, out _, out nint parent, out nint childWindows, out _) == 0) break;
				XFree(childWindows);
				window = parent;
			}
			return -1;
		}
	}

	internal static Frame Capture(VisionWindow target) => WithDisplay(display =>
	{
		if (GetBounds(display, target.Handle) is not { } bounds
			|| GetProcessId(display, target.Handle, XInternAtom(display, "_NET_WM_PID", 1)) != target.ProcessId)
			throw new InvalidOperationException("目标 X11 窗口已关闭或不可见");

		// XGetImage 要求请求区域全部位于屏幕内，只读窗口仍在屏幕上的部分。
		if (XGetWindowAttributes(display, XDefaultRootWindow(display), out XWindowAttributes root) == 0)
			throw new InvalidOperationException("无法读取 X11 屏幕边界");
		int left = Math.Max(0, bounds.Left), top = Math.Max(0, bounds.Top);
		int width = Math.Min(root.Width, bounds.Left + bounds.Width) - left;
		int height = Math.Min(root.Height, bounds.Top + bounds.Height) - top;
		if (width <= 0 || height <= 0) throw new InvalidOperationException("目标 X11 窗口不在屏幕内");
		nint image = XGetImage(display, target.Handle, left - bounds.Left, top - bounds.Top, (uint)width, (uint)height, nuint.MaxValue, ZPixmap);
		if (image == 0) throw new InvalidOperationException("无法读取目标 X11 窗口像素");
		try { return DecodeImage(image); }
		finally { XDestroyImage(image); }
	});

	private static AutomationBounds? GetBounds(nint display, nint window)
	{
		if (XGetWindowAttributes(display, window, out XWindowAttributes attributes) == 0
			|| !IsVisibleWindow(attributes.MapState, attributes.Width, attributes.Height)
			|| XTranslateCoordinates(display, window, attributes.Root, 0, 0, out int x, out int y, out _) == 0)
			return null;
		return new(x, y, attributes.Width, attributes.Height);
	}

	internal static bool IsVisibleWindow(int mapState, int width, int height) => mapState == IsViewable && width > 0 && height > 0;

	private static int GetProcessId(nint display, nint window, nint atom) =>
		ReadProperty<int?>(display, window, atom, CardinalAtom, 32, 1, static (data, count) =>
			count == 0 ? null : unchecked((int)Marshal.ReadIntPtr(data))) ?? 0;

	private static nint[] ReadWindows(nint data, int count)
	{
		// Xlib 将格式 32 的值展开为 C long，LP64 上每项占 8 字节。
		nint[] windows = new nint[count];
		if (count > 0) Marshal.Copy(data, windows, 0, count);
		return windows;
	}

	private static string? ReadTitle(nint display, nint window, nint atom, nint type, Encoding encoding) =>
		ReadProperty(display, window, atom, type, 8, 4096, (data, count) =>
		{
			byte[] bytes = new byte[count];
			Marshal.Copy(data, bytes, 0, count);
			return encoding.GetString(bytes).TrimEnd('\0');
		});

	private static unsafe string? ReadLegacyTitle(nint display, nint window)
	{
		if (XGetWMName(display, window, out XTextProperty property) == 0) return null;
		try
		{
			if (property.Encoding == StringAtom) return Encoding.Latin1.GetString(new ReadOnlySpan<byte>((void*)property.Value, (int)property.Count)).TrimEnd('\0');
			// ICCCM 的 WM_NAME 也可能是 COMPOUND_TEXT，转换由 Xlib 按属性编码处理。
			if (Xutf8TextPropertyToTextList(display, ref property, out nint list, out int count) < 0) return null;
			try { return count > 0 ? Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(list)) : null; }
			finally { XFreeStringList(list); }
		}
		finally { XFree(property.Value); }
	}

	private static T? ReadProperty<T>(nint display, nint window, nint atom, nint type, int format, int length, Func<nint, int, T> read)
	{
		if (atom == 0) return default;
		int status = XGetWindowProperty(display, window, atom, 0, length, 0, type,
			out nint actualType, out int actualFormat, out nuint count, out _, out nint data);
		if (data == 0) return default;
		try { return status == 0 && actualType == type && actualFormat == format ? read(data, (int)count) : default; }
		finally { XFree(data); }
	}

	private static unsafe Frame DecodeImage(nint image)
	{
		XImage info = Marshal.PtrToStructure<XImage>(image);
		ReadOnlySpan<byte> data = new((void*)info.Data, checked(info.BytesPerLine * info.Height));
		return new(LinuxX11Pixels.DecodeBgra(data, info.Width, info.Height, info.BytesPerLine, info.BitsPerPixel,
			info.ByteOrder == 1, (uint)info.RedMask, (uint)info.GreenMask, (uint)info.BlueMask), info.Width, info.Height);
	}

	private static T WithDisplay<T>(Func<nint, T> use)
	{
		nint display;
		try { display = XOpenDisplay(0); }
		catch (DllNotFoundException exception) { throw new InvalidOperationException("缺少 X11 截图所需的 libX11.so.6", exception); }
		if (display == 0) throw new InvalidOperationException("无法连接 X11 显示服务器");

		// Xlib 的错误处理器属于进程；仅本次连接的同步请求依靠各自返回值报告错误。
		lock (ErrorHandlerGate)
		{
			Volatile.Write(ref _activeDisplay, display);
			nint previous = XSetErrorHandler(Marshal.GetFunctionPointerForDelegate(ErrorHandler));
			Volatile.Write(ref _previousErrorHandler, previous);
			try { return use(display); }
			finally
			{
				XCloseDisplay(display);
				XSetErrorHandler(previous);
				Volatile.Write(ref _activeDisplay, 0);
			}
		}
	}

	private static unsafe int HandleError(nint origin, nint error)
	{
		if (origin == Volatile.Read(ref _activeDisplay)) return 0;
		// 其他连接沿用原处理器；它可能是 Avalonia 安装的另一种托管委托。
		SpinWait wait = new();
		nint previous;
		while ((previous = Volatile.Read(ref _previousErrorHandler)) == 0) wait.SpinOnce();
		return ((delegate* unmanaged[Cdecl]<nint, nint, int>)previous)(origin, error);
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct XTextProperty
	{
		public nint Value, Encoding;
		public int Format;
		public nuint Count;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct XWindowAttributes
	{
		public int X, Y, Width, Height, BorderWidth, Depth;
		public nint Visual, Root;
		public int Class, BitGravity, WinGravity, BackingStore;
		public nuint BackingPlanes, BackingPixel;
		public int SaveUnder;
		public nuint Colormap;
		public int MapInstalled, MapState;
		public nint AllEventMasks, YourEventMask, DoNotPropagateMask;
		public int OverrideRedirect;
		public nint Screen;
	}

	// 只读取 XImage 的像素及格式字段，销毁由 XDestroyImage 负责。
	[StructLayout(LayoutKind.Sequential)]
	private struct XImage
	{
		public int Width, Height, XOffset, Format;
		public nint Data;
		public int ByteOrder, BitmapUnit, BitmapBitOrder, BitmapPad, Depth, BytesPerLine, BitsPerPixel;
		public nuint RedMask, GreenMask, BlueMask;
	}

	[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
	private delegate int XErrorHandler(nint display, nint error);

	[DllImport(LibX11)] private static extern nint XOpenDisplay(nint name);
	[DllImport(LibX11)] private static extern int XCloseDisplay(nint display);
	[DllImport(LibX11)] private static extern nint XSetErrorHandler(nint handler);
	[DllImport(LibX11)] private static extern nint XDefaultRootWindow(nint display);
	[DllImport(LibX11)] private static extern nint XInternAtom(nint display, [MarshalAs(UnmanagedType.LPStr)] string name, int onlyIfExists);
	[DllImport(LibX11)] private static extern int XQueryTree(nint display, nint window, out nint root, out nint parent, out nint children, out uint count);
	[DllImport(LibX11)] private static extern int XGetWMName(nint display, nint window, out XTextProperty property);
	[DllImport(LibX11)] private static extern int Xutf8TextPropertyToTextList(nint display, ref XTextProperty property, out nint list, out int count);
	[DllImport(LibX11)] private static extern void XFreeStringList(nint list);
	[DllImport(LibX11)] private static extern int XGetWindowAttributes(nint display, nint window, out XWindowAttributes attributes);
	[DllImport(LibX11)] private static extern int XTranslateCoordinates(nint display, nint source, nint destination,
		int sourceX, int sourceY, out int x, out int y, out nint child);
	[DllImport(LibX11)] private static extern int XGetWindowProperty(nint display, nint window, nint property,
		nint offset, nint length, int delete, nint requestedType, out nint actualType, out int actualFormat,
		out nuint itemCount, out nuint bytesAfter, out nint data);
	[DllImport(LibX11)] private static extern int XFree(nint data);
	[DllImport(LibX11)] private static extern nint XGetImage(nint display, nint window, int x, int y, uint width, uint height, nuint planeMask, int format);
	[DllImport(LibX11)] private static extern int XDestroyImage(nint image);
}
