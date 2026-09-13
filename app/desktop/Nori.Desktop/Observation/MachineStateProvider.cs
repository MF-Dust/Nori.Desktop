using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Nori.Core.Observation;
using Nori.Desktop.Runtime;

namespace Nori.Desktop.Observation;

/// <summary>
/// 采集这台机器的状态。
///
/// 三条原则：
///
/// 1. **不引入依赖。** 内存与 CPU 走 Win32，显卡走 `nvidia-smi` —— 后者随显卡驱动一起装好，
///    是现成工具而不是新依赖；
/// 2. **取不到就是 null。** 不用 0 兜底，调用方要能区分「读数为零」和「读不到」；
/// 3. **自己控制开销。** 每一轮对话前都会被调用，因此显卡那次进程调用带缓存与超时，
///    不能让一次对话卡在子进程上。
/// </summary>
public sealed class MachineStateProvider : IMachineStateProvider
{
	/// <summary>显卡读数的缓存时长。</summary>
	///
	/// <remarks>
	/// 每轮起一次子进程太贵，而显卡状态在几秒内不会有实质变化 —— 何况提示词里只放分档，
	/// 更细的时间分辨率不会改变任何取值。
	/// </remarks>
	public static readonly TimeSpan GpuCacheDuration = TimeSpan.FromSeconds(15);

	/// <summary>`nvidia-smi` 的等待上限。超时即当作取不到，不阻塞对话。</summary>
	public static readonly TimeSpan GpuTimeout = TimeSpan.FromSeconds(2);

	private readonly Lock _gate = new();
	private readonly Func<double?> _idleSeconds;
	private readonly Func<int>? _rgbDeviceCount;
	private (ulong Idle, ulong Total) _previousCpu;
	private GpuState? _gpu;
	private DateTimeOffset _gpuReadAt = DateTimeOffset.MinValue;
	private bool _gpuUnavailable;

	/// <summary>创建采集器。</summary>
	public MachineStateProvider(Func<double?>? idleSeconds = null, Func<int>? rgbDeviceCount = null)
	{
		_idleSeconds = idleSeconds ?? (() => OperatingSystem.IsWindows() ? SystemIdleTime.GetIdleSeconds() : null);
		_rgbDeviceCount = rgbDeviceCount;
	}

	/// <inheritdoc />
	public MachineState Read()
	{
		(int? usedMb, int? totalMb) = ReadMemory();
		double? idle = _idleSeconds();

		return new MachineState
		{
			MemoryUsedMb = usedMb,
			MemoryTotalMb = totalMb,
			OwnMemoryMb = (int)(Environment.WorkingSet / 1024 / 1024),
			CpuPercent = ReadCpuPercent(),
			Gpu = ReadGpu(),
			Uptime = TimeSpan.FromMilliseconds(Environment.TickCount64),
			Idle = idle is {} seconds ? TimeSpan.FromSeconds(seconds) : null,
			RgbDeviceCount = _rgbDeviceCount?.Invoke(),
		};
	}

	private static (int? Used, int? Total) ReadMemory()
	{
		if (!OperatingSystem.IsWindows()) return (null, null);

		MemoryStatusEx status = new() {Length = (uint)Marshal.SizeOf<MemoryStatusEx>()};
		if (!GlobalMemoryStatusEx(ref status)) return (null, null);

		int total = (int)(status.TotalPhys / 1024 / 1024);
		return (total - (int)(status.AvailPhys / 1024 / 1024), total);
	}

	/// <summary>
	/// 两次采样之间的系统 CPU 利用率。
	///
	/// 第一次调用没有基线，返回 null 而不是 0 —— 「刚启动」不等于「CPU 空闲」。
	/// </summary>
	private int? ReadCpuPercent()
	{
		if (!OperatingSystem.IsWindows()) return null;
		if (!GetSystemTimes(out FileTime idleTime, out FileTime kernelTime, out FileTime userTime)) return null;

		ulong idle = idleTime.Value;
		ulong total = kernelTime.Value + userTime.Value;

		lock (_gate)
		{
			(ulong previousIdle, ulong previousTotal) = _previousCpu;
			_previousCpu = (idle, total);
			if (previousTotal == 0 || total <= previousTotal) return null;

			ulong totalDelta = total - previousTotal;
			ulong idleDelta = idle - previousIdle;
			return (int)Math.Clamp(100 - (idleDelta * 100 / totalDelta), 0, 100);
		}
	}

	/// <summary>
	/// 从 `nvidia-smi` 读显卡状态，带缓存。
	///
	/// 第一次失败之后不再重试：没有 N 卡的机器上每轮都去起一个找不到的进程，纯属浪费。
	/// </summary>
	private GpuState? ReadGpu()
	{
		lock (_gate)
		{
			if (_gpuUnavailable) return null;
			if (DateTimeOffset.UtcNow - _gpuReadAt < GpuCacheDuration) return _gpu;
		}

		GpuState? read = QueryGpu();

		lock (_gate)
		{
			_gpuReadAt = DateTimeOffset.UtcNow;
			_gpu = read;
			if (read is null) _gpuUnavailable = true;
			return read;
		}
	}

	private static GpuState? QueryGpu()
	{
		try
		{
			using Process? process = Process.Start(new ProcessStartInfo("nvidia-smi")
			{
				Arguments =
					"--query-gpu=name,temperature.gpu,utilization.gpu,memory.used,memory.total "
					+ "--format=csv,noheader,nounits",
				UseShellExecute = false,
				CreateNoWindow = true,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
			});

			if (process is null) return null;
			string output = process.StandardOutput.ReadToEnd();
			if (!process.WaitForExit((int)GpuTimeout.TotalMilliseconds))
			{
				try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { /* 已退出 */ }
				return null;
			}

			return process.ExitCode == 0 ? Parse(output) : null;
		}
		catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
		{
			// 没装 N 卡驱动时 nvidia-smi 不存在，这是常态而不是故障。
			return null;
		}
	}

	/// <summary>解析 `名称, 温度, 利用率, 已用显存, 总显存` 这一行；多卡只取第一张。</summary>
	private static GpuState? Parse(string output)
	{
		string? line = output.Split('\n').FirstOrDefault(entry => entry.Trim().Length > 0);
		if (line is null) return null;

		string[] fields = line.Split(',', StringSplitOptions.TrimEntries);
		if (fields.Length < 5) return null;

		return int.TryParse(fields[1], CultureInfo.InvariantCulture, out int temperature)
			&& int.TryParse(fields[2], CultureInfo.InvariantCulture, out int utilization)
			&& int.TryParse(fields[3], CultureInfo.InvariantCulture, out int used)
			&& int.TryParse(fields[4], CultureInfo.InvariantCulture, out int total)
			? new GpuState
			{
				Name = fields[0],
				TemperatureCelsius = temperature,
				UtilizationPercent = utilization,
				MemoryUsedMb = used,
				MemoryTotalMb = total,
			}
			: null;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct MemoryStatusEx
	{
		public uint Length;
		public uint MemoryLoad;
		public ulong TotalPhys;
		public ulong AvailPhys;
		public ulong TotalPageFile;
		public ulong AvailPageFile;
		public ulong TotalVirtual;
		public ulong AvailVirtual;
		public ulong AvailExtendedVirtual;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct FileTime
	{
		public uint Low;
		public uint High;

		public readonly ulong Value => ((ulong)High << 32) | Low;
	}

	[SupportedOSPlatform("windows")]
	[DllImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx status);

	[SupportedOSPlatform("windows")]
	[DllImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool GetSystemTimes(out FileTime idle, out FileTime kernel, out FileTime user);
}
