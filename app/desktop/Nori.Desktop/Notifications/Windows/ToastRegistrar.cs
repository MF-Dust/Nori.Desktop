using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;
using Nori.Core.Notifications;

namespace Nori.Desktop.Notifications.Windows;

/// <summary>
/// 让这台机器认得「Nori 这个应用」，从而允许它弹带按钮的系统通知。
///
/// 非打包的 Win32 应用要两样东西：
///
/// 1. **开始菜单里一个带 AUMID 的快捷方式。** 系统靠它把通知归属到某个应用，并在
///    「设置 → 通知」里列出来。没有它，<c>CreateToastNotifierWithId</c> 照样返回对象、
///    <c>Show</c> 照样不报错，但屏幕上什么都不会出现 —— 这条路的失败全是静默的。
/// 2. **HKCU 下注册的 COM 激活器。** 点通知按钮时系统按 CLSID 找进程。
///
/// 这两样都是会留在用户机器上的痕迹，对一个便携 ZIP 分发的应用来说是可见的行为变化。
/// 所以：只在用户打开了通知开关时才注册，关掉时<see cref="Remove"/>清干净。
/// </summary>
[SupportedOSPlatform("windows")]
internal static class ToastRegistrar
{
	/// <summary>
	/// 应用标识。**定死，不要改。**
	///
	/// 用户在「设置 → 通知」里对 Nori 做的开关是挂在这个字符串上的；改一次等于
	/// 换了个应用，用户之前关掉的通知会自己回来。
	/// </summary>
	internal const string AppUserModelId = "Nyco.Nori.Desktop";

	/// <summary>通知激活器的 CLSID。同样定死 —— 已经发出去的通知里带着它。</summary>
	internal static readonly Guid ActivatorClsid = new("6E3B2C41-9A7D-4F55-8E10-2B4C7D9A1F33");

	/// <summary>开始菜单里显示的名字。</summary>
	private const string ShortcutName = "Nori";

	private static string ShortcutPath => Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
		"Microsoft", "Windows", "Start Menu", "Programs", ShortcutName + ".lnk");

	/// <summary>
	/// 注册进注册表的那个可执行文件。
	///
	/// 必须是包根的**稳定入口** Nori.exe，不能是槽目录里的 Nori.Desktop.exe ——
	/// 后者每次更新换一个 app-x.y.z-n 目录，升级之后系统按旧路径拉不起进程，
	/// 通知按钮就变成点了没反应。开发环境下包根里没有 Nori.exe，退回当前进程。
	/// </summary>
	internal static string ResolveEntrypoint(string packageRoot)
	{
		string stable = Path.Combine(packageRoot, "Nori.exe");
		return File.Exists(stable) ? stable : Environment.ProcessPath ?? stable;
	}

	private static string ClsidKeyPath => $@"Software\Classes\CLSID\{ActivatorClsid:B}";

	/// <summary>
	/// 建快捷方式 + 注册激活器。已经是这个指纹就跳过。
	///
	/// 返回是否真的写过 —— 调用方据此决定要不要更新配置里的指纹。任何一步失败都
	/// 抛给调用方，由它降级成「这台机器上不弹通知」而不是让启动失败。
	/// </summary>
	internal static void Write(string executablePath)
	{
		WriteShortcut(executablePath);
		WriteActivator(executablePath);
	}

	/// <summary>用户关掉通知时清干净。清不掉不抛 —— 留下一个快捷方式不值得让设置保存失败。</summary>
	internal static void Remove()
	{
		try { File.Delete(ShortcutPath); } catch { /* 文件被占或已不在，不影响 */ }
		try { Registry.CurrentUser.DeleteSubKeyTree(ClsidKeyPath, throwOnMissingSubKey: false); } catch { /* 同上 */ }
	}

	private static void WriteShortcut(string executablePath)
	{
		Directory.CreateDirectory(Path.GetDirectoryName(ShortcutPath)!);

		ToastNativeApi.IShellLinkW link = (ToastNativeApi.IShellLinkW) new ToastNativeApi.ShellLink();
		try
		{
			link.SetPath(executablePath);
			link.SetWorkingDirectory(Path.GetDirectoryName(executablePath) ?? "");
			link.SetDescription("Nori");

			// AUMID 和激活器 CLSID 是这个快捷方式存在的**唯一**理由；少写一个，
			// 通知要么不归属、要么按钮点了没反应。
			ToastNativeApi.IPropertyStore store = (ToastNativeApi.IPropertyStore) link;
			SetString(store, ToastNativeApi.AppUserModelIdKey, AppUserModelId);
			SetString(store, ToastNativeApi.ToastActivatorClsidKey, ActivatorClsid.ToString("B"));
			store.Commit();

			((ToastNativeApi.IPersistFile) link).Save(ShortcutPath, remember: true);
		}
		finally
		{
			Marshal.FinalReleaseComObject(link);
		}
	}

	private static void SetString(ToastNativeApi.IPropertyStore store, ToastNativeApi.PropertyKey key, string value)
	{
		ToastNativeApi.PropVariant variant = new()
		{
			VarType = ToastNativeApi.VtLpwstr,
			Value = Marshal.StringToCoTaskMemUni(value),
		};
		try
		{
			store.SetValue(ref key, ref variant);
		}
		finally
		{
			// PropVariantClear 负责把上面那块 CoTaskMem 放掉；自己 FreeCoTaskMem 会双重释放。
			ToastNativeApi.PropVariantClear(ref variant);
		}
	}

	/// <summary>
	/// 注册 LocalServer32。
	///
	/// 指向的必须是**稳定入口**（包根的 Nori.exe），不能是槽目录里的
	/// Nori.Desktop.exe —— 后者每次更新换一个 app-x.y.z-n 目录，升级之后系统按旧路径
	/// 拉不起进程，通知按钮就变成点了没反应。
	/// </summary>
	private static void WriteActivator(string executablePath)
	{
		using RegistryKey key = Registry.CurrentUser.CreateSubKey(ClsidKeyPath + @"\LocalServer32");
		key.SetValue(null, $"\"{executablePath}\" " + ToastActivator.CommandLineFlag);
	}
}
