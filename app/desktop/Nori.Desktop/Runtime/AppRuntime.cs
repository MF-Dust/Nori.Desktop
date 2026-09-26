using System.Collections.Concurrent;
using Nori.Core.Agent;
using Nori.Core.Automation;
using Nori.Core.Configuration;
using Nori.Core.Emotion;
using Nori.Core.Logging;
using Nori.Core.Live2D;
using Nori.Core.Memory;
using Nori.Core.Proactive;
using Nori.Core.Skills;
using Nori.Core.Sandbox;
using Nori.Core.Security;
using Nori.Core.Tools;
using Nori.Core.Vision;
using Nori.Core.Expression;
using Nori.Desktop.Expression;
using Nori.Desktop.Observation;
using Nori.Core.Voice;
using Nori.Desktop.Audio;
using Nori.Desktop.Automation;
using Nori.Desktop.Bridge;
using Nori.Desktop.Windows;

namespace Nori.Desktop.Runtime;

/// <summary>
/// 应用运行时协调层
///
/// 承接前端迁移过来的全部业务编排: Agent 会话与取消、工具授权、技能/情绪/提醒/
/// 记忆/语音服务装配, 以及面向 WebView 的带版本号 UI 状态快照。
///
/// 事件出口约定:
/// - nori:agent-event → 只推送给发起会话的原生对话窗口 (状态/chunk/用量/授权/完成/错误)
///
/// 秘密纪律: 快照只返回 hasApiKey 等脱敏标记, 明文绝不回传事件/日志/错误。
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("CodeQuality", "S2931", Justification = "运行时计时器已在 DisposeAsync 中释放，属于分析器误报。")]
public sealed partial class AppRuntime : IAsyncDisposable
{
	/// <summary>工具授权等待超时 (秒); 超时一律 fail-closed 拒绝</summary>
	public const int ApprovalTimeoutSeconds = 60;

	private const string McpToolCategory = "mcp";
	private const string PluginToolCategory = "plugin";
	private const int PluginToolsRefreshDebounceMs = 500;
	private const int McpRefreshLogMaxCharacters = 192;
	private const int McpRefreshLogServerIdMaxCharacters = 64;

	private readonly ConcurrentDictionary<string, AgentSessionState> _sessions = new();
	private readonly ConcurrentDictionary<string, PendingApproval> _approvals = new();
	private readonly Lock _approvalGate = new();
	private readonly Lock _notifierGate = new();
	private Nori.Core.Notifications.INativeNotifier _notifier = Nori.Core.Notifications.NullNativeNotifier.Instance;
	private bool _notifierTried;
	private readonly ConcurrentDictionary<string, PendingDesktopApproval> _desktopApprovals = new();
	private readonly ConcurrentDictionary<Task, byte> _backgroundTasks = new();
	private readonly CancellationTokenSource _lifetimeCts = new();
	/// <summary>
	/// 这一轮装配的是哪一套音频后端：native 或 webview。
	///
	/// 日志里也写了一行，但那条只能事后翻。这个属性让「装配到了哪一份」可断言 ——
	/// 换后端这种改动一旦悄悄回退到旧路径，现象只是「声音还是老样子」，很难发现。
	/// </summary>
	public string AudioBackendName { get; }

	/// <summary>实际在用的播放后端。可能是原生设备，也可能是 WebView 那份。</summary>
	private readonly IAudioPlayback _playback;
	private readonly IMicrophoneRecorder _recorder;

