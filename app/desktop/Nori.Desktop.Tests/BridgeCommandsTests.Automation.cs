using System.Text.Json;
using Nori.Core.Automation;
using Nori.Core.Configuration;
using Nori.Core.Logging;
using Nori.Core.Mcp;
using Nori.Desktop.Automation;
using Nori.Desktop.Automation.Browser;
using Nori.Desktop.Automation.Desktop;
using Nori.Desktop.Automation.Windows;
using Nori.Core.Tools;
using Nori.Desktop.Bridge;
using Nori.Desktop.Settings;
using Nori.Desktop.Windows;

namespace Nori.Desktop.Tests;

public partial class BridgeCommandsTests
{
	[Fact]
	public async Task 设置页自动化命令映射桌面和浏览器开关()
	{
		BridgeCommands commands = CreateCommands();
		FakeBridgeSource main = new(WindowLabels.Main);

		await commands.InvokeAsync(main, "settings_update_automation", Args(new
		{
			enabled = true,
			desktopEnabled = true,
			browserEnabled = true,
		}));

		Assert.True(_config.GetBoolOr(ConfigStore.KeyAutomationEnabled, false));
		Assert.True(_config.GetBoolOr(ConfigStore.KeyAutomationAllowPointer, false));
		Assert.True(_config.GetBoolOr(ConfigStore.KeyAutomationAllowKeyboard, false));
		Assert.True(_config.GetBoolOr(ConfigStore.KeyAutomationAllowScroll, false));
		Assert.True(_config.GetBoolOr(ConfigStore.KeyAutomationBrowserEnabled, false));

		await commands.InvokeAsync(main, "settings_update_automation", Args(new {desktopEnabled = false}));

		Assert.False(_config.GetBoolOr(ConfigStore.KeyAutomationAllowPointer, true));
		Assert.False(_config.GetBoolOr(ConfigStore.KeyAutomationAllowKeyboard, true));
		Assert.False(_config.GetBoolOr(ConfigStore.KeyAutomationAllowScroll, true));
	}

