using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Nori.Desktop.Notifications.Windows;

/// <summary>
/// 系统通知要用到的 WinRT / Shell 调用。
///
/// 为什么手写：<c>Windows.UI.Notifications</c> 那套只在 <c>net10.0-windows10.0.x</c>
/// 这种目标框架下才有投影，而本仓库统一是 <c>net10.0</c>（见 Directory.Build.props）。
/// 为一条通知把 Nori.Desktop 改成多目标，要连带重验三个 RID 的发布路径和四道门，
/// 代价比这一份互操作大。这里的写法与 AppContainerNativeApi 一致。
///
/// **IInspectable 的坑：** .NET Core 起不再支持
/// <c>ComInterfaceType.InterfaceIsIInspectable</c>，所以下面每个 WinRT 接口都按
/// IUnknown 声明，并把 IInspectable 的三个方法（GetIids / GetRuntimeClassName /
/// GetTrustLevel）**原样占住前三个槽位**再接真正的方法。少写一个，vtable 就错位，
/// 现象是调用任意方法都返回 E_NOINTERFACE 或直接崩。
/// </summary>
[SupportedOSPlatform("windows")]
internal static class ToastNativeApi
{
	// ── HSTRING ────────────────────────────────────────────────────────────

	[DllImport("combase.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
	internal static extern IntPtr WindowsCreateString(
		[MarshalAs(UnmanagedType.LPWStr)] string sourceString, int length);

	[DllImport("combase.dll", PreserveSig = false)]
	internal static extern void WindowsDeleteString(IntPtr hstring);

	/// <summary>拿一个 WinRT 运行时类的激活工厂。失败会抛 COMException。</summary>
	[DllImport("combase.dll", PreserveSig = false)]
	internal static extern void RoGetActivationFactory(
		IntPtr activatableClassId, [In] ref Guid iid,
		[MarshalAs(UnmanagedType.IUnknown)] out object factory);

	/// <summary>
	/// 初始化 WinRT。
	///
	/// 不用 PreserveSig = false：Avalonia 的 UI 线程已经是 STA，这里再要
	/// multithreaded 会返回 <c>RPC_E_CHANGED_MODE</c>（0x80010106）—— 那不是错误，
	/// 是「已经初始化过了，模式不同」，必须自己判而不是让它抛。
	/// </summary>
	[DllImport("combase.dll")]
	internal static extern int RoInitialize(int initType);

	internal const int RoInitSingleThreaded = 0;
	internal const int RoInitMultiThreaded = 1;
	internal const int RpcEChangedMode = unchecked((int) 0x80010106);
	internal const int SFalse = 1;

	/// <summary>包一层 HSTRING，用完即删。WinRT 的字符串不归 GC 管。</summary>
	internal readonly struct HString : IDisposable
	{
		internal IntPtr Handle { get; }

		internal HString(string value) => Handle = WindowsCreateString(value, value.Length);

		public void Dispose()
		{
			if (Handle != IntPtr.Zero) WindowsDeleteString(Handle);
		}
	}

	// ── WinRT：通知 ────────────────────────────────────────────────────────

	[ComImport, Guid("50AC103F-D235-4598-BBEF-98FE4D1A3AD4")]
	[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	internal interface IToastNotificationManagerStatics
	{
		// IInspectable 的三个槽位，必须占住。
		void GetIids(out int count, out IntPtr iids);
		void GetRuntimeClassName(out IntPtr className);
		void GetTrustLevel(out int trustLevel);

		[return: MarshalAs(UnmanagedType.Interface)]
		IToastNotifier CreateToastNotifier();

		/// <summary>按 AUMID 取通知器。非打包应用必须走这个重载。</summary>
		[return: MarshalAs(UnmanagedType.Interface)]
		IToastNotifier CreateToastNotifierWithId(IntPtr applicationId);

		// GetTemplateContent 用不到，但槽位不能空着。
		IntPtr GetTemplateContent(int type);
	}

	[ComImport, Guid("75927B93-03F3-41EC-91D3-6E5BAC1B38E7")]
	[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	internal interface IToastNotifier
	{
		void GetIids(out int count, out IntPtr iids);
		void GetRuntimeClassName(out IntPtr className);
		void GetTrustLevel(out int trustLevel);

		void Show([MarshalAs(UnmanagedType.Interface)] IToastNotification notification);

		/// <summary>收掉一条已经显示的通知。传的必须是 Show 时那个同一个对象。</summary>
		void Hide([MarshalAs(UnmanagedType.Interface)] IToastNotification notification);

		/// <summary>系统里这个 AUMID 的通知开关状态。0 = 允许。</summary>
		int GetSetting();
	}

	[ComImport, Guid("04124B20-82C6-4229-B109-FD9ED4662B53")]
	[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	internal interface IToastNotificationFactory
	{
		void GetIids(out int count, out IntPtr iids);
		void GetRuntimeClassName(out IntPtr className);
		void GetTrustLevel(out int trustLevel);

		[return: MarshalAs(UnmanagedType.Interface)]
		IToastNotification CreateToastNotification([MarshalAs(UnmanagedType.Interface)] object content);
	}

	/// <summary>只当句柄用，方法一个都不调，所以不必把 vtable 写全。</summary>
	[ComImport, Guid("997E2675-059E-4E60-8B06-1760917C8B80")]
	[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	internal interface IToastNotification
	{
		void GetIids(out int count, out IntPtr iids);
		void GetRuntimeClassName(out IntPtr className);
		void GetTrustLevel(out int trustLevel);
	}

	// ── WinRT：XML ─────────────────────────────────────────────────────────

	[ComImport, Guid("00000035-0000-0000-C000-000000000046")]
	[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	internal interface IActivationFactory
	{
		void GetIids(out int count, out IntPtr iids);
		void GetRuntimeClassName(out IntPtr className);
		void GetTrustLevel(out int trustLevel);

		[return: MarshalAs(UnmanagedType.IUnknown)]
		object ActivateInstance();
	}

	/// <summary>把一段 XML 文本灌进 XmlDocument。toast 的内容就是这么进去的。</summary>
	[ComImport, Guid("6CD0E74E-EE65-4489-9EBF-CA43E87BA637")]
	[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	internal interface IXmlDocumentIO
	{
		void GetIids(out int count, out IntPtr iids);
		void GetRuntimeClassName(out IntPtr className);
		void GetTrustLevel(out int trustLevel);

		/// <summary>XML 不合法时抛 COMException —— 这正是我们在 XML 那层单独测的原因。</summary>
		void LoadXml(IntPtr xml);
	}

	internal const string ToastNotificationManagerClass = "Windows.UI.Notifications.ToastNotificationManager";
	internal const string ToastNotificationClass = "Windows.UI.Notifications.ToastNotification";
	internal const string XmlDocumentClass = "Windows.Data.Xml.Dom.XmlDocument";

	internal static readonly Guid IidToastNotificationManagerStatics = new("50AC103F-D235-4598-BBEF-98FE4D1A3AD4");
	internal static readonly Guid IidToastNotificationFactory = new("04124B20-82C6-4229-B109-FD9ED4662B53");
	internal static readonly Guid IidActivationFactory = new("00000035-0000-0000-C000-000000000046");

	// ── Shell：快捷方式 ────────────────────────────────────────────────────
	//
	// 非打包的 Win32 应用要弹通知，必须在开始菜单里有一个带 AUMID 的快捷方式 ——
	// 系统靠它把通知归属到某个应用。没有它，CreateToastNotifierWithId 能拿到对象，
	// Show 也不报错，但什么都不会出现。

	[ComImport, Guid("00021401-0000-0000-C000-000000000046")]
	internal class ShellLink { }

	[ComImport, Guid("000214F9-0000-0000-C000-000000000046")]
	[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	internal interface IShellLinkW
	{
		void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder file, int maxPath, IntPtr findData, uint flags);
		void GetIDList(out IntPtr idList);
		void SetIDList(IntPtr idList);
		void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder name, int maxName);
		void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
		void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder dir, int maxPath);
		void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
		void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder args, int maxArgs);
		void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
		void GetHotkey(out short hotkey);
		void SetHotkey(short hotkey);
		void GetShowCmd(out int showCmd);
		void SetShowCmd(int showCmd);
		void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder icon, int maxPath, out int index);
		void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string icon, int index);
		void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
		void Resolve(IntPtr window, uint flags);
		void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
	}

	[ComImport, Guid("0000010B-0000-0000-C000-000000000046")]
	[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	internal interface IPersistFile
	{
		void GetClassID(out Guid classId);
		[PreserveSig] int IsDirty();
		void Load([MarshalAs(UnmanagedType.LPWStr)] string file, uint mode);
		void Save([MarshalAs(UnmanagedType.LPWStr)] string? file, [MarshalAs(UnmanagedType.Bool)] bool remember);
		void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string file);
		void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string file);
	}

	[ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
	[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	internal interface IPropertyStore
	{
		void GetCount(out uint count);
		void GetAt(uint index, out PropertyKey key);
		void GetValue(ref PropertyKey key, out PropVariant value);
		void SetValue(ref PropertyKey key, ref PropVariant value);
		void Commit();
	}

	[StructLayout(LayoutKind.Sequential, Pack = 4)]
	internal struct PropertyKey(Guid formatId, uint propertyId)
	{
		internal Guid FormatId = formatId;
		internal uint PropertyId = propertyId;
	}

	/// <summary>
	/// 只用得到字符串那一种，所以按 VT_LPWSTR 的布局手写。
	/// 用完必须 <c>PropVariantClear</c>，否则那块 CoTaskMem 泄掉。
	/// </summary>
	[StructLayout(LayoutKind.Sequential)]
	internal struct PropVariant
	{
		internal ushort VarType;
		private readonly ushort _reserved1;
		private readonly ushort _reserved2;
		private readonly ushort _reserved3;
		internal IntPtr Value;
		private readonly IntPtr _padding;
	}

	internal const ushort VtLpwstr = 31;

	[DllImport("ole32.dll", PreserveSig = false)]
	internal static extern void PropVariantClear(ref PropVariant value);

	/// <summary>System.AppUserModel.ID —— 通知归属到哪个应用。</summary>
	internal static PropertyKey AppUserModelIdKey =>
		new(new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), 5);

	/// <summary>System.AppUserModel.ToastActivatorCLSID —— 点了按钮找谁。</summary>
	internal static PropertyKey ToastActivatorClsidKey =>
		new(new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), 26);

	// ── COM 激活器注册 ────────────────────────────────────────────────────

	[DllImport("ole32.dll", PreserveSig = false)]
	internal static extern void CoRegisterClassObject(
		[In] ref Guid classId, [MarshalAs(UnmanagedType.IUnknown)] object factory,
		uint context, uint flags, out uint cookie);

	[DllImport("ole32.dll", PreserveSig = false)]
	internal static extern void CoRevokeClassObject(uint cookie);

	internal const uint ClsctxLocalServer = 4;
	internal const uint RegclsMultipleuse = 1;

	/// <summary>把当前进程标成某个 AUMID。通知器据此归属，且要在建窗口之前调。</summary>
	[DllImport("shell32.dll", PreserveSig = false)]
	internal static extern void SetCurrentProcessExplicitAppUserModelID(
		[MarshalAs(UnmanagedType.LPWStr)] string appId);
}