	/// <summary>
	/// WebView 那两份，**只为桥回调保留**。
	///
	/// ReportPlaybackFinished / ReportRecordingReady 这些是 WebView 专有的入口：
	/// 页面播完或录完之后经桥回报。走原生后端时没有页面，这两个字段为 null，
	/// 对应的桥命令变成空操作。
	/// </summary>
	private readonly WebViewAudioPlayback? _webViewPlayback;
	private readonly WebViewMicrophoneRecorder? _webViewRecorder;
	private readonly AudioHostChannel _audioChannel;
	private readonly ReflectionWorker _reflectionWorker;
	private readonly PetInteractionReactionService _petInteractionService;
	private readonly SemaphoreSlim _petInteractionGate = new(1, 1);
	private readonly SemaphoreSlim _mcpRefreshGate = new(1, 1);
	private readonly SemaphoreSlim _pluginToolsRefreshGate = new(1, 1);
	private System.Threading.Timer? _pluginToolsRefreshTimer;
	private readonly Lock _petInteractionThrottleGate = new();
	private readonly Lock _petSpeechGate = new();
	private CancellationTokenSource? _petInteractionCts;
	private CancellationTokenSource? _petSpeechCts;
	private PetInteractionTrigger? _activePetInteractionTrigger;
	private bool _activePetInteractionFallbackPosted;
	private DateTimeOffset _lastPetInteractionAt = DateTimeOffset.MinValue;
	private bool _petInteractionSubscribed;
	private int _disposed;

	public AppServices Services { get; }

	public ToolRegistry Tools { get; }

	public SkillService Skills { get; }

	public EmotionManager Emotion { get; }

	public ProactiveScheduler Proactive { get; }

	public MemoryService Memory { get; }

	public KnowledgeService Knowledge { get; }

	public MemoryLifecycleService Lifecycle { get; }

	public VoiceService Voice { get; }

	public AgentEngine Engine { get; }

	/// <summary>伴侣轻量互动 LLM 服务, 不进入聊天历史和工具链。</summary>
	public PetInteractionReactionService PetInteraction => _petInteractionService;

	/// <summary>当前快照版本号 (每次状态变更递增)</summary>
	public int SnapshotVersion => Volatile.Read(ref _snapshotVersion);

	/// <summary>运行时快照失效时通知原生设置窗口。</summary>
	public event Action? StateChanged;

	private int _snapshotVersion = 1;
	private readonly Lock _snapshotCacheGate = new();
	private object? _cachedSnapshot;
	private int _cachedSnapshotVersion;

	private int _initStartPending;

	/// <summary>
	/// 托盘是否真的可用
	///
	/// 由 App 在装载托盘后回填; 不可用时前端在主窗内显示常驻入口与退出按钮。
	/// </summary>
	public bool TrayAvailable { get; set; } = true;

	/// <summary>
	/// 标记初始化窗口需要补跑开始流程。
	///
	/// 首次运行向导会先置位再打开初始化窗口。窗口变为可见后取走这一位。
	/// </summary>
	public void MarkInitStartPending() => Interlocked.Exchange(ref _initStartPending, 1);

	/// <summary>取走并清除“初始化开始”标志 (只能被消费一次)</summary>
	public bool ConsumeInitStartPending() => Interlocked.Exchange(ref _initStartPending, 0) == 1;

