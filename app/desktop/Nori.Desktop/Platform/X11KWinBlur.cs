using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Threading;
using static Nori.Core.Platform.LinuxX11WindowProvider;

namespace Nori.Desktop.Platform;

/// <summary>仅在根窗口声明支持时，为常规 X11 窗口请求整窗模糊。</summary>
internal static class X11KWinBlur
{
	private const string BlurProperty = "_KDE_NET_WM_BLUR_BEHIND_REGION";
	private const nint Atom = 4;
	private const nint Cardinal = 6;
	private const int Replace = 0;
	private static readonly ConditionalWeakTable<Window, BlurLease> Leases = new();

	internal static void Request(Window window, bool enabled, Action<bool> completed, Action<Exception>? onFailure)
	{
		BlurLease lease = Leases.GetOrCreateValue(window);
		nint handle = enabled && window.TryGetPlatformHandle() is { HandleDescriptor: "XID" } platform ? platform.Handle : 0;
		nint previous = lease.Handle;
		if (handle == 0 && previous == 0) { completed(false); return; }
		lease.Handle = handle;

		// 保留上次的 XID 以便注销时清理，并让同一窗口的请求按提交顺序完成。
		lease.Pending = lease.Pending.ContinueWith(_ =>
		{
			bool active = false;
			try { active = WithDisplay(display => Apply(display, handle, previous)); }
			catch (InvalidOperationException exception) { onFailure?.Invoke(exception); }
			Dispatcher.UIThread.Post(() => completed(active));
		}, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
	}

	internal static bool SupportsBlur(ReadOnlySpan<nint> supported, nint blurAtom) => blurAtom != 0 && supported.Contains(blurAtom);

	private static unsafe bool Apply(nint display, nint handle, nint previous)
	{
		nint blur = XInternAtom(display, BlurProperty, 1);
		if (blur == 0) return false;
		if (previous != 0 && previous != handle) XDeleteProperty(display, previous, blur);
		if (handle == 0) return false;
		nint supported = XInternAtom(display, "_NET_SUPPORTED", 1);
		if (!ReadProperty(display, XDefaultRootWindow(display), supported, Atom, 32, 65536,
			(data, count) => SupportsBlur(new ReadOnlySpan<nint>((void*)data, count), blur))) return false;

		XChangeProperty(display, handle, blur, Cardinal, 32, Replace, 0, 0);
		// 读回空属性以确认请求已被服务器处理，窗口销毁时不会误报成功。
		return ReadProperty(display, handle, blur, Cardinal, 32, 1, static (_, count) => count == 0);
	}

	private sealed class BlurLease
	{
		internal nint Handle;
		internal Task Pending = Task.CompletedTask;
	}

	[DllImport(LibX11)]
	private static extern int XChangeProperty(nint display, nint window, nint property, nint type, int format, int mode, nint data, int count);

	[DllImport(LibX11)]
	private static extern int XDeleteProperty(nint display, nint window, nint property);
}
