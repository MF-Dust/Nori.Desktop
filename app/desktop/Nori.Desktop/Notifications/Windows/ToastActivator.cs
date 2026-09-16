using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Nori.Desktop.Notifications.Windows;

/// <summary>
/// 点通知按钮之后，系统把动作送回来的那个入口。
///
/// Windows 不会把点击直接送给进程，而是按快捷方式上登记的 CLSID 找一个 COM 对象。
/// 进程活着并且 <c>CoRegisterClassObject</c> 注册过，就送给当前进程；否则系统按
/// 注册表里的 LocalServer32 把 exe 拉起来再送。
///
/// 后一种情况这里**什么也不做**：待决授权只存在于运行中的进程内存里，应用没跑就
/// 没有任何一条在等它 —— 把一个找不到对应授权的激活当成「允许」是绝对不行的。
/// 这一点在 <see cref="ToastActivation.Parse"/> 那层也有一道：认不出来一律不算允许。
/// </summary>
[SupportedOSPlatform("windows")]
internal static class ToastActivator
{
	/// <summary>系统拉起 exe 时带的参数。只用来在日志里认出这条启动是通知触发的。</summary>
	internal const string CommandLineFlag = "-ToastActivated";

	private static uint _cookie;
	private static Callback? _callback;

	/// <summary>
	/// 把类对象注册进当前进程。注册之后点按钮就送到 <paramref name="onActivated"/>。
	///
	/// 必须在 COM 已初始化的线程上调（Avalonia 的 UI 线程是 STA，符合）。
	/// 失败抛给调用方，由它降级成「不弹通知」。
	/// </summary>
	internal static void Register(Action<string> onActivated)
	{
		if (_cookie != 0) return;
		_callback = new Callback(onActivated);
		Guid clsid = ToastRegistrar.ActivatorClsid;
		ToastNativeApi.CoRegisterClassObject(
			ref clsid, new Factory(_callback),
			ToastNativeApi.ClsctxLocalServer, ToastNativeApi.RegclsMultipleuse, out _cookie);
	}

	/// <summary>退出或用户关掉通知时撤销。撤销失败不抛 —— 进程都要走了。</summary>
	internal static void Revoke()
	{
		if (_cookie == 0) return;
		try { ToastNativeApi.CoRevokeClassObject(_cookie); } catch { /* 进程退出路径上不报错 */ }
		_cookie = 0;
		_callback = null;
	}

	/// <summary>Windows 定义的通知激活回调。</summary>
	[ComImport, Guid("53E31837-6600-4A81-9395-75CFFE746F94")]
	[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	internal interface INotificationActivationCallback
	{
		/// <summary>
		/// <paramref name="invokedArgs"/> 就是我们写在 toast XML 里那串 arguments，
		/// 原样回来。**它出过进程，当外部输入处理。**
		/// </summary>
		[PreserveSig]
		int Activate(
			[MarshalAs(UnmanagedType.LPWStr)] string appUserModelId,
			[MarshalAs(UnmanagedType.LPWStr)] string invokedArgs,
			IntPtr data, uint count);
	}

	[ComImport, Guid("00000001-0000-0000-C000-000000000046")]
	[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	internal interface IClassFactory
	{
		[PreserveSig]
		int CreateInstance(IntPtr outer, [In] ref Guid iid, out IntPtr instance);

		[PreserveSig]
		int LockServer([MarshalAs(UnmanagedType.Bool)] bool held);
	}

	private const int SOk = 0;
	private const int ClassENoaggregation = unchecked((int) 0x80040110);
	private const int ENointerface = unchecked((int) 0x80004002);

	/// <summary>类厂。每次 CreateInstance 都交同一个回调对象 —— 它本身无状态。</summary>
	[ClassInterface(ClassInterfaceType.None)]
	private sealed class Factory(Callback callback) : IClassFactory
	{
		public int CreateInstance(IntPtr outer, ref Guid iid, out IntPtr instance)
		{
			instance = IntPtr.Zero;
			if (outer != IntPtr.Zero) return ClassENoaggregation;

			IntPtr unknown = Marshal.GetIUnknownForObject(callback);
			try
			{
				return Marshal.QueryInterface(unknown, iid, out instance) == SOk ? SOk : ENointerface;
			}
			finally
			{
				Marshal.Release(unknown);
			}
		}

		public int LockServer(bool held) => SOk;
	}

	[ClassInterface(ClassInterfaceType.None)]
	private sealed class Callback(Action<string> onActivated) : INotificationActivationCallback
	{
		public int Activate(string appUserModelId, string invokedArgs, IntPtr data, uint count)
		{
			// 这条回调跑在 COM 的线程上，不是 UI 线程。让上层自己切。
			// 任何异常都不能冒到 COM 边界外 —— 那会让系统认为激活失败并反复重试。
			try { onActivated(invokedArgs); } catch { /* 见上 */ }
			return SOk;
		}
	}
}