	[System.Diagnostics.CodeAnalysis.SuppressMessage("CodeQuality", "S2486", Justification = "诊断回调失败必须隔离，不能阻断运行时构造。")]
	public AppRuntime(AppServices services)
	{
		Services = services;
		ConfigStore config = services.Config;
		services.Automation ??= new AutomationRuntime(
			config,
			services.SafeMode,
			OperatingSystem.IsWindows(),
			visionAvailable: !services.SafeMode,
			browserRunnerFactory: services.AutomationBrowserRunnerFactory,
			chatService: services.SafeMode ? null : services.Chat,
			desktopVisionRunnerFactory: services.SafeMode ? null : services.AutomationDesktopVisionRunnerFactory,
			desktopVisionPlannerFactory: services.SafeMode ? null : services.AutomationDesktopVisionPlannerFactory,
			desktopVisionActionFactory: services.SafeMode ? null : services.AutomationDesktopVisionActionFactory,
			desktopVisionScreenshotFactory: services.SafeMode ? null : services.AutomationDesktopVisionScreenshotFactory,
			desktopVisionWindowCatalogFactory: services.SafeMode ? null : services.AutomationDesktopVisionWindowCatalogFactory,
			desktopVisionApprovalCallback: services.SafeMode ? null : services.AutomationDesktopVisionApprovalCallback,
			auditSink: services.AutomationAudit);
		services.Automation.AuditSink ??= services.AutomationAudit;
		if (!services.SafeMode && services.Automation.DesktopVisionApprovalCallback is null)
		{
			services.Automation.DesktopVisionApprovalCallback = RequestAutomationApprovalAsync;
		}
		if (!services.SafeMode && services.Automation.BrowserApprovalCallback is null)
		{
			services.Automation.BrowserApprovalCallback = RequestAutomationApprovalAsync;
		}
		services.Automation.Changed += OnAutomationChanged;
		if (services.Update is not null) services.Update.StatusChanged += OnUpdateStatusChanged;

		Memory = new MemoryService(services.Memory, services.Embedding, config, startBackgroundWorker: !services.SafeMode);
		// 嵌入批处理恢复期的单轮失败降级为 warn 日志, 连续多轮失败才由 MemoryService 升级为 Error 遥测。
		Memory.EmbeddingDiagnostic = (severity, message) =>
		{
			try { services.Logger.Write(LogSource.Backend, severity, message); } catch { }
		};
		Knowledge = new KnowledgeService(services.Database, Memory, config, services.Paths.KnowledgePath);
		Knowledge.StatusChanged = () => InvalidateSnapshot();
		Memory.Knowledge = Knowledge;
		Lifecycle = new MemoryLifecycleService(Memory);
		ReflectionService reflection = new(services.Http, services.Chat, Memory, config);
		_reflectionWorker = new ReflectionWorker(reflection, exception =>
		{
			try { services.Logger.Write(LogSource.Backend, "warn", $"记忆整理失败: {ReflectionDiagnostics.Format(exception)}"); }
			catch { }
		}, () => InvalidateSnapshot());
		Skills = new SkillService(config, services.PublicHttp);
		Emotion = new EmotionManager(config);

		ReminderStore reminderStore = new(services.Database);
		Proactive = new ProactiveScheduler(
			reminderStore, config, services.Logger,
			GetIdleSecondsSafe);

		// Windows 默认使用 WASAPI；兼容后端使用独立的隐藏 WebView，不依赖原生 MainWindow。
		MediaExchange media = services.Assets?.Media ?? new MediaExchange();
		Func<string, string> mediaUrl = services.Assets is {} assets
			? assets.MediaUrl
			: _ => throw new InvalidOperationException("资源服务未启动, 音频端点不可用");
		AudioHostChannel channel = new(() => services.Windows?.GetNoriWindow(WindowLabels.AudioHost));
		_audioChannel = channel;

		bool useNativeAudio = Nori.Core.Voice.Audio.AudioBackend.PrefersNative(
			config.GetStringOr(ConfigStore.KeyAudioBackend, Nori.Core.Voice.Audio.AudioBackend.Auto), OperatingSystem.IsWindows());
		if (useNativeAudio)
		{
			_playback = Audio.NativeAudioFactory.CreatePlayback();
			_recorder = Audio.NativeAudioFactory.CreateRecorder();
			_webViewPlayback = null;
			_webViewRecorder = null;
		}
		else
		{
			WebViewAudioPlayback webPlayback = new(media, mediaUrl, channel);
			WebViewMicrophoneRecorder webRecorder = new(media, mediaUrl, channel);
			_playback = _webViewPlayback = webPlayback;
			_recorder = _webViewRecorder = webRecorder;
		}
		AudioBackendName = useNativeAudio ? "native" : "webview";
		services.Logger.Write(LogSource.Backend, "info",
			$"音频后端：{(useNativeAudio ? "原生设备" : "WebView")}");

		Voice = new VoiceService(services.Http, config, _playback,
			() => VoiceRetired() ? null : _recorder, services.Paths);
		_petInteractionService = new PetInteractionReactionService(services.Http, config);

		Tools = BuildToolRegistry(true);
		Engine = new AgentEngine(
			services.Http,
			config,
			services.Chat,
			Tools,
			Skills,
			Emotion,
			Memory,
			pet: new PetActionsAdapter(() => services.PetRuntime),
			motionNames: () => FlattenMotionNames(),
			expressionNames: () => services.PetRuntime?.Expressions ?? [],
			trace: services.AgentTrace,
			// 未启用时这条路完全不参与，桌宠照旧走本机 agent。
			luoLiCore: new Nori.Core.Chat.LuoLiCore.LuoLiCoreConversation(
				new Nori.Core.Chat.LuoLiCore.LuoLiCoreSettingsStore(config),
				options => new Nori.Core.Chat.LuoLiCore.LuoLiCoreSdkClient(services.Http, options)),
			// 走 LuoLiCore 时远端只给文本，表情动作在本地挑。
			replyReaction: new ReplyReactionService(services.Http, config),
			// 机器状态按分档进提示词，与情绪同层；采集器自己控制开销。
			// 灯效设备数进硬件清单：它是稳定量，不像负载那样每轮都变。
			machineState: new MachineStateProvider(rgbDeviceCount: () => RgbChannel.Devices.Count));

		// 窗口显隐变化 (含托盘切换伴侣) 直接作废快照, 主界面的伴侣状态因此不会陈旧
		if (services.Windows is not null)
		{
			services.Windows.VisibilityChanged += (label, visible) =>
			{
				if (label == WindowLabels.Pet && !visible)
				{
					CancelPetInteractionRequest();
					CancelPetInteractionSpeech();
				}
				InvalidateSnapshot();
			};
		}
	}

