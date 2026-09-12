using System.Text.Json;

namespace Nori.Desktop.Settings.Pages;

/// <summary>自动化能力状态。</summary>
public sealed record AutomationCapabilityItem(string Id, string Name, bool Available, string? UnavailableReason);

/// <summary>自动化任务状态，只保留宿主允许公开的脱敏字段。</summary>
public sealed record AutomationTaskItem(
	string Id,
	string State,
	int Step,
	string ProgressCategory,
	string? ErrorCategory,
	string TaskKind,
	string? PauseReason,
	int CurrentStep,
	int? TotalSteps,
	bool HasResult,
	string? ResultSummary,
	string? ApprovalRequestId);

/// <summary>浏览器自动化运行状态。</summary>
public sealed record AutomationBrowserItem(string State, bool Enabled, bool Available, string? UnavailableReason, bool Running);

/// <summary>等待用户决定的桌面自动化权限请求。</summary>
public sealed record AutomationApprovalItem(string RequestId, string TaskId, IReadOnlyList<string> ActionKinds);

/// <summary>自动化设置页的脱敏状态。</summary>
public sealed record AutomationStateModel(
	bool Enabled,
	bool Available,
	string? UnavailableReason,
	bool AllowPointer,
	bool AllowKeyboard,
	bool AllowScroll,
	bool BrowserEnabled,
	bool VisionReady,
	IReadOnlyList<AutomationCapabilityItem> Capabilities,
	IReadOnlyList<AutomationTaskItem> Tasks,
	AutomationTaskItem? ActiveTask,
	int QueuedCount,
	IReadOnlyList<AutomationApprovalItem> PendingApprovals,
	AutomationBrowserItem Browser);

/// <summary>自动化审计记录。</summary>
public sealed record AutomationAuditItem(
	string Time,
	string TaskKind,
	string Category,
	string Outcome,
	string? FailureCode,
	string? DurationMs);

/// <summary>自动化设置页的状态和启停命令。</summary>
public sealed class AutomationSettingsViewModel : SettingsPageViewModelBase
{
	private AutomationStateModel _state = EmptyState;
	private IReadOnlyList<AutomationAuditItem> _audit = [];
	private bool _safeMode;
	private bool _isSupported;
	private bool _platformSupported;

	/// <summary>创建自动化 ViewModel。</summary>
	public AutomationSettingsViewModel(SettingsService service) : base(service) { }

	/// <summary>当前自动化状态。</summary>
	public AutomationStateModel State
	{
		get => _state;
		private set => SetProperty(ref _state, value);
	}

	/// <summary>自动化审计记录。</summary>
	public IReadOnlyList<AutomationAuditItem> Audit
	{
		get => _audit;
		private set => SetProperty(ref _audit, value);
	}

	/// <summary>是否可用。</summary>
	public bool IsSupported => _isSupported;

	/// <summary>安全模式下不允许开启自动化能力。</summary>
	public bool SafeMode => _safeMode;

	/// <summary>平台支持且未进入安全模式时可以配置开关。</summary>
	public bool CanConfigure => IsSupported && !SafeMode && _platformSupported;

	/// <summary>桌面总开关与旧设置菜单保持相同投影。</summary>
	public bool DesktopEnabled => State.AllowPointer || State.AllowKeyboard || State.AllowScroll;

	/// <inheritdoc />
	public override async Task RefreshAsync(CancellationToken cancellationToken = default)
	{
		IsBusy = true;
		try
		{
			JsonElement snapshot = await Service.GetSnapshotAsync(cancellationToken).ConfigureAwait(true);
			_safeMode = SettingsJson.Bool(SettingsJson.Object(snapshot, "app"), "safeMode");
			_isSupported = SettingsJson.IsObject(SettingsJson.Object(snapshot, "automation"));
			JsonElement detailed = _isSupported
				? await Service.GetAutomationSnapshotAsync(cancellationToken).ConfigureAwait(true)
				: default;
			_platformSupported = SettingsJson.Bool(SettingsJson.Object(detailed, "capabilities"), "isWindows");
			State = ParseState(detailed);
			try
			{
				JsonElement browser = await Service.ExecuteAsync("automation_browser_status", cancellationToken: cancellationToken).ConfigureAwait(true);
				AutomationBrowserItem browserState = ParseBrowser(browser);
				State = State with {Browser = browserState};
			}
			catch (InvalidOperationException)
			{
				// 浏览器 feature pack 缺失时，以快照中的 fail-closed 状态为准。
			}
			NotifyChanged();
		}
		finally
		{
			IsBusy = false;
		}
	}

