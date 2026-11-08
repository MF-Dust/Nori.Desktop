using System.Runtime.InteropServices;
using Nori.Core.Live2D;
using Nori.Core.Platform;

namespace Nori.Desktop.Live2D;

/// <summary>读取桌面电源状态。Windows 看交流电是否断开，Linux 看 sysfs；读不到时按交流电。</summary>
internal static class PowerSourceDetector
{
	[StructLayout(LayoutKind.Sequential)]
	[System.Diagnostics.CodeAnalysis.SuppressMessage("CodeQuality", "S3898", Justification = "原生 ABI 结构体仅用于互操作，不参与相等比较。")]
	private struct SystemPowerStatus
	{
		public byte ACLineStatus;
		public byte BatteryFlag;
		public byte BatteryLifePercent;
		public byte Reserved;
		public int BatteryLifeTime;
		public int BatteryFullLifeTime;
	}

	[DllImport("kernel32.dll", ExactSpelling = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);

	public static Live2DPowerSource Detect()
	{
		if (OperatingSystem.IsLinux())
			return LinuxPowerSupply.OnBattery() ? Live2DPowerSource.Battery : Live2DPowerSource.Ac;
		if (!OperatingSystem.IsWindows()) return Live2DPowerSource.Ac;
		try
		{
			if (GetSystemPowerStatus(out SystemPowerStatus status) && status.ACLineStatus == 0)
			{
				return Live2DPowerSource.Battery;
			}
		}
		catch (DllNotFoundException)
		{
			// Windows 必然带 kernel32; 测试宿主或兼容运行时缺失时按交流电降级。
		}
		catch (EntryPointNotFoundException)
		{
			// 同上。
		}
		return Live2DPowerSource.Ac;
	}
}
