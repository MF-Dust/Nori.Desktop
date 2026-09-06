using Nori.Core.Automation;
using Nori.Core.Chat;
using Nori.Core.Configuration;
using Nori.Desktop.Automation.Browser;
using Nori.Desktop.Automation.Desktop;
using Nori.Desktop.Automation.Windows;

namespace Nori.Desktop.Automation;

/// <summary>可注入的浏览器自动化运行器；测试实现不得把真实浏览器拉起。</summary>
public interface IAutomationBrowserRunner : IAsyncDisposable
{
	/// <summary>启动一个隔离的浏览器会话。</summary>
	Task StartAsync(CancellationToken cancellationToken = default);

	/// <summary>在已启动的隔离会话中执行受限结构化 DOM 动作计划。</summary>
	Task<BrowserAutomationExecutionResult> ExecuteAsync(
		BrowserAutomationTaskPlan plan,
		BrowserAutomationExecutionContext executionContext,
		CancellationToken cancellationToken = default);
}

/// <summary>浏览器自动化宿主状态。</summary>
public enum AutomationBrowserState
{
	/// <summary>没有浏览器会话。</summary>
	Stopped,
	/// <summary>正在启动浏览器。</summary>
	Starting,
	/// <summary>浏览器会话已启动。</summary>
	Running,
	/// <summary>最近一次启动失败。</summary>
	Failed,
}

/// <summary>自动化设置的脱敏快照。</summary>
public sealed record AutomationSettingsSnapshot(
	bool Enabled,
	bool AllowPointer,
	bool AllowKeyboard,
	bool AllowScroll)
{
	/// <summary>浏览器自动化显式开关。</summary>
	public bool BrowserEnabled { get; init; }
}

/// <summary>宿主当前显式授权的自动化能力。</summary>
public sealed record AutomationCapabilitiesSnapshot(
	bool IsWindows,
	bool VisionAvailable,
	bool Pointer,
	bool Keyboard,
	bool Scroll,
	string? UnavailableReason)
{
	/// <summary>桌面视觉自动化能力是否已满足平台、配置和显式开关。</summary>
	public bool Desktop { get; init; }

	/// <summary>当前聊天 Provider 是否具备可调用的多模态规划接线。</summary>
	public bool VisionReady { get; init; }

	/// <summary>浏览器自动化能力是否已满足平台和显式开关。</summary>
	public bool Browser { get; init; }
}

/// <summary>浏览器自动化的脱敏生命周期状态；不包含 Cookie、页面内容、地址或截图。</summary>
public sealed record AutomationBrowserStatusSnapshot(
	AutomationBrowserState State,
	bool Enabled,
	bool Available,
	string? UnavailableReason)
{
	/// <summary>当前是否持有浏览器会话。</summary>
	public bool Running => State == AutomationBrowserState.Running;
}

/// <summary>浏览器结构化任务启动结果；不包含动作计划或页面数据。</summary>
public sealed record AutomationBrowserTaskStartSnapshot(Guid TaskId, AutomationTaskState State);

/// <summary>自动化视觉探测结果；不会包含截图或请求正文。</summary>
public sealed record AutomationVisionProbeSnapshot(bool Available, string? Reason);

/// <summary>自动化任务的脱敏状态；只包含生命周期和稳定进度分类。</summary>
public sealed record AutomationTaskStatusSnapshot(
	Guid Id,
	AutomationTaskState State,
	int Step,
	string ProgressCategory,
	string? ErrorCategory)
{
	/// <summary>任务种类；只公开 browser 或 desktop。</summary>
	public string TaskKind { get; init; } = "desktop";

	/// <summary>安全暂停原因；不包含页面或动作正文。</summary>
	public string? PauseReason { get; init; }

	/// <summary>当前步骤的前端兼容字段。</summary>
	public int CurrentStep => Step;

	/// <summary>计划动作总数；桌面视觉任务未公开固定总数。</summary>
	public int? TotalSteps { get; init; }

	/// <summary>是否可通过浏览器专用命令读取短期内存结果。</summary>
	public bool HasResult { get; init; }

	/// <summary>不含页面文本的固定结果摘要。</summary>
	public string? ResultSummary { get; init; }

	/// <summary>等待审批时的动作类别。</summary>
	public IReadOnlyList<AutomationActionKind> ActionKinds { get; init; } = [];

	/// <summary>等待审批时的请求标识。</summary>
	public Guid? ApprovalRequestId { get; init; }
}

/// <summary>自动化运行时汇总；不包含截图、提示词、URL 或工具参数。</summary>
public sealed record AutomationSnapshot(
	bool Enabled,
	bool Available,
	string? UnavailableReason,
	AutomationSettingsSnapshot Settings,
	AutomationCapabilitiesSnapshot Capabilities,
	AutomationTaskStatusSnapshot? ActiveTask,
	int QueuedCount)
{
	/// <summary>前端设置页使用的桌面能力总开关投影。</summary>
	public bool DesktopEnabled => Settings.AllowPointer || Settings.AllowKeyboard || Settings.AllowScroll;

	/// <summary>前端设置页使用的浏览器能力开关投影。</summary>
	public bool BrowserEnabled => Settings.BrowserEnabled;

	/// <summary>前端设置页使用的视觉能力就绪投影。</summary>
	public bool VisionReady => Capabilities.VisionReady;

	/// <summary>最近的桌面视觉任务脱敏状态。</summary>
	public IReadOnlyList<AutomationTaskStatusSnapshot> Tasks { get; init; } = [];

	/// <summary>当前等待用户决定的桌面高风险动作；不包含输入正文。</summary>
	public IReadOnlyList<AutomationDesktopApprovalSnapshot> PendingApprovals { get; init; } = [];

	/// <summary>浏览器自动化脱敏状态。</summary>
	public AutomationBrowserStatusSnapshot Browser { get; init; } = new(
		AutomationBrowserState.Stopped,
		false,
		false,
		"浏览器自动化默认关闭，请先显式启用");
}

/// <summary>把 Playwright Edge 运行器适配到自动化生命周期抽象。</summary>
public sealed class PlaywrightEdgeBrowserAutomationRunner : IAutomationBrowserRunner
{
	private readonly PlaywrightEdgeBrowserRunner _runner = new();
	private PlaywrightEdgeBrowserSession? _session;
	private int _disposed;

	/// <summary>启动可见的隔离 Edge 会话。</summary>
	public async Task StartAsync(CancellationToken cancellationToken = default)
	{
		ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
		_session = await _runner.StartAsync(cancellationToken).ConfigureAwait(false);
	}

	/// <summary>在已启动会话中执行受限 DOM 动作。</summary>
	public async Task<BrowserAutomationExecutionResult> ExecuteAsync(
		BrowserAutomationTaskPlan plan,
		BrowserAutomationExecutionContext executionContext,
		CancellationToken cancellationToken = default)
	{
		ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
		PlaywrightEdgeBrowserSession session = Volatile.Read(ref _session)
			?? throw new InvalidOperationException("浏览器会话尚未启动");
		return await session.ExecuteAsync(plan, executionContext, cancellationToken).ConfigureAwait(false);
	}

	/// <summary>关闭页面、Edge 上下文及临时 profile。</summary>
	public async ValueTask DisposeAsync()
	{
		if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
		PlaywrightEdgeBrowserSession? session = Interlocked.Exchange(ref _session, null);
		try
		{
			if (session is not null) await session.DisposeAsync().ConfigureAwait(false);
		}
		finally
		{
			await _runner.DisposeAsync().ConfigureAwait(false);
		}
	}
}

