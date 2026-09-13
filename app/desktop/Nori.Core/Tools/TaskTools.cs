using System.Text;
using System.Text.Json.Nodes;
using Nori.Core.Sandbox;
using static Nori.Core.Tools.ToolProperty;
using static Nori.Core.Tools.ToolRegistration;

namespace Nori.Core.Tools;

/// <summary>
/// 具名任务执行工具。
///
/// 补上「改完不能自验」这个缺口：此前模型能读能改，但跑不了构建与测试，改动对不对只能
/// 交给用户去跑。
///
/// **模型不参与命令行的构造**，只能按名触发用户配好的任务。这是这一族的核心约束，也是
/// 它与通用 shell 的分界 —— 通用 shell 需要沙箱才能谈安全，而按名触发时一次提示注入最多
/// 只能触发用户已经配置并授权过的任务。
///
/// 执行经由 <see cref="ISandboxLauncher"/>，隔离强度随平台变化，由结果里的 `isolated`
/// 字段如实报告给模型。
/// </summary>
public static class TaskTools
{
	/// <summary>本组工具名。</summary>
	public const string RunTaskName = "runTask";

	/// <summary>单次执行的时间上限。</summary>
	public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(5);

	/// <summary>
	/// 注册本组工具，注册前先注销。
	///
	/// 未配置工作目录、或一条任务都没配时整组不注册：暴露一件必定失败的工具会导致模型反复重试。
	/// </summary>
	public static void RegisterAll(
		ToolRegistry registry,
		WorkspaceAccess workspace,
		IReadOnlyList<WorkspaceTask> tasks,
		ISandboxLauncher launcher)
	{
		ArgumentNullException.ThrowIfNull(registry);
		ArgumentNullException.ThrowIfNull(workspace);
		ArgumentNullException.ThrowIfNull(tasks);
		ArgumentNullException.ThrowIfNull(launcher);

		registry.Unregister(RunTaskName);
		if (!workspace.IsConfigured || tasks.Count == 0) return;

		Register(
			registry,
			RunTaskName,
			Describe(tasks),
			"confirm",
			Schema(Choice("name", "任务名，必须是已配置的其中之一", [.. tasks.Select(task => task.Name)])),
			(args, context) => RunAsync(workspace, tasks, launcher, args, context.CancellationToken));
	}

	/// <summary>
	/// 工具描述里列出每条任务的名字与**完整命令**。
	///
	/// 命令必须出现在描述里：确认对话框展示的是工具描述与参数，而参数只有任务名。不列命令
	/// 的话用户看到的是「要运行 构建 吗」，无从判断那到底会执行什么。
	/// </summary>
	private static string Describe(IReadOnlyList<WorkspaceTask> tasks)
	{
		StringBuilder text = new("在工作目录下运行一条已配置的任务，返回退出码与输出。可用的任务：");
		foreach (WorkspaceTask task in tasks)
		{
			text.Append("\n- ").Append(task.Name).Append(" → ").Append(task.Command);
		}

		text.Append("\n只能运行上面列出的任务，不能自行拼接命令。");
		return ToolLimits.CapText(text.ToString(), ToolLimits.MaxDescriptionCharacters);
	}

	private static async Task<object?> RunAsync(
		WorkspaceAccess workspace,
		IReadOnlyList<WorkspaceTask> tasks,
		ISandboxLauncher launcher,
		JsonNode? args,
		CancellationToken cancellationToken)
	{
		string requested = (args?["name"]?.GetValue<string>() ?? "").Trim();
		if (requested.Length == 0) throw new InvalidOperationException("name 不能为空");

		WorkspaceTask task = tasks.FirstOrDefault(
			candidate => string.Equals(candidate.Name, requested, StringComparison.OrdinalIgnoreCase))
			?? throw new InvalidOperationException(
				$"没有这条任务: {requested}。可用的是 {string.Join("、", tasks.Select(entry => entry.Name))}");

		SandboxPolicy policy = new()
		{
			WorkspaceRoot = workspace.Root,
			ReadOnlyPaths = ExecutableDirectories(task.Command),
			AllowNetwork = false,
			Timeout = Timeout,
		};

		SandboxResult result = await launcher.RunAsync(task.Command, policy, cancellationToken).ConfigureAwait(false);
		return new
		{
			task = task.Name,
			command = task.Command,
			exitCode = result.ExitCode,
			output = result.Output,
			timedOut = result.TimedOut,
			truncated = result.Truncated,
			isolated = launcher.Isolation != SandboxIsolation.None,
		};
	}

	/// <summary>
	/// 推导命令需要只读访问的目录：可执行文件所在的那一个。
	///
	/// 自动推导而不是让用户手填：`dotnet build` 需要读 SDK 安装目录，而用户不一定知道它在哪，
	/// 填错的表现是构建在容器里莫名失败。
	///
	/// **系统目录一律排除**。`C:\Windows` 下的程序对 AppContainer 本就可读，而去改 System32
	/// 的 ACL 既无必要也不该做。
	/// </summary>
	public static IReadOnlyList<string> ExecutableDirectories(string commandLine)
	{
		string executable = Locate(CommandLine.Split(commandLine).FileName);
		if (executable.Length == 0) return [];

		string? directory = Path.GetDirectoryName(executable);
		if (directory is null || directory.Length == 0) return [];
		if (IsSystemDirectory(directory)) return [];

		return [directory];
	}

	private static bool IsSystemDirectory(string directory)
	{
		string system = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
		if (system.Length == 0) return false;

		StringComparison comparison = OperatingSystem.IsWindows()
			? StringComparison.OrdinalIgnoreCase
			: StringComparison.Ordinal;
		return directory.StartsWith(system, comparison);
	}

	/// <summary>按 PATH 解析可执行文件的完整路径；解析不出时返回空串。</summary>
	private static string Locate(string fileName)
	{
		if (fileName.Length == 0) return "";
		if (Path.IsPathRooted(fileName)) return File.Exists(fileName) ? Path.GetFullPath(fileName) : "";

		string[] extensions = OperatingSystem.IsWindows()
			? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT").Split(
				';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
			: [""];

		foreach (string directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(
			Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
		{
			foreach (string extension in Path.HasExtension(fileName) ? [""] : extensions)
			{
				string candidate;
				try
				{
					candidate = Path.Combine(directory, fileName + extension);
				}
				catch (ArgumentException)
				{
					continue; // PATH 里的非法项跳过
				}

				if (File.Exists(candidate)) return Path.GetFullPath(candidate);
			}
		}

		return "";
	}
}
