namespace Nori.Core.Platform;

/// <summary>从 sysfs 电源节点判断是否应走电池画质。读不到或没有节点时按交流电。</summary>
internal static class LinuxPowerSupply
{
	internal readonly record struct Supply(string Type, string? Status, string? Online);

	internal static bool OnBattery()
	{
		if (!OperatingSystem.IsLinux()) return false;
		try
		{
			const string root = "/sys/class/power_supply";
			if (!Directory.Exists(root)) return false;
			List<Supply> supplies = [];
			foreach (string directory in Directory.EnumerateDirectories(root))
			{
				supplies.Add(new Supply(
					ReadTrim(Path.Combine(directory, "type")) ?? "",
					ReadTrim(Path.Combine(directory, "status")),
					ReadTrim(Path.Combine(directory, "online"))));
			}
			return UseBattery(supplies);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return false;
		}
	}

	/// <summary>
	/// 市电、USB 或 UPS 的 <c>online=1</c> 视为交流电。
	/// 没有在线外接电源，且电池正在放电或外接电源明确离线时视为电池。
	/// </summary>
	internal static bool UseBattery(IReadOnlyList<Supply> supplies)
	{
		bool acOnline = false;
		bool explicitlyOffline = false;
		bool discharging = false;
		foreach (Supply supply in supplies)
		{
			if (IsExternal(supply.Type))
			{
				if (supply.Online == "1") acOnline = true;
				else if (supply.Online == "0") explicitlyOffline = true;
			}
			else if (supply.Type.Equals("Battery", StringComparison.OrdinalIgnoreCase)
				&& string.Equals(supply.Status, "Discharging", StringComparison.OrdinalIgnoreCase))
			{
				discharging = true;
			}
		}
		if (acOnline) return false;
		return discharging || explicitlyOffline;
	}

	private static bool IsExternal(string type) =>
		type.Equals("Mains", StringComparison.OrdinalIgnoreCase)
		|| type.Equals("USB", StringComparison.OrdinalIgnoreCase)
		|| type.Equals("UPS", StringComparison.OrdinalIgnoreCase);

	private static string? ReadTrim(string path)
	{
		try
		{
			return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return null;
		}
	}
}
