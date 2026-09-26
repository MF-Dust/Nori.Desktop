using System.Text.Json;
using Nori.Core.Tools;
using Nori.Desktop.Bridge;
using Nori.Desktop.Windows;

namespace Nori.Desktop.Tests;

public partial class BridgeCommandsTests
{
	// ---- 具名任务 ----

	[Fact]
	public async Task 配了任务之后runTask才出现()
	{
		string folder = Path.Combine(_tempDir, "工作区");
		Directory.CreateDirectory(folder);
		BridgeCommands commands = CreateCommands();
		FakeBridgeSource main = new(WindowLabels.Main);
		await commands.InvokeAsync(main, "settings_update_workspace", Args(new { root = folder }));

		// 工作目录有了但一条任务也没配，此时不该有 runTask。
		Assert.Null(_runtime.Tools.Get(TaskTools.RunTaskName));

		await commands.InvokeAsync(
			main,
			"settings_update_tasks",
			Args(new { tasks = new[] { new { name = "构建", command = "dotnet build" } } }));

		Assert.NotNull(_runtime.Tools.Get(TaskTools.RunTaskName));
		Assert.Contains("dotnet build", _runtime.Tools.Get(TaskTools.RunTaskName)!.Description, StringComparison.Ordinal);
	}

	/// <summary>清空清单之后工具必须真的消失，而不是留着持有旧清单的注册项。</summary>
	[Fact]
	public async Task 清空任务清单之后runTask消失()
	{
		string folder = Path.Combine(_tempDir, "工作区");
		Directory.CreateDirectory(folder);
		BridgeCommands commands = CreateCommands();
		FakeBridgeSource main = new(WindowLabels.Main);
		await commands.InvokeAsync(main, "settings_update_workspace", Args(new { root = folder }));
		await commands.InvokeAsync(
			main, "settings_update_tasks", Args(new { tasks = new[] { new { name = "a", command = "echo hi" } } }));
		Assert.NotNull(_runtime.Tools.Get(TaskTools.RunTaskName));

		await commands.InvokeAsync(main, "settings_update_tasks", Args(new { tasks = Array.Empty<object>() }));

		Assert.Null(_runtime.Tools.Get(TaskTools.RunTaskName));
	}

	/// <summary>
	/// 重名当场拒绝。
	///
	/// 读取侧对非法条目是跳过的，写入侧不拦的话用户会看到保存成功而其中一条静默消失。
	/// </summary>
	[Fact]
	public async Task 重复的任务名被就地拒绝()
	{
		string folder = Path.Combine(_tempDir, "工作区");
		Directory.CreateDirectory(folder);
		BridgeCommands commands = CreateCommands();
		FakeBridgeSource main = new(WindowLabels.Main);
		await commands.InvokeAsync(main, "settings_update_workspace", Args(new { root = folder }));

		InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
			commands.InvokeAsync(
				main,
				"settings_update_tasks",
				Args(new
				{
					tasks = new[]
					{
						new { name = "a", command = "echo 1" },
						new { name = "A", command = "echo 2" },
					},
				})));

