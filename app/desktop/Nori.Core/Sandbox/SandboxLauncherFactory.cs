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
	/// <summary>
	/// 本平台预期会用的隔离强度。纯平台判断，不建立任何东西。
	///
	/// 界面要在用户配置命令之前就说清「命令会跑在什么边界里」，而那时启动器还没建。真正建起来
	/// 之后以实例上报的为准 —— AppContainer 建不起来时会退回无隔离，两者可能不一致。
	/// </summary>
	public static SandboxIsolation PlannedIsolation =>
		OperatingSystem.IsWindows() ? SandboxIsolation.AppContainer : SandboxIsolation.None;

	/// <summary>
	/// 建立一个**不创建任何持久状态**的启动器，专供释放授权使用。
	///
	/// <see cref="Create"/> 会顺手把容器配置文件建出来，而清理路径上那是反效果 —— 为了删掉
	/// 残留反而新增一份残留。释放只需要容器 SID，那是容器名的确定性推导，不依赖配置文件。
	/// </summary>
	public static ISandboxLauncher CreateForRelease(string? containerName = null) =>
		OperatingSystem.IsWindows()
			? new AppContainerLauncher(containerName ?? DefaultContainerName)
			: new UnsandboxedLauncher();

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
