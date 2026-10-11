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
	int CurrentStep);

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
	bool VisionReady,
	IReadOnlyList<AutomationCapabilityItem> Capabilities,
	IReadOnlyList<AutomationTaskItem> Tasks,
	AutomationTaskItem? ActiveTask,
	int QueuedCount,
	IReadOnlyList<AutomationApprovalItem> PendingApprovals);

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
			NotifyChanged();
		}
		finally
		{
			IsBusy = false;
		}
	}

	/// <summary>更新总开关或桌面权限。</summary>
	public async Task UpdateSettingsAsync(
		bool? enabled = null,
		bool? allowPointer = null,
		bool? allowKeyboard = null,
		bool? allowScroll = null,
		CancellationToken cancellationToken = default)
	{
		Dictionary<string, object?> args = [];
		if (enabled is { } enabledValue) args["enabled"] = enabledValue;
		if (allowPointer is { } pointerValue) args["allowPointer"] = pointerValue;
		if (allowKeyboard is { } keyboardValue) args["allowKeyboard"] = keyboardValue;
		if (allowScroll is { } scrollValue) args["allowScroll"] = scrollValue;
		if (args.Count == 0) return;
		await ExecuteAsync("automation_update_settings", args, cancellationToken).ConfigureAwait(true);
		await RefreshAsync(cancellationToken).ConfigureAwait(true);
	}

	/// <summary>兼容设置页使用的自动化与桌面总开关。</summary>
	public async Task UpdateFrontendTogglesAsync(bool? enabled = null, bool? desktopEnabled = null, CancellationToken cancellationToken = default)
	{
		Dictionary<string, object?> args = [];
		if (enabled is { } enabledValue) args["enabled"] = enabledValue;
		if (desktopEnabled is { } desktopValue) args["desktopEnabled"] = desktopValue;
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
		[],
		[],
		null,
		0,
		[]);

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
			SettingsJson.Bool(capabilities, "visionReady") || SettingsJson.Bool(value, "visionReady"),
			capabilityItems,
			tasks,
			active.ValueKind is JsonValueKind.Object ? ParseTask(active) : null,
			SettingsJson.Int(value, "queuedCount"),
			approvals);
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
		SettingsJson.Int(value, "currentStep", SettingsJson.Int(value, "step")));

	private static AutomationApprovalItem ParseApproval(JsonElement value) => new(
		SettingsJson.String(value, "requestId"),
		SettingsJson.String(value, "taskId"),
		ReadStrings(value, "actionKinds"));

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
