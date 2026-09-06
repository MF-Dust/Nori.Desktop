using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using Nori.Core.Tools;

namespace Nori.Desktop.Runtime;

/// <summary>
/// 系统信息与电池 (Windows GetSystemPowerStatus)
/// </summary>
public sealed class DesktopSystemInfo(Nori.Core.Configuration.ConfigStore config) : ISystemInfoProvider
{
	[StructLayout(LayoutKind.Sequential)]
	private struct SystemPowerStatus
	{
		public byte ACLineStatus;
		public byte BatteryFlag;
		public byte BatteryLifePercent;
		public byte Reserved1;
		public int BatteryLifeTime;
		public int BatteryFullLifeTime;
	}

	[DllImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);

	public object GetInfo()
	{
		string language = config.GetStringOr("language", "");
		if (language.Length == 0)
		{
			language = System.Globalization.CultureInfo.CurrentUICulture.Name;
		}
		return new
		{
			platform = OperatingSystem.IsWindows() ? "Windows" : (OperatingSystem.IsMacOS() ? "macOS" : "Linux"),
			machineName = Environment.MachineName,
			language,
			online = System.Net.NetworkInformation.NetworkInterface.GetIsNetworkAvailable(),
			osVersion = Environment.OSVersion.VersionString,
		};
	}

	public object? GetBatteryStatus()
	{
		if (!OperatingSystem.IsWindows()) return null;
		try
		{
			if (!GetSystemPowerStatus(out SystemPowerStatus status)) return null;
			// BatteryFlag 128 表示无电池; BatteryLifePercent 255 表示未知
			if ((status.BatteryFlag & 128) != 0) return null;
			int level = status.BatteryLifePercent is >= 0 and <= 100 ? status.BatteryLifePercent : 0;
			return new
			{
				level,
				charging = status.ACLineStatus == 1,
				percent = $"{level}%",
			};
		}
		catch
		{
			return null;
		}
	}
}

/// <summary>伴侣动作/表情控制适配器</summary>
public sealed class PetActionsAdapter(Func<Nori.Desktop.Live2D.PetRuntime?> runtime) : IPetActions
{
	public IReadOnlyList<string> MotionNames => runtime()?.MotionGroups.SelectMany(group => group.Names).Distinct(StringComparer.OrdinalIgnoreCase).ToArray() ?? [];

	public IReadOnlyList<string> ExpressionNames => runtime()?.Expressions ?? [];

	public bool PlayMotionByName(string name)
	{
		Nori.Desktop.Live2D.PetRuntime? pet = runtime();
		if (pet is null) return false;
		return pet.PlayMotionByName(name);
	}

	public bool PlayExpression(string name)
	{
		return runtime()?.PlayExpression(name) ?? false;
	}
}

/// <summary>Avalonia 剪贴板适配器</summary>
public sealed class AvaloniaClipboardOps(Func<Avalonia.Controls.Window?> window) : IClipboardOps
{
	public async Task<string> GetTextAsync(CancellationToken cancellationToken = default)
	{
		IClipboard? clipboard = await ResolveClipboard()
			?? throw new InvalidOperationException("剪贴板不可用");
		// Avalonia 12 移除了 IClipboard.GetTextAsync, 读取走 ClipboardExtensions.TryGetValueAsync
		string? text = await clipboard.TryGetValueAsync<string>(Avalonia.Input.DataFormat.Text);
		return text ?? "";
	}

	public async Task SetTextAsync(string text, CancellationToken cancellationToken = default)
	{
		IClipboard? clipboard = await ResolveClipboard()
			?? throw new InvalidOperationException("剪贴板不可用");
		await clipboard.SetTextAsync(text);
	}

	private Task<IClipboard?> ResolveClipboard() => Dispatcher.UIThread.InvokeAsync(() =>
		Avalonia.Controls.TopLevel.GetTopLevel(window())?.Clipboard).GetTask();
}

/// <summary>系统默认浏览器打开链接 (仅 http/https)</summary>
public static class ShellOpen
{
	public static void OpenUrl(string url)
	{
		if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? parsed) || parsed.Scheme is not ("http" or "https"))
		{
			throw new InvalidOperationException($"不允许打开的链接: {url}");
		}
		Process.Start(new ProcessStartInfo(parsed.ToString()) {UseShellExecute = true});
	}
}