/// <summary>
/// 自动化宿主接线层。
///
/// 装配任务生命周期、Windows adapter 能力探测和隔离 Edge 生命周期。所有执行入口
/// 都经过安全模式、平台、总开关和显式能力授权校验；浏览器状态只返回脱敏摘要。
/// </summary>
public sealed class AutomationRuntime : IAsyncDisposable
{
	private readonly ConfigStore _config;
	private readonly AiSettingsStore _aiSettings;
	private readonly AutomationTaskManager _tasks;
	private readonly bool _safeMode;
	private readonly bool _isWindows;
	private readonly bool _visionAvailable;
	private readonly Func<IAutomationBrowserRunner> _browserRunnerFactory;
	private readonly Func<DesktopVisionRunnerRequest, IAutomationTaskRunner>? _desktopVisionRunnerFactory;
	private readonly Func<IDesktopVisionPlanner>? _desktopVisionPlannerFactory;
	private readonly Func<IDesktopVisionActionExecutor>? _desktopVisionActionFactory;
	private readonly Func<IDesktopVisionScreenshotSource>? _desktopVisionScreenshotFactory;
	private readonly Func<IDesktopVisionWindowCatalog>? _desktopVisionWindowCatalogFactory;
	private readonly object _desktopStateGate = new();
	private readonly Dictionary<Guid, DesktopTaskState> _desktopTasks = [];
	private readonly Dictionary<Guid, BrowserTaskState> _browserTasks = [];
	private readonly Dictionary<string, DesktopWindowTarget> _desktopWindowTargets = new(StringComparer.Ordinal);
	private readonly Dictionary<Guid, AutomationDesktopApprovalSnapshot> _desktopApprovals = [];
	private readonly BrowserAutomationResultStore _browserResults;
	private readonly TimeSpan _browserTaskTimeout;
	private DesktopVisionApprovalCallback? _desktopVisionApprovalCallback;
	private AutomationApprovalCallback? _browserApprovalCallback;
	private AutomationAuditRepository? _auditSink;
	private readonly SemaphoreSlim _browserGate = new(1, 1);
	private readonly object _browserStateGate = new();
	private IAutomationBrowserRunner? _browserRunner;
	private AutomationBrowserState _browserState = AutomationBrowserState.Stopped;
	private string? _browserFailureCode;
	private long _publicTaskSequence;
	private int _disposed;

	private const int MaxPublicAutomationTasks = 32;

	/// <summary>自动化状态发生变化时触发；订阅者只能读取脱敏快照。</summary>
	public event Action? Changed;

	/// <summary>按宿主实际平台装配自动化运行时。</summary>
	public AutomationRuntime(ConfigStore config, bool safeMode, AutomationTaskManager? taskManager = null)
		: this(config, safeMode, OperatingSystem.IsWindows(), visionAvailable: false, taskManager, null)
	{
	}

	/// <summary>可注入平台、视觉能力和浏览器运行器的构造函数，供宿主边界测试使用。</summary>
	public AutomationRuntime(
		ConfigStore config,
		bool safeMode,
		bool isWindows,
		bool visionAvailable,
		AutomationTaskManager? taskManager = null,
		Func<IAutomationBrowserRunner>? browserRunnerFactory = null,
		ChatService? chatService = null,
		Func<DesktopVisionRunnerRequest, IAutomationTaskRunner>? desktopVisionRunnerFactory = null,
		Func<IDesktopVisionPlanner>? desktopVisionPlannerFactory = null,
		Func<IDesktopVisionActionExecutor>? desktopVisionActionFactory = null,
		Func<IDesktopVisionScreenshotSource>? desktopVisionScreenshotFactory = null,
		Func<IDesktopVisionWindowCatalog>? desktopVisionWindowCatalogFactory = null,
		DesktopVisionApprovalCallback? desktopVisionApprovalCallback = null,
		AutomationApprovalCallback? browserApprovalCallback = null,
		AutomationAuditRepository? auditSink = null,
		BrowserAutomationResultStore? browserResults = null,
		TimeSpan? browserTaskTimeout = null)
	{
		ArgumentNullException.ThrowIfNull(config);
		_config = config;
		_aiSettings = new AiSettingsStore(config);
		_safeMode = safeMode;
		_isWindows = isWindows;
		_visionAvailable = visionAvailable;
		_tasks = taskManager ?? new AutomationTaskManager();
		_browserRunnerFactory = browserRunnerFactory ?? (() => new PlaywrightEdgeBrowserAutomationRunner());
		_browserResults = browserResults ?? new BrowserAutomationResultStore();
		_browserTaskTimeout = browserTaskTimeout ?? BrowserAutomationTaskLimits.MaximumDuration;
		if (_browserTaskTimeout <= TimeSpan.Zero || _browserTaskTimeout > BrowserAutomationTaskLimits.MaximumDuration)
			throw new ArgumentOutOfRangeException(nameof(browserTaskTimeout));
		_auditSink = auditSink;

		// 安全模式装配时不保留任何桌面视觉外部依赖工厂；Bridge 和执行入口仍会再次拒绝。
		if (safeMode)
		{
			_desktopVisionRunnerFactory = null;
			_desktopVisionPlannerFactory = null;
			_desktopVisionActionFactory = null;
			_desktopVisionScreenshotFactory = null;
			_desktopVisionWindowCatalogFactory = null;
			_desktopVisionApprovalCallback = null;
			_browserApprovalCallback = null;
		}
		else
		{
			_desktopVisionRunnerFactory = desktopVisionRunnerFactory ?? DefaultDesktopVisionRunnerFactory;
			_desktopVisionPlannerFactory = desktopVisionPlannerFactory
				?? (chatService is null ? null : () => new ChatServiceDesktopVisionPlanner(chatService, _aiSettings));
			_desktopVisionActionFactory = desktopVisionActionFactory ?? (() => new WindowsDesktopVisionActionExecutor());
			_desktopVisionScreenshotFactory = desktopVisionScreenshotFactory ?? (() => new WindowsDesktopVisionScreenshotSource());
			_desktopVisionWindowCatalogFactory = desktopVisionWindowCatalogFactory ?? (() => new WindowsDesktopVisionWindowCatalog());
			_desktopVisionApprovalCallback = desktopVisionApprovalCallback;
			_browserApprovalCallback = browserApprovalCallback;
		}

		_tasks.TaskChanged += OnTaskChanged;
	}

	/// <summary>运行时装配完成后可接入宿主现有审批协调器；空值始终拒绝高风险动作。</summary>
	public DesktopVisionApprovalCallback? DesktopVisionApprovalCallback
	{
		get => _desktopVisionApprovalCallback;
		set
		{
			if (_safeMode) return;
			_desktopVisionApprovalCallback = value;
		}
	}

	/// <summary>浏览器填写动作使用的宿主审批协调器；空值一律拒绝。</summary>
	public AutomationApprovalCallback? BrowserApprovalCallback
	{
		get => _browserApprovalCallback;
		set
		{
			if (_safeMode) return;
			_browserApprovalCallback = value;
		}
	}

	/// <summary>设置审计接收器；审计失败不会影响自动化生命周期。</summary>
	public AutomationAuditRepository? AuditSink
	{
		get => _auditSink;
		set => _auditSink = value;
	}

	/// <summary>读取当前设置。</summary>
	public AutomationSettingsSnapshot GetSettings() => new(
		_config.GetBoolOr(ConfigStore.KeyAutomationEnabled, false),
		_config.GetBoolOr(ConfigStore.KeyAutomationAllowPointer, false),
		_config.GetBoolOr(ConfigStore.KeyAutomationAllowKeyboard, false),
		_config.GetBoolOr(ConfigStore.KeyAutomationAllowScroll, false))
	{
		BrowserEnabled = _config.GetBoolOr(ConfigStore.KeyAutomationBrowserEnabled, false),
	};

