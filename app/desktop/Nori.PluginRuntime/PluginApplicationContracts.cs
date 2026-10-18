namespace Nori.PluginRuntime;

/// <summary>查询、启动应用并等待其退出的宿主能力（API 2.4）；不提供终止进程或凭据访问。</summary>
[PluginCapability(PluginCapabilityIds.Applications)]
public interface IPluginApplicationCapability : IPluginCapability
{
	/// <summary>获取运行中的进程 ID 与名称快照，不包含命令行、路径或环境变量。</summary>
	Task<IReadOnlyList<PluginApplicationInfo>> GetRunningAsync(CancellationToken cancellationToken = default);

	/// <summary>直接启动指定可执行文件并返回进程 ID，不经由系统外壳解释参数，也不接管进程所有权。</summary>
	Task<int> LaunchAsync(PluginApplicationLaunchRequest request, CancellationToken cancellationToken = default);

	/// <summary>等待指定进程退出；正数 ID 对应的进程已不存在时直接完成。取消或插件停止只取消等待，不终止应用。</summary>
	Task WaitForExitAsync(int processId, CancellationToken cancellationToken = default);
}

/// <summary>运行中的应用进程信息。</summary>
/// <param name="ProcessId">进程 ID。</param>
/// <param name="Name">进程名称，不包含可执行文件路径或命令行。</param>
public sealed record PluginApplicationInfo(int ProcessId, string Name);

/// <summary>应用启动请求；路径必须为有效的绝对路径，参数按独立项原样传递。</summary>
public sealed record PluginApplicationLaunchRequest
{
	/// <summary>已存在的可执行文件绝对路径，不进行 PATH 查找或文件关联打开。</summary>
	public required string FileName { get; init; }

	/// <summary>独立的命令行参数；允许空字符串，不允许 null 或控制字符。</summary>
	public IReadOnlyList<string> Arguments { get; init; } = [];

	/// <summary>可选的已存在工作目录绝对路径；未指定时继承宿主工作目录。</summary>
	public string? WorkingDirectory { get; init; }
}
