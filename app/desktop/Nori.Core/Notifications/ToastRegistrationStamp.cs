using System.Security.Cryptography;
using System.Text;

namespace Nori.Core.Notifications;

/// <summary>
/// 「这台机器上的通知注册还用不用重做」这个判断。
///
/// 注册一次要写两样东西：开始菜单里的快捷方式，和 HKCU 下的 COM 激活器。两样都不便宜
/// （快捷方式要起 COM、要落盘），而且每次启动都重写会让开始菜单的项目**时间戳一直变**，
/// 在「最近添加」里反复冒头。所以存一个指纹，只有指纹变了才重做。
///
/// 指纹里必须包含可执行文件路径：便携版可以被整个文件夹搬走，搬走之后快捷方式指向的
/// 还是旧路径，点通知按钮就找不到人。槽位更新不会改这个路径（指向的是稳定入口
/// <c>Nori.exe</c> 而不是 <c>app-x.y.z-n\Nori.Desktop.exe</c>），所以升级不触发重写。
/// </summary>
public static class ToastRegistrationStamp
{
	/// <summary>存指纹的配置键。</summary>
	public const string ConfigKey = "toast_registration";

	/// <summary>
	/// 算一个指纹。三项任意一项变了都要重做注册。
	///
	/// 路径比较按不区分大小写归一化 —— Windows 的路径大小写不敏感，把
	/// <c>D:\Nori\Nori.exe</c> 和 <c>d:\nori\nori.exe</c> 当成两回事会导致每次启动都重写。
	/// </summary>
	public static string Compute(string executablePath, string appUserModelId, Guid activatorClsid)
	{
		ArgumentException.ThrowIfNullOrEmpty(executablePath);
		ArgumentException.ThrowIfNullOrEmpty(appUserModelId);

		string payload = string.Join('\n',
			executablePath.Replace('/', '\\').TrimEnd('\\').ToUpperInvariant(),
			appUserModelId,
			activatorClsid.ToString("B").ToUpperInvariant());
		return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(payload)))[..32];
	}

	/// <summary>存着的指纹和现在算出来的一致就不用重做。</summary>
	public static bool NeedsWrite(string? stored, string current) =>
		string.IsNullOrEmpty(stored) || !string.Equals(stored, current, StringComparison.Ordinal);
}
