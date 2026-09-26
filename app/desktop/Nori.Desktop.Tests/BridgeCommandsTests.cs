using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Nori.Core.Automation;
using Nori.Core.Chat;
using Nori.Core.Chat.LuoLiCore;
using Nori.Core.Configuration;
using Nori.Core.Data;
using Nori.Core.Logging;
using Nori.Core.Mcp;
using Nori.Core.Tools;
using Nori.Desktop.Automation;
using Nori.Desktop.Automation.Browser;
using Nori.Desktop.Automation.Desktop;
using Nori.Desktop.Automation.Windows;
using Nori.Desktop.Bridge;
using Nori.Desktop.Runtime;
using Nori.Desktop.Windows;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;

namespace Nori.Desktop.Tests;

/// <summary>
/// 后端化桥接命令面测试: 来源授权、快照脱敏、历史规范化与提醒持久化
/// </summary>
[Collection("Native settings")]
public partial class BridgeCommandsTests : IDisposable
{
	/// <summary>无界面测试使用生产主题，但不启动桌面服务。</summary>
	public static AppBuilder BuildAvaloniaApp() =>
		AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions());

	[Theory]
	[InlineData("en-US")]
	[InlineData("zh-CN")]
	public Task NativeTestFixtureLanguageIsIndependentOfHostCulture(string hostCulture) => WithSettingsUiAsync(async () =>
	{
		// Headless 使用自己的 UI 线程；必须在该线程构造夹具前模拟宿主语言。
		CultureInfo previous = CultureInfo.CurrentUICulture;
		try
		{
			CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(hostCulture);
			using BridgeCommandsTests fixture = new(safeMode: true);
			Assert.Equal(hostCulture, ConfigStore.SystemLanguage());
			Assert.Equal("zh-CN", fixture._config.GetInitConfig().Language);
			ModelsWindow models = new(fixture._services);
			MemoryWindow memory = new(fixture._services);
			try
			{
				models.Show(); memory.Show();
				await models.RefreshAsync(); await memory.RefreshAsync();
				Assert.Equal("Nori · 模型", models.Title);
				Assert.Contains("记忆", memory.Title);
				fixture._config.Set(ConfigStore.KeyLanguage, new ConfigValue.Text("en-US"));
				fixture._runtime.InvalidateSnapshot();
				await models.RefreshAsync(); await memory.RefreshAsync();
				await WaitUntilAsync(() => models.Title == "Nori · Models" && memory.Title?.Contains("Memory", StringComparison.Ordinal) == true);
				Assert.Equal("Nori · Models", models.Title);
				Assert.Contains("Memory", memory.Title);
				await models.PrepareShutdownAsync(); await memory.PrepareShutdownAsync();
			}
			finally { models.AllowClose = memory.AllowClose = true; models.Close(); memory.Close(); }
		}
		finally { CultureInfo.CurrentUICulture = previous; }
	});

	// 会话线程保留到测试进程退出，避免 Avalonia 12.1.1 启停竞态；每次 Dispatch 仍独立创建和清理应用。
	private static readonly Lazy<HeadlessUnitTestSession> SettingsUiSession = new(() =>
		HeadlessUnitTestSession.StartNew(typeof(BridgeCommandsTests), AvaloniaTestIsolationLevel.PerTest));

	internal static async Task WithSettingsUiAsync(Func<Task> action)
	{
		await SettingsUiSession.Value.Dispatch(async () =>
		{
			await action();
			return true;
		}, CancellationToken.None);
	}

	private sealed class FakeBridgeSource(string label, bool isVisible = true) : IBridgeSource
	{
		public string Label => label;
		public bool IsVisible => isVisible;
		public Window? Self => null;
		public List<(string Name, object? Payload)> Events { get; } = [];

		public void PostEvent(string name, object? payload) => Events.Add((name, payload));

		public void PostResult(long id, object? value, string? error)
		{
		}
	}

	private sealed class NativeSettingsCommandSource(bool isVisible = true) : INativeSettingsSource
	{
		public string Label => WindowLabels.Settings;
		public bool IsVisible => isVisible;
		public Window? Self => null;
		public void PostEvent(string name, object? payload) { }
		public void PostResult(long id, object? value, string? error) { }
	}

	private sealed class FakeBrowserRunner : IAutomationBrowserRunner
	{
		public int StartCount { get; private set; }
		public int ExecuteCount { get; private set; }
		public int CompletedActionCount { get; private set; }
		public int DisposeCount { get; private set; }
		public bool FailOnStart { get; init; }
		public bool PauseForSafety { get; set; }
		public string VisibleText { get; set; } = "受限测试文本";
		public TaskCompletionSource<bool>? StartedExecution { get; set; }
		public TaskCompletionSource<bool>? WaitForRelease { get; set; }

		public Task StartAsync(CancellationToken cancellationToken = default)
		{
			StartCount++;
			if (FailOnStart) throw new InvalidOperationException("模拟 Edge 启动失败: https://example.test/?token=secret");
			return Task.CompletedTask;
		}

		public async Task<BrowserAutomationExecutionResult> ExecuteAsync(
			BrowserAutomationTaskPlan plan,
			BrowserAutomationExecutionContext executionContext,
			CancellationToken cancellationToken = default)
		{
			ExecuteCount++;
			StartedExecution?.TrySetResult(true);
			if (WaitForRelease is not null) await WaitForRelease.Task.WaitAsync(cancellationToken);
			if (PauseForSafety) throw new BrowserAutomationPolicy.PausedException(
				BrowserAutomationPolicy.PauseReason.SensitivePage,
				"模拟安全页面: secret");
			int completed = 0;
			string? visibleText = null;
			foreach (BrowserAutomationAction action in plan.Actions)
			{
				cancellationToken.ThrowIfCancellationRequested();
				await executionContext.EnsureExecutionAllowedAsyncCore(cancellationToken);
				int step = completed + 1;
				executionContext.Report(new BrowserAutomationProgress(step, action.Kind, BrowserAutomationProgressState.Running));
				if (action is BrowserFillAction)
				{
					AutomationApprovalCallback? callback = executionContext.ApprovalCallback;
					if (callback is null) throw new AutomationTaskExecutionException("approval_denied");
					AutomationApprovalRequest request = new(Guid.NewGuid(), executionContext.TaskId, [AutomationActionKind.TypeText], DateTimeOffset.UtcNow);
					executionContext.Report(new BrowserAutomationProgress(
						step,
						BrowserAutomationActionKind.Fill,
						BrowserAutomationProgressState.AwaitingApproval,
						request.RequestId));
					AutomationApprovalDecision decision = await callback(request, cancellationToken);
					if (decision.RequestId != request.RequestId || decision.Outcome != AutomationApprovalOutcome.Approved)
						throw new AutomationTaskExecutionException("approval_denied");
					executionContext.Report(new BrowserAutomationProgress(step, BrowserAutomationActionKind.Fill, BrowserAutomationProgressState.Running));
				}
				if (action is BrowserReadVisibleTextAction) visibleText = VisibleText;
				completed = step;
				CompletedActionCount++;
				executionContext.Report(new BrowserAutomationProgress(step, action.Kind, BrowserAutomationProgressState.ActionSucceeded));
			}
			return BrowserAutomationExecutionResult.Completed(completed, visibleText);
		}

		public ValueTask DisposeAsync()
		{
			DisposeCount++;
			return ValueTask.CompletedTask;
		}
	}

	private sealed class FakeDesktopWindowCatalog : IDesktopVisionWindowCatalog
	{
		public IReadOnlyList<WindowsTopLevelWindow> Enumerate() =>
		[
			new(new nint(0x1234), "窗口标题-secret", 4321, new AutomationBounds(10, 20, 800, 600), 96, true),
		];
	}

	private sealed class FakeDesktopRunner(Action<DesktopVisionProgress>? progress, bool waitForRelease = false) : IAutomationTaskRunner
	{
		public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public TaskCompletionSource<bool> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

		public async Task RunAsync(AutomationTaskContext context, CancellationToken cancellationToken)
		{
			Started.TrySetResult(true);
			if (waitForRelease) await Release.Task.WaitAsync(cancellationToken);
			progress?.Invoke(new DesktopVisionProgress(1, DesktopVisionAutomationCategory.Completed));
		}
	}

	private sealed class FakeDesktopPlanner(string response) : IDesktopVisionPlanner
	{
		public Task<string> PlanAsync(IReadOnlyList<ChatMessageInput> messages, CancellationToken cancellationToken = default) =>
			Task.FromResult(response);
	}

	private sealed class FakeDesktopScreenshot : IDesktopVisionScreenshotSource
	{
		public Task<DesktopVisionScreenshotResult> CaptureAsync(nint targetWindow, CancellationToken cancellationToken = default) =>
			Task.FromResult(DesktopVisionScreenshotResult.Succeeded(new DesktopVisionScreenshot([1, 2, 3], "image/png")));
	}

	private sealed class FakeDesktopAction : IDesktopVisionActionExecutor
	{
		public int Count { get; private set; }

		public Task<DesktopVisionActionResult> ExecuteAsync(
			nint targetWindow,
			AutomationAction action,
			AutomationPolicy policy,
			CancellationToken cancellationToken = default)
		{
			Count++;
			return Task.FromResult(DesktopVisionActionResult.Succeeded);
		}
	}

	private sealed class SynchronousUiDispatcher : IUiDispatcher
	{
		public int InvokeCount { get; private set; }

		public void Post(Action action)
		{
			InvokeCount++;
			action();
		}

		public Task<T> InvokeAsync<T>(Func<T> action)
		{
			InvokeCount++;
			return Task.FromResult(action());
		}

		public Task InvokeTaskAsync(Func<Task> action)
		{
			InvokeCount++;
			return action();
		}

		public Task<T> InvokeTaskAsync<T>(Func<Task<T>> action)
		{
			InvokeCount++;
			return action();
		}
	}

	private sealed class FakeWindowManager : IWindowManager
	{
		public List<string?> SettingsPages { get; } = [];
		public List<string?> MemoryPages { get; } = [];
		public int ModelsShowCount { get; private set; }
		public int ChatShowCount { get; private set; }
		public List<bool> BackgroundBlurChanges { get; } = [];
		public void UpdateBackgroundBlurEnabled(bool enabled) => BackgroundBlurChanges.Add(enabled);
		private readonly Dictionary<string, bool> _visible = [];

		public event Action<string, bool>? VisibilityChanged;

		public Window? Get(string? label) => null;
		public NoriWindow? GetNoriWindow(string? label) => null;
		public PetWindow? Pet => null;

		public void CreateAll(NoriBridge bridge, AppServices services)
		{
		}
		public void Show(string label) => SetVisible(label, true);

		public void ShowSettings(string? page = null) => SettingsPages.Add(page);

		public void ShowMemory(string? page = null) => MemoryPages.Add(page);

		public void ShowModels() => ModelsShowCount++;

		public void ShowChat() => ChatShowCount++;

		public void Hide(string label) => SetVisible(label, false);

		public void Close(string label) => SetVisible(label, false);

		public void TogglePet() => SetVisible(WindowLabels.Pet, !IsWindowVisible(WindowLabels.Pet));

		public bool IsWindowVisible(string label) => _visible.TryGetValue(label, out bool visible) && visible;

		/// <summary>测试替身, 直接拨可见性。用例要造「某个窗口开着」这种局面。</summary>
		public void SetVisible(string label, bool visible)
		{
			if (IsWindowVisible(label) == visible) return;
			_visible[label] = visible;
			VisibilityChanged?.Invoke(label, visible);
		}


		public void ShowPetSpeech(string text)
		{
		}

		public void ClearPetSpeech()
		{
		}

		public void Shutdown()
		{
		}
	}

	private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"nori-bridge-{Guid.NewGuid():N}");
	private readonly string _dbPath;
	private readonly NoriDatabase _database;
	private readonly ConfigStore _config;
	private readonly HttpClient _http;
	private readonly FakeWindowManager _windows = new();
	private readonly AppServices _services;
	/// <summary>
	/// 记录释放调用的假启动器。
	///
	/// 隔离强度报 None，行为与 <c>UnsandboxedLauncher</c> 一致，只是把 <c>Release</c> 记下来 ——
	/// 「授权释放有没有被接上」这件事只能从调用侧观察，而它恰恰是漏过一次的地方。
	/// </summary>
	private sealed class RecordingSandbox : Nori.Core.Sandbox.ISandboxLauncher
	{
		public List<string> Released { get; } = [];

		public Nori.Core.Sandbox.SandboxIsolation Isolation => Nori.Core.Sandbox.SandboxIsolation.None;

		public Task<Nori.Core.Sandbox.SandboxResult> RunAsync(
			string commandLine, Nori.Core.Sandbox.SandboxPolicy policy, CancellationToken cancellationToken) =>
			Task.FromResult(new Nori.Core.Sandbox.SandboxResult
			{
				ExitCode = 0, Output = "", TimedOut = false, Truncated = false,
			});

		public void Release(Nori.Core.Sandbox.SandboxPolicy policy) => Released.Add(policy.WorkspaceRoot);
	}

	private readonly RecordingSandbox _sandbox = new();
	private readonly AppRuntime _runtime;

	public BridgeCommandsTests() : this(false, null)
	{
	}

	private BridgeCommandsTests(bool safeMode) : this(safeMode, null)
	{
	}

	private BridgeCommandsTests(
		bool safeMode,
		bool? automationWindows,
		Func<IAutomationBrowserRunner>? browserRunnerFactory = null,
		bool automationVision = false,
		Func<DesktopVisionRunnerRequest, IAutomationTaskRunner>? desktopVisionRunnerFactory = null,
		Func<IDesktopVisionPlanner>? desktopVisionPlannerFactory = null,
		Func<IDesktopVisionActionExecutor>? desktopVisionActionFactory = null,
		Func<IDesktopVisionScreenshotSource>? desktopVisionScreenshotFactory = null,
		Func<IDesktopVisionWindowCatalog>? desktopVisionWindowCatalogFactory = null,
		DesktopVisionApprovalCallback? desktopVisionApprovalCallback = null,
		TimeSpan? browserTaskTimeout = null)
	{
		Directory.CreateDirectory(_tempDir);
		_dbPath = Path.Combine(_tempDir, "nori.db");
		_database = NoriDatabase.Open(_dbPath);
		_config = new ConfigStore(_database);
		_config.InitDefaults("0.1.0");
		// 中文断言使用固定测试语言，不依赖 CI 系统语言；双语测试可显式覆盖此配置。
		_config.Set(ConfigStore.KeyLanguage, new ConfigValue.Text("zh-CN"));
		_http = new HttpClient();
		ChatService chat = new(_http, _database, _config);
		_services = new AppServices
		{
			Paths = new AppStoragePaths(_tempDir),
			Database = _database,
			Config = _config,
			AiSettings = new AiSettingsStore(_config),
			Logger = new FileLogger(Path.Combine(_tempDir, "logs")),
			Resources = new Nori.Core.Resources.ResourceManager(_tempDir),
			Chat = chat,
			Memory = new Nori.Core.Memory.MemoryStore(_database),
			Embedding = new Nori.Core.Embedding.OpenAiEmbeddingAdapter(_http),
			Llm = new LlmClient(_http),
			Mcp = new McpManager(_http, _config),
			Http = _http,
			AgentOperations = new AgentOperationRegistry(),
			Automation = automationWindows is { } isWindows
				? new Nori.Desktop.Automation.AutomationRuntime(
					_config,
					safeMode,
					isWindows,
					visionAvailable: automationVision,
					browserRunnerFactory: browserRunnerFactory,
					chatService: chat,
					desktopVisionRunnerFactory: desktopVisionRunnerFactory,
					desktopVisionPlannerFactory: desktopVisionPlannerFactory,
					desktopVisionActionFactory: desktopVisionActionFactory,
					desktopVisionScreenshotFactory: desktopVisionScreenshotFactory,
					desktopVisionWindowCatalogFactory: desktopVisionWindowCatalogFactory,
					desktopVisionApprovalCallback: desktopVisionApprovalCallback,
					browserTaskTimeout: browserTaskTimeout)
				: new Nori.Desktop.Automation.AutomationRuntime(
					_config,
					safeMode,
					OperatingSystem.IsWindows(),
					visionAvailable: automationVision,
					browserRunnerFactory: browserRunnerFactory,
					chatService: chat,
					desktopVisionRunnerFactory: desktopVisionRunnerFactory,
					desktopVisionPlannerFactory: desktopVisionPlannerFactory,
					desktopVisionActionFactory: desktopVisionActionFactory,
					desktopVisionScreenshotFactory: desktopVisionScreenshotFactory,
					desktopVisionWindowCatalogFactory: desktopVisionWindowCatalogFactory,
					desktopVisionApprovalCallback: desktopVisionApprovalCallback,
					browserTaskTimeout: browserTaskTimeout),
			Windows = _windows,
			SafeMode = safeMode,
			// 不用自动挑选：Windows 上它会创建 AppContainer 配置文件，测试跑完会留在机器上。
			Sandbox = _sandbox,
			Update = new Nori.Core.Update.UpdateService(new AppStoragePaths(_tempDir), "win-x64", "0.1.0", safeMode, httpClient: _http),
		};
		_runtime = new AppRuntime(_services);
		_services.Runtime = _runtime;
		_services.Commands = new BridgeCommands(_services, new SynchronousUiDispatcher());
	}

	public void Dispose()
	{
		_services.Update?.Dispose();
		_runtime.DisposeAsync().GetAwaiter().GetResult();
		if (!_databaseReleasedByServices) _database.Dispose();
		_http.Dispose();
		_services.Logger.Dispose();
		try
		{
			Directory.Delete(_tempDir, true);
		}
		catch (IOException)
		{
		}
	}

	private BridgeCommands CreateCommands(IUiDispatcher? uiDispatcher = null) =>
		new(_services, uiDispatcher ?? new SynchronousUiDispatcher());

	private static JsonElement Args(object payload) =>
		JsonSerializer.SerializeToElement(payload, new JsonSerializerOptions {PropertyNamingPolicy = JsonNamingPolicy.CamelCase});

	private static RegisteredTool MakeMcpTool(string name) => new()
	{
		Name = name,
		Description = name,
		Parameters = new JsonObject {["type"] = "object"},
		PermissionLevel = "confirm",
		Category = "mcp",
		Execute = (_, _) => Task.FromResult<object?>(null),
	};

	private void ConfigureDesktop()
	{
		_config.Set(ConfigStore.KeyAutomationEnabled, new ConfigValue.Boolean(true));
		_config.Set(ConfigStore.KeyAutomationAllowPointer, new ConfigValue.Boolean(true));
		_config.Set(AiSettingsStore.KeyLlmBaseUrl, new ConfigValue.Text("http://127.0.0.1:18080/v1"));
		_config.Set(AiSettingsStore.KeyLlmApiKey, new ConfigValue.Text("planner-secret"));
		_config.Set(AiSettingsStore.KeyLlmModel, new ConfigValue.Text("vision-model"));
	}

	private static async Task WaitUntilAsync(Func<bool> condition)
	{
		for (int attempt = 0; attempt < 100 && !condition(); attempt++) await Task.Delay(10);
		Assert.True(condition());
	}

	private static AutomationDesktopWindowSnapshot SingleWindow(object? result) =>
		Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<AutomationDesktopWindowSnapshot>>(result));
	/// <summary>
	/// 把 LuoLiCore 配成「已启用、已有会话、地址指向一个不可达的测试域名」。
	///
	/// 用 <c>nori-network-test.invalid</c> 让 DNS 立即失败，避免 Windows 上对未监听回环端口
	/// 连接时的长时间重试。非安全模式下真去调那个重置端点必然连接失败，安全模式下必须压根
	/// 不发这次请求 —— 两条用例靠这个差别互为对照。
	/// </summary>
	private void ConfigureUnreachableLuoLiCore()
	{
		_config.Set(LuoLiCoreSettingsStore.KeyEnabled, new ConfigValue.Boolean(true));
		_config.Set(LuoLiCoreSettingsStore.KeyBaseUrl, new ConfigValue.Text("http://nori-network-test.invalid"));
		_config.Set(LuoLiCoreSettingsStore.KeyApiKey, new ConfigValue.Text("sk-unreachable"));
		_config.Set(LuoLiCoreSettingsStore.KeySessionId, new ConfigValue.Text("sess_fixed"));
	}
}