	/// <summary>读取当前可用能力；能力必须由配置显式授权。</summary>
	public AutomationCapabilitiesSnapshot GetCapabilities()
	{
		AutomationSettingsSnapshot settings = GetSettings();
		bool visionReady = IsVisionConfigurationValid();
		return new(
			_isWindows,
			visionReady,
			_isWindows && settings.Enabled && settings.AllowPointer,
			_isWindows && settings.Enabled && settings.AllowKeyboard,
			_isWindows && settings.Enabled && settings.AllowScroll,
			GetUnavailableReason(settings))
		{
			Desktop = IsExecutionAvailable(settings),
			VisionReady = visionReady,
			Browser = IsBrowserExecutionAvailable(settings),
		};
	}

	/// <summary>读取当前浏览器的脱敏生命周期状态。</summary>
	public AutomationBrowserStatusSnapshot GetBrowserStatus() => GetBrowserStatus(GetSettings());

	/// <summary>读取自动化脱敏汇总。</summary>
	public AutomationSnapshot GetSnapshot()
	{
		AutomationSettingsSnapshot settings = GetSettings();
		AutomationCapabilitiesSnapshot capabilities = GetCapabilities();
		return new(
			settings.Enabled,
			IsExecutionAvailable(settings),
			GetUnavailableReason(settings),
			settings,
			capabilities,
			ToStatus(_tasks.ActiveTask),
			_tasks.QueuedCount)
		{
			Browser = GetBrowserStatus(settings),
			Tasks = GetTaskSnapshots(),
			PendingApprovals = GetDesktopApprovalSnapshots(),
		};
	}

	/// <summary>更新显式授权设置；未提供的字段保持原值。</summary>
	public AutomationSettingsSnapshot UpdateSettings(
		bool? enabled,
		bool? allowPointer,
		bool? allowKeyboard,
		bool? allowScroll,
		bool? browserEnabled = null)
	{
		ThrowIfControlBlocked();
		if (enabled is { } enabledValue) SetBool(ConfigStore.KeyAutomationEnabled, enabledValue);
		if (allowPointer is { } pointerValue) SetBool(ConfigStore.KeyAutomationAllowPointer, pointerValue);
		if (allowKeyboard is { } keyboardValue) SetBool(ConfigStore.KeyAutomationAllowKeyboard, keyboardValue);
		if (allowScroll is { } scrollValue) SetBool(ConfigStore.KeyAutomationAllowScroll, scrollValue);
		if (browserEnabled is { } browserValue) SetBool(ConfigStore.KeyAutomationBrowserEnabled, browserValue);

		// 任一桌面能力或总开关被收回后，已有任务也必须立即取消，不能只阻止下一次启动。
		if (enabled == false || allowPointer == false || allowKeyboard == false || allowScroll == false)
			_tasks.CancelAll();
		// 总开关或浏览器子开关关闭后，已经存在的 Edge 也必须立即收回，不能只阻止下一次启动。
		if (enabled == false || browserEnabled == false) StopBrowserSynchronously();
		NotifyChanged();
		return GetSettings();
	}

	/// <summary>探测视觉能力；不发起网络请求，也不返回 Provider 密钥或原文。</summary>
	public AutomationVisionProbeSnapshot ProbeVision()
	{
		if (_safeMode) return new(false, "安全模式已禁用自动化视觉能力");
		if (!_isWindows) return new(false, "Windows 桌面自动化仅支持 Windows");
		if (!_visionAvailable || _desktopVisionRunnerFactory is null || _desktopVisionPlannerFactory is null
			|| _desktopVisionActionFactory is null || _desktopVisionScreenshotFactory is null)
			return new(false, "当前版本未接入桌面多模态视觉能力");
		if (!IsChatVisionConfigurationValid()) return new(false, "当前聊天 Provider 未配置有效的视觉模型");
		return new(true, null);
	}

	/// <summary>列出当前可选窗口；返回值只包含一次性 token、尺寸和前台标记。</summary>
	public IReadOnlyList<AutomationDesktopWindowSnapshot> ListDesktopWindows()
	{
		ThrowIfDesktopExecutionBlocked();
		IDesktopVisionWindowCatalog catalog = CreateWindowCatalog();
		IReadOnlyList<WindowsTopLevelWindow> windows;
		try { windows = catalog.Enumerate(); }
		catch { throw new InvalidOperationException("桌面窗口列表不可用"); }

		Dictionary<string, DesktopWindowTarget> targets = new(StringComparer.Ordinal);
		List<AutomationDesktopWindowSnapshot> result = [];
		foreach (WindowsTopLevelWindow window in windows)
		{
			if (window.Handle == 0 || window.Bounds.Width <= 0 || window.Bounds.Height <= 0) continue;
			string token = $"desktop-{Guid.NewGuid():N}";
			targets[token] = new(window.Handle, window.Bounds.Width, window.Bounds.Height, window.IsForeground);
			result.Add(new(token, window.Bounds.Width, window.Bounds.Height, window.IsForeground));
		}

		lock (_desktopStateGate)
		{
			_desktopWindowTargets.Clear();
			foreach ((string token, DesktopWindowTarget target) in targets) _desktopWindowTargets[token] = target;
		}
		return result;
	}

	/// <summary>创建一个桌面视觉任务；任务正文只在内存执行链中传递。</summary>
	public AutomationDesktopTaskStartSnapshot StartDesktopTask(string task, string targetToken)
	{
		ThrowIfDesktopExecutionBlocked();
		if (string.IsNullOrWhiteSpace(task) || task.Trim().Length > 4096)
			throw new InvalidOperationException("桌面视觉任务内容无效");
		if (string.IsNullOrWhiteSpace(targetToken)) throw new InvalidOperationException("目标窗口 token 无效");

		DesktopWindowTarget? target;
		lock (_desktopStateGate)
		{
			if (!_desktopWindowTargets.TryGetValue(targetToken, out target) || target is null)
				throw new InvalidOperationException("目标窗口 token 无效或已过期");
		}

		IDesktopVisionScreenshotSource screenshotSource = CreateScreenshotSource();
		IDesktopVisionActionExecutor actionExecutor = CreateActionExecutor();
		IDesktopVisionPlanner planner = CreatePlanner();
		AutomationCapability granted = GetGrantedCapabilities(GetSettings());
		AutomationPolicy policy = new(granted, AutomationPolicy.Default.ScreenBounds);
		DesktopTaskState state = new();
		DesktopVisionRunnerRequest request = new(
			"桌面视觉任务",
			task.Trim(),
			target.Handle,
			screenshotSource,
			actionExecutor,
			planner,
			_desktopVisionApprovalCallback,
			policy,
			progress => OnDesktopProgress(state, progress));

		IAutomationTaskRunner runner;
		try
		{
			runner = (_desktopVisionRunnerFactory ?? throw new InvalidOperationException("桌面视觉执行器未装配"))(request)
				?? throw new InvalidOperationException("桌面视觉执行器未装配");
		}
		catch (InvalidOperationException exception) when (exception.Message == "桌面视觉执行器未装配")
		{
			throw;
		}
		catch
		{
			throw new InvalidOperationException("桌面视觉执行器装配失败");
		}

		AutomationTask taskModel = _tasks.Enqueue(new DesktopVisionRunnerAdapter(runner, state));
		state.Attach(taskModel.Id, NextTaskSequence());
		lock (_desktopStateGate)
		{
			_desktopTasks[taskModel.Id] = state;
			TrimPublicTasks();
		}
		ApplyTaskSnapshot(taskModel.Snapshot);
		RecordAudit(new AutomationAuditEvent(
			DateTimeOffset.UtcNow,
			taskModel.Id,
			AutomationAuditTaskKind.Desktop,
			AutomationAuditEventCategory.Task,
			AutomationAuditOutcome.Queued));
		NotifyChanged();
		return new(taskModel.Id, ToStatus(taskModel.Snapshot) ?? throw new InvalidOperationException("自动化任务状态不可用"));
	}

