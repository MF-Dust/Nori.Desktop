using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Nori.Desktop.Account;

/// <summary>
/// 系统的「减少动画」偏好。
///
/// 网页端由 <c>prefers-reduced-motion</c> 提供，原生端无等价机制，需直接查询系统。
/// Windows 上对应「显示 → 动画效果」开关。关闭该开关的用户通常出于前庭功能或注意力
/// 方面的需要，忽略该设置会导致界面不可用，而非仅影响观感。
///
/// 查询失败时按允许动画处理：该偏好默认开启，查询失败通常是环境问题而非用户意愿，
/// 据此关闭全部动效属于越权判断。
/// </summary>
internal static partial class MotionPreference
{
	private const uint SpiGetClientAreaAnimation = 0x1042;

	private static bool? _cached;

	/// <summary>用户要不要动效。</summary>
	internal static bool AllowAnimation => _cached ??= Query();

	/// <summary>测试用：强制取值，传 null 恢复成问系统。</summary>
	internal static void Override(bool? allow) => _cached = allow;

	private static bool Query()
	{
		if (!OperatingSystem.IsWindows()) return true;
		try
		{
			return QueryWindows();
		}
		catch (Exception)
		{
			// 查询失败按允许处理，理由见类型注释。
			return true;
		}
	}

	[SupportedOSPlatform("windows")]
	private static bool QueryWindows()
	{
		int enabled = 1;
		return !SystemParametersInfo(SpiGetClientAreaAnimation, 0, ref enabled, 0) || enabled != 0;
	}

	[LibraryImport("user32.dll", EntryPoint = "SystemParametersInfoW")]
	[return: MarshalAs(UnmanagedType.Bool)]
	[SupportedOSPlatform("windows")]
	private static partial bool SystemParametersInfo(uint action, uint param, ref int value, uint winIni);
}
