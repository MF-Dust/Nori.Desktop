using System.Text.Json;
using Nori.Core.Automation;
using Nori.Core.Configuration;
using Nori.Core.Logging;
using Nori.Core.Mcp;
using Nori.Desktop.Automation;
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
	public async Task 设置页自动化命令映射桌面开关()
	{
		BridgeCommands commands = CreateCommands();
		FakeBridgeSource main = new(WindowLabels.Main);

		await commands.InvokeAsync(main, "settings_update_automation", Args(new
		{
			enabled = true,
			desktopEnabled = true,
		}));

		Assert.True(_config.GetBoolOr(ConfigStore.KeyAutomationEnabled, false));
		Assert.True(_config.GetBoolOr(ConfigStore.KeyAutomationAllowPointer, false));
		Assert.True(_config.GetBoolOr(ConfigStore.KeyAutomationAllowKeyboard, false));
		Assert.True(_config.GetBoolOr(ConfigStore.KeyAutomationAllowScroll, false));

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

	[Theory]
	[InlineData("automation_browser_start")]
	[InlineData("automation_browser_stop")]
	[InlineData("automation_browser_status")]
	[InlineData("automation_browser_start_task")]
	[InlineData("automation_browser_get_result")]
	[InlineData("automation_browser_stop_task")]
	public async Task 浏览器自动化迁到插件后宿主不再提供浏览器命令(string command)
	{
		BridgeCommands commands = CreateCommands();
		InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
			commands.InvokeAsync(new FakeBridgeSource(WindowLabels.Main), command, Args(new { })));
		Assert.Contains("未知的命令", error.Message, StringComparison.Ordinal);
	}

	[Fact]
	public async Task 自动化快照不再包含浏览器状态()
	{
		string snapshot = JsonSerializer.Serialize(
			await CreateCommands().InvokeAsync(new NativeSettingsCommandSource(), "automation_get_snapshot", Args(new { })), BridgeJson.Options);
		Assert.DoesNotContain("browser", snapshot, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public async Task 历史浏览器审计记录仍可读取()
	{
		_services.AutomationAudit.Record(new AutomationAuditEvent(
			DateTimeOffset.UtcNow,
			Guid.NewGuid(),
			AutomationAuditTaskKind.Browser,
			AutomationAuditEventCategory.ReadVisibleText,
			AutomationAuditOutcome.Succeeded));
		string audit = JsonSerializer.Serialize(await CreateCommands().InvokeAsync(
			new FakeBridgeSource(WindowLabels.Main), "automation_audit_list", Args(new {limit = 10})), BridgeJson.Options);
		Assert.Contains("\"taskKind\":\"browser\"", audit, StringComparison.Ordinal);
		Assert.Contains("read_visible_text", audit, StringComparison.Ordinal);
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