	private IReadOnlyList<IExpressionChannel>? _expressionChannels;
	private ExpressionCoordinator? _expression;
	private DesktopStateBackup? _desktopBackup;
	private IDesktopAppearance? _appearance;
	private RgbLightingChannel? _rgbChannel;
	private AmbientSoundChannel? _ambientChannel;
	private AccentColorChannel? _accentChannel;
	private ISandboxLauncher? _sandbox;
	private IScreenCapture? _screenCapture;
	private IVisionAnalyzer? _visionAnalyzer;

	private int _sessionCounter;
	private long _chatHistoryRevision;

	/// <summary>Agent 事件通道名</summary>
	public const string AgentEventName = "nori:agent-event";

	private static bool? ParseBoolFlag(string raw) => Live2DModelConfig.ParseBool(raw);

	private static float? ReadFloat(ConfigStore config, string key) =>
		Live2DModelConfig.ParseFloat(config.GetStringOr(key, ""));

	[System.Diagnostics.CodeAnalysis.SuppressMessage("CodeQuality", "S2486", Justification = "退出清理按资源逐项隔离，单项失败不能阻断其余释放。")]
	public async ValueTask DisposeAsync()
	{
		if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
		_lifetimeCts.Cancel();
		if (_petInteractionSubscribed && Services.PetRuntime is not null)
		{
			Services.PetRuntime.InteractionTriggered -= OnPetInteractionTriggered;
			Services.PetRuntime.ModelLoadRequested -= CancelPetInteractionRequest;
			Services.PetRuntime.ModelLoadRequested -= CancelPetInteractionPresentation;
			Services.PetRuntime.ModelLoadRequested -= OnPetModelStateChanged;
			Services.PetRuntime.ModelChanged -= OnPetModelStateChanged;
			Services.PetRuntime.ModelLoadFailed -= OnPetModelStateChanged;
			_petInteractionSubscribed = false;
		}
		CancelPetInteractionRequest();
		CancelPetInteractionSpeech();
		if (Services.Automation is not null) Services.Automation.Changed -= OnAutomationChanged;
		if (Services.Update is not null) Services.Update.StatusChanged -= OnUpdateStatusChanged;

		foreach ((string _, AgentSessionState session) in _sessions)
		{
			session.Cts.Cancel();
		}

		foreach ((string _, PendingApproval approval) in _approvals)
		{
			approval.Tcs.TrySetResult(false);
			approval.Dispose();
			HideApprovalNotice(approval.RequestId);
		}
		DisposeNotifier();
		foreach ((string _, PendingDesktopApproval approval) in _desktopApprovals)
		{
			approval.Tcs.TrySetResult(false);
			approval.Dispose();
			Services.Automation?.ClearAutomationApproval(approval.Request.RequestId);
			Services.Automation?.RecordApprovalCancellation(approval.Request);
		}
		_desktopApprovals.Clear();

		Task[] workers = _sessions.Values.Select(session => session.Worker).OfType<Task>().ToArray();
		await WaitBoundedAsync(workers, TimeSpan.FromSeconds(5)).ConfigureAwait(false);
		foreach (AgentSessionState session in _sessions.Values) session.Dispose();
		_sessions.Clear();
		await WaitBoundedAsync(_backgroundTasks.Keys.ToArray(), TimeSpan.FromSeconds(5)).ConfigureAwait(false);
		_backgroundTasks.Clear();

		try { await _reflectionWorker.DisposeAsync().ConfigureAwait(false); } catch { }
		try { await Knowledge.DisposeAsync().ConfigureAwait(false); } catch { }
		try { Proactive.Dispose(); } catch { }
		try { Emotion.Dispose(); } catch { }
		// Voice.Dispose 会逆向释放 _playback; 录音票据要单独作废
		try { _recorder.Dispose(); } catch { }
		try { Voice.Dispose(); } catch { }
		// 音频宿主通道最后解除: 让所有 WaitUntilReadyAsync 等待者立即结束而不是等超时
		try { _audioChannel.Dispose(); } catch { }
		try { if (Services.Automation is not null) await Services.Automation.DisposeAsync().ConfigureAwait(false); } catch { }
		_petInteractionGate.Dispose();
		_pluginToolsRefreshTimer?.Dispose();
		_pluginToolsRefreshGate.Dispose();
		_mcpRefreshGate.Dispose();
		_lifetimeCts.Dispose();
	}

