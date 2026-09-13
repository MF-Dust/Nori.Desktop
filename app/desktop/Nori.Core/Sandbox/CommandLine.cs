namespace Nori.Core.Sandbox;

/// <summary>命令行的拆分与拼接。</summary>
public static class CommandLine
{
	/// <summary>
	/// 把一条命令行拆成可执行文件与参数两段。
	///
	/// 只处理首段的引号，其余原样交给被启动进程 —— 参数的再拆分由目标进程自己按它的
	/// 规则做，此处不参与。首段必须支持引号是因为路径常含空格
	/// （<c>"C:\Program Files\dotnet\dotnet.exe" build</c>）。
	/// </summary>
	public static (string FileName, string Arguments) Split(string commandLine)
	{
		ArgumentNullException.ThrowIfNull(commandLine);
		string trimmed = commandLine.Trim();
		if (trimmed.Length == 0) return ("", "");

		if (trimmed[0] == '"')
		{
			int closing = trimmed.IndexOf('"', 1);
			if (closing > 0)
			{
				return (trimmed[1..closing], trimmed[(closing + 1)..].TrimStart());
			}
		}

		int space = trimmed.IndexOf(' ', StringComparison.Ordinal);
		return space < 0 ? (trimmed, "") : (trimmed[..space], trimmed[(space + 1)..].TrimStart());
	}
}
