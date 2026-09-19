using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Nori.Desktop.Runtime;

/// <summary>
/// 系统空闲时长 (Windows GetLastInputInfo)
///
/// 挂机主动关怀依赖系统级的键鼠空闲时间, WebView 内的 DOM 事件覆盖不了窗口外,
/// 因此由宿主直接查询系统输入状态。
/// </summary>
public static class SystemIdleTime
{
	[StructLayout(LayoutKind.Sequential)]
	[System.Diagnostics.CodeAnalysis.SuppressMessage("CodeQuality", "S3898", Justification = "原生 ABI 结构体仅用于互操作，不参与相等比较。")]
	private struct LastInputInfo
	{
		public uint CbSize;
		public uint DwTime;
	}

	[DllImport("user32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool GetLastInputInfo(ref LastInputInfo info);

	/// <summary>
	/// 获取系统空闲秒数; 非 Windows 或查询失败返回 null
	/// </summary>
	[SupportedOSPlatform("windows")]
	public static double? GetIdleSeconds()
	{
		if (!OperatingSystem.IsWindows()) return null;
		try
		{
			LastInputInfo info = new() {CbSize = (uint)Marshal.SizeOf<LastInputInfo>()};
			if (!GetLastInputInfo(ref info)) return null;
			uint tick = info.DwTime;
			uint now = (uint)Environment.TickCount;
			return Math.Max(0, unchecked(now - tick) / 1000.0);
		}
		catch
		{
			return null;
		}
	}
}
