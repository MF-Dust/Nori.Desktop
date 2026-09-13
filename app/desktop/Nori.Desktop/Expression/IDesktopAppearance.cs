using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia.Media;
using Microsoft.Win32;

namespace Nori.Desktop.Expression;

/// <summary>
/// 桌面外观的读写。
///
/// 抽成接口不是为了跨平台 —— 目前只有 Windows 实现 —— 而是为了**测试不碰真实桌面**。
/// 这一族改的是持久的系统设置，用例跑一遍把开发机的壁纸换掉是不可接受的。
/// </summary>
public interface IDesktopAppearance
{
	/// <summary>本平台能不能读写这些设置。</summary>
	bool IsAvailable { get; }

	/// <summary>当前壁纸的绝对路径；读不到返回 null。</summary>
	string? GetWallpaper();

	/// <summary>换壁纸。不动壁纸样式（填充/平铺等），那是另一项用户设置。</summary>
	void SetWallpaper(string path);

	/// <summary>当前强调色的原始存储值；读不到返回 null。</summary>
	uint? GetAccentColor();

	/// <summary>换强调色。</summary>
	void SetAccentColor(Color color);
}

/// <summary>
/// 不支持的平台上的空实现。
///
/// 与 <c>UnsupportedPlatformServices</c> 同一套做法：不抛 <c>PlatformNotSupportedException</c>
/// 让调用方去 try/catch，而是如实报告不可用，由通道自己跳过。
/// </summary>
public sealed class UnsupportedDesktopAppearance : IDesktopAppearance
{
	/// <inheritdoc />
	public bool IsAvailable => false;

	/// <inheritdoc />
	public string? GetWallpaper() => null;

	/// <inheritdoc />
	public void SetWallpaper(string path)
	{
	}

	/// <inheritdoc />
	public uint? GetAccentColor() => null;

	/// <inheritdoc />
	public void SetAccentColor(Color color)
	{
	}
}

/// <summary>
/// Windows 下的桌面外观读写。
///
/// **两个键的字节序是相反的**，这是实测确认的，也是这里最容易静默出错的地方：
///
/// <code>
/// DWM\ColorizationColor = 0xC477C6D4   AARRGGBB
/// DWM\AccentColor       = 0xFFD4C677   AABBGGRR   ← 同一个颜色
/// </code>
///
/// 写反了不会报错，桌面会变成补色。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsDesktopAppearance : IDesktopAppearance
{
	private const string DwmKey = @"Software\Microsoft\Windows\DWM";
	private const string DesktopKey = @"Control Panel\Desktop";
	private const uint SpiSetDeskWallpaper = 0x0014;
	private const uint SpiUpdateIniFile = 0x01;
	private const uint SpiSendChange = 0x02;

	/// <inheritdoc />
	public bool IsAvailable => OperatingSystem.IsWindows();

	/// <inheritdoc />
	public string? GetWallpaper() =>
		Registry.CurrentUser.OpenSubKey(DesktopKey)?.GetValue("Wallpaper") as string;

	/// <inheritdoc />
	public void SetWallpaper(string path)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(path);
		if (!SystemParametersInfo(SpiSetDeskWallpaper, 0, path, SpiUpdateIniFile | SpiSendChange))
		{
			throw new InvalidOperationException($"设置壁纸失败: {Marshal.GetLastWin32Error()}");
		}
	}

	/// <inheritdoc />
	public uint? GetAccentColor()
	{
		object? value = Registry.CurrentUser.OpenSubKey(DwmKey)?.GetValue("AccentColor");
		return value is null ? null : Convert.ToUInt32(value, System.Globalization.CultureInfo.InvariantCulture);
	}

	/// <inheritdoc />
	public void SetAccentColor(Color color)
	{
		using RegistryKey key = Registry.CurrentUser.CreateSubKey(DwmKey);

		// AccentColor 是 AABBGGRR，Colorization* 是 AARRGGBB。两个都要写，只写一个的话
		// 标题栏与任务栏会取到不同的颜色。
		key.SetValue("AccentColor", unchecked((int)(0xFF000000u | ExpressionColors.ToWindowsBgr(color))), RegistryValueKind.DWord);
		int argb = unchecked((int)(0xC4000000u | (uint)(color.R << 16 | color.G << 8 | color.B)));
		key.SetValue("ColorizationColor", argb, RegistryValueKind.DWord);
		key.SetValue("ColorizationAfterglow", argb, RegistryValueKind.DWord);
	}

	[DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool SystemParametersInfo(uint action, uint param, string value, uint flags);
}
