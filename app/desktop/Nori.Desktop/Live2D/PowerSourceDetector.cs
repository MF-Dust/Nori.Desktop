using System.Runtime.InteropServices;
using Nori.Core.Live2D;

namespace Nori.Desktop.Live2D;

/// <summary>读取桌面电源状态；非 Windows 或读取失败时按交流电处理。</summary>
internal static class PowerSourceDetector
{
	[StructLayout(LayoutKind.Sequential)]
	private struct SystemPowerStatus // NOSONAR -- 原生 ABI 结构体仅用于互操作，不参与相等比较
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
