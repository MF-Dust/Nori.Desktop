namespace Nori.Desktop.Diagnostics;

/// <summary>进程级启动开关。</summary>
public sealed record StartupOptions(bool SafeMode)
{
	/// <summary>解析安全模式参数。</summary>
	public static bool TryParse(
		IReadOnlyList<string> args,
		out StartupOptions? options,
		out string error)
	{
		options = null;
		error = "";
		bool safeMode = false;
		foreach (string arg in args)
		{
			if (!arg.Equals("--safe-mode", StringComparison.Ordinal)) continue;
			if (safeMode)
			{
				error = "--safe-mode 只能指定一次";
				return false;
			}
			safeMode = true;
		}

		options = new StartupOptions(safeMode);
		return true;
	}
}