	/// <summary>登记当前高风险动作审批，供桌面和浏览器共用宿主 RespondApproval 流程。</summary>
	public void SetAutomationApproval(AutomationApprovalRequest request)
	{
		ArgumentNullException.ThrowIfNull(request);
		if (_safeMode) return;
		AutomationAuditTaskKind taskKind = GetAuditTaskKind(request.TaskId);
		lock (_desktopStateGate)
		{
			_desktopApprovals[request.RequestId] = new(request.RequestId, request.TaskId, request.ActionKinds);
			if (_browserTasks.TryGetValue(request.TaskId, out BrowserTaskState? browserState))
			{
				browserState.MarkApprovalPending(request.RequestId, request.ActionKinds);
			}
		}
		RecordAudit(new AutomationAuditEvent(
			request.RequestedAt,
			request.TaskId,
			taskKind,
			AutomationAuditEventCategory.Approval,
			AutomationAuditOutcome.Requested));
		NotifyChanged();
	}

	/// <summary>兼容现有桌面视觉调用的审批登记入口。</summary>
	public void SetDesktopApproval(AutomationApprovalRequest request) => SetAutomationApproval(request);

	/// <summary>清除高风险动作审批。</summary>
	public void ClearAutomationApproval(Guid requestId)
	{
		lock (_desktopStateGate) _desktopApprovals.Remove(requestId);
		NotifyChanged();
	}

	/// <summary>兼容现有桌面视觉调用的审批清理入口。</summary>
	public void ClearDesktopApproval(Guid requestId) => ClearAutomationApproval(requestId);

	/// <summary>记录由宿主现有审批协调器产生的固定结论。</summary>
	public void RecordApprovalOutcome(AutomationApprovalRequest request, AutomationApprovalOutcome outcome)
	{
		ArgumentNullException.ThrowIfNull(request);
		AutomationAuditOutcome auditOutcome = outcome switch
		{
			AutomationApprovalOutcome.Approved => AutomationAuditOutcome.Approved,
			AutomationApprovalOutcome.Denied => AutomationAuditOutcome.Denied,
			AutomationApprovalOutcome.Expired => AutomationAuditOutcome.TimedOut,
			_ => AutomationAuditOutcome.Denied,
		};
		string? failureCode = outcome switch
		{
			AutomationApprovalOutcome.Expired => "approval_timeout",
			AutomationApprovalOutcome.Denied => "approval_denied",
			_ => null,
		};
		RecordAudit(new AutomationAuditEvent(
			DateTimeOffset.UtcNow,
			request.TaskId,
			GetAuditTaskKind(request.TaskId),
			AutomationAuditEventCategory.Approval,
			auditOutcome,
			failureCode));
	}

	/// <summary>记录审批因取消而 fail-closed 的结论。</summary>
	public void RecordApprovalCancellation(AutomationApprovalRequest request)
	{
		ArgumentNullException.ThrowIfNull(request);
		RecordAudit(new AutomationAuditEvent(
			DateTimeOffset.UtcNow,
			request.TaskId,
			GetAuditTaskKind(request.TaskId),
			AutomationAuditEventCategory.Approval,
			AutomationAuditOutcome.Cancelled,
			"approval_cancelled"));
	}

	/// <summary>登记一个经过显式能力授权的执行任务。</summary>
	public AutomationTask Enqueue(AutomationCapability requiredCapability, Func<CancellationToken, Task> operation, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(operation);
		ThrowIfExecutionBlocked(requiredCapability);
		return _tasks.Enqueue(operation, cancellationToken);
	}

