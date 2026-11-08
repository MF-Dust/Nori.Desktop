using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Nori.Core.Platform;

namespace Nori.Desktop.Appearance;

/// <summary>
/// 动效偏好：个性化设置中的「跟随系统 / 减少动效 / 始终开启」。
///
/// 原生端没有 CSS <c>prefers-reduced-motion</c> 那样的现成机制，跟随系统时需直接查询系统。
/// Windows 上对应「显示 → 动画效果」开关。关闭该开关的用户通常出于前庭功能或注意力
/// 方面的需要，忽略该设置会导致界面不可用，而非仅影响观感。
///
/// 跟随系统时保留两种既有口径：装饰动效（页面过场、光环、连线）查询失败按允许处理，
/// 因为据此关闭全部动效属于越权判断；快捷聊天与扫描浮层沿用平台服务的保守默认，
/// 读不到系统设置时减少动效。Linux 的系统值来自门户、GTK 与 KDE 动画开关的缓存，
/// 读缓存本身不发起 IPC。用户显式选择的两档同时覆盖这两种口径。
/// </summary>
internal static partial class MotionPreference
{
	private const uint SpiGetClientAreaAnimation = 0x1042;

	private static bool? _cached;
	private static MotionMode _mode;

	/// <summary>当前动效偏好。</summary>
	internal static MotionMode Mode => _mode;

	/// <summary>装饰动效是否播放。</summary>
	internal static bool AllowAnimation => _mode switch
	{
		MotionMode.Reduce => false,
		MotionMode.Full => true,
		_ => _cached ??= Query(),
	};

	/// <summary>快捷聊天与扫描浮层是否减少动效。</summary>
	internal static bool PrefersReducedMotion => _mode switch
	{
		MotionMode.Reduce => true,
		MotionMode.Full => false,
		_ => PlatformServices.Current.PrefersReducedMotion,
	};

	/// <summary>由应用外观在 UI 线程写入用户偏好。</summary>
	internal static void SetMode(MotionMode mode) => _mode = mode;

	/// <summary>测试用：强制系统查询结果，传 null 恢复成问系统。</summary>
	internal static void Override(bool? allow) => _cached = allow;

	private static bool Query()
	{
		if (OperatingSystem.IsWindows())
		{
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
		if (OperatingSystem.IsLinux()) return Nori.Core.Platform.LinuxMotionPreference.AllowDecorativeAnimations;
		return true;
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
