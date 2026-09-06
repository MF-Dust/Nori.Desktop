using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Nori.Core;
using Nori.Core.Assets;
using Nori.Core.Chat;
using Nori.Core.Configuration;
using Nori.Core.Data;
using Nori.Core.Logging;
using Nori.Core.Network;
using Nori.Core.Resources;
using Nori.Core.Security;
using Nori.Core.Telemetry;
using Nori.Desktop.Automation.Desktop;
using Nori.Desktop.Bridge;
using Nori.Desktop.Diagnostics;
using Nori.Desktop.Telemetry;
using Nori.Desktop.Tray;
using Nori.Desktop.Windows;
using Nori.Desktop.Startup;
using Nori.PluginRuntime;

namespace Nori.Desktop;

/// <summary>桌面宿主的资源装配、启动顺序和逆序清理。</summary>
internal sealed class DesktopBootstrapper
{
	private readonly App _application;
	private readonly CancellationTokenSource _shutdownCts = new();
	private AppServices? _services;
	private NoriDatabase? _startupDatabase;
	private SentryTelemetry? _startupTelemetry;
	private NoriHttpClients? _startupHttpClients;
	private AssetServer? _startupAssets;
	private Nori.Core.Mcp.McpManager? _startupMcp;
	private PluginRuntimeHost? _startupPluginRuntime;
	private Task? _shutdownTask;
	private int _shutdownStarted;
	private int _secondInstanceActivationPending;

	public DesktopBootstrapper(App application) => _application = application;

	public void RequestShutdown()
	{
		_shutdownCts.Cancel();
		if (Interlocked.CompareExchange(ref _shutdownStarted, 1, 0) == 0) _shutdownTask = ShutdownAsync();
		// Exit 处理器返回后进程即可终止；必须在统一上限内等待清理，而不能把它留成后台任务。
		try { (_shutdownTask ?? Task.CompletedTask).GetAwaiter().GetResult(); }
		catch (Exception exception) { WriteShutdownFailure(exception); }
	}

	public async Task StartAsync(IClassicDesktopStyleApplicationLifetime desktop)
	{
		try
		{
			await StartAsyncCore(desktop, _shutdownCts.Token);
		}
		catch (OperationCanceledException) when (_shutdownCts.IsCancellationRequested)
		{
			// 应用在启动完成前退出时, 由 Exit 事件负责清理已创建的资源。
		}
		catch (Exception exception)
		{
			// 记日志与崩溃窗展示都在 Report 内部完成 (critical: 关窗即退出码 1)
			CrashReporter.Report(exception, critical: true);
		}
	}