	/// <summary>更新总开关、桌面权限或浏览器开关。</summary>
	public async Task UpdateSettingsAsync(
		bool? enabled = null,
		bool? allowPointer = null,
		bool? allowKeyboard = null,
		bool? allowScroll = null,
		bool? browserEnabled = null,
		CancellationToken cancellationToken = default)
	{
		Dictionary<string, object?> args = [];
		if (enabled is { } enabledValue) args["enabled"] = enabledValue;
		if (allowPointer is { } pointerValue) args["allowPointer"] = pointerValue;
		if (allowKeyboard is { } keyboardValue) args["allowKeyboard"] = keyboardValue;
		if (allowScroll is { } scrollValue) args["allowScroll"] = scrollValue;
		if (browserEnabled is { } browserValue) args["browserEnabled"] = browserValue;
		if (args.Count == 0) return;
		await ExecuteAsync("automation_update_settings", args, cancellationToken).ConfigureAwait(true);
		await RefreshAsync(cancellationToken).ConfigureAwait(true);
	}

	/// <summary>兼容设置页使用的桌面和浏览器总开关。</summary>
	public async Task UpdateFrontendTogglesAsync(bool? enabled = null, bool? desktopEnabled = null, bool? browserEnabled = null, CancellationToken cancellationToken = default)
	{
		Dictionary<string, object?> args = [];
		if (enabled is { } enabledValue) args["enabled"] = enabledValue;
		if (desktopEnabled is { } desktopValue) args["desktopEnabled"] = desktopValue;
		if (browserEnabled is { } browserValue) args["browserEnabled"] = browserValue;
		if (args.Count == 0) return;
		await ExecuteAsync("settings_update_automation", args, cancellationToken).ConfigureAwait(true);
		await RefreshAsync(cancellationToken).ConfigureAwait(true);
	}

	/// <summary>探测桌面视觉能力。</summary>
	public async Task<(bool Available, string? Reason)> ProbeVisionAsync(CancellationToken cancellationToken = default)
	{
		JsonElement result = await ExecuteAsync("automation_probe_vision", cancellationToken: cancellationToken).ConfigureAwait(true);
		return (SettingsJson.Bool(result, "available"), SettingsJson.NullableString(result, "reason"));
	}

	/// <summary>启动隔离浏览器会话。</summary>
	public async Task<AutomationBrowserItem> StartBrowserAsync(CancellationToken cancellationToken = default)
	{
		JsonElement result = await ExecuteAsync("automation_browser_start", cancellationToken: cancellationToken).ConfigureAwait(true);
		AutomationBrowserItem state = ParseBrowser(result);
		State = State with {Browser = state};
		NotifyChanged();
		return state;
	}

	/// <summary>停止隔离浏览器会话。</summary>
	public async Task<AutomationBrowserItem> StopBrowserAsync(CancellationToken cancellationToken = default)
	{
		JsonElement result = await ExecuteAsync("automation_browser_stop", cancellationToken: cancellationToken).ConfigureAwait(true);
		AutomationBrowserItem state = ParseBrowser(result);
		State = State with {Browser = state};
		NotifyChanged();
		return state;
	}

	/// <summary>启动受限浏览器任务。参数必须是浏览器动作数组。</summary>
	public async Task<string?> StartBrowserTaskAsync(string actionsJson, CancellationToken cancellationToken = default)
	{
		JsonElement actions = SettingsJson.Parse(string.IsNullOrWhiteSpace(actionsJson) ? "[]" : actionsJson);
		if (actions.ValueKind != JsonValueKind.Array || actions.GetArrayLength() is < 1 or > 16)
			throw new ArgumentException("浏览器任务动作必须是 1 到 16 项的 JSON 数组。", nameof(actionsJson));
		JsonElement result = await ExecuteAsync("automation_browser_start_task", new {actions}, cancellationToken).ConfigureAwait(true);
		string id = SettingsJson.String(result, "taskId", SettingsJson.String(result, "id"));
		await RefreshAsync(cancellationToken).ConfigureAwait(true);
		return id.Length == 0 ? null : id;
	}

