using System.Diagnostics;

namespace Nori.Startup;

/// <summary>启动失败时尽量给图形会话可见提示；工具缺失或无显示服务器仍由调用方输出标准错误。</summary>
internal static class LinuxStartupError
{
	internal static bool TryShow(string title, string message)
	{
		if (!OperatingSystem.IsLinux()) return false;
		foreach (string tool in new[] { "zenity", "kdialog" })
		{
			try
			{
				using Process? process = Process.Start(CreateCommand(tool, title, message));
				if (process is not null && (!process.WaitForExit(250) || process.ExitCode == 0)) return true;
			}
			catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
			{
				// 未安装该桌面的对话框工具时继续尝试另一种，不隐藏原始启动错误。
			}
		}
		return false;
	}

	internal static ProcessStartInfo CreateCommand(string tool, string title, string message)
	{
		ProcessStartInfo start = new(tool) { UseShellExecute = false };
		start.ArgumentList.Add("--title");
		start.ArgumentList.Add(title);
		start.ArgumentList.Add("--error");
		if (tool == "zenity")
		{
			start.ArgumentList.Add("--no-markup");
			start.ArgumentList.Add("--text");
		}
		start.ArgumentList.Add(message);
		return start;
	}
}