	private async Task StartAsyncCore(IClassicDesktopStyleApplicationLifetime desktop, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		bool devMode = string.Equals(Nori.Core.ProductVersion.Current, "Dev", StringComparison.Ordinal)
			&& Environment.GetEnvironmentVariable("NORI_DEV") == "1";
		bool safeMode = Program.Options?.SafeMode == true;
		AppStoragePaths paths = Program.StoragePaths ?? throw new InvalidOperationException("存储路径尚未初始化");

		FileLogger logger = new(paths.LogsDirectory);
		logger.Initialize();
		CrashReporter.AttachLogger(logger); // 兜底日志与应用共用同一个写入器
		logger.Write(LogSource.Backend, "info", "日志系统初始化完成");

		// 先挂接但保持关闭。数据库中的明确同意状态读取完成前, Native Sentry 不得初始化,
		// 这样 WebView/数据库探测等启动失败始终只留在本机。
		SentryTelemetry telemetry = new(SentryBuildConfig.NativeDsn, SentryBuildConfig.Release, SentryBuildConfig.Environment);
		_startupTelemetry = telemetry;
		CrashReporter.AttachTelemetry(telemetry);

		// WebView 运行时缺失时给个能看懂的提示, 而不是弹几个空白窗口。
		// 三平台各自的原生引擎: Windows→WebView2, macOS→WKWebView, Linux→WebKitGTK
		(WebViewAdapterType adapterType, string engineName, string installHint) = OperatingSystem.IsWindows()
			? (WebViewAdapterType.WebView2, "WebView2", "请安装 Microsoft Edge WebView2 Evergreen Runtime 后重试。")
			: OperatingSystem.IsMacOS()
				? (WebViewAdapterType.WkWebView, "WKWebView", "系统 WebKit 组件异常, 请确认 macOS 版本受支持。")
				: (WebViewAdapterType.WebKitGtk, "WebKitGTK", "请安装 WebKitGTK 运行时 (Debian/Ubuntu: libwebkit2gtk-4.1-0; Fedora: webkit2gtk4.1)。");

		DetailedWebViewAdapterInfo adapter = WebViewAdapterInfo.GetAdapterInfo(adapterType);
		logger.Write(LogSource.Backend, "info", $"{engineName}: installed={adapter.IsInstalled} version={adapter.Version}");
		if (!adapter.IsInstalled)
		{
			logger.Write(LogSource.Backend, "error", $"{engineName} 运行时不可用: {adapter.UnavailableReason}");
			CrashReporter.ReportStartupFatal($"缺少 {engineName} 运行时", $"Nori 需要 {engineName} 才能显示界面。{Environment.NewLine}{installHint}");
			return;
		}

		cancellationToken.ThrowIfCancellationRequested();
		NoriDatabase database;
		try
		{
			database = NoriDatabase.Open(paths: paths);
		}
		catch (Exception exception) when (exception is InvalidOperationException
			or IOException
			or UnauthorizedAccessException
			or Microsoft.Data.Sqlite.SqliteException)
		{
			logger.Write(LogSource.Backend, "error", $"数据库打开或迁移失败: {SensitiveDataRedactor.ExceptionSummary(exception)}");
			CrashReporter.ReportStartupFatal("数据库打开或迁移失败", SensitiveDataRedactor.ExceptionSummary(exception));
			return;
		}
		_startupDatabase = database;
		ConfigStore config = new(database, new Nori.Core.Security.SecretKeyStore(paths));
		try
		{
			config.InitDefaults(AppVersion());
			config.EnsureSchemaVersion();
			if (SmokeTestRuntime.Current?.Mode == SmokeTestMode.Initialized)
			{
				// 只在显式隔离 profile 的冒烟模式中预置完成标记, 不改变普通启动流程。
				config.MarkFirstRunCompleted();
				config.MarkInitialized();
			}
		}
		catch (InvalidOperationException exception)
		{
			logger.Write(LogSource.Backend, "error", SensitiveDataRedactor.ExceptionSummary(exception));
			CrashReporter.ReportStartupFatal("配置数据库版本过高", SensitiveDataRedactor.ExceptionSummary(exception));
			return;
		}
		if (Program.StorageMigration is { Migrated: true, LegacyDataPath: not null } migration)
		{
			string oldKnowledgePath = Path.Combine(migration.LegacyDataPath, "knowledge", "Memory.md");
			StorageBootstrapper.RelocateKnowledgeIdentifier(database, config, oldKnowledgePath, paths.KnowledgePath);
		}
		// 只有完成配置迁移并确认 consent=granted 后才允许初始化 Native Sentry。
		telemetry.Configure(config.GetTelemetryConsent() == TelemetryConsent.Granted);
		using ITelemetryTransaction startupTransaction = telemetry.StartTransaction("app.startup");
		logger.Write(LogSource.Backend, "info", "数据库已打开");

		// 默认校验服务器证书。自签名/私有部署的大模型端点可通过 allow_insecure_tls 显式放开。
		bool insecureTls = ParseBoolFlag(config.GetStringOr("allow_insecure_tls", "")) ?? false;
		// 公网请求默认使用明确直连的地址校验客户端；必须依赖系统代理时可显式放行。
		bool publicSystemProxy = ParseBoolFlag(config.GetStringOr("allow_public_system_proxy", "")) ?? false;
		NoriHttpClients httpClients = NoriHttpClients.Create(
			insecureTls,
			TimeSpan.FromSeconds(ChatService.TimeoutSeconds + 10),
			publicSystemProxy);
		_startupHttpClients = httpClients;
		HttpClient http = httpClients.Local;
		HttpClient publicHttp = httpClients.Public;
		if (insecureTls)
		{
			logger.Write(LogSource.Backend, "warn", "已启用 allow_insecure_tls: 出站 HTTPS 不再校验服务器证书, 仅建议对本地/自签名端点使用");
		}
		cancellationToken.ThrowIfCancellationRequested();
		AssetServer? assetServer = null;
		PluginRuntimeHost pluginRuntime = new(new PluginRuntimeHostOptions
		{
			DataDirectory = paths.DataRoot,
			PluginsDirectory = paths.PluginsInstalledDirectory,
			PluginDataDirectory = paths.PluginsDataDirectory,
			WebViewDataDirectory = paths.PluginsWebViewCacheDirectory,
			PackageInboxDirectory = paths.PluginsPackageInboxDirectory,
			StagingDirectory = paths.PluginsStagingDirectory,
			HostVersion = PluginHostVersion(),
			DevelopmentHost = string.Equals(Nori.Core.ProductVersion.Current, "Dev", StringComparison.Ordinal),
			SafeMode = safeMode,
			Logger = logger,
			AssetUriFactory = (pluginId, path) => assetServer is { } server
				? new Uri(server.PublicUrl("plugins", $"{pluginId}/{path}"), UriKind.RelativeOrAbsolute)
				: throw new InvalidOperationException("插件资源服务尚未启动"),
			OnError = exception =>
			{
				// 统一分类: 预期失败只记 warn, 程序缺陷 (如 TypeLoad/激活失败) 上报遥测并带安全标签。
				BridgeFailure failure = BridgeFailureClassifier.Classify(exception);
				if (failure.Telemetry)
					telemetry.CaptureException(exception, "plugin.failure", tags: PluginFailureTags(exception, failure.Tags));
				logger.Write(LogSource.Backend, failure.LogLevel, $"插件 {exception.Code}: {exception.Message}");
			},
			OnLog = (descriptor, message, exception) => logger.Write(LogSource.Backend, "info", $"插件 [{descriptor.Id}@{descriptor.Version}] {message}"),
		});
		_startupPluginRuntime = pluginRuntime;
		assetServer = await AssetServer.StartAsync(new AssetServerOptions
		{
			AppRoot = AppRoot(),
			ResourcesRoot = paths.ResourcesInstalledDirectory,
			DevMode = devMode,
			AdditionalRoutes = [pluginRuntime.AssetRoute],
		});
		AssetServer assets = assetServer ?? throw new InvalidOperationException("资源服务启动失败");
		_startupAssets = assets;
		logger.Write(LogSource.Backend, "info", $"资源服务已启动: {assets.Origin} (dev={devMode})");
		pluginRuntime.Discover();

		Nori.Core.Mcp.McpManager mcp = new(http, config);
		_startupMcp = mcp;

		ChatService chat = new(http, database, config);
		AppServices services = new()
		{
			Database = database,
			Config = config,
			AiSettings = new AiSettingsStore(config),
			Logger = logger,
			Telemetry = telemetry,
			Paths = paths,
			Resources = new ResourceManager(paths),
			Chat = chat,
			Memory = new Nori.Core.Memory.MemoryStore(database),
			Embedding = new Nori.Core.Embedding.OpenAiEmbeddingAdapter(http),
			Llm = new LlmClient(http),
			Mcp = mcp,
			Assets = assets,
			PluginRuntime = pluginRuntime,
			Http = http,
			PublicHttp = publicHttp,
			AgentOperations = new Bridge.AgentOperationRegistry(),
			Automation = new Automation.AutomationRuntime(
				config,
				safeMode,
				OperatingSystem.IsWindows(),
				visionAvailable: !safeMode,
				chatService: safeMode ? null : chat,
				desktopVisionPlannerFactory: safeMode
					? null
					: () => new ChatServiceDesktopVisionPlanner(chat, new AiSettingsStore(config))),
			ShutdownToken = _shutdownCts.Token,
			SafeMode = safeMode,
		};
		_services = services;
		_startupDatabase = null;
		_startupTelemetry = null;
		_startupHttpClients = null;
		_startupAssets = null;
		_startupMcp = null;
		_startupPluginRuntime = null;

		// 安全模式不自动连接 MCP, 便于用户进入界面修复配置。
		if (!safeMode)
		{
			// 异步自动连接已启用的 MCP 服务; 后台任务失败只记日志, 不崩进程
			Task mcpAutoConnectTask = mcp.AutoConnectEnabledAsync();
			CrashReporter.Forget(mcpAutoConnectTask, "MCP 自动连接");
		}

		cancellationToken.ThrowIfCancellationRequested();
		await Dispatcher.UIThread.InvokeAsync(() =>
		{
			services.Windows = new WindowManager(assets, desktop, paths);
			services.Commands = new BridgeCommands(services);
			NoriBridge bridge = new(services);
			services.Bridge = bridge;
			services.Windows.CreateAll(bridge, services);

			// 业务运行时 (Agent/技能/情绪/提醒/语音): 伴侣窗口就绪后启动,
			// 桥接命令通过 services.Runtime 访问
			Runtime.AppRuntime runtime = new(services);
			services.Runtime = runtime;
			runtime.Start();

			// 托盘失败 (常见于部分 Linux 桌面) 时前端要显示内建入口
			runtime.TrayAvailable = TrayMenu.Install(_application, services);

			// 首次启动显示向导, 否则直接进初始化窗口
			bool firstRun = config.IsFirstRun();
			logger.Write(LogSource.Backend, "info", firstRun ? "首次启动应用" : "应用启动完成");
			bool activationPending = Program.ConsumePendingActivation()
				|| Interlocked.Exchange(ref _secondInstanceActivationPending, 0) == 1;
			if (firstRun)
				services.Windows.Show(WindowLabels.FirstRun);
			else if (activationPending)
				services.Windows.Show(WindowLabels.Main);
			else
				services.Windows.Show(WindowLabels.Init);

			if (SmokeTestRuntime.Current is { } smokeTest)
			{
				bool expectedFirstRun = smokeTest.Mode == SmokeTestMode.FirstRun;
				if (firstRun != expectedFirstRun)
					throw new InvalidOperationException("启动冒烟分支与 profile 状态不一致");
				SmokeTestRuntime.WriteReady(smokeTest, firstRun, safeMode, paths);
				SmokeTestRuntime.ScheduleBoundedExit(services.Windows);
			}
		});

		// 数据库、assets、固定窗口与初始窗口 ready 后才清理旧源；失败保留收据，下次启动重试。
		if (Program.StorageMigration is { } cleanupMigration && File.Exists(paths.CleanupReceiptPath))
			CrashReporter.Forget(Task.Run(() => StorageBootstrapper.CleanupLegacy(cleanupMigration, paths, LegacyDataPathResolver.Resolve()), cancellationToken), "旧数据清理");

		// 固定窗口与插件窗口宿主都就绪后再执行第三方入口。
		// 单个插件失败只记录状态，不阻断桌面宿主启动。
		if (!safeMode)
		{
			Task pluginStartTask = pluginRuntime.StartAllAsync(cancellationToken);
			CrashReporter.Forget(pluginStartTask, "插件启动");
		}
	}

