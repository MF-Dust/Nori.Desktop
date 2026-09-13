namespace Nori.Core.Sandbox;

/// <summary>
/// 一次受限执行的约束描述。
///
/// 这一层只描述「要什么约束」，不描述「怎么实现」。各平台的实现能满足多少由
/// <see cref="ISandboxLauncher.Isolation"/> 报告，调用方据此决定是否还需要用户确认。
/// </summary>
public sealed record SandboxPolicy
{
	/// <summary>工作目录的绝对路径。进程的读写范围，同时也是它的初始当前目录。</summary>
	public required string WorkspaceRoot { get; init; }

	/// <summary>额外的只读路径，通常是工具链安装目录。</summary>
	public IReadOnlyList<string> ReadOnlyPaths { get; init; } = [];

	/// <summary>是否允许出站网络。</summary>
	public bool AllowNetwork { get; init; }

	/// <summary>超时上限。到时结束整棵进程树并把 <see cref="SandboxResult.TimedOut"/> 置位。</summary>
	public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(5);

	/// <summary>追加到子进程的环境变量，覆盖同名继承项。</summary>
	public IReadOnlyDictionary<string, string> EnvironmentOverrides { get; init; } =
		new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

	/// <summary>输出字符上限。超出时保留首尾两段，中间截掉。</summary>
	public int MaxOutputCharacters { get; init; } = 24_000;
}

/// <summary>受限执行的结果。</summary>
public sealed record SandboxResult
{
	/// <summary>被启动进程的退出码。超时被结束时为 -1。</summary>
	public required int ExitCode { get; init; }

	/// <summary>合并后的标准输出与标准错误。</summary>
	public required string Output { get; init; }

	/// <summary>是否因超时被结束。</summary>
	public required bool TimedOut { get; init; }

	/// <summary>输出是否被截断。</summary>
	public required bool Truncated { get; init; }
}

/// <summary>
/// 实际达到的隔离强度。
///
/// 调用方必须能区分这两档：<see cref="None"/> 时进程拥有当前用户的全部权限，
/// 把它当成受限执行来放宽确认要求是错的。
/// </summary>
public enum SandboxIsolation
{
	/// <summary>没有隔离。进程以当前用户身份运行，仅有工作目录与超时约束。</summary>
	None,

	/// <summary>Windows AppContainer。文件按 ACL 授权，网络按 capability 授权。</summary>
	AppContainer,
}

/// <summary>受限执行的启动器。</summary>
public interface ISandboxLauncher
{
	/// <summary>本实现实际达到的隔离强度。</summary>
	SandboxIsolation Isolation { get; }

	/// <summary>给用户看的一句话说明，用于确认对话框。</summary>
	string Describe();

	/// <summary>按给定约束执行一条命令行。</summary>
	Task<SandboxResult> RunAsync(string commandLine, SandboxPolicy policy, CancellationToken cancellationToken);

	/// <summary>
	/// 释放此前为该约束授予的**持久**权限。
	///
	/// 放在接口上而不是某个实现上，是因为调用方无法、也不应该判断某个平台的授权是不是持久的。
	/// Windows 的 AppContainer 把授权写进文件系统 ACL，不释放就会残留在用户目录上；Landlock
	/// 与 Seatbelt 是进程级规则集，进程一退就没了，实现为空操作即可。
	///
	/// 必须可重复调用、且对从未授权过的路径安全。
	/// </summary>
	void Release(SandboxPolicy policy);
}