	/// <summary>启动隔离 Edge；未满足安全边界时 fail-closed。</summary>
	public async Task<AutomationBrowserStatusSnapshot> StartBrowserAsync(CancellationToken cancellationToken = default)
	{
		ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
		await _browserGate.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
			AutomationSettingsSnapshot settings = GetSettings();
			ThrowIfBrowserExecutionBlocked(settings);
			if (_browserRunner is not null) return GetBrowserStatus(settings);

			SetBrowserState(AutomationBrowserState.Starting, null);
			NotifyChanged();
			IAutomationBrowserRunner? runner = null;
			try
			{
				runner = _browserRunnerFactory()
					?? throw new InvalidOperationException("浏览器运行器未装配");
				_browserRunner = runner;
				await runner.StartAsync(cancellationToken).ConfigureAwait(false);
				SetBrowserState(AutomationBrowserState.Running, null);
				NotifyChanged();
				return GetBrowserStatus(settings);
			}
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
			{
				await DisposeBrowserRunnerAsync(runner).ConfigureAwait(false);
				_browserRunner = null;
				SetBrowserState(AutomationBrowserState.Stopped, null);
				NotifyChanged();
				throw;
			}
			catch (Exception)
			{
				await DisposeBrowserRunnerAsync(runner).ConfigureAwait(false);
				_browserRunner = null;
				SetBrowserState(AutomationBrowserState.Failed, "start_failed");
				NotifyChanged();
				// 不把 Playwright 的路径、地址或页面信息带回桥接层。
				throw new InvalidOperationException("浏览器启动失败");
			}
		}
		finally
		{
			_browserGate.Release();
		}
	}

	/// <summary>启动并排队一个受限浏览器 DOM 任务；动作正文只存在于内存执行链。</summary>
	public async Task<AutomationBrowserTaskStartSnapshot> StartBrowserTaskAsync(
		BrowserAutomationTaskPlan plan,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(plan);
		ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
		BrowserAutomationPolicy.ValidatePlan(plan);
		ThrowIfBrowserExecutionBlocked(GetSettings());
		await StartBrowserAsync(cancellationToken).ConfigureAwait(false);
		// 启动浏览器期间可能发生配置或安全模式切换，入队前必须再复核一次。
		ThrowIfBrowserExecutionBlocked(GetSettings());
		IAutomationBrowserRunner runner = _browserRunner
			?? throw new InvalidOperationException("浏览器会话不可用");
		BrowserTaskState state = new(plan.Actions.Count);
		AutomationTask task = _tasks.Enqueue(new BrowserTaskRunnerAdapter(this, runner, plan, state));
		state.Attach(task.Id, NextTaskSequence());
		lock (_desktopStateGate)
		{
			_browserTasks[task.Id] = state;
			TrimPublicTasks();
		}
		ApplyTaskSnapshot(task.Snapshot);
		RecordAudit(new AutomationAuditEvent(
			DateTimeOffset.UtcNow,
			task.Id,
			AutomationAuditTaskKind.Browser,
			AutomationAuditEventCategory.Task,
			AutomationAuditOutcome.Queued));
		NotifyChanged();
		return new(task.Id, task.State);
	}

	/// <summary>读取短期内存中的浏览器任务结果；过期后返回空且不会回退到持久化数据。</summary>
	public BrowserAutomationTaskResult? GetBrowserTaskResult(Guid taskId)
	{
		ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
		ThrowIfBrowserExecutionBlocked(GetSettings());
		return _browserResults.Get(taskId);
	}

	/// <summary>取消一个浏览器任务；任务和临时 profile 的后续清理由现有停止入口完成。</summary>
	public bool StopBrowserTask(Guid taskId)
	{
		ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
		return IsBrowserTask(taskId) && StopTask(taskId);
	}

	/// <summary>停止隔离 Edge；没有会话时重复调用保持幂等。</summary>
	public async Task<AutomationBrowserStatusSnapshot> StopBrowserAsync(CancellationToken cancellationToken = default)
	{
		ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
		// 停止是清理操作：先终止浏览器任务，再尽力删除隔离 profile。
		CancelBrowserTasks();
		await _browserGate.WaitAsync().ConfigureAwait(false);
		try { return await StopBrowserCoreAsync(notify: true).ConfigureAwait(false); }
		finally { _browserGate.Release(); }
	}

	/// <summary>取消一个任务；任务不存在或已终态时返回 false。</summary>
	public bool StopTask(Guid taskId)
	{
		ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
		bool browserTask = IsBrowserTask(taskId);
		bool stopped = _tasks.Cancel(taskId);
		if (stopped && browserTask) StopBrowserSynchronously();
		if (stopped) NotifyChanged();
		return stopped;
	}

	/// <summary>取消一个桌面视觉任务；重复调用保持幂等。</summary>
	public bool StopDesktopTask(Guid taskId) => StopTask(taskId);

	/// <summary>取消全部未终态任务并关闭浏览器；重复调用返回零。</summary>
	public int StopAll() => StopAllAsync().GetAwaiter().GetResult();

	/// <summary>异步取消全部未终态任务并关闭浏览器。</summary>
	public async Task<int> StopAllAsync(CancellationToken cancellationToken = default)
	{
		ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
		int stopped = _tasks.CancelAll();
		// StopAll 是清理屏障，必须尽力拿到锁并关闭临时 profile。
		await _browserGate.WaitAsync().ConfigureAwait(false);
		try
		{
			await StopBrowserCoreAsync(notify: true).ConfigureAwait(false);
			return stopped;
		}
		finally
		{
			_browserGate.Release();
		}
	}

	/// <summary>释放自动化任务执行器与隔离 Edge。</summary>
	public async ValueTask DisposeAsync()
	{
		if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
		_tasks.TaskChanged -= OnTaskChanged;
		try
		{
			await _browserGate.WaitAsync().ConfigureAwait(false);
			try { await StopBrowserCoreAsync(notify: false).ConfigureAwait(false); }
			finally { _browserGate.Release(); }
		}
		catch
		{
			// 释放阶段仍继续关闭任务执行器。
		}
		try { await _tasks.DisposeAsync().ConfigureAwait(false); }
		finally { _browserGate.Dispose(); }
	}

	private bool IsExecutionAvailable(AutomationSettingsSnapshot settings) =>
		GetUnavailableReason(settings) is null;

	private bool IsBrowserExecutionAvailable(AutomationSettingsSnapshot settings) =>
		!_safeMode && _isWindows && settings.Enabled && settings.BrowserEnabled;

	private string? GetUnavailableReason(AutomationSettingsSnapshot settings)
	{
		if (_safeMode) return "安全模式已禁用自动化";
		if (!_isWindows) return "Windows 桌面自动化仅支持 Windows";
		if (!settings.Enabled) return "自动化默认关闭，请在设置中显式启用";
		if (!settings.AllowPointer && !settings.AllowKeyboard && !settings.AllowScroll)
			return "未显式授权任何自动化输入能力";
		if (!_visionAvailable || _desktopVisionRunnerFactory is null || _desktopVisionPlannerFactory is null
			|| _desktopVisionActionFactory is null || _desktopVisionScreenshotFactory is null)
			return "当前版本未接入桌面多模态视觉能力";
		if (!IsChatVisionConfigurationValid()) return "当前聊天 Provider 未配置有效的视觉模型";
		return null;
	}

	private bool IsVisionConfigurationValid() =>
		!_safeMode && _isWindows && _visionAvailable
		&& _desktopVisionRunnerFactory is not null
		&& _desktopVisionPlannerFactory is not null
		&& _desktopVisionActionFactory is not null
		&& _desktopVisionScreenshotFactory is not null
		&& IsChatVisionConfigurationValid();

	private bool IsChatVisionConfigurationValid()
	{
		AiChatSettings chat = _aiSettings.Read().Chat;
		if (!chat.IsConfigured) return false;
		return Uri.TryCreate(chat.BaseUrl, UriKind.Absolute, out Uri? uri)
			&& uri.Scheme is "http" or "https";
	}

	private void ThrowIfDesktopExecutionBlocked()
	{
		ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
		if (_safeMode) throw new InvalidOperationException("安全模式已禁用桌面视觉自动化");
		AutomationSettingsSnapshot settings = GetSettings();
		string? reason = GetUnavailableReason(settings);
		if (reason is not null) throw new InvalidOperationException(reason);
	}

	private IDesktopVisionWindowCatalog CreateWindowCatalog()
	{
		try
		{
			return (_desktopVisionWindowCatalogFactory ?? throw new InvalidOperationException("桌面窗口能力未装配"))()
				?? throw new InvalidOperationException("桌面窗口能力未装配");
		}
		catch (InvalidOperationException exception) when (exception.Message == "桌面窗口能力未装配")
		{
			throw new InvalidOperationException("桌面窗口能力未装配");
		}
		catch
		{
			throw new InvalidOperationException("桌面窗口能力装配失败");
		}
	}

	private IDesktopVisionScreenshotSource CreateScreenshotSource()
	{
		try
		{
			return (_desktopVisionScreenshotFactory ?? throw new InvalidOperationException("桌面截图能力未装配"))()
				?? throw new InvalidOperationException("桌面截图能力未装配");
		}
		catch (InvalidOperationException exception) when (exception.Message == "桌面截图能力未装配")
		{
			throw new InvalidOperationException("桌面截图能力未装配");
		}
		catch
		{
			throw new InvalidOperationException("桌面截图能力装配失败");
		}
	}

	private IDesktopVisionActionExecutor CreateActionExecutor()
	{
		try
		{
			return (_desktopVisionActionFactory ?? throw new InvalidOperationException("桌面输入能力未装配"))()
				?? throw new InvalidOperationException("桌面输入能力未装配");
		}
		catch (InvalidOperationException exception) when (exception.Message == "桌面输入能力未装配")
		{
			throw new InvalidOperationException("桌面输入能力未装配");
		}
		catch
		{
			throw new InvalidOperationException("桌面输入能力装配失败");
		}
	}

	private IDesktopVisionPlanner CreatePlanner()
	{
		try
		{
			return (_desktopVisionPlannerFactory ?? throw new InvalidOperationException("桌面视觉规划器未装配"))()
				?? throw new InvalidOperationException("桌面视觉规划器未装配");
		}
		catch (InvalidOperationException exception) when (exception.Message == "桌面视觉规划器未装配")
		{
			throw new InvalidOperationException("桌面视觉规划器未装配");
		}
		catch
		{
			throw new InvalidOperationException("桌面视觉规划器装配失败");
		}
	}

	private string? GetBrowserUnavailableReason(AutomationSettingsSnapshot settings)
	{
		if (_safeMode) return "安全模式已禁用浏览器自动化";
		if (!_isWindows) return "Windows 浏览器自动化仅支持 Windows";
		if (!settings.Enabled) return "自动化默认关闭，请先显式启用";
		if (!settings.BrowserEnabled) return "浏览器自动化默认关闭，请先显式启用";
		return null;
	}

	private void ThrowIfControlBlocked()
	{
		ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
		if (_safeMode) throw new InvalidOperationException("安全模式已禁用自动化设置");
		if (!_isWindows) throw new InvalidOperationException("Windows 桌面自动化仅支持 Windows");
	}

	private void ThrowIfBrowserExecutionBlocked(AutomationSettingsSnapshot settings)
	{
		ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
		if (_safeMode) throw new InvalidOperationException("安全模式已禁用浏览器自动化");
		if (!_isWindows) throw new InvalidOperationException("Windows 浏览器自动化仅支持 Windows");
		if (!settings.Enabled) throw new InvalidOperationException("自动化默认关闭，请先显式启用");
		if (!settings.BrowserEnabled) throw new InvalidOperationException("浏览器自动化默认关闭，请先显式启用");
	}

	private void ThrowIfExecutionBlocked(AutomationCapability requiredCapability)
	{
		ThrowIfControlBlocked();
		AutomationSettingsSnapshot settings = GetSettings();
		if (!_visionAvailable) throw new InvalidOperationException("当前版本未接入多模态视觉能力");
		if (!settings.Enabled) throw new InvalidOperationException("自动化默认关闭，请先显式启用");
		if (requiredCapability == AutomationCapability.None
			|| (requiredCapability & (AutomationCapability.Pointer | AutomationCapability.Keyboard | AutomationCapability.Scroll)) != requiredCapability)
			throw new InvalidOperationException("自动化执行能力不在显式白名单内");
		AutomationCapability granted = GetGrantedCapabilities(settings);
		if ((granted & requiredCapability) != requiredCapability)
			throw new InvalidOperationException("自动化能力未被显式授权");
	}

	private static AutomationCapability GetGrantedCapabilities(AutomationSettingsSnapshot settings) =>
		(settings.AllowPointer ? AutomationCapability.Pointer : AutomationCapability.None)
		| (settings.AllowKeyboard ? AutomationCapability.Keyboard : AutomationCapability.None)
		| (settings.AllowScroll ? AutomationCapability.Scroll : AutomationCapability.None);

	private AutomationBrowserStatusSnapshot GetBrowserStatus(AutomationSettingsSnapshot settings)
	{
		AutomationBrowserState state;
		string? failureCode;
		lock (_browserStateGate)
		{
			state = _browserState;
			failureCode = _browserFailureCode;
		}
		string? reason = GetBrowserUnavailableReason(settings);
		if (reason is null && failureCode is not null) reason = "浏览器启动失败";
		return new(state, settings.BrowserEnabled, IsBrowserExecutionAvailable(settings), reason);
	}

	private void SetBrowserState(AutomationBrowserState state, string? failureCode)
	{
		lock (_browserStateGate)
		{
			_browserState = state;
			_browserFailureCode = failureCode;
		}
	}

	private async Task<AutomationBrowserStatusSnapshot> StopBrowserCoreAsync(bool notify)
	{
		IAutomationBrowserRunner? runner = _browserRunner;
		_browserRunner = null;
		bool changed;
		lock (_browserStateGate)
		{
			changed = runner is not null || _browserState != AutomationBrowserState.Stopped;
			_browserState = AutomationBrowserState.Stopped;
			_browserFailureCode = null;
		}

		await DisposeBrowserRunnerAsync(runner).ConfigureAwait(false);
		if (notify && changed) NotifyChanged();
		return GetBrowserStatus();
	}

	private static async Task DisposeBrowserRunnerAsync(IAutomationBrowserRunner? runner)
	{
		if (runner is null) return;
		try { await runner.DisposeAsync().ConfigureAwait(false); }
		catch { /* 浏览器已崩溃时仍保持 fail-closed 状态 */ }
	}

	private void StopBrowserSynchronously() => StopBrowserAsync().GetAwaiter().GetResult();

	private void SetBool(string key, bool value) => _config.Set(key, new ConfigValue.Boolean(value));

	private static IAutomationTaskRunner DefaultDesktopVisionRunnerFactory(DesktopVisionRunnerRequest request) =>
		new DesktopVisionAutomationRunner(
			request.TaskTitle,
			request.Goal,
			request.TargetWindow,
			request.ScreenshotSource,
			request.ActionExecutor,
			request.Planner,
			request.ApprovalCallback,
			request.Policy,
			progress: request.Progress);

	private void OnDesktopProgress(DesktopTaskState state, DesktopVisionProgress progress)
	{
		state.Report(progress);
		NotifyChanged();
	}

	private void OnBrowserProgress(BrowserTaskState state, BrowserAutomationProgress progress)
	{
		state.Report(progress);
		if (progress.State == BrowserAutomationProgressState.ActionSucceeded && progress.ActionKind is { } actionKind)
		{
			RecordAudit(new AutomationAuditEvent(
				DateTimeOffset.UtcNow,
				state.TaskId,
				AutomationAuditTaskKind.Browser,
				ToAuditCategory(actionKind),
				AutomationAuditOutcome.Succeeded));
		}
		NotifyChanged();
	}

	private void OnBrowserSafePagePause(BrowserTaskState state)
	{
		state.Report(new BrowserAutomationProgress(
			state.Step,
			null,
			BrowserAutomationProgressState.Paused,
			null,
			"safe_page"));
		RecordAudit(new AutomationAuditEvent(
			DateTimeOffset.UtcNow,
			state.TaskId,
			AutomationAuditTaskKind.Browser,
			AutomationAuditEventCategory.SafePage,
			AutomationAuditOutcome.Rejected,
			"safe_page"));
		NotifyChanged();
	}

	private void StoreBrowserResult(BrowserTaskState state, BrowserAutomationExecutionResult result)
	{
		string? failureCode = result.Succeeded ? null : NormalizePublicFailureCode(result.FailureCode);
		BrowserAutomationTaskResult stored = new(
			state.TaskId,
			result.Succeeded,
			result.Succeeded ? result.VisibleText : null,
			failureCode,
			DateTimeOffset.UtcNow);
		_browserResults.Set(stored);
		state.SetResult(stored);
		NotifyChanged();
	}

	private Task EnsureBrowserTaskExecutionAsync(CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		ThrowIfBrowserExecutionBlocked(GetSettings());
		if (_browserRunner is null) throw new AutomationTaskExecutionException("browser_unavailable");
		return Task.CompletedTask;
	}

	private void ApplyTaskSnapshot(AutomationTaskSnapshot snapshot)
	{
		lock (_desktopStateGate)
		{
			if (_desktopTasks.TryGetValue(snapshot.Id, out DesktopTaskState? desktopState)) desktopState.ApplyLifecycle(snapshot);
			if (_browserTasks.TryGetValue(snapshot.Id, out BrowserTaskState? browserState)) browserState.ApplyLifecycle(snapshot);
		}
	}

	private IReadOnlyList<AutomationTaskStatusSnapshot> GetTaskSnapshots()
	{
		lock (_desktopStateGate)
		{
			return _desktopTasks.Values.Select(state => (state.Sequence, Snapshot: state.ToSnapshot()))
				.Concat(_browserTasks.Values.Select(state => (state.Sequence, Snapshot: state.ToSnapshot())))
				.OrderByDescending(item => item.Sequence)
				.Select(item => item.Snapshot)
				.ToArray();
		}
	}

	private IReadOnlyList<AutomationDesktopApprovalSnapshot> GetDesktopApprovalSnapshots()
	{
		lock (_desktopStateGate) return _desktopApprovals.Values.ToArray();
	}

	private void TrimPublicTasks()
	{
		while (_desktopTasks.Count + _browserTasks.Count > MaxPublicAutomationTasks)
		{
			long oldestDesktop = _desktopTasks.Count == 0 ? long.MaxValue : _desktopTasks.Min(pair => pair.Value.Sequence);
			long oldestBrowser = _browserTasks.Count == 0 ? long.MaxValue : _browserTasks.Min(pair => pair.Value.Sequence);
			if (oldestDesktop <= oldestBrowser)
			{
				Guid id = _desktopTasks.First(pair => pair.Value.Sequence == oldestDesktop).Key;
				_desktopTasks.Remove(id);
			}
			else
			{
				Guid id = _browserTasks.First(pair => pair.Value.Sequence == oldestBrowser).Key;
				_browserTasks.Remove(id);
				_browserResults.Remove(id);
			}
		}
	}

	private long NextTaskSequence() => Interlocked.Increment(ref _publicTaskSequence);

	private void OnTaskChanged(AutomationTaskSnapshot snapshot)
	{
		ApplyTaskSnapshot(snapshot);
		if (TryGetAuditTaskKind(snapshot.Id, out AutomationAuditTaskKind taskKind)) RecordTaskLifecycle(snapshot, taskKind);
		NotifyChanged();
	}

	private void RecordTaskLifecycle(AutomationTaskSnapshot snapshot, AutomationAuditTaskKind taskKind)
	{
		AutomationAuditOutcome outcome = snapshot.State switch
		{
			AutomationTaskState.Queued => AutomationAuditOutcome.Queued,
			AutomationTaskState.Running => AutomationAuditOutcome.Running,
			AutomationTaskState.Paused => AutomationAuditOutcome.Paused,
			AutomationTaskState.Completed => AutomationAuditOutcome.Succeeded,
			AutomationTaskState.Cancelled => AutomationAuditOutcome.Cancelled,
			AutomationTaskState.Failed => AutomationAuditOutcome.Failed,
			_ => AutomationAuditOutcome.Failed,
		};
		TimeSpan? duration = snapshot.StartedAt is { } startedAt && snapshot.FinishedAt is { } finishedAt
			? finishedAt - startedAt
			: null;
		RecordAudit(new AutomationAuditEvent(
			DateTimeOffset.UtcNow,
			snapshot.Id,
			taskKind,
			AutomationAuditEventCategory.Task,
			outcome,
			snapshot.State == AutomationTaskState.Failed ? NormalizePublicFailureCode(snapshot.FailureCode) : null,
			duration));
	}

	private void RecordAudit(AutomationAuditEvent entry)
	{
		try { _auditSink?.Record(entry); }
		catch { /* 审计持久化失败不能破坏自动化 fail-closed 生命周期 */ }
	}

	private void CancelBrowserTasks()
	{
		Guid[] taskIds;
		lock (_desktopStateGate) taskIds = _browserTasks.Keys.ToArray();
		foreach (Guid taskId in taskIds) _tasks.Cancel(taskId);
	}

	private bool IsBrowserTask(Guid taskId)
	{
		lock (_desktopStateGate) return _browserTasks.ContainsKey(taskId);
	}

	private AutomationAuditTaskKind GetAuditTaskKind(Guid taskId) =>
		TryGetAuditTaskKind(taskId, out AutomationAuditTaskKind taskKind) ? taskKind : AutomationAuditTaskKind.Desktop;

	private bool TryGetAuditTaskKind(Guid taskId, out AutomationAuditTaskKind taskKind)
	{
		lock (_desktopStateGate)
		{
			if (_browserTasks.ContainsKey(taskId))
			{
				taskKind = AutomationAuditTaskKind.Browser;
				return true;
			}
			if (_desktopTasks.ContainsKey(taskId))
			{
				taskKind = AutomationAuditTaskKind.Desktop;
				return true;
			}
		}
		taskKind = default;
		return false;
	}

	private void NotifyChanged()
	{
		try { Changed?.Invoke(); }
		catch { /* 状态通知不能影响自动化生命周期 */ }
	}

	private AutomationTaskStatusSnapshot? ToStatus(AutomationTaskSnapshot? task)
	{
		if (task is null) return null;
		lock (_desktopStateGate)
		{
			if (_desktopTasks.TryGetValue(task.Id, out DesktopTaskState? desktopState)) return desktopState.ToSnapshot(task);
			if (_browserTasks.TryGetValue(task.Id, out BrowserTaskState? browserState)) return browserState.ToSnapshot(task);
			return new(task.Id, task.State, 0, LifecycleCategory(task.State), task.State == AutomationTaskState.Failed ? task.FailureCode : null);
		}
	}

	private static string LifecycleCategory(AutomationTaskState state) => state switch
	{
		AutomationTaskState.Queued => "queued",
		AutomationTaskState.Running => "running",
		AutomationTaskState.Paused => "paused",
		AutomationTaskState.Completed => "completed",
		AutomationTaskState.Cancelled => "cancelled",
		AutomationTaskState.Failed => "execution_failed",
		_ => "unknown",
	};

	private static AutomationAuditEventCategory ToAuditCategory(BrowserAutomationActionKind actionKind) => actionKind switch
	{
		BrowserAutomationActionKind.Navigate => AutomationAuditEventCategory.Navigate,
		BrowserAutomationActionKind.Click => AutomationAuditEventCategory.Click,
		BrowserAutomationActionKind.Fill => AutomationAuditEventCategory.Fill,
		BrowserAutomationActionKind.Scroll => AutomationAuditEventCategory.Scroll,
		BrowserAutomationActionKind.Wait => AutomationAuditEventCategory.Wait,
		BrowserAutomationActionKind.ReadVisibleText => AutomationAuditEventCategory.ReadVisibleText,
		_ => AutomationAuditEventCategory.Task,
	};

	private static string NormalizePublicFailureCode(string? failureCode) => failureCode switch
	{
		"timeout" => "timeout",
		"safe_page" => "safe_page",
		"approval_denied" => "approval_denied",
		"approval_failed" => "approval_failed",
		"policy_rejected" => "policy_rejected",
		"invalid_action" => "invalid_action",
		"browser_unavailable" => "browser_unavailable",
		"cancelled" => "cancelled",
		_ => "execution_failed",
	};

	private sealed record DesktopWindowTarget(nint Handle, int Width, int Height, bool IsForeground);

	private sealed class DesktopTaskState
	{
		private readonly object _gate = new();
		private Guid _taskId;
		private AutomationTaskSnapshot? _lifecycle;
		private int _step;
		private string _category = "queued";
		private string? _errorCategory;
		private long _sequence;

		public long Sequence => Volatile.Read(ref _sequence);

		public void Attach(Guid taskId, long sequence)
		{
			lock (_gate)
			{
				_taskId = taskId;
				_sequence = sequence;
			}
		}

		public void Report(DesktopVisionProgress progress)
		{
			lock (_gate)
			{
				_step = Math.Max(0, progress.Step);
				_category = progress.Category switch
				{
					DesktopVisionAutomationCategory.Running => "running",
					DesktopVisionAutomationCategory.StepSucceeded => "step_succeeded",
					_ => new DesktopVisionAutomationResult(progress.Category, progress.Step).StableCategory,
				};
				_errorCategory = IsError(progress.Category) ? new DesktopVisionAutomationResult(progress.Category, progress.Step).StableCategory : null;
				Interlocked.Increment(ref _sequence);
			}
		}

		public void MarkReturned()
		{
			lock (_gate)
			{
				if (_category is "queued" or "running" or "step_succeeded") _category = "completed";
				Interlocked.Increment(ref _sequence);
			}
		}

		public void ApplyLifecycle(AutomationTaskSnapshot snapshot)
		{
			lock (_gate)
			{
				_lifecycle = snapshot;
				if (snapshot.State == AutomationTaskState.Cancelled)
				{
					_category = "cancelled";
					_errorCategory = null;
				}
				else if (snapshot.State == AutomationTaskState.Failed)
				{
					_category = snapshot.FailureCode ?? "execution_failed";
					_errorCategory = _category;
				}
				else if (snapshot.State == AutomationTaskState.Paused)
				{
					_category = "paused";
					_errorCategory = null;
				}
				else if (snapshot.State == AutomationTaskState.Running && _category == "queued")
				{
					_category = "running";
				}
				Interlocked.Increment(ref _sequence);
			}
		}

		public AutomationTaskStatusSnapshot ToSnapshot(AutomationTaskSnapshot? current = null)
		{
			lock (_gate)
			{
				AutomationTaskSnapshot lifecycle = current ?? _lifecycle ?? new AutomationTaskSnapshot(
					_taskId,
					AutomationTaskState.Queued,
					DateTimeOffset.UtcNow,
					null,
					null,
					null);
				AutomationTaskState state = lifecycle.State;
				if (state == AutomationTaskState.Completed && _errorCategory is not null) state = AutomationTaskState.Failed;
				string category = _category;
				string? error = _errorCategory;
				return new(_taskId, state, _step, category, error)
				{
					TaskKind = "desktop",
				};
			}
		}

		private static bool IsError(DesktopVisionAutomationCategory category) => category is not (
			DesktopVisionAutomationCategory.Running
			or DesktopVisionAutomationCategory.StepSucceeded
			or DesktopVisionAutomationCategory.Completed);
	}

	private sealed class BrowserTaskState
	{
		private readonly object _gate = new();
		private readonly int _totalSteps;
		private Guid _taskId;
		private AutomationTaskSnapshot? _lifecycle;
		private int _step;
		private string _category = "queued";
		private string? _errorCategory;
		private string? _pauseReason;
		private Guid? _approvalRequestId;
		private IReadOnlyList<AutomationActionKind> _actionKinds = [];
		private bool _hasResult;
		private string? _resultSummary;
		private long _sequence;

		public BrowserTaskState(int totalSteps)
		{
			if (totalSteps is < 1 or > BrowserAutomationTaskLimits.MaxActions)
				throw new ArgumentOutOfRangeException(nameof(totalSteps));
			_totalSteps = totalSteps;
		}

		public Guid TaskId { get { lock (_gate) return _taskId; } }
		public int Step { get { lock (_gate) return _step; } }
		public long Sequence => Volatile.Read(ref _sequence);

		public void Attach(Guid taskId, long sequence)
		{
			lock (_gate)
			{
				_taskId = taskId;
				_sequence = sequence;
			}
		}

		public void Report(BrowserAutomationProgress progress)
		{
			lock (_gate)
			{
				_step = Math.Clamp(Math.Max(_step, progress.Step), 0, _totalSteps);
				switch (progress.State)
				{
					case BrowserAutomationProgressState.Running:
						if (_category is "queued" or "awaiting_approval") _category = "running";
						_approvalRequestId = null;
						_actionKinds = [];
						break;
					case BrowserAutomationProgressState.AwaitingApproval:
						// 审批请求先由宿主登记，再由 MarkApprovalPending 对外公开，避免 UI 看到空的 pendingApprovals。
						_approvalRequestId = progress.ApprovalRequestId;
						break;
					case BrowserAutomationProgressState.ActionSucceeded:
						_category = "step_succeeded";
						_approvalRequestId = null;
						_actionKinds = [];
						break;
					case BrowserAutomationProgressState.Paused:
						_category = "paused";
						_pauseReason = progress.PauseReason == "safe_page" ? "safe_page" : "safe_page";
						_approvalRequestId = null;
						_actionKinds = [];
						break;
				}
			}
		}

		public void MarkApprovalPending(Guid requestId, IReadOnlyList<AutomationActionKind> actionKinds)
		{
			lock (_gate)
			{
				_category = "awaiting_approval";
				_approvalRequestId = requestId;
				_actionKinds = actionKinds;
			}
		}

		public void SetResult(BrowserAutomationTaskResult result)
		{
			lock (_gate)
			{
				_hasResult = true;
				_resultSummary = result.Succeeded ? "浏览器任务已完成" : null;
				if (!result.Succeeded && result.FailureCode != "safe_page" && _errorCategory is null)
					_errorCategory = NormalizePublicFailureCode(result.FailureCode);
			}
		}

		public void ApplyLifecycle(AutomationTaskSnapshot snapshot)
		{
			lock (_gate)
			{
				_lifecycle = snapshot;
				switch (snapshot.State)
				{
					case AutomationTaskState.Queued:
						_category = "queued";
						break;
					case AutomationTaskState.Running when _category == "queued":
						_category = "running";
						break;
					case AutomationTaskState.Paused:
						_category = "paused";
						_pauseReason = snapshot.PauseReason == "safe_page" ? "safe_page" : "safe_page";
						_errorCategory = null;
						break;
					case AutomationTaskState.Completed:
						if (_category is "queued" or "running" or "step_succeeded") _category = "completed";
						break;
					case AutomationTaskState.Cancelled:
						_category = "cancelled";
						_errorCategory = null;
						_approvalRequestId = null;
						_actionKinds = [];
						break;
					case AutomationTaskState.Failed:
						_category = NormalizePublicFailureCode(snapshot.FailureCode);
						_errorCategory = _category;
						_approvalRequestId = null;
						_actionKinds = [];
						break;
				}
			}
		}

		public AutomationTaskStatusSnapshot ToSnapshot(AutomationTaskSnapshot? current = null)
		{
			lock (_gate)
			{
				AutomationTaskSnapshot lifecycle = current ?? _lifecycle ?? new AutomationTaskSnapshot(
					_taskId,
					AutomationTaskState.Queued,
					DateTimeOffset.UtcNow,
					null,
					null,
					null);
				return new(_taskId, lifecycle.State, _step, _category, _errorCategory)
				{
					TaskKind = "browser",
					PauseReason = lifecycle.State == AutomationTaskState.Paused ? _pauseReason : null,
					TotalSteps = _totalSteps,
					HasResult = _hasResult,
					ResultSummary = _resultSummary,
					ActionKinds = _actionKinds,
					ApprovalRequestId = _approvalRequestId,
				};
			}
		}
	}

	private sealed class BrowserTaskRunnerAdapter(
		AutomationRuntime owner,
		IAutomationBrowserRunner runner,
		BrowserAutomationTaskPlan plan,
		BrowserTaskState state) : IAutomationTaskRunner
	{
		public async Task RunAsync(AutomationTaskContext context, CancellationToken cancellationToken)
		{
			DateTimeOffset startedAt = DateTimeOffset.UtcNow;
			using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
			timeout.CancelAfter(owner._browserTaskTimeout);
			BrowserAutomationExecutionContext executionContext = new(
				context.TaskId,
				owner.BrowserApprovalCallback,
				owner.EnsureBrowserTaskExecutionAsync,
				progress => owner.OnBrowserProgress(state, progress));
			try
			{
				BrowserAutomationExecutionResult result = await runner.ExecuteAsync(plan, executionContext, timeout.Token).ConfigureAwait(false);
				if (!result.Succeeded)
				{
					string failureCode = NormalizePublicFailureCode(result.FailureCode);
					owner.StoreBrowserResult(state, BrowserAutomationExecutionResult.Failed(result.CompletedActions, failureCode));
					throw new AutomationTaskExecutionException(failureCode);
				}
				owner.StoreBrowserResult(state, result);
			}
			catch (BrowserAutomationPolicy.PausedException) when (!cancellationToken.IsCancellationRequested)
			{
				TimeSpan remaining = owner._browserTaskTimeout - (DateTimeOffset.UtcNow - startedAt);
				if (remaining <= TimeSpan.Zero)
				{
					owner.StoreBrowserResult(state, BrowserAutomationExecutionResult.Failed(state.Step, "timeout"));
					throw new AutomationTaskExecutionException("timeout");
				}
				owner.OnBrowserSafePagePause(state);
				owner.StoreBrowserResult(state, BrowserAutomationExecutionResult.Failed(state.Step, "safe_page"));
				throw new AutomationTaskPausedException("safe_page", remaining);
			}
			catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeout.IsCancellationRequested)
			{
				owner.StoreBrowserResult(state, BrowserAutomationExecutionResult.Failed(state.Step, "timeout"));
				throw new AutomationTaskExecutionException("timeout");
			}
			catch (OperationCanceledException)
			{
				owner.StoreBrowserResult(state, BrowserAutomationExecutionResult.Failed(state.Step, "cancelled"));
				throw;
			}
			catch (AutomationTaskExecutionException exception)
			{
				owner.StoreBrowserResult(state, BrowserAutomationExecutionResult.Failed(state.Step, NormalizePublicFailureCode(exception.FailureCode)));
				throw;
			}
			catch
			{
				owner.StoreBrowserResult(state, BrowserAutomationExecutionResult.Failed(state.Step, "execution_failed"));
				throw new AutomationTaskExecutionException("execution_failed");
			}
		}
	}

	private sealed class DesktopVisionRunnerAdapter(IAutomationTaskRunner inner, DesktopTaskState state) : IAutomationTaskRunner
	{
		public async Task RunAsync(AutomationTaskContext context, CancellationToken cancellationToken)
		{
			await inner.RunAsync(context, cancellationToken).ConfigureAwait(false);
			state.MarkReturned();
		}
	}
}
