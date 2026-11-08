namespace Nori.Core.Platform;

/// <summary>Linux <c>/proc</c> 内存与 CPU 的纯解析。读不到时保持 null，不用 0 代替。</summary>
internal static class LinuxProcStats
{
	internal readonly record struct CpuTimes(ulong Idle, ulong Total);

	internal static (int? UsedMb, int? TotalMb) ReadMemory()
	{
		if (!OperatingSystem.IsLinux()) return (null, null);
		try
		{
			return ParseMeminfo(File.ReadAllText("/proc/meminfo"));
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return (null, null);
		}
	}

	internal static bool TryReadCpu(out CpuTimes times)
	{
		times = default;
		if (!OperatingSystem.IsLinux()) return false;
		try
		{
			return TryParseCpu(File.ReadAllText("/proc/stat"), out times);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return false;
		}
	}

	/// <summary>
	/// <c>MemTotal</c> 与 <c>MemAvailable</c> 单位是 kB。总量缺失或两者矛盾时整对作废；
	/// 只有总量时仍可进入硬件清单，已用量保持 null。
	/// </summary>
	internal static (int? UsedMb, int? TotalMb) ParseMeminfo(string text)
	{
		long? totalKb = null;
		long? availableKb = null;
		foreach (string raw in text.Split('\n'))
		{
			if (TryReadKb(raw, "MemTotal:", out long total)) totalKb = total;
			else if (TryReadKb(raw, "MemAvailable:", out long available)) availableKb = available;
		}
		if (totalKb is not > 0 || totalKb > (long)int.MaxValue * 1024) return (null, null);
		int totalMb = (int)(totalKb.Value / 1024);
		if (totalMb <= 0) return (null, null);
		if (availableKb is null) return (null, totalMb);
		if (availableKb < 0 || availableKb > totalKb) return (null, null);
		long usedKb = totalKb.Value - availableKb.Value;
		if (usedKb > (long)int.MaxValue * 1024) return (null, totalMb);
		return ((int)(usedKb / 1024), totalMb);
	}

	/// <summary>
	/// 取汇总行 <c>cpu </c>。<c>guest</c> 已计入 <c>user</c>，不再相加。
	/// 空闲包含 <c>iowait</c>；老内核没有该列时只计 <c>idle</c>。
	/// </summary>
	internal static bool TryParseCpu(string text, out CpuTimes times)
	{
		times = default;
		foreach (string raw in text.Split('\n'))
		{
			if (!raw.StartsWith("cpu ", StringComparison.Ordinal)) continue;
			string[] fields = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
			// 至少 user nice system idle。
			if (fields.Length < 5) return false;
			ulong[] values = new ulong[fields.Length - 1];
			for (int index = 1; index < fields.Length; index++)
			{
				if (!ulong.TryParse(fields[index], out values[index - 1])) return false;
			}
			ulong idle = values[3];
			if (values.Length > 4) idle += values[4];
			int counted = Math.Min(values.Length, 8);
			ulong total = 0;
			for (int index = 0; index < counted; index++) total += values[index];
			times = new CpuTimes(idle, total);
			return true;
		}
		return false;
	}

	/// <summary>两次采样之间的利用率。没有基线、计数回绕或增量为 0 时返回 null。</summary>
	internal static int? Percent(CpuTimes previous, CpuTimes current)
	{
		if (previous.Total == 0 || current.Total <= previous.Total || current.Idle < previous.Idle) return null;
		ulong totalDelta = current.Total - previous.Total;
		ulong idleDelta = current.Idle - previous.Idle;
		if (idleDelta > totalDelta) return null;
		return (int)Math.Clamp(100 - (idleDelta * 100 / totalDelta), 0, 100);
	}

	private static bool TryReadKb(string line, string label, out long kilobytes)
	{
		kilobytes = 0;
		if (!line.StartsWith(label, StringComparison.Ordinal)) return false;
		string[] fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
		if (fields.Length < 3 || !fields[2].Equals("kB", StringComparison.Ordinal)) return false;
		return long.TryParse(fields[1], out kilobytes) && kilobytes >= 0;
	}
}