	/// <summary>停止指定自动化任务。</summary>
	public async Task<bool> StopTaskAsync(string taskId, CancellationToken cancellationToken = default)
	{
		if (string.IsNullOrWhiteSpace(taskId)) throw new ArgumentException("任务 ID 不能为空。", nameof(taskId));
		JsonElement result = await ExecuteAsync("automation_stop_task", new {taskId = taskId.Trim()}, cancellationToken).ConfigureAwait(true);
		await RefreshAsync(cancellationToken).ConfigureAwait(true);
		return result.ValueKind != JsonValueKind.False;
	}

	/// <summary>停止所有自动化任务。</summary>
	public async Task StopAllAsync(CancellationToken cancellationToken = default)
	{
		await ExecuteAsync("automation_stop_all", cancellationToken: cancellationToken).ConfigureAwait(true);
		await RefreshAsync(cancellationToken).ConfigureAwait(true);
	}

	/// <summary>回应待处理的自动化授权请求。</summary>
	public async Task<bool> RespondApprovalAsync(string requestId, bool approved, CancellationToken cancellationToken = default)
	{
		if (string.IsNullOrWhiteSpace(requestId)) throw new ArgumentException("授权请求 ID 不能为空。", nameof(requestId));
		JsonElement result = await ExecuteAsync("approval_respond", new {requestId = requestId.Trim(), approved}, cancellationToken).ConfigureAwait(true);
		await RefreshAsync(cancellationToken).ConfigureAwait(true);
		return result.ValueKind != JsonValueKind.False;
	}

	/// <summary>读取自动化审计列表。</summary>
	public async Task<IReadOnlyList<AutomationAuditItem>> LoadAuditAsync(int limit = 50, CancellationToken cancellationToken = default)
	{
		JsonElement result = await ExecuteAsync("automation_audit_list", new {limit = Math.Clamp(limit, 1, 100)}, cancellationToken).ConfigureAwait(true);
		Audit = SettingsJson.RootArray(result).Select(ParseAudit).ToArray();
		NotifyChanged(nameof(Audit));
		return Audit;
	}

	private static readonly AutomationStateModel EmptyState = new(
		false,
		false,
		"",
		false,
		false,
		false,
		false,
		false,
		[],
		[],
		null,
		0,
		[],
		new AutomationBrowserItem("stopped", false, false, "", false));

	private static AutomationStateModel ParseState(JsonElement value)
	{
		if (!SettingsJson.IsObject(value)) return EmptyState;
		JsonElement settings = SettingsJson.Object(value, "settings");
		JsonElement capabilities = SettingsJson.Object(value, "capabilities");
		IReadOnlyList<AutomationCapabilityItem> capabilityItems = ParseCapabilities(capabilities, SettingsJson.Array(value, "capabilities"));
		IReadOnlyList<AutomationTaskItem> tasks = SettingsJson.Array(value, "tasks").Select(ParseTask).ToArray();
		IReadOnlyList<AutomationApprovalItem> approvals = SettingsJson.Array(value, "pendingApprovals").Select(ParseApproval).ToArray();
		JsonElement active = SettingsJson.Property(value, "activeTask");
		return new AutomationStateModel(
			SettingsJson.Bool(value, "enabled"),
			SettingsJson.Bool(value, "available", SettingsJson.Bool(capabilities, "desktop")),
			SettingsJson.NullableString(value, "unavailableReason") ?? SettingsJson.NullableString(capabilities, "unavailableReason"),
			SettingsJson.Bool(settings, "allowPointer", SettingsJson.Bool(value, "desktopEnabled")),
			SettingsJson.Bool(settings, "allowKeyboard"),
			SettingsJson.Bool(settings, "allowScroll"),
			SettingsJson.Bool(settings, "browserEnabled", SettingsJson.Bool(value, "browserEnabled")),
			SettingsJson.Bool(capabilities, "visionReady") || SettingsJson.Bool(value, "visionReady"),
			capabilityItems,
			tasks,
			active.ValueKind is JsonValueKind.Object ? ParseTask(active) : null,
			SettingsJson.Int(value, "queuedCount"),
			approvals,
			ParseBrowser(SettingsJson.Object(value, "browser")));
	}

