using System.Text.Json;

namespace Nori.Desktop.Settings.Pages;

/// <summary>宿主日志条目。</summary>
public sealed record DebugLogItem(string Time, string Level, string Source, string Message);

/// <summary>诊断导出结果。</summary>
public sealed record DiagnosticExportItem(string FileName, long Bytes, IReadOnlyList<string> Skipped);

/// <summary>调试诊断页的状态和宿主命令编排。</summary>
public sealed class DebugSettingsViewModel : SettingsPageViewModelBase
{
	private IReadOnlyList<DebugLogItem> _logs = [];
	private IReadOnlyDictionary<string, string> _diagnostic = new Dictionary<string, string>(StringComparer.Ordinal);
	private string _levelFilter = "all";
	private bool _crashTestsAvailable;
	private long? _releasedBytes;

	/// <summary>创建调试诊断 ViewModel。</summary>
	public DebugSettingsViewModel(SettingsService service) : base(service) { }

	/// <summary>当前日志。</summary>
	public IReadOnlyList<DebugLogItem> Logs
	{
		get => _logs;
		private set
		{
			if (_logs.SequenceEqual(value)) return;
			SetProperty(ref _logs, value);
			NotifyChanged(nameof(FilteredLogs));
		}
	}

	/// <summary>当前诊断信息。</summary>
	public IReadOnlyDictionary<string, string> Diagnostic
	{
		get => _diagnostic;
		private set => SetProperty(ref _diagnostic, value);
	}

	/// <summary>日志过滤级别。</summary>
	public string LevelFilter
	{
		get => _levelFilter;
		set
		{
			string next = value is "error" or "warn" or "info" ? value : "all";
			if (!SetProperty(ref _levelFilter, next)) return;
			NotifyChanged(nameof(FilteredLogs));
		}
	}

	/// <summary>是否允许危险崩溃探针。</summary>
	public bool CrashTestsAvailable
	{
		get => _crashTestsAvailable;
		private set => SetProperty(ref _crashTestsAvailable, value);
	}

	/// <summary>最近一次垃圾回收释放的字节数。</summary>
	public long? ReleasedBytes
	{
		get => _releasedBytes;
		private set => SetProperty(ref _releasedBytes, value);
	}

	/// <summary>过滤后的日志。</summary>
	public IReadOnlyList<DebugLogItem> FilteredLogs =>
		LevelFilter == "all" ? Logs : Logs.Where(item => string.Equals(item.Level, LevelFilter, StringComparison.OrdinalIgnoreCase)).ToArray();

	/// <inheritdoc />
	public override async Task RefreshAsync(CancellationToken cancellationToken = default)
	{
		IsBusy = true;
		try
		{
			JsonElement snapshot = await Service.GetSnapshotAsync(cancellationToken).ConfigureAwait(true);
			CrashTestsAvailable = SettingsJson.Bool(SettingsJson.Object(snapshot, "app"), "debugCrashTestsAvailable");
			await RefreshDiagnosticCoreAsync(cancellationToken).ConfigureAwait(true);
			await RefreshLogsCoreAsync(cancellationToken).ConfigureAwait(true);
		}
		finally
		{
			IsBusy = false;
		}
	}

	/// <summary>刷新日志。</summary>
	public async Task RefreshLogsAsync(CancellationToken cancellationToken = default)
	{
		await RefreshLogsCoreAsync(cancellationToken).ConfigureAwait(true);
	}

	/// <summary>刷新诊断信息。</summary>
	public async Task RefreshDiagnosticAsync(CancellationToken cancellationToken = default)
	{
		await RefreshDiagnosticCoreAsync(cancellationToken).ConfigureAwait(true);
	}

	/// <summary>清空内存中的日志缓冲。</summary>
	public async Task ClearLogsAsync(CancellationToken cancellationToken = default)
	{
		await ExecuteAsync("clear_recent_logs", cancellationToken: cancellationToken).ConfigureAwait(true);
		Logs = [];
	}

	/// <summary>把当前日志复制到剪贴板。</summary>
	public Task CopyLogsAsync(CancellationToken cancellationToken = default) => CopyTextAsync(
		string.Join(Environment.NewLine, FilteredLogs.Select(item => $"[{item.Time}] [{item.Level}] [{item.Source}] {item.Message}")),
		cancellationToken);

