using System.Diagnostics;
using System.Globalization;

namespace Nori.AppLauncher;

/// <summary>启动画面随部署槽提供；缺失或失败不会阻断稳定根入口。</summary>
internal sealed class SplashProcess(Process process) : IDisposable
{
	internal static SplashProcess? TryStart(string desktopEntrypoint, string pipe, Process child, List<string> arguments)
	{
		if (arguments.Any(argument => argument.StartsWith("--smoke-test", StringComparison.Ordinal))) return null;
		try
		{
			string executable = Path.Combine(Path.GetDirectoryName(desktopEntrypoint)!,
				OperatingSystem.IsWindows() ? "Nori.SplashHost.exe" : "Nori.SplashHost");
			if (!File.Exists(executable) || (File.GetAttributes(executable) & FileAttributes.ReparsePoint) != 0) return null;
			if (!OperatingSystem.IsWindows())
				File.SetUnixFileMode(executable, File.GetUnixFileMode(executable) | UnixFileMode.UserExecute);
			ProcessStartInfo info = new(executable) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(executable)! };
			info.ArgumentList.Add(pipe);
			info.ArgumentList.Add(child.Id.ToString(CultureInfo.InvariantCulture));
			info.ArgumentList.Add(child.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture));
			info.Environment.Remove("NORI_SPLASH_PIPE");
			// 固定槽内的本地可执行文件，不经过 shell，管道名与 PID 只作为独立参数传递。
			Process? splash = Process.Start(info); // nosemgrep
			return splash is null ? null : new SplashProcess(splash);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException)
		{
			Console.Error.WriteLine($"启动画面不可用：{exception.GetType().Name}");
			return null;
		}
	}

	public void Dispose()
	{
		try { if (!process.HasExited) process.Kill(); }
		catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
		{
			// 展示进程可能已自行退出，不能覆盖桌面宿主的退出码。
		}
		finally { process.Dispose(); }
	}
}