	private void TrackTask(Task task)
	{
		_backgroundTasks.TryAdd(task, 0);
		_ = task.ContinueWith(
			completed => _backgroundTasks.TryRemove(completed, out _),
			CancellationToken.None,
			TaskContinuationOptions.ExecuteSynchronously,
			TaskScheduler.Default);
	}

	private Task TrackBackground(Func<Task> operation, string name)
	{
		Task task = ObserveBackgroundAsync(operation, name);
		TrackTask(task);
		return task;
	}

	private async Task InitializeKnowledgeAsync()
	{
		Knowledge.EnsureDefaultFile();
		Knowledge.StartWatcher();
		await Knowledge.ReindexAsync(_lifetimeCts.Token).ConfigureAwait(false);
	}

	/// <summary>Embedding 配置变化后重新检查知识和个人记忆向量。</summary>
	public void QueueEmbeddingRebuild()
	{
		TrackBackground(() => Knowledge.ReindexAsync(_lifetimeCts.Token), "Memory.md embedding rebuild");
		TrackBackground(() => Memory.ReembedAllAsync(_lifetimeCts.Token, false), "memory embedding rebuild");
		InvalidateSnapshot();
	}

	private async Task RunMemoryMaintenanceAsync()
	{
		while (!_lifetimeCts.IsCancellationRequested)
		{
			int changed = Lifecycle.RunOnce();
			if (changed > 0) InvalidateSnapshot();
			try { await Task.Delay(TimeSpan.FromHours(6), _lifetimeCts.Token).ConfigureAwait(false); }
			catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested) { break; }
		}
	}

	[System.Diagnostics.CodeAnalysis.SuppressMessage("CodeQuality", "S2486", Justification = "后台任务的遥测和日志失败不能覆盖已观察到的原始异常。")]
	private async Task ObserveBackgroundAsync(Func<Task> operation, string name)
	{
		try { await operation().ConfigureAwait(false); }
		catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested) { }
		catch (Exception exception)
		{
			try
			{
				Services.Telemetry.CaptureException(exception, "runtime.background_task");
				Services.Logger.Write(LogSource.Backend, "warn", $"{name} failed: {SensitiveDataRedactor.ExceptionSummary(exception)}");
			}
			catch { }
		}
	}

	private static async Task WaitBoundedAsync(IReadOnlyCollection<Task> tasks, TimeSpan timeout)
	{
		if (tasks.Count == 0) return;
		Task all = Task.WhenAll(tasks);
		await Task.WhenAny(all, Task.Delay(timeout)).ConfigureAwait(false);
	}

	/// <summary>活动 Agent 会话状态</summary>
	private sealed class AgentSessionState(IBridgeSource source, CancellationToken lifetimeToken) : IDisposable
	{
		public IBridgeSource Source { get; } = source;

		public CancellationTokenSource Cts { get; } = CancellationTokenSource.CreateLinkedTokenSource(
			lifetimeToken, source is INativeChatSource native ? native.LifetimeToken : CancellationToken.None);

		public Task? Worker { get; set; }

		public void Dispose()
		{
			Cts.Dispose();
		}
	}

	/// <summary>待决桌面视觉授权请求；只保存动作种类和任务标识。</summary>
	[System.Diagnostics.CodeAnalysis.SuppressMessage("CodeQuality", "S2931", Justification = "待决授权的计时器已在 Dispose 中释放，属于分析器误报。")]
	private sealed class PendingDesktopApproval(AutomationApprovalRequest request, TaskCompletionSource<bool> tcs) : IDisposable
	{
		public AutomationApprovalRequest Request { get; } = request;
		public TaskCompletionSource<bool> Tcs { get; } = tcs;
		public DateTimeOffset DeadlineUtc { get; private set; }

		private System.Threading.Timer? _timeout;

		public void ArmTimeout(int seconds, Action onExpired)
		{
			DeadlineUtc = DateTimeOffset.UtcNow.AddSeconds(seconds);
			_timeout = new System.Threading.Timer(_ => onExpired(), null, seconds * 1000, Timeout.Infinite);
		}

		public void Dispose()
		{
			_timeout?.Dispose();
			_timeout = null;
		}
	}

	/// <summary>待决授权请求</summary>
	private sealed class PendingApproval(string requestId, string toolName, IBridgeSource source, string sessionId, DateTimeOffset maximumDeadlineUtc, CancellationToken cancellationToken) : IDisposable
	{
		public string RequestId { get; } = requestId;

		/// <summary>工具名。「本轮记住」那一档在用户按下允许时要记它。</summary>
		public string ToolName { get; } = toolName;
		public IBridgeSource Source { get; } = source;
		public string SessionId { get; } = sessionId;
		public CancellationToken CancellationToken { get; } = cancellationToken;
		public TaskCompletionSource<bool> Tcs { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public DateTimeOffset DeadlineUtc { get; private set; } = Cap(DateTimeOffset.UtcNow.AddSeconds(ApprovalTimeoutSeconds), maximumDeadlineUtc);
		private Timer? _timeout;

		public void ArmTimeout(Action onExpired)
		{
			_timeout = new Timer(_ => onExpired(), null, Timeout.Infinite, Timeout.Infinite);
			RearmTimeout();
		}

		public void Extend()
		{
			DeadlineUtc = Cap(DeadlineUtc.AddSeconds(ApprovalTimeoutSeconds), maximumDeadlineUtc);
			RearmTimeout();
		}

		public void RearmTimeout()
		{
			TimeSpan remaining = DeadlineUtc - DateTimeOffset.UtcNow;
			_timeout?.Change(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero, Timeout.InfiniteTimeSpan);
		}

		private static DateTimeOffset Cap(DateTimeOffset deadline, DateTimeOffset maximum) => deadline < maximum ? deadline : maximum;

		public void Dispose()
		{
			_timeout?.Dispose();
			_timeout = null;
		}
	}
}
