using System.Text.Json;
using System.Text.Json.Nodes;
using Nori.Core.Configuration;
using Nori.Core.Sandbox;
using Nori.Core.Tools;

namespace Nori.Core.Tests;

/// <summary>
/// 具名任务执行。
///
/// 这一族的核心判据是**模型不参与命令行的构造**：它只能按名触发用户配好的任务。这条成立时，
/// 一次提示注入最多只能触发一条用户已经配置并授权过的命令；不成立就等于任意执行。
/// </summary>
public sealed class TaskToolsTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), $"nori-task-{Guid.NewGuid():N}");

	public TaskToolsTests() => Directory.CreateDirectory(_root);

	public void Dispose()
	{
		try { Directory.Delete(_root, recursive: true); } catch (IOException) { /* 清理失败不影响断言 */ }
	}

	/// <summary>记录收到的命令行，不真的执行。</summary>
	private sealed class RecordingLauncher : ISandboxLauncher
	{
		public List<string> Commands { get; } = [];

		public SandboxPolicy? LastPolicy { get; private set; }

		public SandboxIsolation Isolation { get; init; } = SandboxIsolation.AppContainer;

		public List<SandboxPolicy> Released { get; } = [];

		public string Describe() => "测试用";

		public void Release(SandboxPolicy policy) => Released.Add(policy);

		public Task<SandboxResult> RunAsync(string commandLine, SandboxPolicy policy, CancellationToken cancellationToken)
		{
			Commands.Add(commandLine);
			LastPolicy = policy;
			return Task.FromResult(new SandboxResult
			{
				ExitCode = 0,
				Output = "构建成功",
				TimedOut = false,
				Truncated = false,
			});
		}
	}

	private static readonly IReadOnlyList<WorkspaceTask> TwoTasks =
	[
		new WorkspaceTask { Name = "构建", Command = "dotnet build" },
		new WorkspaceTask { Name = "测试", Command = "dotnet test" },
	];

	private ToolRegistry Registry(
		IReadOnlyList<WorkspaceTask>? tasks = null, ISandboxLauncher? launcher = null, bool configured = true)
	{
		ToolRegistry registry = new();
		TaskTools.RegisterAll(
			registry,
			new WorkspaceAccess(configured ? _root : ""),
			tasks ?? TwoTasks,
			launcher ?? new RecordingLauncher());
		return registry;
	}

	private static async Task<JsonElement> CallAsync(ToolRegistry registry, object args)
	{
		ToolResult result = await registry.ExecuteAsync(
			TaskTools.RunTaskName,
			JsonNode.Parse(JsonSerializer.Serialize(args)),
			new ToolContext { Approve = _ => Task.FromResult(true) });
		Assert.True(result.IsSuccess, result.Error);
		return JsonSerializer.SerializeToElement(result.Result);
	}

	private static async Task<string> FailAsync(ToolRegistry registry, object args)
	{
		ToolResult result = await registry.ExecuteAsync(
			TaskTools.RunTaskName,
			JsonNode.Parse(JsonSerializer.Serialize(args)),
			new ToolContext { Approve = _ => Task.FromResult(true) });
		Assert.False(result.IsSuccess);
		return result.Error ?? "";
	}

	// ---- 注册 ----

	[Fact]
	public void 没配工作目录时不注册()
	{
		Assert.Null(Registry(configured: false).Get(TaskTools.RunTaskName));
	}

	/// <summary>一条任务都没配时不注册：暴露一件必定失败的工具会让模型反复重试。</summary>
	[Fact]
	public void 一条任务都没有时不注册()
	{
		Assert.Null(Registry(tasks: []).Get(TaskTools.RunTaskName));
	}

	[Fact]
	public void 执行任务需要逐次确认()
	{
		Assert.Equal("confirm", Registry().Get(TaskTools.RunTaskName)!.PermissionLevel);
	}

	/// <summary>
	/// 工具描述里必须带上完整命令。
	///
	/// 确认对话框展示的是工具描述与参数，而参数只有任务名。不列命令的话用户看到的是
	/// 「要运行 构建 吗」，无从判断那会执行什么 —— 确认就退化成了走过场。
	/// </summary>
	[Fact]
	public void 工具描述里带出完整命令()
	{
		string description = Registry().Get(TaskTools.RunTaskName)!.Description;

		Assert.Contains("dotnet build", description, StringComparison.Ordinal);
		Assert.Contains("dotnet test", description, StringComparison.Ordinal);
	}

	/// <summary>参数用枚举限定取值，让模型在调用前就知道有哪些任务。</summary>
	[Fact]
	public void 参数用枚举限定任务名()
	{
		JsonNode parameters = Registry().Get(TaskTools.RunTaskName)!.Parameters;
		JsonArray options = parameters["properties"]!["name"]!["enum"]!.AsArray();

		Assert.Equal(["构建", "测试"], options.Select(node => node!.GetValue<string>()).ToArray());
	}

	[Fact]
	public void 改了任务清单之后重新注册会换掉旧的()
	{
		ToolRegistry registry = Registry();
		TaskTools.RegisterAll(
			registry,
			new WorkspaceAccess(_root),
			[new WorkspaceTask { Name = "只剩这个", Command = "echo hi" }],
			new RecordingLauncher());

		Assert.Contains("只剩这个", registry.Get(TaskTools.RunTaskName)!.Description, StringComparison.Ordinal);
		Assert.DoesNotContain("dotnet build", registry.Get(TaskTools.RunTaskName)!.Description, StringComparison.Ordinal);
	}

	// ---- 执行 ----

	[Fact]
	public async Task 按名触发配好的命令()
	{
		RecordingLauncher launcher = new();

		JsonElement result = await CallAsync(Registry(launcher: launcher), new { name = "构建" });

		Assert.Equal(["dotnet build"], launcher.Commands);
		Assert.Equal("构建", result.GetProperty("task").GetString());
		Assert.Equal("dotnet build", result.GetProperty("command").GetString());
		Assert.Equal(0, result.GetProperty("exitCode").GetInt32());
		Assert.Equal("构建成功", result.GetProperty("output").GetString());
	}

	/// <summary>
	/// 未配置的名字一律拒绝，且不得落到执行侧。
	///
	/// 这是整条功能的安全判据：模型给出任何不在清单里的东西都必须停在这里。
	/// </summary>
	[Fact]
	public async Task 清单之外的任务名被拒绝且不执行()
	{
		RecordingLauncher launcher = new();

		string error = await FailAsync(Registry(launcher: launcher), new { name = "rm -rf /" });

		Assert.Empty(launcher.Commands);
		Assert.Contains("没有这条任务", error, StringComparison.Ordinal);
		Assert.Contains("构建", error, StringComparison.Ordinal);
	}

	[Fact]
	public async Task 空的任务名被拒绝()
	{
		Assert.Contains("不能为空", await FailAsync(Registry(), new { name = "  " }), StringComparison.Ordinal);
	}

	[Fact]
	public async Task 执行钉在工作目录且默认不给网络()
	{
		RecordingLauncher launcher = new();

		await CallAsync(Registry(launcher: launcher), new { name = "测试" });

		Assert.Equal(_root, launcher.LastPolicy!.WorkspaceRoot);
		Assert.False(launcher.LastPolicy.AllowNetwork);
	}

	/// <summary>
	/// 隔离强度要如实报给模型。
	///
	/// 它据此决定要不要在回复里提醒用户 —— 无隔离时命令拥有当前用户的全部权限，
	/// 把两种情形说成一样是误导。
	/// </summary>
	[Fact]
	public async Task 隔离强度如实报给模型()
	{
		JsonElement isolated = await CallAsync(
			Registry(launcher: new RecordingLauncher { Isolation = SandboxIsolation.AppContainer }), new { name = "构建" });
		JsonElement plain = await CallAsync(
			Registry(launcher: new RecordingLauncher { Isolation = SandboxIsolation.None }), new { name = "构建" });

		Assert.True(isolated.GetProperty("isolated").GetBoolean());
		Assert.False(plain.GetProperty("isolated").GetBoolean());
	}

	// ---- 配置存取 ----

	/// <summary>
	/// 任务清单必须按 <see cref="ConfigValue.Json"/> 存取。
	///
	/// 存成 Text 时配置层会把内容形如 JSON 容器的文本识别成 Json 值，而 <c>AsStringOr</c> 没有
	/// Json 分支，<c>GetStringOr</c> 读回来的是 fallback —— 表现为保存成功、界面刷新后却是空的。
	/// 同一个坑在本仓库已经出现过三次（luolicore_enabled、工具轮数、这里），单独立一条。
	/// </summary>
	[Fact]
	public void 任务清单按Json存取而不是Text()
	{
		ConfigValue written = WorkspaceTaskList.Write(TwoTasks);

		ConfigValue.Json json = Assert.IsType<ConfigValue.Json>(written);
		Assert.Equal(2, WorkspaceTaskList.Read(written).Count);

		// 旧的 Text 值仍要读得出来，否则升级会静默丢配置。
		Assert.Equal(2, WorkspaceTaskList.Read(new ConfigValue.Text(json.Value.ToJsonString())).Count);
	}

	[Fact]
	public void 读取时跳过非法条目而不是整体失败()
	{
		IReadOnlyList<WorkspaceTask> tasks = WorkspaceTaskList.Read(
			"""[{"name":"好的","command":"echo 1"},{"name":""},{"command":"没有名字"},"不是对象"]""");

		Assert.Single(tasks);
		Assert.Equal("好的", tasks[0].Name);
	}

	[Fact]
	public void 损坏的配置读成空清单()
	{
		Assert.Empty(WorkspaceTaskList.Read("{ 这不是 json"));
		Assert.Empty(WorkspaceTaskList.Read((string?)null));
		Assert.Empty(WorkspaceTaskList.Read((ConfigValue?)null));
	}

	[Fact]
	public void 读取时按不区分大小写去重()
	{
		Assert.Single(WorkspaceTaskList.Read("""[{"name":"a","command":"1"},{"name":"A","command":"2"}]"""));
	}

	[Theory]
	[InlineData("", "echo hi", "不能为空")]
	[InlineData("构建", "", "不能为空")]
	public void 写入前校验拒绝空值(string name, string command, string expected)
	{
		InvalidOperationException error = Assert.Throws<InvalidOperationException>(
			() => WorkspaceTaskList.Validate([new WorkspaceTask { Name = name, Command = command }]));

		Assert.Contains(expected, error.Message, StringComparison.Ordinal);
	}

	[Fact]
	public void 写入前校验拒绝重名()
	{
		InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
			WorkspaceTaskList.Validate(
			[
				new WorkspaceTask { Name = "a", Command = "1" },
				new WorkspaceTask { Name = "A", Command = "2" },
			]));

		Assert.Contains("重复", error.Message, StringComparison.Ordinal);
	}

	[Fact]
	public void 写入前校验拒绝超出条数上限()
	{
		InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
			WorkspaceTaskList.Validate(Enumerable.Range(0, WorkspaceTaskList.MaxTasks + 1)
				.Select(index => new WorkspaceTask { Name = $"t{index}", Command = "echo" })));

		Assert.Contains("最多", error.Message, StringComparison.Ordinal);
	}

	// ---- 只读路径推导 ----

	/// <summary>
	/// 系统目录一律排除：`C:\Windows` 下的程序对 AppContainer 本就可读，
	/// 而去改 System32 的 ACL 既无必要也不该做。
	/// </summary>
	[Fact]
	public void 系统目录不进只读授权()
	{
		if (!OperatingSystem.IsWindows()) return;

		Assert.Empty(TaskTools.ExecutableDirectories(@"C:\Windows\System32\cmd.exe /c echo hi"));
	}

	[Fact]
	public void 解析不出可执行文件时不授权()
	{
		Assert.Empty(TaskTools.ExecutableDirectories("这个命令并不存在-zzz --version"));
	}

	/// <summary>工具链目录要自动推出来：用户不一定知道 SDK 装在哪，填错的表现是构建莫名失败。</summary>
	[Fact]
	public void 可执行文件所在目录进只读授权()
	{
		string directory = Path.Combine(_root, "toolchain");
		Directory.CreateDirectory(directory);
		string executable = Path.Combine(directory, OperatingSystem.IsWindows() ? "faketool.exe" : "faketool");
		File.WriteAllText(executable, "");

		Assert.Equal([directory], TaskTools.ExecutableDirectories($"\"{executable}\" build"));
	}
}