	[Fact]
	public async Task 自动化默认关闭且快照不含敏感正文()
	{
		BridgeCommands commands = CreateCommands();
		await Assert.ThrowsAsync<InvalidOperationException>(() =>
			commands.InvokeAsync(new FakeBridgeSource(WindowLabels.Main), "automation_get_snapshot", Args(new { })));
		object? result = await commands.InvokeAsync(new NativeSettingsCommandSource(), "automation_get_snapshot", Args(new { }));
		string json = JsonSerializer.Serialize(result, BridgeJson.Options);

		Assert.Contains("\"enabled\":false", json, StringComparison.Ordinal);
		Assert.Contains("自动化默认关闭", json, StringComparison.Ordinal);
		Assert.DoesNotContain("screenshot", json, StringComparison.OrdinalIgnoreCase);
		Assert.DoesNotContain("prompt", json, StringComparison.OrdinalIgnoreCase);
		Assert.DoesNotContain("url", json, StringComparison.OrdinalIgnoreCase);
		Assert.DoesNotContain("tool", json, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public async Task 自动化状态变更只允许可见main且安全模式拒绝设置()
	{
		BridgeCommands commands = CreateCommands();
		await Assert.ThrowsAsync<InvalidOperationException>(() =>
			commands.InvokeAsync(new FakeBridgeSource(WindowLabels.Init), "automation_update_settings", Args(new {enabled = true})));
		await Assert.ThrowsAsync<InvalidOperationException>(() =>
			commands.InvokeAsync(new FakeBridgeSource(WindowLabels.Main, false), "automation_update_settings", Args(new {enabled = true})));

		using BridgeCommandsTests safeFixture = new(true, true);
		await Assert.ThrowsAsync<InvalidOperationException>(() =>
			safeFixture.CreateCommands().InvokeAsync(new FakeBridgeSource(WindowLabels.Main), "automation_update_settings", Args(new {enabled = true})));
	}

	[Fact]
	public void 桌面视觉在默认关闭安全模式和非Windows时拒绝()
	{
		using BridgeCommandsTests defaultFixture = new(false, true, automationVision: true);
		InvalidOperationException defaultError = Assert.Throws<InvalidOperationException>(() =>
			defaultFixture._services.Automation!.ListDesktopWindows());
		Assert.Contains("默认关闭", defaultError.Message, StringComparison.Ordinal);

		using BridgeCommandsTests safeFixture = new(true, true, automationVision: true);
		safeFixture.ConfigureDesktop();
		InvalidOperationException safeError = Assert.Throws<InvalidOperationException>(() =>
			safeFixture._services.Automation!.ListDesktopWindows());
		Assert.Contains("安全模式", safeError.Message, StringComparison.Ordinal);

		using BridgeCommandsTests linuxFixture = new(false, false, automationVision: true);
		linuxFixture.ConfigureDesktop();
		InvalidOperationException linuxError = Assert.Throws<InvalidOperationException>(() =>
			linuxFixture._services.Automation!.ListDesktopWindows());
		Assert.Contains("Windows", linuxError.Message, StringComparison.Ordinal);
	}

	[Fact]
	public async Task 桌面窗口列表只返回脱敏token和尺寸()
	{
		using BridgeCommandsTests fixture = new(
			false,
			true,
			automationVision: true,
			desktopVisionRunnerFactory: request => new FakeDesktopRunner(request.Progress),
			desktopVisionPlannerFactory: () => new FakeDesktopPlanner("{\"status\":\"completed\"}"),
			desktopVisionActionFactory: () => new FakeDesktopAction(),
			desktopVisionScreenshotFactory: () => new FakeDesktopScreenshot(),
			desktopVisionWindowCatalogFactory: () => new FakeDesktopWindowCatalog());
		fixture.ConfigureDesktop();

		string json = JsonSerializer.Serialize(fixture._services.Automation!.ListDesktopWindows(), BridgeJson.Options);

		Assert.Contains("\"width\":800", json, StringComparison.Ordinal);
		Assert.Contains("\"height\":600", json, StringComparison.Ordinal);
		Assert.Contains("\"isForeground\":true", json, StringComparison.Ordinal);
		Assert.DoesNotContain("窗口标题-secret", json, StringComparison.Ordinal);
		Assert.DoesNotContain("4321", json, StringComparison.Ordinal);
		Assert.DoesNotContain("4660", json, StringComparison.Ordinal);
		Assert.DoesNotContain("handle", json, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public async Task 桌面视觉成功任务进入任务管理器且公开状态不含输入()
	{
		FakeDesktopRunner? runner = null;
		using BridgeCommandsTests fixture = new(
			false,
			true,
			automationVision: true,
			desktopVisionRunnerFactory: request =>
			{
				runner = new FakeDesktopRunner(request.Progress);
				return runner;
			},
			desktopVisionPlannerFactory: () => new FakeDesktopPlanner("{\"status\":\"completed\"}"),
			desktopVisionActionFactory: () => new FakeDesktopAction(),
			desktopVisionScreenshotFactory: () => new FakeDesktopScreenshot(),
			desktopVisionWindowCatalogFactory: () => new FakeDesktopWindowCatalog());
		fixture.ConfigureDesktop();
		AutomationRuntime automation = fixture._services.Automation!;
		AutomationDesktopWindowSnapshot window = SingleWindow(automation.ListDesktopWindows());

		// 脱敏回归夹具，不是真实凭据。
		const string secretTask = "把窗口中的 secret-input 发送出去"; // nosemgrep
		AutomationDesktopTaskStartSnapshot start = automation.StartDesktopTask(secretTask, window.Token);
		await runner!.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
		await WaitUntilAsync(() => fixture._services.Automation!.GetSnapshot().Tasks.Any(item => item.Id == start.TaskId && item.State == AutomationTaskState.Completed));

		string json = JsonSerializer.Serialize(new
		{
			start,
			snapshot = fixture._services.Automation!.GetSnapshot(),
		}, BridgeJson.Options);
		Assert.DoesNotContain(secretTask, json, StringComparison.Ordinal);
		Assert.DoesNotContain("secret-input", json, StringComparison.Ordinal);
		Assert.Contains(start.TaskId.ToString(), json, StringComparison.Ordinal);
	}

	[Fact]
	public async Task 桌面视觉任务可取消且公开状态稳定()
	{
		FakeDesktopRunner? runner = null;
		using BridgeCommandsTests fixture = new(
			false,
			true,
			automationVision: true,
			desktopVisionRunnerFactory: request =>
			{
				runner = new FakeDesktopRunner(request.Progress, waitForRelease: true);
				return runner;
			},
			desktopVisionPlannerFactory: () => new FakeDesktopPlanner("{\"status\":\"completed\"}"),
			desktopVisionActionFactory: () => new FakeDesktopAction(),
			desktopVisionScreenshotFactory: () => new FakeDesktopScreenshot(),
			desktopVisionWindowCatalogFactory: () => new FakeDesktopWindowCatalog());
		fixture.ConfigureDesktop();
		AutomationRuntime automation = fixture._services.Automation!;
		AutomationDesktopWindowSnapshot window = SingleWindow(automation.ListDesktopWindows());
		AutomationDesktopTaskStartSnapshot start = automation.StartDesktopTask("可取消任务", window.Token);
		await runner!.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

		Assert.True(automation.StopDesktopTask(start.TaskId));
		await WaitUntilAsync(() => fixture._services.Automation!.GetSnapshot().Tasks.Any(item => item.Id == start.TaskId && item.State == AutomationTaskState.Cancelled));
	}

	[Fact]
	public async Task 高风险输入没有审批或审批拒绝时不会自动放行()
	{
		FakeDesktopAction action = new();
		using BridgeCommandsTests fixture = new(
			false,
			true,
			automationVision: true,
			desktopVisionRunnerFactory: request => new DesktopVisionAutomationRunner(
				request.TaskTitle, request.Goal, request.TargetWindow, request.ScreenshotSource, action,
				request.Planner, request.ApprovalCallback, request.Policy, progress: request.Progress),
			desktopVisionPlannerFactory: () => new FakeDesktopPlanner("{\"type\":\"type_text\",\"text\":\"do-not-send\"}"),
			desktopVisionActionFactory: () => action,
			desktopVisionScreenshotFactory: () => new FakeDesktopScreenshot(),
			desktopVisionWindowCatalogFactory: () => new FakeDesktopWindowCatalog(),
			desktopVisionApprovalCallback: (request, _) => Task.FromResult(
			AutomationApprovalDecision.Create(request, AutomationApprovalOutcome.Denied, DateTimeOffset.UtcNow)));
		fixture.ConfigureDesktop();
		fixture._config.Set(ConfigStore.KeyAutomationAllowKeyboard, new ConfigValue.Boolean(true));
		AutomationRuntime automation = fixture._services.Automation!;
		AutomationDesktopWindowSnapshot window = SingleWindow(automation.ListDesktopWindows());
		AutomationDesktopTaskStartSnapshot start = automation.StartDesktopTask("高风险输入", window.Token);

		await WaitUntilAsync(() => fixture._services.Automation!.GetSnapshot().Tasks.Any(item => item.Id == start.TaskId && item.ErrorCategory == "approval_denied"));
		Assert.Equal(0, action.Count);
	}

	[Fact]
	public async Task 非法规划和敏感执行上下文不会进入公开状态()
	{
		FakeDesktopAction action = new();
		using BridgeCommandsTests fixture = new(
			false,
			true,
			automationVision: true,
			desktopVisionRunnerFactory: request => new DesktopVisionAutomationRunner(
				request.TaskTitle, request.Goal, request.TargetWindow, request.ScreenshotSource, action,
				request.Planner, approvalCallback: null, request.Policy, progress: request.Progress),
			desktopVisionPlannerFactory: () => new FakeDesktopPlanner("{\"type\":\"click\",\"x\":10,\"y\":20,\"extra\":\"model-secret\"}"),
			desktopVisionActionFactory: () => action,
			desktopVisionScreenshotFactory: () => new FakeDesktopScreenshot(),
			desktopVisionWindowCatalogFactory: () => new FakeDesktopWindowCatalog());
		fixture.ConfigureDesktop();
		AutomationRuntime automation = fixture._services.Automation!;
		AutomationDesktopWindowSnapshot window = SingleWindow(automation.ListDesktopWindows());
		AutomationDesktopTaskStartSnapshot start = automation.StartDesktopTask("正文 secret-task", window.Token);

		await WaitUntilAsync(() => fixture._services.Automation!.GetSnapshot().Tasks.Any(item => item.Id == start.TaskId && item.ErrorCategory == "invalid_action"));
		string json = JsonSerializer.Serialize(fixture._services.Automation!.GetSnapshot(), BridgeJson.Options);
		Assert.DoesNotContain("secret-task", json, StringComparison.Ordinal);
		Assert.DoesNotContain("model-secret", json, StringComparison.Ordinal);
		Assert.DoesNotContain("type_text", json, StringComparison.Ordinal);
		Assert.Equal(0, action.Count);
	}

	[Fact]
	public async Task 非Windows自动化返回明确拒绝原因()
	{
		using BridgeCommandsTests fixture = new(false, false);
		BridgeCommands commands = fixture.CreateCommands();
		object? probe = await commands.InvokeAsync(new FakeBridgeSource(WindowLabels.Main), "automation_probe_vision", Args(new { }));
		string json = JsonSerializer.Serialize(probe, BridgeJson.Options);
		Assert.Contains("Windows", json, StringComparison.Ordinal);
		await Assert.ThrowsAsync<InvalidOperationException>(() =>
			commands.InvokeAsync(new FakeBridgeSource(WindowLabels.Main), "automation_update_settings", Args(new {enabled = true})));
	}

	[Fact]
	public async Task 自动化停止命令幂等且只允许可见main()
	{
		BridgeCommands commands = CreateCommands();
		string taskId = Guid.NewGuid().ToString();
		object? first = await commands.InvokeAsync(new FakeBridgeSource(WindowLabels.Main), "automation_stop_task", Args(new {taskId}));
		object? second = await commands.InvokeAsync(new FakeBridgeSource(WindowLabels.Main), "automation_stop_task", Args(new {taskId}));
		object? all = await commands.InvokeAsync(new FakeBridgeSource(WindowLabels.Main), "automation_stop_all", Args(new { }));
		Assert.Equal(false, first);
		Assert.Equal(false, second);
		Assert.Equal(0, all);
		await Assert.ThrowsAsync<InvalidOperationException>(() =>
			commands.InvokeAsync(new FakeBridgeSource(WindowLabels.Init), "automation_stop_all", Args(new { })));
	}

	[Fact]
	public async Task 浏览器生命周期只使用fake且停止幂等并纳入快照()
	{
		FakeBrowserRunner fake = new();
		using BridgeCommandsTests fixture = new(false, true, () => fake);
		fixture._config.Set(ConfigStore.KeyAutomationEnabled, new ConfigValue.Boolean(true));
		fixture._config.Set(ConfigStore.KeyAutomationBrowserEnabled, new ConfigValue.Boolean(true));
		BridgeCommands commands = fixture.CreateCommands();
		FakeBridgeSource main = new(WindowLabels.Main);

		object? started = await commands.InvokeAsync(main, "automation_browser_start", Args(new { }));
		string startedJson = JsonSerializer.Serialize(started, BridgeJson.Options);
		Assert.Contains("\"running\":true", startedJson, StringComparison.Ordinal);
		Assert.Equal(1, fake.StartCount);

		string snapshotJson = JsonSerializer.Serialize(
			await commands.InvokeAsync(new NativeSettingsCommandSource(), "automation_get_snapshot", Args(new { })), BridgeJson.Options);
		Assert.Contains("\"browser\":", snapshotJson, StringComparison.Ordinal);
		Assert.Contains("\"running\":true", snapshotJson, StringComparison.Ordinal);
		Assert.DoesNotContain("https://example.test", snapshotJson, StringComparison.Ordinal);
		Assert.DoesNotContain("token=secret", snapshotJson, StringComparison.Ordinal);
		Assert.DoesNotContain("cookie", snapshotJson, StringComparison.OrdinalIgnoreCase);
		Assert.DoesNotContain("screenshot", snapshotJson, StringComparison.OrdinalIgnoreCase);
		Assert.DoesNotContain("prompt", snapshotJson, StringComparison.OrdinalIgnoreCase);
		Assert.DoesNotContain("tool", snapshotJson, StringComparison.OrdinalIgnoreCase);

		await commands.InvokeAsync(main, "automation_update_settings", Args(new {browserEnabled = false}));
		Assert.Equal(1, fake.DisposeCount);
		await commands.InvokeAsync(main, "automation_browser_stop", Args(new { }));
		await commands.InvokeAsync(main, "automation_browser_stop", Args(new { }));
		Assert.Equal(1, fake.DisposeCount);
		string stoppedJson = JsonSerializer.Serialize(
			await commands.InvokeAsync(main, "automation_browser_status", Args(new { })), BridgeJson.Options);
		Assert.Contains("\"running\":false", stoppedJson, StringComparison.Ordinal);
	}

	[Fact]
	public async Task 浏览器命令只允许可见main且未启用时failClosed()
	{
		FakeBrowserRunner fake = new();
		using BridgeCommandsTests fixture = new(false, true, () => fake);
		BridgeCommands commands = fixture.CreateCommands();

		await Assert.ThrowsAsync<InvalidOperationException>(() =>
			commands.InvokeAsync(new FakeBridgeSource(WindowLabels.Init), "automation_browser_status", Args(new { })));
		await Assert.ThrowsAsync<InvalidOperationException>(() =>
			commands.InvokeAsync(new FakeBridgeSource(WindowLabels.Main, false), "automation_browser_start", Args(new { })));
		InvalidOperationException notEnabled = await Assert.ThrowsAsync<InvalidOperationException>(() =>
			commands.InvokeAsync(new FakeBridgeSource(WindowLabels.Main), "automation_browser_start", Args(new { })));
		Assert.Contains("默认关闭", notEnabled.Message, StringComparison.Ordinal);
		Assert.Equal(0, fake.StartCount);

		fixture._config.Set(ConfigStore.KeyAutomationEnabled, new ConfigValue.Boolean(true));
		fixture._config.Set(ConfigStore.KeyAutomationBrowserEnabled, new ConfigValue.Boolean(true));
		await commands.InvokeAsync(new FakeBridgeSource(WindowLabels.Main), "automation_browser_start", Args(new { }));
		int stopped = Assert.IsType<int>(await commands.InvokeAsync(
			new FakeBridgeSource(WindowLabels.Main), "automation_stop_all", Args(new { })));
		Assert.Equal(0, stopped);
		Assert.Equal(1, fake.DisposeCount);
	}

	[Fact]
	public async Task 浏览器在安全模式和非Windows上不启动()
	{
		FakeBrowserRunner safeFake = new();
		using BridgeCommandsTests safeFixture = new(true, true, () => safeFake);
		safeFixture._config.Set(ConfigStore.KeyAutomationEnabled, new ConfigValue.Boolean(true));
		safeFixture._config.Set(ConfigStore.KeyAutomationBrowserEnabled, new ConfigValue.Boolean(true));
		InvalidOperationException safeError = await Assert.ThrowsAsync<InvalidOperationException>(() =>
			safeFixture.CreateCommands().InvokeAsync(new FakeBridgeSource(WindowLabels.Main), "automation_browser_start", Args(new { })));
		Assert.Contains("安全模式", safeError.Message, StringComparison.Ordinal);
		Assert.Equal(0, safeFake.StartCount);

		FakeBrowserRunner linuxFake = new();
		using BridgeCommandsTests linuxFixture = new(false, false, () => linuxFake);
		linuxFixture._config.Set(ConfigStore.KeyAutomationEnabled, new ConfigValue.Boolean(true));
		linuxFixture._config.Set(ConfigStore.KeyAutomationBrowserEnabled, new ConfigValue.Boolean(true));
		InvalidOperationException linuxError = await Assert.ThrowsAsync<InvalidOperationException>(() =>
			linuxFixture.CreateCommands().InvokeAsync(new FakeBridgeSource(WindowLabels.Main), "automation_browser_start", Args(new { })));
		Assert.Contains("Windows", linuxError.Message, StringComparison.Ordinal);
		Assert.Equal(0, linuxFake.StartCount);
	}

	[Fact]
	public async Task 浏览器启动异常会清理fake且不泄露异常正文()
	{
		FakeBrowserRunner fake = new() {FailOnStart = true};
		using BridgeCommandsTests fixture = new(false, true, () => fake);
		fixture._config.Set(ConfigStore.KeyAutomationEnabled, new ConfigValue.Boolean(true));
		fixture._config.Set(ConfigStore.KeyAutomationBrowserEnabled, new ConfigValue.Boolean(true));

		InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
			fixture.CreateCommands().InvokeAsync(new FakeBridgeSource(WindowLabels.Main), "automation_browser_start", Args(new { })));
		Assert.Equal("浏览器启动失败", error.Message);
		Assert.Equal(1, fake.DisposeCount);
		string status = JsonSerializer.Serialize(fixture._services.Automation!.GetBrowserStatus(), BridgeJson.Options);
		Assert.Contains("\"running\":false", status, StringComparison.Ordinal);
		Assert.DoesNotContain("example.test", status, StringComparison.Ordinal);
		Assert.DoesNotContain("token=secret", status, StringComparison.Ordinal);
	}

	[Fact]
	public async Task 浏览器结构化任务执行受限计划且结果不进入快照()
	{
		FakeBrowserRunner fake = new() {VisibleText = "页面可见但受限的测试文本"};
		using BridgeCommandsTests fixture = new(false, true, () => fake);
		fixture._config.Set(ConfigStore.KeyAutomationEnabled, new ConfigValue.Boolean(true));
		fixture._config.Set(ConfigStore.KeyAutomationBrowserEnabled, new ConfigValue.Boolean(true));
		BridgeCommands commands = fixture.CreateCommands();
		FakeBridgeSource main = new(WindowLabels.Main);

		AutomationBrowserTaskStartSnapshot start = Assert.IsType<AutomationBrowserTaskStartSnapshot>(await commands.InvokeAsync(main,
			"automation_browser_start_task", Args(new
			{
				actions = new object[]
				{
					new {type = "navigate", url = "https://example.test/private?token=secret"},
					new {type = "read_visible_text"},
				},
			})));
		await WaitUntilAsync(() => fixture._services.Automation!.GetSnapshot().Tasks.Any(task =>
			task.Id == start.TaskId && task.State == AutomationTaskState.Completed && task.HasResult));

		AutomationTaskStatusSnapshot task = Assert.Single(fixture._services.Automation!.GetSnapshot().Tasks, item => item.Id == start.TaskId);
		Assert.Equal("browser", task.TaskKind);
		Assert.Equal(2, task.TotalSteps);
		Assert.True(task.HasResult);
		Assert.Equal(2, fake.CompletedActionCount);
		string snapshot = JsonSerializer.Serialize(fixture._services.Automation!.GetSnapshot(), BridgeJson.Options);
		Assert.DoesNotContain("example.test", snapshot, StringComparison.Ordinal);
		Assert.DoesNotContain("token=secret", snapshot, StringComparison.Ordinal);
		Assert.DoesNotContain("页面可见但受限的测试文本", snapshot, StringComparison.Ordinal);

		object? result = await commands.InvokeAsync(main, "automation_browser_get_result", Args(new {taskId = start.TaskId}));
		string resultJson = JsonSerializer.Serialize(result, BridgeJson.Options);
		Assert.Contains("页面可见但受限的测试文本", resultJson, StringComparison.Ordinal);
		Assert.DoesNotContain("example.test", resultJson, StringComparison.Ordinal);

		string audit = JsonSerializer.Serialize(await commands.InvokeAsync(main, "automation_audit_list", Args(new {limit = 100})), BridgeJson.Options);
		Assert.Contains("\"taskKind\":\"browser\"", audit, StringComparison.Ordinal);
		Assert.Contains("read_visible_text", audit, StringComparison.Ordinal);
		Assert.DoesNotContain("example.test", audit, StringComparison.Ordinal);
		Assert.DoesNotContain("页面可见但受限的测试文本", audit, StringComparison.Ordinal);
	}

	[Fact]
	public async Task 浏览器填写忽略客户端confirmed并等待现有审批流()
	{
		FakeBrowserRunner fake = new();
		using BridgeCommandsTests fixture = new(false, true, () => fake);
		fixture._config.Set(ConfigStore.KeyAutomationEnabled, new ConfigValue.Boolean(true));
		fixture._config.Set(ConfigStore.KeyAutomationBrowserEnabled, new ConfigValue.Boolean(true));
		BridgeCommands commands = fixture.CreateCommands();
		AutomationBrowserTaskStartSnapshot start = Assert.IsType<AutomationBrowserTaskStartSnapshot>(await commands.InvokeAsync(
			new FakeBridgeSource(WindowLabels.Main),
			"automation_browser_start_task",
			Args(new
			{
				actions = new object[]
				{
					new {type = "fill", selector = "#secret-field", text = "private-input", confirmed = true},
				},
			})));
		await WaitUntilAsync(() => fixture._services.Automation!.GetSnapshot().Tasks.Any(task =>
			task.Id == start.TaskId && task.ProgressCategory == "awaiting_approval"));
		AutomationDesktopApprovalSnapshot approval = Assert.Single(fixture._services.Automation!.GetSnapshot().PendingApprovals);
		Assert.Equal(start.TaskId, approval.TaskId);

		Assert.True(await commands.InvokeAsync(new FakeBridgeSource(WindowLabels.Main), "approval_respond", Args(new
		{
			requestId = approval.RequestId,
			approved = false,
		})) is true);
		await WaitUntilAsync(() => fixture._services.Automation!.GetSnapshot().Tasks.Any(task =>
			task.Id == start.TaskId && task.State == AutomationTaskState.Failed && task.ErrorCategory == "approval_denied"));
		Assert.Equal(0, fake.CompletedActionCount);

		AutomationBrowserTaskStartSnapshot approvedStart = Assert.IsType<AutomationBrowserTaskStartSnapshot>(await commands.InvokeAsync(
			new FakeBridgeSource(WindowLabels.Main), "automation_browser_start_task", Args(new
			{
				actions = new object[]
				{
					new {type = "fill", selector = "#other-secret", text = "another-private-input", confirmed = false},
				},
			})));
		await WaitUntilAsync(() => fixture._services.Automation!.GetSnapshot().PendingApprovals.Any(item => item.TaskId == approvedStart.TaskId));
		AutomationDesktopApprovalSnapshot approvedRequest = Assert.Single(fixture._services.Automation!.GetSnapshot().PendingApprovals);
		Assert.True(await commands.InvokeAsync(new FakeBridgeSource(WindowLabels.Main), "approval_respond", Args(new
		{
			requestId = approvedRequest.RequestId,
			approved = true,
		})) is true);
		await WaitUntilAsync(() => fixture._services.Automation!.GetSnapshot().Tasks.Any(task =>
			task.Id == approvedStart.TaskId && task.State == AutomationTaskState.Completed));
		Assert.Equal(1, fake.CompletedActionCount);

		string snapshot = JsonSerializer.Serialize(fixture._services.Automation!.GetSnapshot(), BridgeJson.Options);
		string audit = JsonSerializer.Serialize(await commands.InvokeAsync(new FakeBridgeSource(WindowLabels.Main), "automation_audit_list", Args(new { })), BridgeJson.Options);
		Assert.DoesNotContain("private-input", snapshot, StringComparison.Ordinal);
		Assert.DoesNotContain("secret-field", snapshot, StringComparison.Ordinal);
		Assert.DoesNotContain("another-private-input", audit, StringComparison.Ordinal);
		Assert.DoesNotContain("other-secret", audit, StringComparison.Ordinal);
		Assert.Contains("approval", audit, StringComparison.Ordinal);
		Assert.Contains("\"outcome\":\"denied\"", audit, StringComparison.Ordinal);
		Assert.Contains("\"outcome\":\"approved\"", audit, StringComparison.Ordinal);
	}

	[Fact]
	public async Task 浏览器安全页面暂停可取消并清理隔离profile()
	{
		FakeBrowserRunner fake = new() {PauseForSafety = true};
		using BridgeCommandsTests fixture = new(false, true, () => fake);
		fixture._config.Set(ConfigStore.KeyAutomationEnabled, new ConfigValue.Boolean(true));
		fixture._config.Set(ConfigStore.KeyAutomationBrowserEnabled, new ConfigValue.Boolean(true));
		BridgeCommands commands = fixture.CreateCommands();
		AutomationBrowserTaskStartSnapshot start = Assert.IsType<AutomationBrowserTaskStartSnapshot>(await commands.InvokeAsync(
			new FakeBridgeSource(WindowLabels.Main), "automation_browser_start_task", Args(new
			{
				actions = new object[] {new {type = "read_visible_text"}},
			})));
		await WaitUntilAsync(() => fixture._services.Automation!.GetSnapshot().Tasks.Any(task =>
			task.Id == start.TaskId && task.State == AutomationTaskState.Paused && task.PauseReason == "safe_page"));

		string paused = JsonSerializer.Serialize(fixture._services.Automation!.GetSnapshot(), BridgeJson.Options);
		Assert.Contains("\"pauseReason\":\"safe_page\"", paused, StringComparison.Ordinal);
		Assert.DoesNotContain("模拟安全页面", paused, StringComparison.Ordinal);
		Assert.True(await commands.InvokeAsync(new FakeBridgeSource(WindowLabels.Main), "automation_browser_stop_task", Args(new {taskId = start.TaskId})) is true);
		await WaitUntilAsync(() => fixture._services.Automation!.GetSnapshot().Tasks.Any(task =>
			task.Id == start.TaskId && task.State == AutomationTaskState.Cancelled));
		Assert.Equal(1, fake.DisposeCount);

		string audit = JsonSerializer.Serialize(await commands.InvokeAsync(new FakeBridgeSource(WindowLabels.Main), "automation_audit_list", Args(new { })), BridgeJson.Options);
		Assert.Contains("safe_page", audit, StringComparison.Ordinal);
		Assert.DoesNotContain("模拟安全页面", audit, StringComparison.Ordinal);
	}

	[Fact]
	public async Task 浏览器任务超时和专用命令来源均被边界保护()
	{
		FakeBrowserRunner fake = new()
		{
			WaitForRelease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously),
		};
		using BridgeCommandsTests fixture = new(false, true, () => fake, browserTaskTimeout: TimeSpan.FromMilliseconds(50));
		fixture._config.Set(ConfigStore.KeyAutomationEnabled, new ConfigValue.Boolean(true));
		fixture._config.Set(ConfigStore.KeyAutomationBrowserEnabled, new ConfigValue.Boolean(true));
		BridgeCommands commands = fixture.CreateCommands();
		await Assert.ThrowsAsync<InvalidOperationException>(() => commands.InvokeAsync(
			new FakeBridgeSource(WindowLabels.Init), "automation_browser_start_task", Args(new {actions = Array.Empty<object>()})));
		await Assert.ThrowsAsync<InvalidOperationException>(() => commands.InvokeAsync(
			new FakeBridgeSource(WindowLabels.Main, false), "automation_audit_list", Args(new { })));

		AutomationBrowserTaskStartSnapshot start = Assert.IsType<AutomationBrowserTaskStartSnapshot>(await commands.InvokeAsync(
			new FakeBridgeSource(WindowLabels.Main), "automation_browser_start_task", Args(new
			{
				actions = new object[] {new {type = "wait", milliseconds = 1}},
			})));
		await WaitUntilAsync(() => fixture._services.Automation!.GetSnapshot().Tasks.Any(task =>
			task.Id == start.TaskId && task.State == AutomationTaskState.Failed && task.ErrorCategory == "timeout"));
		string result = JsonSerializer.Serialize(await commands.InvokeAsync(
			new FakeBridgeSource(WindowLabels.Main), "automation_browser_get_result", Args(new {taskId = start.TaskId})), BridgeJson.Options);
		Assert.Contains("timeout", result, StringComparison.Ordinal);
	}

	[Fact]
	public async Task 安全模式在Bridge入口拒绝联网命令()
	{
		using BridgeCommandsTests safeFixture = new(true);
		BridgeCommands commands = safeFixture.CreateCommands();
		string[] networkCommands =
		[
			"llm_fetch_models", "ai_test_connection", "chat_start",
			"memory_reembed_all", "memory_recall_debug", "memory_knowledge_reindex",
			"skills_install_url", "mcp_get_servers", "mcp_connect_server", "mcp_test_server",
			"mcp_call_tool", "mcp_import_url", "tts_test", "stt_start", "stt_stop", "open_url",
		];

		foreach (string command in networkCommands)
		{
			InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
				commands.InvokeAsync(new FakeBridgeSource(WindowLabels.Main), command, Args(new { })));
			Assert.Contains("安全模式", exception.Message, StringComparison.Ordinal);
		}

		InvalidOperationException autoConnectException = await Assert.ThrowsAsync<InvalidOperationException>(() =>
			commands.InvokeAsync(new FakeBridgeSource(WindowLabels.Main), "mcp_save_server",
				Args(new {enabled = true, autoConnect = true})));
		Assert.Contains("安全模式", autoConnectException.Message, StringComparison.Ordinal);
	}


	[Fact]
	public async Task 安全模式运行时不刷新MCP工具()
	{
		using BridgeCommandsTests safeFixture = new(true);
		RegisteredTool previous = MakeMcpTool("mcp__previous__tool");
		safeFixture._runtime.Tools.Register(previous);

		await safeFixture._runtime.RefreshMcpToolsAsync();

		Assert.Same(previous, safeFixture._runtime.Tools.Get(previous.Name));
	}

	[Fact]
	public async Task MCP刷新取消时保留上一版工具()
	{
		RegisteredTool previous = MakeMcpTool("mcp__previous__tool");
		_runtime.Tools.Register(previous);
		using CancellationTokenSource cancellation = new();
		cancellation.Cancel();

		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _runtime.RefreshMcpToolsAsync(cancellation.Token));

		Assert.Same(previous, _runtime.Tools.Get(previous.Name));
		LogEntry log = Assert.Single(_services.Logger.RecentLogs(), entry => entry.Message.Contains("category=cancelled", StringComparison.Ordinal));
		Assert.True(log.Message.Length <= 192);
	}
}
