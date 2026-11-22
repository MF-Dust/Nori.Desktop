using System.Globalization;
using System.Runtime.Versioning;
using Avalonia.Media;
using Microsoft.Win32;
using Nori.Core.Platform;

namespace Nori.Desktop.Expression;

/// <summary>
/// 桌面外观的读写；测试必须使用替身，不能修改真实桌面的持久设置。
/// </summary>
public interface IDesktopAppearance
{
	/// <summary>本平台能不能读写这些设置。</summary>
	bool IsAvailable { get; }

	/// <summary>备份按桌面环境隔离；Windows 沿用未加后缀的旧键。</summary>
	string? BackupScope => null;

	/// <summary>捕获可还原的原值；读不到返回 null。</summary>
	string? CaptureBackup();

	/// <summary>换强调色。</summary>
	void SetAccentColor(Color color);

	/// <summary>用原值还原；忽略损坏的备份，写入失败时抛出异常以保留备份。</summary>
	void RestoreBackup(string backup);
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
	public string? CaptureBackup() => null;

	/// <inheritdoc />
	public void SetAccentColor(Color color)
	{
	}

	/// <inheritdoc />
	public void RestoreBackup(string backup)
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

	/// <inheritdoc />
	public bool IsAvailable => OperatingSystem.IsWindows() && PlatformServices.Current.DesktopIntegration.IsAvailable;

	/// <inheritdoc />
	public string? CaptureBackup() => GetAccentColor()?.ToString(CultureInfo.InvariantCulture);

	/// <inheritdoc />
	public void RestoreBackup(string backup)
	{
		if (ExpressionColors.TryParseWindowsAccentBackup(backup, out Color color)) SetAccentColor(color);
	}

	/// <summary>当前强调色的 Windows AABBGGRR 原始存储值。</summary>
	public uint? GetAccentColor()
	{
		if (!IsAvailable) return null;
		try
		{
			DesktopAccent? color = PlatformServices.Current.DesktopIntegration.GetAccentColor();
			return color is null ? null : PackAccent(color);
		}
		catch (PlatformNotSupportedException) { return null; }
	}

	/// <summary>备份继续使用 Windows AABBGGRR 格式，SDK 返回的 RGBA 不能按 ARGB 解释。</summary>
	internal static uint PackAccent(DesktopAccent color) =>
		(uint)color.Alpha << 24 | (uint)color.Blue << 16 | (uint)color.Green << 8 | color.Red;

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
}
