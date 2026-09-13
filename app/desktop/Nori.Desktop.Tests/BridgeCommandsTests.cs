using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Nori.Core.Automation;
using Nori.Core.Chat;
using Nori.Core.Chat.LuoLiCore;
using Nori.Core.Configuration;
using Nori.Core.Data;
using Nori.Core.Logging;
using Nori.Core.Live2D;
using Nori.Core.Mcp;
using Nori.Core.Resources;
using Nori.Core.Tools;
using Nori.Desktop.Automation;
using Nori.Desktop.Automation.Browser;
using Nori.Desktop.Automation.Desktop;
using Nori.Desktop.Automation.Windows;
using Nori.Desktop.Bridge;
using Nori.Desktop.Runtime;
using Nori.Desktop.Settings;
using Nori.Desktop.Windows;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;
using Devolutions.AvaloniaTheme.MacOS;
using Nori.Desktop.Settings.Pages;

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
				fixture._runtime.InvalidateSnapshot("general");
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
	private static readonly Lazy<HeadlessUnitTestSession> VisualUiSession = new(() =>
		HeadlessUnitTestSession.StartNew(typeof(NativeSettingsVisualApplicationBuilder), AvaloniaTestIsolationLevel.PerTest));

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
		public List<(string Name, object? Payload)> Broadcasts { get; } = [];
		public List<string?> SettingsPages { get; } = [];
		public List<string?> MemoryPages { get; } = [];
		public int ModelsShowCount { get; private set; }
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

		public void Hide(string label) => SetVisible(label, false);

		public void Close(string label) => SetVisible(label, false);

		public void TogglePet() => SetVisible(WindowLabels.Pet, !IsWindowVisible(WindowLabels.Pet));

		public bool IsWindowVisible(string label) => _visible.TryGetValue(label, out bool visible) && visible;

		private void SetVisible(string label, bool visible)
		{
			if (IsWindowVisible(label) == visible) return;
			_visible[label] = visible;
			VisibilityChanged?.Invoke(label, visible);
		}

		public void Broadcast(string name, object? payload) => Broadcasts.Add((name, payload));

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
		_database.Dispose();
		_http.Dispose();
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

	// ---- 快照与秘密 ----

	[Fact]
	public async Task model_interactions按模型保存并返回()
	{
		string modelDir = _services.Resources.ResourceDir(ResourceType.Live2D, "nori");
		Directory.CreateDirectory(modelDir);
		File.WriteAllText(Path.Combine(modelDir, "nori.model3.json"), """
			{
				"FileReferences": {
					"Moc": "nori.moc3",
					"Textures": [],
					"Expressions": [{"Name": "01_Smile", "File": "01_Smile.exp3.json"}],
					"Motions": {"Reactions": [{"File": "motions/01_Nod.motion3.json"}]}
				}
			}
			""");
		File.WriteAllText(Path.Combine(modelDir, "nori.moc3"), "MOC3");
		File.WriteAllText(Path.Combine(modelDir, "01_Smile.exp3.json"), "{}");
		Directory.CreateDirectory(Path.Combine(modelDir, "motions"));
		File.WriteAllText(Path.Combine(modelDir, "motions", "01_Nod.motion3.json"), "{}");
		PetInteractionConfig config = new()
		{
			Regions =
			[
				new PetInteractionRegion
				{
					Id = "head",
					Name = "头部",
					Rect = new PetInteractionRect {X = 0.2, Y = 0.1, Width = 0.3, Height = 0.2},
					Motion = new PetInteractionAction
					{
						Mode = PetInteractionActionMode.Selected,
						Group = "Reactions",
						Name = "01_Nod",
					},
					Expression = new PetInteractionAction
					{
						Mode = PetInteractionActionMode.Selected,
						Name = "01_Smile",
					},
				},
			],
		};
		JsonElement interactionJson = JsonSerializer.Deserialize<JsonElement>(config.ToJsonNode().ToJsonString());
		BridgeCommands commands = CreateCommands();
		FakeBridgeSource source = new(WindowLabels.Main);

		await commands.InvokeAsync(source, "model_set_interactions", Args(new {modelId = "nori", interactions = interactionJson}));
		object? meta = await commands.InvokeAsync(source, "model_get_meta", Args(new {modelId = "nori"}));
		string json = JsonSerializer.Serialize(meta, BridgeJson.Options);

		Assert.Contains("\"head\"", json, StringComparison.Ordinal);
		Assert.Contains("\"01_Nod\"", json, StringComparison.Ordinal);
		Assert.True(_config.Exists(PetInteractionConfig.StorageKey("nori")));
	}

	[Fact]
	public async Task ai_interaction开关按领域命令持久化()
	{
		BridgeCommands commands = CreateCommands();
		FakeBridgeSource source = new(WindowLabels.Main);

		await commands.InvokeAsync(source, "model_set_behavior", Args(new {aiInteraction = true}));

		Assert.True(_config.GetBoolOr(PetInteractionConfig.AiEnabledKey, false));
		object? snapshot = await commands.InvokeAsync(source, "ui_get_snapshot", Args(new { }));
		Assert.Contains("\"aiInteraction\":true", JsonSerializer.Serialize(snapshot, BridgeJson.Options), StringComparison.Ordinal);
	}

	[Fact]
	public async Task ui_get_snapshot任意窗口可读且秘密不回传()
	{
		_config.Set("llm_api_key", new ConfigValue.Text("sk-super-secret"));
		BridgeCommands commands = CreateCommands();

		object? snapshot = await commands.InvokeAsync(new FakeBridgeSource("init"), "ui_get_snapshot", Args(new { }));
		string json = JsonSerializer.Serialize(snapshot);

		Assert.Contains("\"hasApiKey\":true", json, StringComparison.Ordinal);
		Assert.Contains("\"aiInteraction\":false", json, StringComparison.Ordinal);
		Assert.DoesNotContain("sk-super-secret", json, StringComparison.Ordinal);
	}

	// ---- 来源授权 ----

	[Fact]
	public async Task 业务命令拒绝非main窗口()
	{
		BridgeCommands commands = CreateCommands();
		string[] businessCommands = ["settings_update_voice", "chat_start", "memory_clear", "reminder_add", "tts_stop"];
		foreach (string cmd in businessCommands)
		{
			await Assert.ThrowsAsync<InvalidOperationException>(() =>
				commands.InvokeAsync(new FakeBridgeSource("init"), cmd, Args(new { })));
		}
	}

	[Fact]
	public async Task memory_transfer只允许可见main窗口()
	{
		BridgeCommands commands = CreateCommands();
		string[] commandsToCheck = ["memory_export", "memory_import_preview", "memory_import_commit"];
		foreach (string command in commandsToCheck)
		{
			await Assert.ThrowsAsync<InvalidOperationException>(() => commands.InvokeAsync(
				new FakeBridgeSource(WindowLabels.Init), command, Args(new {fileContent = "{}"})));
			await Assert.ThrowsAsync<InvalidOperationException>(() => commands.InvokeAsync(
				new FakeBridgeSource(WindowLabels.Main, false), command, Args(new {fileContent = "{}"})));
		}
	}

	[Fact]
	public async Task memory_transfer桥接使用服务端预览并返回既有前端DTO()
	{
		BridgeCommands commands = CreateCommands();
		FakeBridgeSource main = new(WindowLabels.Main);
		string content = $"bridge-transfer-{Guid.NewGuid():N}";
		string transfer = JsonSerializer.Serialize(new
		{
			version = "nori-memory-v1",
			format = "nori-memory-v1",
			memories = new[]
			{
				new
				{
					content,
					canonical_summary = content,
					kind = "preference",
					importance = 0.9,
					confidence = 0.8,
					tags = "coffee",
				},
			},
		});
		int snapshotBefore = _runtime.SnapshotVersion;
		Assert.Empty(_services.Memory.GetAll());

		object? previewObject = await commands.InvokeAsync(main, "memory_import_preview", Args(new
		{
			fileContent = transfer,
			fileName = "memory.json",
			fileSize = 1,
		}));
		using JsonDocument preview = JsonDocument.Parse(JsonSerializer.Serialize(previewObject, BridgeJson.Options));
		Assert.True(preview.RootElement.GetProperty("valid").GetBoolean());
		Assert.Equal(1, preview.RootElement.GetProperty("newCount").GetInt32());
		Assert.Equal("none", preview.RootElement.GetProperty("items")[0].GetProperty("conflictType").GetString());
		string token = preview.RootElement.GetProperty("previewToken").GetString()!;

		object? commitObject = await commands.InvokeAsync(main, "memory_import_commit", Args(new
		{
			previewToken = token,
			conflictStrategy = "skip",
			items = new[] {new {content = "客户端伪造内容", kind = "identity"}},
		}));
		using JsonDocument commit = JsonDocument.Parse(JsonSerializer.Serialize(commitObject, BridgeJson.Options));
		Assert.True(commit.RootElement.GetProperty("success").GetBoolean());
		Assert.Equal(1, commit.RootElement.GetProperty("importedCount").GetInt32());
		Assert.Equal(0, commit.RootElement.GetProperty("updatedCount").GetInt32());
		Assert.Equal(0, commit.RootElement.GetProperty("skippedCount").GetInt32());
		Assert.True(_runtime.SnapshotVersion > snapshotBefore);
		Nori.Core.Memory.MemoryItem imported = Assert.Single(_services.Memory.GetAll());
		Assert.Equal(content, imported.Content);
		Assert.Equal("memory_transfer", imported.Source);
		Assert.Single(_services.Memory.GetAtoms(imported.Id));

		object? exportObject = await commands.InvokeAsync(main, "memory_export", Args(new { }));
		using JsonDocument export = JsonDocument.Parse(JsonSerializer.Serialize(exportObject, BridgeJson.Options));
		Assert.Equal(1, export.RootElement.GetProperty("totalCount").GetInt32());
		string exportContent = export.RootElement.GetProperty("content").GetString()!;
		using JsonDocument exportDocument = JsonDocument.Parse(exportContent);
		JsonElement exported = Assert.Single(exportDocument.RootElement.GetProperty("memories").EnumerateArray());
		Assert.False(exported.TryGetProperty("embedding", out _));
		Assert.False(exported.TryGetProperty("status", out _));

		const string secret = "bridge-secret-must-not-leak";
		object? invalidObject = await commands.InvokeAsync(main, "memory_import_preview", Args(new
		{
			fileContent = $"{{\"version\":\"nori-memory-v1\",\"memories\":[{{\"content\":\"安全\",\"kind\":\"general\",\"embedding\":\"{secret}\"}}]}}",
		}));
		string invalidJson = JsonSerializer.Serialize(invalidObject, BridgeJson.Options);
		Assert.DoesNotContain(secret, invalidJson, StringComparison.Ordinal);
		Assert.Contains("记忆传输条目不符合安全格式", invalidJson, StringComparison.Ordinal);
	}

	[Fact]
	public async Task 内置市场安装正确接线并返回脱敏技能DTO()
	{
		BridgeCommands commands = CreateCommands();
		FakeBridgeSource main = new(WindowLabels.Main);
		int before = _runtime.SnapshotVersion;

		object? result = await commands.InvokeAsync(main, "skills_install_marketplace", Args(new {skillId = "gaming-partner"}));
		string json = JsonSerializer.Serialize(result, BridgeJson.Options);

		Assert.Contains("\"id\":\"gaming-partner\"", json, StringComparison.Ordinal);
		Assert.Contains("\"enabled\":true", json, StringComparison.Ordinal);
		Assert.Contains("\"instructions\":\"\"", json, StringComparison.Ordinal);
		Assert.DoesNotContain("【技能：二次元游戏陪玩与攻略解说】", json, StringComparison.Ordinal);
		Assert.DoesNotContain("searchWeb", json, StringComparison.Ordinal);
		Assert.True(_runtime.SnapshotVersion > before);
		var installed = Assert.Single(_runtime.Skills.GetInstalled(), skill => skill.Id == "gaming-partner");
		Assert.Equal("market", installed.Source);
		Assert.True(installed.Enabled);
	}

	[Fact]
	public async Task 内置市场安装只允许可见main()
	{
		BridgeCommands commands = CreateCommands();
		await Assert.ThrowsAsync<InvalidOperationException>(() =>
			commands.InvokeAsync(new FakeBridgeSource(WindowLabels.Init), "skills_install_marketplace",
				Args(new {skillId = "gaming-partner"})));
		await Assert.ThrowsAsync<InvalidOperationException>(() =>
			commands.InvokeAsync(new FakeBridgeSource(WindowLabels.Main, false), "skills_install_marketplace",
				Args(new {skillId = "gaming-partner"})));
		Assert.DoesNotContain(_runtime.Skills.GetInstalled(), skill => skill.Id == "gaming-partner");
	}

	[Fact]
	public async Task 内置市场安装拒绝未知ID并保持稳定错误()
	{
		BridgeCommands commands = CreateCommands();
		const string skillId = "not-a-market-skill";
		int before = _runtime.SnapshotVersion;

		InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
			commands.InvokeAsync(new FakeBridgeSource(WindowLabels.Main), "skills_install_marketplace",
				Args(new {skillId})));

		Assert.Equal($"未在市场中找到技能 ID: {skillId}", error.Message);
		Assert.Equal(before, _runtime.SnapshotVersion);
		Assert.DoesNotContain(_runtime.Skills.GetInstalled(), skill => skill.Id == skillId);
	}

	[Fact]
	public async Task 内置市场安装不接受自定义技能正文或SaveCustom()
	{
		BridgeCommands commands = CreateCommands();
		const string skillId = "custom-bypass";

		InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
			commands.InvokeAsync(new FakeBridgeSource(WindowLabels.Main), "skills_install_marketplace",
				Args(new
				{
					skillId,
					skill = new
					{
						id = skillId,
						name = "伪造技能",
						instructions = "不应被保存的隐藏正文",
					},
				})));

		Assert.Equal($"未在市场中找到技能 ID: {skillId}", error.Message);
		Assert.DoesNotContain(_runtime.Skills.GetInstalled(), skill => skill.Id == skillId);
	}


	/// <summary>
	/// 安全模式跳过的是**出网那一步**，不是「什么都不做」。
	///
	/// 本地记着的会话必须作废，否则退出安全模式之后那段「已经清掉」的上下文会原样回来。
	/// </summary>
	[Fact]
	public async Task 安全模式下清空同时作废本地会话()
	{
		using BridgeCommandsTests fixture = new(safeMode: true);
		fixture.ConfigureUnreachableLuoLiCore();
		Assert.Equal("sess_fixed", fixture._config.GetStringOr(LuoLiCoreSettingsStore.KeySessionId, ""));

		await fixture.CreateCommands().InvokeAsync(
			new FakeBridgeSource(WindowLabels.Main), "chat_clear", Args(new { }));

		// 连不上那个端口的话上面那一句会抛，能走到这里就说明没发请求。
		Assert.Equal("", fixture._config.GetStringOr(LuoLiCoreSettingsStore.KeySessionId, ""));
		Assert.Equal("", fixture._config.GetStringOr(LuoLiCoreSettingsStore.KeySessionOwner, ""));
	}

	/// <summary>
	/// 把 LuoLiCore 配成「已启用、已有会话、地址指向一个没人监听的端口」。
	///
	/// 端口选 1 是因为它不会有人在听：非安全模式下真去调那个重置端点必然连接失败，安全模式下
	/// 必须压根不发这次请求 —— 两条用例靠这个差别互为对照。
	/// </summary>
	private void ConfigureUnreachableLuoLiCore()
	{
		_config.Set(LuoLiCoreSettingsStore.KeyEnabled, new ConfigValue.Boolean(true));
		_config.Set(LuoLiCoreSettingsStore.KeyBaseUrl, new ConfigValue.Text("http://127.0.0.1:1"));
		_config.Set(LuoLiCoreSettingsStore.KeyApiKey, new ConfigValue.Text("sk-unreachable"));
		_config.Set(LuoLiCoreSettingsStore.KeySessionId, new ConfigValue.Text("sess_fixed"));
	}

	// ---- 文件访问设置页（工作目录 + 工具轮数）----

	[Fact]
	public async Task 设置工作目录之后文件工具才出现()
	{
		string folder = Path.Combine(_tempDir, "工作区");
		Directory.CreateDirectory(folder);
		BridgeCommands commands = CreateCommands();

		// 没配之前一件都没有 —— 让模型看见一件永远失败的工具只会让它反复试。
		Assert.Null(_runtime.Tools.Get("readFile"));

		await commands.InvokeAsync(
			new FakeBridgeSource(WindowLabels.Main), "settings_update_workspace", Args(new { root = folder }));

		Assert.NotNull(_runtime.Tools.Get("readFile"));
		Assert.NotNull(_runtime.Tools.Get("writeFile"));
	}

	/// <summary>
	/// 清空之后必须真的消失。
	///
	/// 工具在注册时把工作目录焊进了闭包，只靠「不再注册」的话旧工具还挂着、还指着旧目录 ——
	/// 用户在界面上清空了，她却照样读得到，这种不一致很难查。
	/// </summary>
	[Fact]
	public async Task 清空工作目录之后文件工具消失()
	{
		string folder = Path.Combine(_tempDir, "工作区");
		Directory.CreateDirectory(folder);
		BridgeCommands commands = CreateCommands();
		FakeBridgeSource main = new(WindowLabels.Main);

		await commands.InvokeAsync(main, "settings_update_workspace", Args(new { root = folder }));
		Assert.NotNull(_runtime.Tools.Get("readFile"));

		await commands.InvokeAsync(main, "settings_update_workspace", Args(new { root = "" }));

		Assert.Null(_runtime.Tools.Get("readFile"));
		Assert.Null(_runtime.Tools.Get("writeFile"));
	}

	/// <summary>
	/// 存一个不存在的路径不会报错，但那一族工具会静默不注册 —— 用户看到自己填了值、她却说
	/// 「我看不到文件」。就地拒绝比事后排查便宜得多。
	/// </summary>
	[Fact]
	public async Task 不存在的文件夹被就地拒绝()
	{
		BridgeCommands commands = CreateCommands();

		InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
			commands.InvokeAsync(
				new FakeBridgeSource(WindowLabels.Main),
				"settings_update_workspace",
				Args(new { root = Path.Combine(_tempDir, "并不存在") })));

		Assert.Contains("不存在", error.Message, StringComparison.Ordinal);
		Assert.Equal("", _config.GetStringOr(ConfigStore.KeyWorkspaceRoot, ""));
	}

	[Fact]
	public async Task 工具轮数存成整数并被夹回范围()
	{
		BridgeCommands commands = CreateCommands();
		FakeBridgeSource main = new(WindowLabels.Main);

		await commands.InvokeAsync(main, "settings_update_workspace", Args(new { maxToolIterations = 20 }));
		Assert.Equal(20, _runtime.Engine.ConfiguredToolIterations);

		await commands.InvokeAsync(main, "settings_update_workspace", Args(new { maxToolIterations = 999 }));
		Assert.Equal(Nori.Core.Agent.AgentEngine.MaxToolIterationsLimit, _runtime.Engine.ConfiguredToolIterations);

		// 存的是 Integer 而不是 Text：文本那条路上 "0"/"1" 会被解析成布尔再渲染成 "false"/"true"。
		await commands.InvokeAsync(main, "settings_update_workspace", Args(new { maxToolIterations = 1 }));
		Assert.Equal(1, _runtime.Engine.ConfiguredToolIterations);
	}

	[Fact]
	public async Task 快照把工作目录与轮数报给界面()
	{
		string folder = Path.Combine(_tempDir, "工作区");
		Directory.CreateDirectory(folder);
		BridgeCommands commands = CreateCommands();
		await commands.InvokeAsync(
			new FakeBridgeSource(WindowLabels.Main),
			"settings_update_workspace",
			Args(new { root = folder, maxToolIterations = 9 }));

		JsonElement snapshot = JsonSerializer.SerializeToElement(_runtime.BuildSnapshot(), BridgeJson.Options);
		JsonElement workspace = snapshot.GetProperty("workspace");

		Assert.Equal(folder, workspace.GetProperty("root").GetString());
		Assert.True(workspace.GetProperty("available").GetBoolean());
		Assert.Equal(9, workspace.GetProperty("maxToolIterations").GetInt32());
	}

	/// <summary>目录被删掉之后配置还在，但工具已经不注册了 —— 界面要能说出这个差别。</summary>
	[Fact]
	public async Task 目录事后被删掉时快照报告不可用()
	{
		string folder = Path.Combine(_tempDir, "会被删掉");
		Directory.CreateDirectory(folder);
		await CreateCommands().InvokeAsync(
			new FakeBridgeSource(WindowLabels.Main), "settings_update_workspace", Args(new { root = folder }));

		Directory.Delete(folder);
		_runtime.InvalidateSnapshot("workspace");

		JsonElement snapshot = JsonSerializer.SerializeToElement(_runtime.BuildSnapshot(), BridgeJson.Options);
		JsonElement workspace = snapshot.GetProperty("workspace");

		Assert.Equal(folder, workspace.GetProperty("root").GetString());
		Assert.False(workspace.GetProperty("available").GetBoolean());
	}

	[Fact]
	public async Task 设置窗口可以执行这两条命令()
	{
		// 原生设置页不走 WebView invoke，命令必须在 SettingsService 的白名单里，否则界面上
		// 的按钮点了会报「不允许执行」。
		Assert.Contains("settings_update_workspace", SettingsService.Commands);
		Assert.Contains("settings_pick_workspace", SettingsService.Commands);
		await Task.CompletedTask;
	}

	/// <summary>
	/// 安全模式禁用一切外部调用（AGENTS.md §1），重置远端会话是一次真正的出网请求。
	///
	/// 但清空本地记录本身是纯本地操作，不该被一起禁掉 —— 排障的时候连清个记录都做不到是过度
	/// 收紧。所以这一条要的是：跳过远端、本地照清、并且把「远端没清」如实报给调用方。
	/// </summary>
	[Fact]
	public async Task 安全模式下清空聊天记录不去碰远端()
	{
		using BridgeCommandsTests fixture = new(safeMode: true);
		fixture.ConfigureUnreachableLuoLiCore();
		fixture._services.Chat.SaveMessage("user", "在吗");
		fixture._services.Chat.SaveMessage("assistant", "在的");

		object? result = await fixture.CreateCommands().InvokeAsync(
			new FakeBridgeSource(WindowLabels.Main), "chat_clear", Args(new { }));

		// 连不上那个端口的话这里会抛，能返回就说明确实没发那次请求。
		string json = JsonSerializer.Serialize(result, BridgeJson.Options);
		Assert.Contains("\"remoteReset\":false", json, StringComparison.Ordinal);
		Assert.Contains("安全模式", json, StringComparison.Ordinal);
		Assert.Empty(fixture._services.Chat.GetHistory());
	}

	/// <summary>
	/// 非安全模式下这条命令**必须**真的去调远端 —— 否则上一条用例是恒真的。
	///
	/// 远端不可达时整条命令失败、本地记录原样留着：两边宁可都不清，也不能一边清了一边没清。
	/// </summary>
	[Fact]
	public async Task 非安全模式下清空会真的去调远端()
	{
		ConfigureUnreachableLuoLiCore();
		_services.Chat.SaveMessage("user", "在吗");

		await Assert.ThrowsAnyAsync<Exception>(() => CreateCommands().InvokeAsync(
			new FakeBridgeSource(WindowLabels.Main), "chat_clear", Args(new { })));

		Assert.NotEmpty(_services.Chat.GetHistory());
	}

	/// <summary>没接外部后端时不该给出那句提示 —— 它只会让人以为有什么东西没清干净。</summary>
	[Fact]
	public async Task 没接外部后端时安全模式下照常清空且不提示()
	{
		using BridgeCommandsTests fixture = new(safeMode: true);
		fixture._services.Chat.SaveMessage("user", "在吗");

		object? result = await fixture.CreateCommands().InvokeAsync(
			new FakeBridgeSource(WindowLabels.Main), "chat_clear", Args(new { }));

		string json = JsonSerializer.Serialize(result, BridgeJson.Options);
		Assert.DoesNotContain("安全模式", json, StringComparison.Ordinal);
		Assert.Empty(fixture._services.Chat.GetHistory());
	}

	[Fact]
	public async Task 安全模式仍可安装内置市场技能()
	{
		using BridgeCommandsTests fixture = new(true);
		object? result = await fixture.CreateCommands().InvokeAsync(
			new FakeBridgeSource(WindowLabels.Main), "skills_install_marketplace", Args(new {skillId = "gaming-partner"}));

		string json = JsonSerializer.Serialize(result, BridgeJson.Options);
		Assert.Contains("\"id\":\"gaming-partner\"", json, StringComparison.Ordinal);
		Assert.DoesNotContain("安全模式", json, StringComparison.Ordinal);
	}

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
		object? result = await commands.InvokeAsync(new FakeBridgeSource(WindowLabels.Main), "automation_get_snapshot", Args(new { }));
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
	public async Task 桌面视觉命令在默认关闭安全模式非Windows和错误调用方时拒绝()
	{
		using BridgeCommandsTests defaultFixture = new(false, true, automationVision: true);
		BridgeCommands defaultCommands = defaultFixture.CreateCommands();
		InvalidOperationException defaultError = await Assert.ThrowsAsync<InvalidOperationException>(() =>
			defaultCommands.InvokeAsync(new FakeBridgeSource(WindowLabels.Main), "automation_desktop_list_windows", Args(new { })));
		Assert.Contains("默认关闭", defaultError.Message, StringComparison.Ordinal);
		await Assert.ThrowsAsync<InvalidOperationException>(() =>
			defaultCommands.InvokeAsync(new FakeBridgeSource(WindowLabels.Init), "automation_desktop_list_windows", Args(new { })));

		using BridgeCommandsTests safeFixture = new(true, true, automationVision: true);
		safeFixture.ConfigureDesktop();
		InvalidOperationException safeError = await Assert.ThrowsAsync<InvalidOperationException>(() =>
			safeFixture.CreateCommands().InvokeAsync(new FakeBridgeSource(WindowLabels.Main), "automation_desktop_list_windows", Args(new { })));
		Assert.Contains("安全模式", safeError.Message, StringComparison.Ordinal);

		using BridgeCommandsTests linuxFixture = new(false, false, automationVision: true);
		linuxFixture.ConfigureDesktop();
		InvalidOperationException linuxError = await Assert.ThrowsAsync<InvalidOperationException>(() =>
			linuxFixture.CreateCommands().InvokeAsync(new FakeBridgeSource(WindowLabels.Main), "automation_desktop_list_windows", Args(new { })));
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

		object result = await fixture.CreateCommands().InvokeAsync(
			new FakeBridgeSource(WindowLabels.Main), "automation_desktop_list_windows", Args(new { })) ?? throw new InvalidOperationException();
		string json = JsonSerializer.Serialize(result, BridgeJson.Options);

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
		BridgeCommands commands = fixture.CreateCommands();
		AutomationDesktopWindowSnapshot window = SingleWindow(await commands.InvokeAsync(
			new FakeBridgeSource(WindowLabels.Main), "automation_desktop_list_windows", Args(new { })));

		const string secretTask = "把窗口中的 secret-input 发送出去";
		AutomationDesktopTaskStartSnapshot start = Assert.IsType<AutomationDesktopTaskStartSnapshot>(await commands.InvokeAsync(
			new FakeBridgeSource(WindowLabels.Main), "automation_desktop_start",
			Args(new {task = secretTask, targetToken = window.Token})));
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
		BridgeCommands commands = fixture.CreateCommands();
		AutomationDesktopWindowSnapshot window = SingleWindow(await commands.InvokeAsync(
			new FakeBridgeSource(WindowLabels.Main), "automation_desktop_list_windows", Args(new { })));
		AutomationDesktopTaskStartSnapshot start = Assert.IsType<AutomationDesktopTaskStartSnapshot>(await commands.InvokeAsync(
			new FakeBridgeSource(WindowLabels.Main), "automation_desktop_start",
			Args(new {task = "可取消任务", targetToken = window.Token})));
		await runner!.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

		Assert.Equal(true, await commands.InvokeAsync(new FakeBridgeSource(WindowLabels.Main), "automation_desktop_stop", Args(new {taskId = start.TaskId})));
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
		BridgeCommands commands = fixture.CreateCommands();
		AutomationDesktopWindowSnapshot window = SingleWindow(await commands.InvokeAsync(
			new FakeBridgeSource(WindowLabels.Main), "automation_desktop_list_windows", Args(new { })));
		AutomationDesktopTaskStartSnapshot start = Assert.IsType<AutomationDesktopTaskStartSnapshot>(await commands.InvokeAsync(
			new FakeBridgeSource(WindowLabels.Main), "automation_desktop_start",
			Args(new {task = "高风险输入", targetToken = window.Token})));

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
		BridgeCommands commands = fixture.CreateCommands();
		AutomationDesktopWindowSnapshot window = SingleWindow(await commands.InvokeAsync(
			new FakeBridgeSource(WindowLabels.Main), "automation_desktop_list_windows", Args(new { })));
		AutomationDesktopTaskStartSnapshot start = Assert.IsType<AutomationDesktopTaskStartSnapshot>(await commands.InvokeAsync(
			new FakeBridgeSource(WindowLabels.Main), "automation_desktop_start",
			Args(new {task = "正文 secret-task", targetToken = window.Token})));

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
			await commands.InvokeAsync(main, "automation_get_snapshot", Args(new { })), BridgeJson.Options);
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
			"llm_fetch_models", "llm_test_connection", "embedding_test_connection", "settings_test_ai", "settings_test_embedding", "ai_test_connection", "chat_start",
			"memory_search_hybrid", "memory_reembed_all", "memory_recall_debug", "memory_knowledge_reindex",
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

	[Fact]
	public async Task 窗口命令只能操作自身且主界面唤出伴侣要求有效模型()
	{
		BridgeCommands commands = CreateCommands();
		await commands.InvokeAsync(new FakeBridgeSource(WindowLabels.FirstRun), "exit_app", Args(new { }));
		await Assert.ThrowsAsync<InvalidOperationException>(() =>
			commands.InvokeAsync(new FakeBridgeSource(WindowLabels.Init), "window_show", Args(new {label = WindowLabels.Main})));
		await Assert.ThrowsAsync<InvalidOperationException>(() =>
			commands.InvokeAsync(new FakeBridgeSource(WindowLabels.Main), "window_show", Args(new {label = WindowLabels.Pet})));

		InstallKnownModel("nori");
		_config.Set(ConfigStore.KeySelectedModel, new ConfigValue.Text("nori"));
		await commands.InvokeAsync(new FakeBridgeSource(WindowLabels.Main), "window_show", Args(new {label = WindowLabels.Pet}));
		Assert.True(_windows.IsWindowVisible(WindowLabels.Pet));
	}

	[Fact]
	public async Task 首启与主界面都可更新AI设置但密钥只写不读()
	{
		BridgeCommands commands = CreateCommands();
		await commands.InvokeAsync(new FakeBridgeSource("first-run"), "settings_update_ai", Args(new
		{
			baseUrl = "https://api.example.com/v1",
			apiKey = "sk-new",
			model = "gpt-x",
		}));
		Assert.Equal("sk-new", _config.GetStringOr("llm_api_key", ""));

		// 显式空串清除密钥
		await commands.InvokeAsync(new FakeBridgeSource("main"), "settings_update_ai", Args(new {apiKey = ""}));
		Assert.False(_config.Exists("llm_api_key"));
	}

	[Fact]
	public async Task 统一AI设置更新保持聊天与Embedding独立()
	{
		BridgeCommands commands = CreateCommands();
		await commands.InvokeAsync(new FakeBridgeSource(WindowLabels.Main), "settings_update_ai", Args(new
		{
			baseUrl = "https://chat.example/v1",
			apiKey = "chat-secret",
			model = "chat-model",
			embedding = new
			{
				baseUrl = "http://localhost:11434/v1",
				model = "local-embedding",
				apiKey = "",
			},
		}));

		AiProviderSettings settings = _services.AiSettings.Read();
		Assert.Equal("https://chat.example/v1", settings.Chat.BaseUrl);
		Assert.Equal("chat-secret", settings.Chat.ApiKey);
		Assert.Equal("http://localhost:11434/v1", settings.Embedding.BaseUrl);
		Assert.Equal("local-embedding", settings.Embedding.Model);
		Assert.Empty(settings.Embedding.ApiKey);
		Assert.True(settings.Embedding.IsConfigured);
	}

	[Fact]
	public async Task 新统一AI命令接受嵌套聊天与Embedding补丁()
	{
		BridgeCommands commands = CreateCommands();
		await commands.InvokeAsync(new FakeBridgeSource(WindowLabels.Main), "settings_update_ai_providers", Args(new
		{
			chat = new
			{
				baseUrl = "https://chat.example/v1",
				model = "chat-model",
			},
			persona = "保持简洁",
			embedding = new
			{
				baseUrl = "http://127.0.0.1:11434/v1",
				model = "nomic-embed-text",
				dimensions = "768",
			},
		}));

		AiProviderSettings settings = _services.AiSettings.Read();
		Assert.Equal("https://chat.example/v1", settings.Chat.BaseUrl);
		Assert.Equal("chat-model", settings.Chat.Model);
		Assert.Equal("保持简洁", settings.Chat.Persona);
		Assert.Equal("http://127.0.0.1:11434/v1", settings.Embedding.BaseUrl);
		Assert.Equal("nomic-embed-text", settings.Embedding.Model);
		Assert.Equal(768, settings.Embedding.Dimensions);
	}

	[Fact]
	public async Task approval_respond未匹配请求返回false()
	{
		BridgeCommands commands = CreateCommands();
		object? result = await commands.InvokeAsync(
			new FakeBridgeSource("main"), "approval_respond", Args(new {requestId = "missing", approved = true}));
		Assert.Equal(false, result);
	}

	// ---- 历史规范化 ----

	[Fact]
	public async Task chat_history_page过滤反馈行并规范化旧协议JSON()
	{
		_services.Chat.SaveMessage("assistant", "```json\n{\"type\": \"message\", \"text\": \"旧版回复\"}\n```");
		_services.Chat.SaveMessage("user", "【系统工具执行反馈 - getTime】:\n{}");
		_services.Chat.SaveMessage("user", "你好");

		BridgeCommands commands = CreateCommands();
		var page = await commands.InvokeAsync(new FakeBridgeSource("main"), "chat_history_page", Args(new {limit = 10}));
		var rows = ((IEnumerable<object>)page!).ToList();

		Assert.Equal(2, rows.Count);
		JsonSerializerOptions relaxed = new() {Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping};
		string json = JsonSerializer.Serialize(rows, relaxed);
		Assert.Contains("旧版回复", json, StringComparison.Ordinal);
		Assert.DoesNotContain("系统工具执行反馈", json, StringComparison.Ordinal);
	}

	// ---- 提醒持久化 ----

	[Fact]
	public async Task reminder_add落库并可被新store恢复()
	{
		BridgeCommands commands = CreateCommands();
		object? added = await commands.InvokeAsync(
			new FakeBridgeSource("main"), "reminder_add", Args(new {content = "喝水", delayMinutes = 30}));
		Assert.NotNull(added);

		// 新的 store 实例从同一数据库读到该提醒 (重启恢复语义)
		Nori.Core.Proactive.ReminderStore store = new(_database);
		Assert.Single(store.List(), item => item.Content == "喝水");
	}

	[Fact]
	public async Task 到期提醒由TakeDue领取并等待确认()
	{
		Nori.Core.Proactive.ReminderStore store = new(_database);
		store.Add("过期提醒", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 1000);

		var due = store.TakeDue(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
		Assert.Single(due);
		Assert.Empty(store.TakeDue(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
	}

	[Fact]
	public async Task reminder_update_snooze_complete更新快照并停止调度()
	{
		BridgeCommands commands = CreateCommands();
		FakeBridgeSource main = new(WindowLabels.Main);
		Nori.Core.Proactive.ReminderItem added = Assert.IsType<Nori.Core.Proactive.ReminderItem>(await commands.InvokeAsync(
			main, "reminder_add", Args(new {content = "原始提醒", delayMinutes = 30})));

		int before = _runtime.SnapshotVersion;
		long triggerTime = DateTimeOffset.UtcNow.AddMinutes(20).ToUnixTimeMilliseconds();
		Nori.Core.Proactive.ReminderItem updated = Assert.IsType<Nori.Core.Proactive.ReminderItem>(await commands.InvokeAsync(
			main, "reminder_update", Args(new
			{
				id = added.Id,
				content = "更新提醒",
				triggerTime,
				repeatDaily = true,
				timezone = "UTC",
				recurrenceJson = "{\"type\":\"daily\"}",
			})));
		Assert.True(_runtime.SnapshotVersion > before);
		Assert.Equal("更新提醒", updated.Content);
		Assert.True(updated.RepeatDaily);
		Assert.Equal("UTC", updated.Timezone);
		Assert.Equal("{\"type\":\"daily\"}", updated.RecurrenceJson);

		Nori.Core.Proactive.ReminderItem snoozed = Assert.IsType<Nori.Core.Proactive.ReminderItem>(await commands.InvokeAsync(
			main, "reminder_snooze", Args(new {id = added.Id, delayMinutes = 15})));
		Assert.NotNull(snoozed.SnoozedUntil);
		Assert.Equal(true, await commands.InvokeAsync(main, "reminder_complete", Args(new {id = added.Id})));
		Assert.Empty(new Nori.Core.Proactive.ReminderStore(_database).TakeDue(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 86_400_000));
		object? listed = await commands.InvokeAsync(main, "reminder_list", Args(new { }));
		Assert.NotNull(listed);
		Assert.Empty((IReadOnlyList<Nori.Core.Proactive.ReminderItem>)listed!);
	}

	[Fact]
	public async Task reminder_cancel保持旧返回值并写入取消终态()
	{
		BridgeCommands commands = CreateCommands();
		FakeBridgeSource main = new(WindowLabels.Main);
		Nori.Core.Proactive.ReminderItem added = Assert.IsType<Nori.Core.Proactive.ReminderItem>(await commands.InvokeAsync(
			main, "reminder_add", Args(new {content = "待取消提醒", delayMinutes = 15})));
		Assert.Equal(true, await commands.InvokeAsync(main, "reminder_cancel", Args(new {id = added.Id})));
		Assert.Equal("cancelled", new Nori.Core.Proactive.ReminderStore(_database).Get(added.Id)!.Status);
		Assert.Equal(false, await commands.InvokeAsync(main, "reminder_cancel", Args(new {id = added.Id})));
	}

	[Fact]
	public async Task reminder命令拒绝非main和越界参数()
	{
		BridgeCommands commands = CreateCommands();
		FakeBridgeSource pet = new(WindowLabels.Pet);
		string[] commandsToCheck = ["reminder_update", "reminder_snooze", "reminder_complete", "reminder_list"];
		foreach (string command in commandsToCheck)
		{
			await Assert.ThrowsAsync<InvalidOperationException>(() => commands.InvokeAsync(pet, command, Args(new {id = "missing"})));
		}

		await Assert.ThrowsAsync<InvalidOperationException>(() => commands.InvokeAsync(
			new FakeBridgeSource(WindowLabels.Main), "reminder_add", Args(new {content = new string('x', 201), delayMinutes = 15})));
		Nori.Core.Proactive.ReminderItem added = Assert.IsType<Nori.Core.Proactive.ReminderItem>(await commands.InvokeAsync(
			new FakeBridgeSource(WindowLabels.Main), "reminder_add", Args(new {content = "边界提醒", delayMinutes = 15})));
		await Assert.ThrowsAsync<InvalidOperationException>(() => commands.InvokeAsync(
			new FakeBridgeSource(WindowLabels.Main), "reminder_snooze", Args(new {id = added.Id, delayMinutes = 0})));
		await Assert.ThrowsAsync<InvalidOperationException>(() => commands.InvokeAsync(
			new FakeBridgeSource(WindowLabels.Main), "reminder_update", Args(new {id = added.Id, timezone = "Not/AZone"})));
		await Assert.ThrowsAsync<InvalidOperationException>(() => commands.InvokeAsync(
			new FakeBridgeSource(WindowLabels.Main), "reminder_update", Args(new {id = added.Id})));
		Assert.DoesNotContain("边界提醒", string.Join("\n", _services.Logger.RecentLogs().Select(entry => entry.Message)), StringComparison.Ordinal);
	}

	// ---- 工具手动测试边界 ----

	[Fact]
	public async Task tools_execute_manual非safe工具拒绝()
	{
		BridgeCommands commands = CreateCommands();
		await Assert.ThrowsAsync<InvalidOperationException>(() =>
			commands.InvokeAsync(new FakeBridgeSource("main"), "tools_execute_manual",
				Args(new {name = "setClipboardText", arguments = new {text = "x"}})));
	}

	// ---- 旧通用入口已移除 ----

	[Fact]
	public async Task 旧版通用config命令已下线()
	{
		BridgeCommands commands = CreateCommands();
		string[] legacyCommands = ["get_config", "set_config", "fetch_remote_text", "search_anysearch"];
		foreach (string cmd in legacyCommands)
		{
			await Assert.ThrowsAsync<InvalidOperationException>(() =>
				commands.InvokeAsync(new FakeBridgeSource("main"), cmd, Args(new {key = "x"})));
		}
	}

	// ---- 初始化握手与窗口状态 ----

	private void InstallKnownModel(string modelId)
	{
		string directory = _services.Resources.ResourceDir(ResourceType.Live2D, modelId);
		Directory.CreateDirectory(directory);
		File.WriteAllText(Path.Combine(directory, $"{modelId}.model3.json"),
			"{\"FileReferences\":{\"Moc\":\"model.moc3\",\"Textures\":[]}}");
		File.WriteAllText(Path.Combine(directory, "model.moc3"), "MOC3");
	}

	[Fact]
	public async Task model_select与显示参数拒绝未知未安装和越界输入()
	{
		BridgeCommands commands = CreateCommands();
		FakeBridgeSource main = new(WindowLabels.Main);
		await Assert.ThrowsAsync<InvalidOperationException>(() =>
			commands.InvokeAsync(main, "model_select", Args(new {modelId = "other"})));
		await Assert.ThrowsAsync<InvalidOperationException>(() =>
			commands.InvokeAsync(main, "model_select", Args(new {modelId = "nori"})));

		InstallKnownModel("nori");
		await commands.InvokeAsync(main, "model_select", Args(new {modelId = "nori"}));
		Assert.Equal("nori", _config.GetStringOr(ConfigStore.KeySelectedModel, ""));

		await Assert.ThrowsAsync<InvalidOperationException>(() =>
			commands.InvokeAsync(main, "model_set_display", Args(new {modelId = "nori", opacity = 2})));
		await Assert.ThrowsAsync<InvalidOperationException>(() =>
			commands.InvokeAsync(main, "model_set_display", Args(new {modelId = "nori", qualityMode = "unknown"})));
	}

	[Fact]
	public async Task 首启初始化和窗口命令通过同步UI调度器完成()
	{
		InstallKnownModel("nori");
		SynchronousUiDispatcher dispatcher = new();
		BridgeCommands commands = CreateCommands(dispatcher);

		await commands.InvokeAsync(new FakeBridgeSource(WindowLabels.FirstRun), "complete_first_run",
			Args(new {modelId = "nori", telemetryEnabled = false}));
		await commands.InvokeAsync(new FakeBridgeSource(WindowLabels.Init), "init_enter_main", Args(new { }));
		await commands.InvokeAsync(new FakeBridgeSource(WindowLabels.Main), "window_hide", Args(new { }));
		await commands.InvokeAsync(new FakeBridgeSource(WindowLabels.Main), "window_show", Args(new { }));

		Assert.True(_windows.IsWindowVisible(WindowLabels.Main));
		Assert.False(_windows.IsWindowVisible(WindowLabels.Init));
		Assert.Equal(6, dispatcher.InvokeCount);
	}

	[Fact]
	public async Task complete_first_run要求可见首启窗口和已安装已知模型并原子提交()
	{
		InstallKnownModel("nori");
		BridgeCommands commands = CreateCommands();

		await Assert.ThrowsAsync<InvalidOperationException>(() =>
			commands.InvokeAsync(new FakeBridgeSource(WindowLabels.Main), "complete_first_run",
				Args(new {modelId = "nori", telemetryEnabled = false})));
		await Assert.ThrowsAsync<InvalidOperationException>(() =>
			commands.InvokeAsync(new FakeBridgeSource(WindowLabels.FirstRun, false), "complete_first_run",
				Args(new {modelId = "nori", telemetryEnabled = false})));
		await Assert.ThrowsAsync<InvalidOperationException>(() =>
			commands.InvokeAsync(new FakeBridgeSource(WindowLabels.FirstRun), "complete_first_run",
				Args(new {modelId = "other", telemetryEnabled = false})));
		Assert.True(_config.IsFirstRun());

		await commands.InvokeAsync(new FakeBridgeSource(WindowLabels.FirstRun), "complete_first_run",
			Args(new {modelId = "nori", telemetryEnabled = false}));

		Assert.False(_config.IsFirstRun());
		Assert.Equal("nori", _config.GetStringOr(ConfigStore.KeySelectedModel, ""));
		Assert.False(_config.GetBoolOr(ConfigStore.KeyTelemetryEnabled, true));
		Assert.NotNull(_config.GetInitConfig().InitializedAt);
		Assert.True(_windows.IsWindowVisible(WindowLabels.Init));
	}

	[Fact]
	public async Task init_enter_main只允许可见init并按有效模型和自动唤出切换窗口()
	{
		InstallKnownModel("arg-nori");
		_config.Set(ConfigStore.KeySelectedModel, new ConfigValue.Text("arg-nori"));
		_config.Set("pet_auto_summon", new ConfigValue.Boolean(true));
		BridgeCommands commands = CreateCommands();

		await Assert.ThrowsAsync<InvalidOperationException>(() =>
			commands.InvokeAsync(new FakeBridgeSource(WindowLabels.Main), "init_enter_main", Args(new { })));
		await Assert.ThrowsAsync<InvalidOperationException>(() =>
			commands.InvokeAsync(new FakeBridgeSource(WindowLabels.Init, false), "init_enter_main", Args(new { })));

		_windows.Show(WindowLabels.Init);
		await commands.InvokeAsync(new FakeBridgeSource(WindowLabels.Init), "init_enter_main", Args(new { }));

		Assert.True(_windows.IsWindowVisible(WindowLabels.Main));
		Assert.True(_windows.IsWindowVisible(WindowLabels.Pet));
		Assert.False(_windows.IsWindowVisible(WindowLabels.Init));
	}

	[Fact]
	public async Task init_enter_main无效模型时不显示伴侣但仍进入主界面()
	{
		_config.Set(ConfigStore.KeySelectedModel, new ConfigValue.Text("other"));
		_windows.Show(WindowLabels.Init);
		_windows.Show(WindowLabels.Pet);
		BridgeCommands commands = CreateCommands();

		await commands.InvokeAsync(new FakeBridgeSource(WindowLabels.Init), "init_enter_main", Args(new { }));

		Assert.True(_windows.IsWindowVisible(WindowLabels.Main));
		Assert.False(_windows.IsWindowVisible(WindowLabels.Pet));
		Assert.False(_windows.IsWindowVisible(WindowLabels.Init));
	}

	[Fact]
	public async Task init_ready只允许init窗口且标志只能取一次()
	{
		BridgeCommands commands = CreateCommands();
		await Assert.ThrowsAsync<InvalidOperationException>(() =>
			commands.InvokeAsync(new FakeBridgeSource("main"), "init_ready", Args(new { })));

		// 尚未发生时为 false
		string before = JsonSerializer.Serialize(
			await commands.InvokeAsync(new FakeBridgeSource("init"), "init_ready", Args(new { })));
		Assert.Contains("\"initStartPending\":false", before, StringComparison.Ordinal);

		_runtime.MarkInitStartPending();
		string first = JsonSerializer.Serialize(
			await commands.InvokeAsync(new FakeBridgeSource("init"), "init_ready", Args(new { })));
		string second = JsonSerializer.Serialize(
			await commands.InvokeAsync(new FakeBridgeSource("init"), "init_ready", Args(new { })));

		Assert.Contains("\"initStartPending\":true", first, StringComparison.Ordinal);
		Assert.Contains("\"initStartPending\":false", second, StringComparison.Ordinal);
	}

	[Fact]
	public async Task 快照包含伴侣可见性与侧边栏折叠态()
	{
		BridgeCommands commands = CreateCommands();

		string hidden = JsonSerializer.Serialize(
			await commands.InvokeAsync(new FakeBridgeSource("main"), "ui_get_snapshot", Args(new { })));
		using JsonDocument hiddenDocument = JsonDocument.Parse(hidden);
		Assert.False(hiddenDocument.RootElement.GetProperty("pet").GetProperty("visible").GetBoolean());
		Assert.False(hiddenDocument.RootElement.GetProperty("general").GetProperty("sidebarCollapsed").GetBoolean());

		_windows.Show(WindowLabels.Pet);
		await commands.InvokeAsync(new FakeBridgeSource("main"), "settings_update_general", Args(new {sidebarCollapsed = true}));

		string shown = JsonSerializer.Serialize(
			await commands.InvokeAsync(new FakeBridgeSource("main"), "ui_get_snapshot", Args(new { })));
		using JsonDocument shownDocument = JsonDocument.Parse(shown);
		Assert.True(shownDocument.RootElement.GetProperty("pet").GetProperty("visible").GetBoolean());
		Assert.True(shownDocument.RootElement.GetProperty("general").GetProperty("sidebarCollapsed").GetBoolean());
	}

	[Fact]
	public async Task 快照包含遥测状态且通用设置可即时关闭()
	{
		_config.SetTelemetryConsent(TelemetryConsent.Granted);
		BridgeCommands commands = CreateCommands();
		string before = JsonSerializer.Serialize(
			await commands.InvokeAsync(new FakeBridgeSource("main"), "ui_get_snapshot", Args(new { })));
		using JsonDocument beforeDocument = JsonDocument.Parse(before);
		JsonElement beforeTelemetry = beforeDocument.RootElement.GetProperty("telemetry");
		Assert.True(beforeTelemetry.GetProperty("enabled").GetBoolean());
		Assert.False(beforeTelemetry.GetProperty("available").GetBoolean());
		Assert.Equal("granted", beforeTelemetry.GetProperty("consent").GetString());

		await commands.InvokeAsync(new FakeBridgeSource("main"), "settings_update_general", Args(new {telemetryEnabled = false}));
		Assert.Equal(TelemetryConsent.Denied, _config.GetTelemetryConsent());
		string after = JsonSerializer.Serialize(
			await commands.InvokeAsync(new FakeBridgeSource("main"), "ui_get_snapshot", Args(new { })));
		using JsonDocument afterDocument = JsonDocument.Parse(after);
		JsonElement afterTelemetry = afterDocument.RootElement.GetProperty("telemetry");
		Assert.False(afterTelemetry.GetProperty("enabled").GetBoolean());
		Assert.False(afterTelemetry.GetProperty("available").GetBoolean());
		Assert.Equal("denied", afterTelemetry.GetProperty("consent").GetString());
	}

	[Fact]
	public void 同版本快照复用缓存且失效后重建()
	{
		FakeBridgeSource source = new(WindowLabels.Main);
		object first = _runtime.BuildSnapshot(source);
		object cached = _runtime.BuildSnapshot(source);
		Assert.Same(first, cached);

		_runtime.InvalidateSnapshot("test");
		object rebuilt = _runtime.BuildSnapshot(source);
		Assert.NotSame(first, rebuilt);
	}

	[Fact]
	public void 伴侣显隐变化作废快照()
	{
		// 广播本体走 Dispatcher.UIThread, 单测无 UI 循环, 因此只验证版本递增与快照投影
		int before = _runtime.SnapshotVersion;

		_windows.TogglePet();

		Assert.True(_runtime.SnapshotVersion > before);
		Assert.True(_windows.IsWindowVisible(WindowLabels.Pet));
	}

	[Fact]
	public async Task Snapshot_ContainsUpdaterState()
	{
		BridgeCommands commands = CreateCommands();
		object? snapshot = await commands.InvokeAsync(new FakeBridgeSource(WindowLabels.Main), "ui_get_snapshot", Args(new { }));
		Assert.NotNull(snapshot);

		string json = JsonSerializer.Serialize(snapshot);
		using JsonDocument doc = JsonDocument.Parse(json);
		Assert.True(doc.RootElement.TryGetProperty("updater", out JsonElement updaterEl));
		Assert.Equal("idle", updaterEl.GetProperty("state").GetString());
	}

	[Fact]
	public async Task WindowOpenSettings_MainSourceDelegatesToWindowManager()
	{
		BridgeCommands commands = CreateCommands();
		object? result = await commands.InvokeAsync(
			new FakeBridgeSource(WindowLabels.Main),
			"window_open_settings",
			Args(new {page = "ai"}));

		Assert.Null(result);
		Assert.Equal(["ai"], _windows.SettingsPages);
	}

	[Fact]
	public async Task WindowOpenSettings_NonMainSourceThrows()
	{
		BridgeCommands commands = CreateCommands();

		await Assert.ThrowsAsync<InvalidOperationException>(() => commands.InvokeAsync(
			new FakeBridgeSource(WindowLabels.Pet),
			"window_open_settings",
			Args(new {page = "ai"})));
		Assert.Empty(_windows.SettingsPages);
	}

	[Fact]
	public Task NativeSettingsServiceUsesSharedSnapshotAndStateNotification() => WithSettingsUiAsync(async () =>
	{
		using SettingsService settings = new(_services, new Window());
		int stateChanged = 0;
		settings.StateChanged += () => stateChanged++;

		await settings.ExecuteAsync("settings_update_general", new {autoCheckUpdates = false});
		Assert.Equal("false", _config.GetStringOr("auto_check_updates", "true"));
		Assert.True(stateChanged > 0);

		JsonElement snapshot = await settings.GetSnapshotAsync();
		Assert.False(snapshot.GetProperty("general").GetProperty("autoCheckUpdates").GetBoolean());
	});

	[Fact]
	public Task NativeMcpReadDoesNotTriggerAnotherRefresh() => WithSettingsUiAsync(async () =>
	{
		Window window = new();
		using SettingsService settings = new(_services, window);
		int changes = 0;
		settings.StateChanged += () => changes++;
		try
		{
			window.Show();
			await settings.ExecuteAsync("mcp_get_servers");
			Assert.Equal(0, changes);
		}
		finally { window.Close(); }
	});

	[Fact]
	public Task NativeSettingsServiceRejectsCommandsOutsideSettingsPolicy() => WithSettingsUiAsync(async () =>
	{
		using SettingsService settings = new(_services, new Window());

		await Assert.ThrowsAsync<InvalidOperationException>(() => settings.ExecuteAsync("chat_start", new {text = "不能从设置窗口发起聊天"}));
		await Assert.ThrowsAsync<InvalidOperationException>(() => settings.ExecuteAsync("window_open_settings", new {page = "ai"}));
	});

	[Fact]
	public Task NativeSettingsThemeInitializesControlTemplates() => WithSettingsUiAsync(() =>
	{
		DevolutionsMacOsTheme theme = Assert.IsType<DevolutionsMacOsTheme>(Assert.Single(Application.Current!.Styles));
		Assert.NotEmpty(theme);
		Button button = new() {Content = "测试"};
		TextBox input = new();
		Window window = new() {Content = new StackPanel {Children = {button, input}}};
		try
		{
			window.Show();
			window.UpdateLayout();
			Assert.NotNull(button.Template);
			Assert.NotNull(input.Template);
		}
		finally { window.Close(); }
		return Task.CompletedTask;
	});

	[Theory]
	[InlineData(720, 480)]
	[InlineData(1920, 1080)]
	public Task NativeSettingsWindowRefreshesSnapshotOnUiThread(int width, int height) => WithSettingsUiAsync(async () =>
	{
		using BridgeCommandsTests fixture = new(safeMode: true);
		fixture._config.Set(ConfigStore.KeyLanguage, new ConfigValue.Text("en-US"));
		using SettingsService service = new(fixture._services, new Window());
		using SettingsViewModel viewModel = new(service);
		SettingsWindow window = new() {DataContext = viewModel, Width = width, Height = height};
		try
		{
			window.Show();
			await viewModel.RefreshSnapshotAsync();
			window.UpdateLayout();
			Assert.Equal(string.Empty, viewModel.ErrorMessage);
			Assert.Equal("en-US", viewModel.Language);
			SettingsPagePresenter presenter = Assert.IsType<SettingsPagePresenter>(window.FindControl<SettingsPagePresenter>("PagePresenter"));
			Assert.IsType<StackPanel>(presenter.Content);
			Assert.True(presenter.Bounds.Width > 0);
			Assert.True(presenter.Bounds.Height > 0);
			// 状态通知来自后台线程，也必须走同一条 UI 刷新路径。
			await Task.Run(() => viewModel.RefreshSnapshotAsync());
			Assert.Equal(string.Empty, viewModel.ErrorMessage);
		}
		finally
		{
			window.DataContext = null;
			window.Close();
			SettingsLocalization.SetLanguage("zh-CN");
		}
	});

	[Fact]
	public Task NativeSettingsPagesSurviveAttachmentAndNavigation() => WithSettingsUiAsync(async () =>
	{
		Window window = new() {Width = 720, Height = 480};
		using SettingsService service = new(_services, window);
		using SettingsViewModel viewModel = new(service);
		SettingsPagePresenter presenter = new();
		try
		{
			window.Show();
			foreach (string key in new[] {"ai", "skills", "mcp", "automation", "plugins", "debug", "general", "ai"})
			{
				window.Content = null;
				viewModel.Navigate(key);
				await viewModel.RefreshSnapshotAsync();
				presenter.DataContext = viewModel.CurrentPage;
				object? content = presenter.Content;
				window.Content = presenter;
				window.UpdateLayout();
				if (viewModel.CurrentPage is NativeSettingsPageBase)
				{
					Assert.IsType<NativeSettingsPagePresenter>(presenter.Content);
					Assert.Same(content, presenter.Content);
				}
				else
				{
					StackPanel form = Assert.IsType<StackPanel>(presenter.Content);
					Assert.Contains(form.Children, child => child is Border);
				}
				Assert.True(presenter.Bounds.Width > 0);
				Assert.True(presenter.Bounds.Height > 0);
				presenter.RefreshPage();
			}
		}
		finally
		{
			presenter.DataContext = null;
			window.Close();
		}
	});

	[Theory]
	[InlineData(720, 480)]
	[InlineData(1920, 1080)]
	public Task NativeDiagnosticsRefreshPreservesControlsAndScroll(int width, int height) => WithSettingsUiAsync(async () =>
	{
		using BridgeCommandsTests fixture = new(safeMode: true);
		for (int index = 0; index < 80; index++)
			fixture._services.Logger.Write(LogSource.Backend, "warn", $"诊断滚动回归日志 {index:D3}");
		SettingsWindow window = new() {Width = width, Height = height};
		using SettingsService service = new(fixture._services, window);
		using SettingsViewModel viewModel = new(service);
		window.DataContext = viewModel;
		try
		{
			window.Show();
			viewModel.Navigate("debug");
			await viewModel.RefreshSnapshotAsync();
			await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
			Assert.Empty(viewModel.ErrorMessage);
			SettingsPagePresenter pagePresenter = Assert.IsType<SettingsPagePresenter>(window.FindControl<SettingsPagePresenter>("PagePresenter"));
			NativeSettingsPagePresenter presenter = Assert.IsType<NativeSettingsPagePresenter>(pagePresenter.Content);
			Control root = Assert.IsAssignableFrom<Control>(presenter.Content);
			ComboBox filter = Assert.Single(root.GetLogicalDescendants().OfType<ComboBox>());
			ScrollViewer logs = root.GetLogicalDescendants().OfType<ScrollViewer>().Single(scroll => scroll.Name == "DebugLogScroll");
			ScrollViewer pageScroll = pagePresenter.GetVisualAncestors().OfType<ScrollViewer>().First();
			filter.SelectedIndex = 2;
			await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
			Assert.True(filter.Focus());
			logs.Offset = new Vector(0, 100);
			window.UpdateLayout();
			Assert.True(logs.Offset.Y > 0);
			Vector logOffset = logs.Offset;
			Vector pageOffset = pageScroll.Offset;
			object? logContent = logs.Content;
			ScrollBar[] bars = pageScroll.GetVisualDescendants().OfType<ScrollBar>().ToArray();
			Assert.NotEmpty(bars);
			for (int iteration = 0; iteration < 3; iteration++)
			{
				await Task.WhenAll(viewModel.RefreshSnapshotAsync(), viewModel.RefreshSnapshotAsync());
				await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
				Assert.Empty(viewModel.ErrorMessage);
				Assert.Same(root, presenter.Content);
				Assert.Same(filter, Assert.Single(root.GetLogicalDescendants().OfType<ComboBox>()));
				Assert.Same(logs, root.GetLogicalDescendants().OfType<ScrollViewer>().Single(scroll => scroll.Name == "DebugLogScroll"));
				Assert.Same(logContent, logs.Content);
				Assert.Equal(2, filter.SelectedIndex);
				Assert.True(filter.IsFocused);
				Assert.Equal(logOffset, logs.Offset);
				Assert.Equal(pageOffset, pageScroll.Offset);
				Assert.Equal(bars, pageScroll.GetVisualDescendants().OfType<ScrollBar>().ToArray());
			}
		}
		finally
		{
			window.DataContext = null;
			window.Close();
		}
	});

	[Fact]
	public Task NativeSettingsRefreshDoesNotLoadHiddenComplexPages() => WithSettingsUiAsync(async () =>
	{
		using BridgeCommandsTests fixture = new(safeMode: true);
		Window window = new();
		using SettingsService service = new(fixture._services, window);
		using SettingsViewModel viewModel = new(service);
		NativeSettingsPageBase[] hidden = viewModel.Groups.SelectMany(group => group.Pages)
			.Select(item => item.Page).OfType<NativeSettingsPageBase>().Where(page => page.Key != "debug").ToArray();
		List<string> refreshed = [];
		foreach (NativeSettingsPageBase page in hidden)
			page.ComplexViewModel.PropertyChanged += (_, args) =>
			{
				if (args.PropertyName == nameof(SettingsPageViewModelBase.IsBusy) && page.ComplexViewModel.IsBusy)
					refreshed.Add(page.Key);
			};
		try
		{
			window.Show();
			viewModel.Navigate("debug");
			for (int iteration = 0; iteration < 3; iteration++) await viewModel.RefreshSnapshotAsync();
			Assert.Empty(viewModel.ErrorMessage);
			Assert.Empty(refreshed);
			viewModel.Navigate("mcp");
			await viewModel.RefreshSnapshotAsync();
			Assert.Empty(viewModel.ErrorMessage);
			Assert.Contains("mcp", refreshed);
			Assert.All(refreshed, key => Assert.Equal("mcp", key));
			Assert.Equal("mcp", Assert.Single(viewModel.Groups.SelectMany(group => group.Pages), item => item.IsSelected).Key);
		}
		finally { window.Close(); }
	});

	[Fact]
	public async Task UpdaterCancel_ReturnsTrue()
	{
		BridgeCommands commands = CreateCommands();
		object? result = await commands.InvokeAsync(new FakeBridgeSource(WindowLabels.Main), "updater_cancel", Args(new { }));
		Assert.Equal(true, result);
	}

	[Fact]
	public async Task SettingsUpdateGeneral_UpdatesAutoCheckUpdates()
	{
		BridgeCommands commands = CreateCommands();

		// 更新 autoCheckUpdates 为 false
		await commands.InvokeAsync(new FakeBridgeSource(WindowLabels.Main), "settings_update_general", Args(new { autoCheckUpdates = false }));
		Assert.Equal("false", _config.GetStringOr("auto_check_updates", "true"));

		// 验证快照反映该值
		object? snapshot = await commands.InvokeAsync(new FakeBridgeSource(WindowLabels.Main), "ui_get_snapshot", Args(new { }));
		string snapshotJson = JsonSerializer.Serialize(snapshot);
		using JsonDocument doc = JsonDocument.Parse(snapshotJson);
		Assert.False(doc.RootElement.GetProperty("general").GetProperty("autoCheckUpdates").GetBoolean());

		// 重新开启
		await commands.InvokeAsync(new FakeBridgeSource(WindowLabels.Main), "settings_update_general", Args(new { autoCheckUpdates = true }));
		Assert.Equal("true", _config.GetStringOr("auto_check_updates", "false"));
	}

	[Fact]
	public async Task UpdaterCommands_NonMainSource_Throws()
	{
		BridgeCommands commands = CreateCommands();
		// 从 pet 窗口调用 updater_check 必须拒绝
		await Assert.ThrowsAsync<InvalidOperationException>(() =>
			commands.InvokeAsync(new FakeBridgeSource(WindowLabels.Pet), "updater_check", Args(new { })));

		// 从 first-run 窗口调用 updater_install 必须拒绝
		await Assert.ThrowsAsync<InvalidOperationException>(() =>
			commands.InvokeAsync(new FakeBridgeSource(WindowLabels.FirstRun), "updater_install", Args(new { })));
	}
}
