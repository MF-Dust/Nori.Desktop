using System.Text.Json;
using Nori.Desktop.Diagnostics;

namespace Nori.Desktop.Settings.Pages;

/// <summary>宿主日志条目。</summary>
public sealed record DebugLogItem(string Time, string Level, string Source, string Message,
	long Sequence = 0, string Category = "", string EventId = "", string WindowLabel = "", string ExceptionType = "")
{
	public string DisplayText => $"[{Time}] [{Level}] [{Source}/{Category}] [{EventId}] {Message}";
}

/// <summary>诊断导出结果。</summary>
public sealed record DiagnosticExportItem(string FileName, long Bytes, IReadOnlyList<string> Skipped);

/// <summary>调试诊断页的状态和宿主命令编排。</summary>
public sealed class DebugSettingsViewModel : SettingsPageViewModelBase
{
	private IReadOnlyList<DebugLogItem> _logs = [];
	private IReadOnlyDictionary<string, string> _diagnostic = new Dictionary<string, string>(StringComparer.Ordinal);
	private string _levelFilter = "all";
	private string _sourceFilter = "all";
	private string _categoryFilter = "";
	private string _searchText = "";
	private string _minimumLevel = "info";
	private bool _autoRefresh = true;
	private string _healthText = "";
	private long _logRequest;
	private bool _refreshingLogs;
	private Live2DPerformanceSnapshot _performance = Live2DPerformanceSnapshot.Empty;

	/// <summary>创建调试诊断 ViewModel。</summary>
	public DebugSettingsViewModel(SettingsService service) : base(service) { }

	/// <summary>性能面板实际读取次数。隐藏期间不得增加。</summary>
	internal int PerformanceReadCount { get; private set; }

	/// <summary>最近一次只读性能快照。</summary>
	public Live2DPerformanceSnapshot Performance
	{
		get => _performance;
		private set => SetProperty(ref _performance, value);
	}

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
			string next = Nori.Core.Logging.FileLogger.IsLevel(value) ? value : "all";
			if (!SetProperty(ref _levelFilter, next)) return;
			NotifyChanged(nameof(FilteredLogs));
		}
	}

	public string SourceFilter { get => _sourceFilter; set { if (SetProperty(ref _sourceFilter, value)) NotifyChanged(nameof(FilteredLogs)); } }
	public string CategoryFilter { get => _categoryFilter; set { if (SetProperty(ref _categoryFilter, value)) NotifyChanged(nameof(FilteredLogs)); } }
	public string SearchText { get => _searchText; set { if (SetProperty(ref _searchText, value)) NotifyChanged(nameof(FilteredLogs)); } }
	public string MinimumLevel { get => _minimumLevel; private set => SetProperty(ref _minimumLevel, value); }
	public bool AutoRefresh { get => _autoRefresh; set => SetProperty(ref _autoRefresh, value); }
	public string HealthText { get => _healthText; private set => SetProperty(ref _healthText, value); }

	public async Task SetMinimumLevelAsync(string level)
	{
		await ExecuteAsync("set_logging_level", new { level }).ConfigureAwait(true);
		MinimumLevel = level;
	}

	/// <summary>过滤后的日志。</summary>
	public IReadOnlyList<DebugLogItem> FilteredLogs => Logs.Where(item =>
		(LevelFilter == "all" || item.Level.Equals(LevelFilter, StringComparison.OrdinalIgnoreCase))
		&& (SourceFilter == "all" || item.Source.Equals(SourceFilter, StringComparison.OrdinalIgnoreCase))
		&& (CategoryFilter.Length == 0 || item.Category.Contains(CategoryFilter, StringComparison.OrdinalIgnoreCase))
		&& (SearchText.Length == 0 || item.DisplayText.Contains(SearchText, StringComparison.OrdinalIgnoreCase))).ToArray();

	/// <inheritdoc />
	public override async Task RefreshAsync(CancellationToken cancellationToken = default)
	{
		IsBusy = true;
		try
		{
			RefreshPerformance();
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

	/// <summary>读取已有渲染统计。失败留在快照里，不伪装成成功。</summary>
	public void RefreshPerformance(DateTime? utcNow = null)
	{
		PerformanceReadCount++;
		try
		{
			Performance = Service.ReadLive2DPerformance(utcNow ?? DateTime.UtcNow);
		}
		catch (Exception exception)
		{
			Performance = Live2DPerformanceSnapshot.ReadFailed(exception);
		}
	}

	/// <summary>把当前性能面板复制到剪贴板。</summary>
	public Task CopyPerformanceAsync(CancellationToken cancellationToken = default) =>
		CopyTextAsync(Live2DPerformanceText.Present(Performance).CopyText, cancellationToken);

	/// <summary>刷新诊断信息。</summary>
	public async Task RefreshDiagnosticAsync(CancellationToken cancellationToken = default)
	{
		await RefreshDiagnosticCoreAsync(cancellationToken).ConfigureAwait(true);
	}

	/// <summary>清空内存中的日志缓冲。</summary>
	public async Task ClearLogsAsync(CancellationToken cancellationToken = default)
	{
		await ExecuteAsync("clear_recent_logs", cancellationToken: cancellationToken).ConfigureAwait(true);
		_logRequest++;
		Logs = [];
	}

	/// <summary>把当前筛选结果复制到剪贴板。</summary>
	public Task CopyLogsAsync(CancellationToken cancellationToken = default) => CopyTextAsync(
		string.Join(Environment.NewLine, FilteredLogs.Select(item => item.DisplayText)),
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

	private async Task RefreshLogsCoreAsync(CancellationToken cancellationToken)
	{
		if (_refreshingLogs) return;
		_refreshingLogs = true;
		long request = ++_logRequest;
		try
		{
			JsonElement result = await Service.ExecuteAsync("get_recent_logs", cancellationToken: cancellationToken).ConfigureAwait(true);
			if (request != _logRequest) return;
			Logs = SettingsJson.RootArray(result).Select(value => new DebugLogItem(
				SettingsJson.String(value, "time"),
				SettingsJson.String(value, "level"),
				SettingsJson.String(value, "source"),
				SettingsJson.String(value, "message"), SettingsJson.Long(value, "sequence"),
				SettingsJson.String(value, "category"), SettingsJson.String(value, "eventId"),
				SettingsJson.String(value, "windowLabel"), SettingsJson.String(value, "exceptionType"))).ToArray();
			JsonElement status = await Service.ExecuteAsync("get_logging_status", cancellationToken: cancellationToken).ConfigureAwait(true);
			MinimumLevel = SettingsJson.String(status, "minimumLevel", "info");
			string error = SettingsJson.String(status, "lastError");
			HealthText = $"{NativeSettingsResources.Get("debug.dropped")}: {SettingsJson.Long(status, "droppedCount")} · {NativeSettingsResources.Get("debug.writeFailures")}: {SettingsJson.Long(status, "writeFailureCount")}"
				+ (error.Length == 0 ? "" : $" · {error}");
		}
		finally { _refreshingLogs = false; }
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
