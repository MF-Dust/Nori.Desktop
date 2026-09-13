using Nori.Core.Sandbox.Windows;

namespace Nori.Core.Sandbox;

/// <summary>按当前平台挑选可用的执行方式。</summary>
public static class SandboxLauncherFactory
{
	/// <summary>
	/// 容器名。进入 <c>AppData\Local\Packages</c>，全应用共用一个。
	///
	/// 配置文件是持久的，用于承载容器的私有存储（重定向后的 TEMP 与 LOCALAPPDATA）。
	/// 测试须传入独立名称并自行删除，否则会在开发机上留下状态。
	/// </summary>
	public const string DefaultContainerName = "Nori.Desktop.Task";

	/// <summary>
	/// 建立启动器。
	///
	/// Windows 上优先 AppContainer，建立容器失败时退回无隔离执行而不是让整条功能不可用 ——
	/// 隔离强度由 <see cref="ISandboxLauncher.Isolation"/> 如实报告，调用方据此决定确认强度。
	/// 其余平台目前只有无隔离执行；Linux 的 Landlock 与 macOS 的 Seatbelt 在此处接入。
	/// </summary>
	public static ISandboxLauncher Create(bool preferIsolation = true, string? containerName = null)
	{
		if (!preferIsolation || !OperatingSystem.IsWindows()) return new UnsandboxedLauncher();

		try
		{
			AppContainerLauncher launcher = new(containerName ?? DefaultContainerName);
			launcher.EnsureReady();
			return launcher;
		}
		catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
		{
			// 组策略禁用 AppContainer、或容器配置文件损坏时走这里。
			return new UnsandboxedLauncher();
		}
	}
}