		Assert.Contains("重复", error.Message, StringComparison.Ordinal);
		Assert.Null(_runtime.Tools.Get(TaskTools.RunTaskName));
	}

	/// <summary>
	/// 换工作目录必须释放旧目录上的授权。
	///
	/// 这是回归测试。原实现在类文档里写了「必须在工作目录变更时调用」，却没有任何生产调用点 ——
	/// 用户换一个目录，旧目录上的 ACE 就永久残留，他看不见也无从清理。
	/// </summary>
	[Fact]
	public async Task 换工作目录会释放旧目录上的授权()
	{
		string first = Path.Combine(_tempDir, "旧工作区");
		string second = Path.Combine(_tempDir, "新工作区");
		Directory.CreateDirectory(first);
		Directory.CreateDirectory(second);
		BridgeCommands commands = CreateCommands();
		FakeBridgeSource main = new(WindowLabels.Main);

		await commands.InvokeAsync(main, "settings_update_workspace", Args(new { root = first }));
		Assert.Empty(_sandbox.Released);

		await commands.InvokeAsync(main, "settings_update_workspace", Args(new { root = second }));

		Assert.Contains(first, _sandbox.Released);
		Assert.DoesNotContain(second, _sandbox.Released);
	}

	[Fact]
	public async Task 清空工作目录会释放授权()
	{
		string folder = Path.Combine(_tempDir, "工作区");
		Directory.CreateDirectory(folder);
		BridgeCommands commands = CreateCommands();
		FakeBridgeSource main = new(WindowLabels.Main);
		await commands.InvokeAsync(main, "settings_update_workspace", Args(new { root = folder }));

		await commands.InvokeAsync(main, "settings_update_workspace", Args(new { root = "" }));

		Assert.Contains(folder, _sandbox.Released);
	}

	/// <summary>仍在用的路径不该被释放：撤了立刻还要加回来，反复增删 ACE 只会放大出错面。</summary>
	[Fact]
	public async Task 改任务清单不会释放仍在用的工作目录()
	{
		string folder = Path.Combine(_tempDir, "工作区");
		Directory.CreateDirectory(folder);
		BridgeCommands commands = CreateCommands();
		FakeBridgeSource main = new(WindowLabels.Main);
		await commands.InvokeAsync(main, "settings_update_workspace", Args(new { root = folder }));

		await commands.InvokeAsync(
			main, "settings_update_tasks", Args(new { tasks = new[] { new { name = "a", command = "echo hi" } } }));
		await commands.InvokeAsync(main, "settings_update_tasks", Args(new { tasks = Array.Empty<object>() }));

		Assert.DoesNotContain(folder, _sandbox.Released);
	}

	[Fact]
	public async Task 授权面包含工作目录与任务的可执行文件目录()
	{
		string folder = Path.Combine(_tempDir, "工作区");
		Directory.CreateDirectory(folder);
		await CreateCommands().InvokeAsync(
			new FakeBridgeSource(WindowLabels.Main), "settings_update_workspace", Args(new { root = folder }));

		Assert.Equal([folder], _runtime.CurrentGrantPaths());
	}

	/// <summary>
	/// 快照必须在启动器建立之前就报出本平台的隔离强度。
	///
	/// 界面要在用户配置命令**之前**说清「命令会跑在什么边界里」—— 无隔离时命令拥有用户的全部
	/// 权限，与 AppContainer 下「只能读写工作文件夹、默认不联网」是两回事。报 unknown 等于没报。
	/// </summary>
	[Fact]
	public void 快照报出执行边界()
	{
		JsonElement snapshot = JsonSerializer.SerializeToElement(_runtime.BuildSnapshot(), BridgeJson.Options);

		// 测试注入的是无隔离实现，快照应当如实反映，而不是报平台的预期值。
		Assert.Equal("none", snapshot.GetProperty("workspace").GetProperty("isolation").GetString());
	}

	[Fact]
	public async Task 快照把任务清单报给界面()
	{
		string folder = Path.Combine(_tempDir, "工作区");
		Directory.CreateDirectory(folder);
		BridgeCommands commands = CreateCommands();
		FakeBridgeSource main = new(WindowLabels.Main);
		await commands.InvokeAsync(main, "settings_update_workspace", Args(new { root = folder }));
		await commands.InvokeAsync(
			main,
			"settings_update_tasks",
			Args(new { tasks = new[] { new { name = "构建", command = "dotnet build" } } }));

		JsonElement snapshot = JsonSerializer.SerializeToElement(_runtime.BuildSnapshot(), BridgeJson.Options);
		JsonElement tasks = snapshot.GetProperty("workspace").GetProperty("tasks");

		Assert.Equal(1, tasks.GetArrayLength());
		Assert.Equal("构建", tasks[0].GetProperty("name").GetString());
		Assert.Equal("dotnet build", tasks[0].GetProperty("command").GetString());
	}
}