	/// <summary>把当前诊断信息复制到剪贴板。</summary>
	public Task CopyDiagnosticAsync(CancellationToken cancellationToken = default) => CopyTextAsync(
		string.Join(Environment.NewLine, Diagnostic.Select(item => $"{item.Key}: {item.Value}")),
		cancellationToken);

	/// <summary>打开宿主日志目录。</summary>
	public async Task OpenLogFolderAsync(CancellationToken cancellationToken = default)
	{
		await ExecuteAsync("open_log_folder", cancellationToken: cancellationToken).ConfigureAwait(true);
	}

	/// <summary>导出脱敏诊断压缩包。</summary>
	public async Task<DiagnosticExportItem?> ExportDiagnosticsAsync(CancellationToken cancellationToken = default)
	{
		JsonElement result = await ExecuteAsync("export_diagnostics", cancellationToken: cancellationToken).ConfigureAwait(true);
		if (result.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return null;
		return new DiagnosticExportItem(
			SettingsJson.String(result, "fileName"),
			SettingsJson.Long(result, "bytes"),
			ReadStrings(result, "skipped"));
	}

	/// <summary>触发一次托管垃圾回收。</summary>
	public async Task<long> CollectGarbageAsync(CancellationToken cancellationToken = default)
	{
		JsonElement result = await ExecuteAsync("run_gc_collect", cancellationToken: cancellationToken).ConfigureAwait(true);
		long released = SettingsJson.Long(result, "released_bytes", SettingsJson.Long(result, "releasedBytes"));
		ReleasedBytes = released;
		return released;
	}

	/// <summary>写入一条调试日志。</summary>
	public async Task WriteTestLogAsync(CancellationToken cancellationToken = default)
	{
		await ExecuteAsync("write_log", new {level = "warn", message = "调试页测试日志: 原生设置页到宿主日志链路正常"}, cancellationToken).ConfigureAwait(true);
		await RefreshLogsCoreAsync(cancellationToken).ConfigureAwait(true);
	}

	/// <summary>触发受宿主构建限制保护的崩溃探针。</summary>
	public async Task TriggerCrashAsync(string mode, CancellationToken cancellationToken = default)
	{
		if (!CrashTestsAvailable) throw new InvalidOperationException("当前构建未开放调试崩溃探针。");
		if (mode is not ("ui_thread" or "background_thread" or "unobserved_task")) throw new ArgumentException("未知的崩溃探针。", nameof(mode));
		await ExecuteAsync("debug_crash_test", new {mode}, cancellationToken).ConfigureAwait(true);
	}

	private async Task RefreshLogsCoreAsync(CancellationToken cancellationToken)
	{
		JsonElement result = await ExecuteAsync("get_recent_logs", cancellationToken: cancellationToken).ConfigureAwait(true);
		Logs = SettingsJson.RootArray(result).Select(value => new DebugLogItem(
			SettingsJson.String(value, "time"),
			SettingsJson.String(value, "level"),
			SettingsJson.String(value, "source"),
			SettingsJson.String(value, "message"))).ToArray();
	}

	private async Task RefreshDiagnosticCoreAsync(CancellationToken cancellationToken)
	{
		JsonElement result = await ExecuteAsync("get_diagnostic_info", cancellationToken: cancellationToken).ConfigureAwait(true);
		Dictionary<string, string> values = new(StringComparer.Ordinal);
		if (result.ValueKind == JsonValueKind.Object)
		{
			foreach (JsonProperty property in result.EnumerateObject())
				values[property.Name] = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() ?? "" : property.Value.GetRawText();
		}
		Diagnostic = values;
	}

	private async Task CopyTextAsync(string text, CancellationToken cancellationToken)
	{
		if (text.Length == 0) return;
		await ExecuteAsync("clipboard_write_text", new {text}, cancellationToken).ConfigureAwait(true);
	}

	private static IReadOnlyList<string> ReadStrings(JsonElement value, string name)
	{
		JsonElement array = SettingsJson.Property(value, name);
		return array.ValueKind == JsonValueKind.Array
			? array.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString() ?? "").ToArray()
			: [];
	}
}