	/// <summary>响应第二个实例的激活请求。</summary>
	public void ActivateMainWindow()
	{
		if (!Dispatcher.UIThread.CheckAccess()) { Dispatcher.UIThread.Post(ActivateMainWindow); return; }
		if (_services?.Windows is { } windows) { windows.Show(WindowLabels.Main); return; }
		Interlocked.Exchange(ref _secondInstanceActivationPending, 1);
	}

	private async Task ShutdownAsync()
	{
		try { await ShutdownCoreAsync().WaitAsync(TimeSpan.FromSeconds(8)).ConfigureAwait(false); }
		catch (TimeoutException) { }
		catch (Exception exception) { WriteShutdownFailure(exception); }
	}

	private async Task ShutdownCoreAsync()
	{
		// 服务各自负责取消并收拢启动任务；先等 MCP/插件启动反而会推迟它们收到 Dispose 取消。
		if (_services is not null)
		{
			try { await _services.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(7)).ConfigureAwait(false); }
			catch (Exception exception) { WriteShutdownFailure(exception); }
			_services = null;
		}

		if (_startupPluginRuntime is not null)
		{
			try { await _startupPluginRuntime.DisposeAsync().ConfigureAwait(false); } catch { }
			_startupPluginRuntime = null;
		}
		if (_startupMcp is not null)
		{
			try { await _startupMcp.DisposeAsync().ConfigureAwait(false); } catch { }
			_startupMcp = null;
		}
		if (_startupAssets is not null)
		{
			try { await _startupAssets.DisposeAsync().ConfigureAwait(false); } catch { }
			_startupAssets = null;
		}
		try { _startupHttpClients?.Dispose(); } catch { }
		_startupHttpClients = null;
		try { _startupDatabase?.Dispose(); } catch { }
		_startupDatabase = null;
		if (_startupTelemetry is not null)
		{
			try { await _startupTelemetry.FlushAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false); } catch { }
			try { _startupTelemetry.Dispose(); } catch { }
			_startupTelemetry = null;
		}
	}

	private static void WriteShutdownFailure(Exception exception)
	{
		try { System.Diagnostics.Debug.WriteLine($"Nori 关闭流程失败: {SensitiveDataRedactor.ExceptionSummary(exception)}"); } catch { }
	}

	/// <summary>
	/// 前端 bundle 目录
	///
	/// 生产: 与可执行文件同目录的 wwwroot; 开发模式下不使用 (页面由 vite 提供)
	/// </summary>
	private static string AppRoot() => Path.Combine(AppContext.BaseDirectory, "wwwroot");

	/// <summary>
	/// 应用版本, 写入 app_version 配置
	/// </summary>
	private static string AppVersion() => Nori.Core.ProductVersion.Current;

	/// <summary>提取插件运行时使用的数字宿主版本。</summary>
	private static PluginVersion PluginHostVersion()
	{
		string raw = Nori.Core.ProductVersion.Current.TrimStart('v', 'V');
		string core = raw.Split(['-', '+'], 2, StringSplitOptions.None)[0];
		return PluginVersion.TryParse(core, out PluginVersion version) ? version : new PluginVersion(1, 0, 0);
	}

	/// <summary>插件失败遥测标签: 白名单键 + 宿主装配的脱敏诊断字段 (值在发送边界再归一化)。</summary>
	private static IReadOnlyDictionary<string, string> PluginFailureTags(
		PluginException exception, IReadOnlyDictionary<string, string>? failureTags)
	{
		Dictionary<string, string> tags = new();
		if (failureTags is not null)
		{
			foreach ((string key, string value) in failureTags) tags[key] = value;
		}
		if (!string.IsNullOrWhiteSpace(exception.DiagnosticPluginId)) tags["plugin_id"] = exception.DiagnosticPluginId!;
		if (!string.IsNullOrWhiteSpace(exception.DiagnosticPluginVersion)) tags["plugin_version"] = exception.DiagnosticPluginVersion!;
		if (!string.IsNullOrWhiteSpace(exception.DiagnosticHostApiVersion)) tags["host_api"] = exception.DiagnosticHostApiVersion!;
		if (!string.IsNullOrWhiteSpace(exception.DiagnosticExceptionType)) tags["exception_kind"] = exception.DiagnosticExceptionType!;
		return tags;
	}

	/// <summary>
	/// 解析布尔配置文本, 与 PetRuntime.ParseBool 同口径
	/// </summary>
	private static bool? ParseBoolFlag(string raw) => raw switch
	{
		"1" => true,
		"0" => false,
		_ when raw.Equals("true", StringComparison.OrdinalIgnoreCase) => true,
		_ when raw.Equals("false", StringComparison.OrdinalIgnoreCase) => false,
		_ => null,
	};
}