	private static IReadOnlyList<AutomationCapabilityItem> ParseCapabilities(JsonElement objectValue, IEnumerable<JsonElement> arrayValues)
	{
		IReadOnlyList<AutomationCapabilityItem> array = arrayValues.Select(value => new AutomationCapabilityItem(
			SettingsJson.String(value, "id"),
			SettingsJson.String(value, "name", SettingsJson.String(value, "id")),
			SettingsJson.Bool(value, "available"),
			SettingsJson.NullableString(value, "unavailableReason"))).ToArray();
		if (array.Count > 0) return array;
		if (!SettingsJson.IsObject(objectValue)) return [];
		string? reason = SettingsJson.NullableString(objectValue, "unavailableReason");
		return [
			new("desktop", "desktop", SettingsJson.Bool(objectValue, "desktop"), reason),
			new("browser", "browser", SettingsJson.Bool(objectValue, "browser"), reason),
			new("vision", "vision", SettingsJson.Bool(objectValue, "visionReady"), reason),
			new("pointer", "pointer", SettingsJson.Bool(objectValue, "pointer"), reason),
			new("keyboard", "keyboard", SettingsJson.Bool(objectValue, "keyboard"), reason),
			new("scroll", "scroll", SettingsJson.Bool(objectValue, "scroll"), reason),
		];
	}

	private static AutomationTaskItem ParseTask(JsonElement value) => new(
		SettingsJson.String(value, "id", SettingsJson.String(value, "taskId")),
		SettingsJson.String(value, "state", "unknown"),
		SettingsJson.Int(value, "step"),
		SettingsJson.String(value, "progressCategory", "unknown"),
		SettingsJson.NullableString(value, "errorCategory"),
		SettingsJson.String(value, "taskKind", "desktop"),
		SettingsJson.NullableString(value, "pauseReason"),
		SettingsJson.Int(value, "currentStep", SettingsJson.Int(value, "step")),
		SettingsJson.Has(value, "totalSteps") ? SettingsJson.Int(value, "totalSteps") : null,
		SettingsJson.Bool(value, "hasResult"),
		SettingsJson.NullableString(value, "resultSummary"),
		SettingsJson.NullableString(value, "approvalRequestId"));

	private static AutomationApprovalItem ParseApproval(JsonElement value) => new(
		SettingsJson.String(value, "requestId"),
		SettingsJson.String(value, "taskId"),
		ReadStrings(value, "actionKinds"));

	private static AutomationBrowserItem ParseBrowser(JsonElement value) => new(
		SettingsJson.String(value, "state", "stopped"),
		SettingsJson.Bool(value, "enabled"),
		SettingsJson.Bool(value, "available"),
		SettingsJson.NullableString(value, "unavailableReason"),
		SettingsJson.Bool(value, "running"));

	private static AutomationAuditItem ParseAudit(JsonElement value) => new(
		SettingsJson.String(value, "timestamp", SettingsJson.String(value, "time")),
		SettingsJson.String(value, "taskKind"),
		SettingsJson.String(value, "category"),
		SettingsJson.String(value, "outcome"),
		SettingsJson.NullableString(value, "failureCode"),
		SettingsJson.NullableString(value, "durationMs"));

	private static IReadOnlyList<string> ReadStrings(JsonElement value, string name)
	{
		JsonElement array = SettingsJson.Property(value, name);
		return array.ValueKind == JsonValueKind.Array
			? array.EnumerateArray().Select(item => item.ValueKind == JsonValueKind.String ? item.GetString() ?? "" : item.GetRawText()).Where(item => item.Length > 0).ToArray()
			: [];
	}
}
