using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Nori.AppLauncher;

/// <summary>Nori 稳定根入口：只选择并启动一个已提交的部署槽。</summary>
internal static class Program
{
	private const string WaitPidArgument = "--launcher-wait-pid";
	private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(30);

	[STAThread]
	private static int Main(string[] args)
	{
		try
		{
			WaitPid(args, out int? waitPid, out long? waitStartTicks, out List<string> forwarded);
			if (waitPid is not null) WaitForProcess(waitPid.Value, waitStartTicks);
			string packageRoot = ResolvePackageRoot();
			DeploymentSelection selection = DeploymentSelector.Select(packageRoot, RuntimeRid());
			EnsureExecutable(selection.Entrypoint);
			ProcessStartInfo startInfo = new(selection.Entrypoint)
			{
				WorkingDirectory = packageRoot,
				UseShellExecute = false,
			};
			foreach (string argument in forwarded) startInfo.ArgumentList.Add(argument);
			startInfo.Environment["NORI_PACKAGE_ROOT"] = packageRoot;
			startInfo.Environment["NORI_DEPLOYMENT_ROOT"] = selection.DeploymentRoot;
			startInfo.Environment["NORI_LAUNCHER_PATH"] = Environment.ProcessPath ?? selection.Entrypoint;
			startInfo.Environment["NORI_EXECUTABLE_PATH"] = selection.Entrypoint;
			using Process child = Process.Start(startInfo) ?? throw new InvalidOperationException("无法启动 Nori 宿主");
			child.WaitForExit();
			return child.ExitCode;
		}
		catch (Exception exception)
		{
			ShowPlatformError("Nori 启动失败", exception.Message);
			return 1;
		}
	}

	private static string ResolvePackageRoot()
	{
		string launcher = Environment.ProcessPath ?? throw new InvalidOperationException("无法确定启动器路径");
		string directory = Path.GetDirectoryName(Path.GetFullPath(launcher)) ?? throw new InvalidOperationException("无法确定包根目录");
		// macOS 的 launcher 位于 Nori.app/Contents/MacOS，槽目录在 bundle 外层。
		if (OperatingSystem.IsMacOS() && Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(directory)) ?? "").Equals("Nori.app", StringComparison.Ordinal))
		{
			DirectoryInfo contents = Directory.GetParent(directory) ?? throw new InvalidOperationException("无法确定 macOS bundle 路径");
			DirectoryInfo bundle = contents.Parent ?? throw new InvalidOperationException("无法确定 macOS bundle 路径");
			return (bundle.Parent ?? throw new InvalidOperationException("无法确定 macOS 分发目录")).FullName;
		}
		return directory;
	}

	private static string RuntimeRid()
	{
		string rid = System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier;
		if (rid.StartsWith("win-", StringComparison.Ordinal) || rid.StartsWith("linux-", StringComparison.Ordinal) || rid.StartsWith("osx-", StringComparison.Ordinal)) return rid;
		string os = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux";
		string architecture = RuntimeInformation.OSArchitecture switch
		{
			Architecture.X64 => "x64",
			Architecture.Arm64 => "arm64",
			_ => throw new InvalidOperationException("不支持的 CPU 架构"),
		};
		return $"{os}-{architecture}";
	}

	private static void WaitPid(string[] args, out int? waitPid, out long? waitStartTicks, out List<string> forwarded)
	{
		waitPid = null;
		waitStartTicks = null;
		forwarded = [];
		for (int index = 0; index < args.Length; index++)
		{
			if (args[index].Equals(WaitPidArgument, StringComparison.Ordinal))
			{
				if (waitPid is not null || index + 1 >= args.Length || !int.TryParse(args[++index], out int pid) || pid <= 0)
					throw new ArgumentException("--launcher-wait-pid 必须带一个正整数 PID 且只能指定一次");
				waitPid = pid;
				continue;
			}
			if (args[index].Equals("--launcher-wait-start-ticks", StringComparison.Ordinal))
			{
				if (waitStartTicks is not null || index + 1 >= args.Length || !long.TryParse(args[++index], out long ticks) || ticks <= 0)
					throw new ArgumentException("--launcher-wait-start-ticks 必须带一个正整数且只能指定一次");
				waitStartTicks = ticks;
				continue;
			}
			forwarded.Add(args[index]);
		}
		if (waitStartTicks is not null && waitPid is null) throw new ArgumentException("--launcher-wait-start-ticks 必须配合 --launcher-wait-pid");
		if (waitPid is not null && waitStartTicks is null) throw new ArgumentException("--launcher-wait-pid 必须同时带 --launcher-wait-start-ticks");
	}

	private static void WaitForProcess(int pid, long? expectedStartTicks)
	{
		try
		{
			using Process process = Process.GetProcessById(pid);
			if (expectedStartTicks is not null && process.StartTime.ToUniversalTime().Ticks != expectedStartTicks.Value)
				throw new InvalidOperationException("等待的旧进程身份已变化，拒绝启动新实例");
			if (!process.WaitForExit((int)WaitTimeout.TotalMilliseconds))
				throw new TimeoutException("等待旧 Nori 进程退出超时，拒绝启动新实例");
		}
		catch (ArgumentException)
		{
			// 旧宿主已经退出。
		}

	}

	private static void EnsureExecutable(string path)
	{
		if (OperatingSystem.IsWindows()) return;
		UnixFileMode mode = File.GetUnixFileMode(path);
		UnixFileMode execute = UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
		if ((mode & execute) != execute) File.SetUnixFileMode(path, mode | execute);
	}

	private static void ShowPlatformError(string title, string message)
	{
		if (OperatingSystem.IsWindows())
		{
			MessageBox(nint.Zero, message, title, 0x10);
			return;
		}
		if (OperatingSystem.IsMacOS())
		{
			try
			{
				ProcessStartInfo alert = new("osascript") { UseShellExecute = false };
				alert.ArgumentList.Add("-e");
				alert.ArgumentList.Add("display alert (system attribute \"NORI_ALERT_TITLE\") message (system attribute \"NORI_ALERT_MESSAGE\")");
				alert.Environment["NORI_ALERT_TITLE"] = title;
				alert.Environment["NORI_ALERT_MESSAGE"] = message;
				using Process process = Process.Start(alert)!;
				process.WaitForExit(5000);
				return;
			}
			catch { }
		}
		Console.Error.WriteLine($"{title}: {message}");
	}

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	private static extern int MessageBox(nint hWnd, string text, string caption, uint type);
}
