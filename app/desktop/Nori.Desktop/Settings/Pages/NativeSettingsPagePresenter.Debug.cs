using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Controls.Templates;
using Avalonia.Threading;
using Avalonia.VisualTree;
using System.Collections.ObjectModel;
using Nori.Core.Agent;

namespace Nori.Desktop.Settings.Pages;

public sealed partial class NativeSettingsPagePresenter
{
	private StackPanel? _debugDiagnostic;
	private readonly Dictionary<string, TextBlock> _debugDiagnosticRows = new(StringComparer.Ordinal);
	private ListBox? _debugLogItems;
	private readonly ObservableCollection<DebugLogItem> _debugRows = [];
	private DispatcherTimer? _debugRefreshTimer;
	private CancellationTokenSource? _debugRefreshLifetime;
	private TextBlock? _debugHealth;
	private TextBlock? _debugEmpty;
	private ComboBox? _debugMinimum;
	private static readonly string[] DebugLevels = ["all", "error", "warn", "info", "debug", "trace", "fatal"];
	private IReadOnlyList<DebugLogItem>? _debugRenderedLogs;
	private ComboBox? _debugFilter;
	private Button? _debugCopyLogs;
	private Button? _debugCopyDiagnostic;
	private StackPanel? _performanceRows;
	private TextBlock? _performanceStatus;
	private TextBlock? _performanceDetail;
	private Button? _performanceCopy;
	private readonly Dictionary<string, TextBlock> _performanceRowText = new(StringComparer.Ordinal);
	private TextBlock? _executionSummary;
	private TextBlock? _executionEmpty;
	private Button? _executionCopy;
	private ListBox? _executionItems;
	private readonly ObservableCollection<AgentExecutionLine> _executionRows = [];
	private long _executionRevision = -1;
	private Action? _executionSignal;
	private int _executionRefreshQueued;

	/// <summary>调试页自动刷新间隔。性能读取与原有日志刷新共用这一次低频计时。</summary>
	internal static readonly TimeSpan DebugRefreshInterval = TimeSpan.FromSeconds(2);

	/// <summary>调试页计时器是否还在跑。</summary>
	internal bool DebugRefreshActive => _debugRefreshTimer is not null;

	/// <summary>执行一次与计时器相同的刷新。不可见时不读取性能。</summary>
	internal void RunDebugRefreshTickForTests() => OnDebugRefreshTick(CancellationToken.None);

	private void BuildDebug(StackPanel root, DebugSettingsViewModel viewModel)
	{
		StackPanel performance = CardBody(NativeSettingsResources.Get("perf.title"), NativeSettingsResources.Get("perf.subtitle"));
		performance.Name = "Live2DPerformanceCard";
		_performanceCopy = Button(NativeSettingsResources.Get("common.copy"), () => _ = RunAsync(() => viewModel.CopyPerformanceAsync()));
		performance.Children.Add(ActionGroup(_performanceCopy));
		_performanceStatus = new TextBlock
		{
			Name = "Live2DPerformanceStatus",
			FontWeight = FontWeight.SemiBold,
			TextWrapping = TextWrapping.Wrap,
		};
		_performanceDetail = new TextBlock
		{
			Name = "Live2DPerformanceDetail",
			Foreground = Brush("SettingsSecondaryBrush"),
			TextWrapping = TextWrapping.Wrap,
		};
		performance.Children.Add(_performanceStatus);
		performance.Children.Add(_performanceDetail);
		_performanceRowText.Clear();
		_performanceRows = new StackPanel {Spacing = 4, Name = "Live2DPerformanceRows"};
		performance.Children.Add(_performanceRows);
		root.Children.Add(WrapCard(performance));

		StackPanel execution = CardBody(NativeSettingsResources.Get("exec.title"), NativeSettingsResources.Get("exec.subtitle"));
		execution.Name = "AgentExecutionCard";
		_executionCopy = Button(NativeSettingsResources.Get("common.copy"), () => _ = RunAsync(() => viewModel.CopyExecutionAsync()));
		_executionCopy.Name = "AgentExecutionCopy";
		AutomationProperties.SetName(_executionCopy, NativeSettingsResources.Get("exec.copy"));
		execution.Children.Add(ActionGroup(_executionCopy));
		_executionSummary = new TextBlock
		{
			Name = "AgentExecutionSummary",
			TextWrapping = TextWrapping.Wrap,
			Foreground = Brush("SettingsSecondaryBrush"),
		};
		execution.Children.Add(_executionSummary);
		_executionRows.Clear();
		_executionRevision = -1;
		ListBox executionItems = new()
		{
			Name = "AgentExecutionList",
			Height = 220,
			ItemsSource = _executionRows,
			Focusable = true,
			ItemsPanel = new FuncTemplate<Panel?>(() => new VirtualizingStackPanel()),
			ItemTemplate = new FuncDataTemplate<AgentExecutionLine>((item, _) => new TextBlock
			{
				Text = item?.Text,
				TextWrapping = TextWrapping.Wrap,
				Foreground = item?.Attention == true ? Brush("SettingsErrorBrush") : Brush("SettingsPrimaryBrush"),
			}),
		};
		AutomationProperties.SetName(executionItems, NativeSettingsResources.Get("exec.list"));
		ScrollViewer.SetHorizontalScrollBarVisibility(executionItems, Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled);
		ScrollViewer.SetVerticalScrollBarVisibility(executionItems, Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);
		_executionItems = executionItems;
		_executionEmpty = Empty(NativeSettingsResources.Get("exec.empty"));
		_executionEmpty.Name = "AgentExecutionEmpty";
		_executionEmpty.HorizontalAlignment = HorizontalAlignment.Center;
		_executionEmpty.IsHitTestVisible = false;
		execution.Children.Add(new Grid { Children = { executionItems, _executionEmpty } });
		root.Children.Add(WrapCard(execution));

		StackPanel diagnostic = CardBody(NativeSettingsResources.Get("debug.diagnostic"), null);
		_debugCopyDiagnostic = Button(NativeSettingsResources.Get("common.copy"), () => _ = RunAsync(() => viewModel.CopyDiagnosticAsync()));
		WrapPanel diagnosticActions = ActionGroup(
			Button(NativeSettingsResources.Get("debug.refresh"), () => _ = RunAsync(() => viewModel.RefreshDiagnosticAsync())),
			_debugCopyDiagnostic,
			Button(NativeSettingsResources.Get("debug.export"), () => _ = RunAsync(() => ExportDiagnosticsAsync(viewModel))),
			Button(NativeSettingsResources.Get("debug.openFolder"), () => _ = RunAsync(() => viewModel.OpenLogFolderAsync())));

		diagnostic.Children.Add(diagnosticActions);
		_debugDiagnosticRows.Clear();
		_debugDiagnostic = new StackPanel {Spacing = 4};
		diagnostic.Children.Add(_debugDiagnostic);
		root.Children.Add(WrapCard(diagnostic));

		StackPanel logs = CardBody(NativeSettingsResources.Get("debug.logs"), null);
		logs.Name = "DebugLogCard";
		WrapPanel logToolbar = new();
		ComboBox filter = new() {Name = "DebugLevelFilter", ItemsSource = DebugLevels.Select(level => level == "all" ? NativeSettingsResources.Get("debug.all") : level).ToArray(), SelectedIndex = Array.IndexOf(DebugLevels, viewModel.LevelFilter), MinWidth = 100};
		_debugFilter = filter;
		filter.SelectionChanged += (_, _) => viewModel.LevelFilter = filter.SelectedIndex >= 0 ? DebugLevels[filter.SelectedIndex] : "all";
		logToolbar.Children.Add(filter);
		Button refresh = Button(NativeSettingsResources.Get("debug.refresh"), () => _ = RunAsync(() => viewModel.RefreshLogsAsync()));
		Grid.SetColumn(refresh, 1);
		logToolbar.Children.Add(refresh);
		Button clear = Button(NativeSettingsResources.Get("debug.clear"), () => _ = RunAsync(async () =>
		{
			if (await NativeSettingsDialogs.ConfirmAsync(Owner(), NativeSettingsResources.Get("debug.clear"), NativeSettingsResources.Get("debug.clearConfirm"), true).ConfigureAwait(true)) await viewModel.ClearLogsAsync().ConfigureAwait(true);
		}));
		Grid.SetColumn(clear, 2);
		logToolbar.Children.Add(clear);
		Button copy = Button(NativeSettingsResources.Get("common.copy"), () => _ = RunAsync(() => viewModel.CopyLogsAsync()));
		_debugCopyLogs = copy;
		Grid.SetColumn(copy, 3);
		logToolbar.Children.Add(copy);
		foreach (Control control in logToolbar.Children) control.Margin = new Avalonia.Thickness(0, 0, 8, 6);
		logs.Children.Add(logToolbar);
		ComboBox source = new() {Name = "DebugSourceFilter", ItemsSource = new[] {NativeSettingsResources.Get("debug.allSources"), "backend", "frontend"}, SelectedIndex = viewModel.SourceFilter switch {"backend" => 1, "frontend" => 2, _ => 0}, MinWidth = 120};
		source.SelectionChanged += (_, _) => viewModel.SourceFilter = source.SelectedIndex switch {1 => "backend", 2 => "frontend", _ => "all"};
		TextBox category = new() {Name = "DebugCategoryFilter", PlaceholderText = NativeSettingsResources.Get("debug.category"), Text = viewModel.CategoryFilter, MinWidth = 140, MaxLength = 160};
		category.TextChanged += (_, _) => viewModel.CategoryFilter = category.Text ?? "";
		TextBox search = new() {Name = "DebugSearch", PlaceholderText = NativeSettingsResources.Get("debug.search"), Text = viewModel.SearchText, MinWidth = 160, MaxLength = 1000};
		search.TextChanged += (_, _) => viewModel.SearchText = search.Text ?? "";
		logs.Children.Add(ActionGroup(source, category, search));
		_debugMinimum = new ComboBox {Name = "DebugMinimumLevel", ItemsSource = new[] {"info", "debug", "trace", "warn", "error", "fatal"}, SelectedItem = viewModel.MinimumLevel, MinWidth = 100};
		_debugMinimum.SelectionChanged += (_, _) =>
		{
			if (_debugMinimum.SelectedItem is string level && level != viewModel.MinimumLevel)
				_ = RunAsync(() => viewModel.SetMinimumLevelAsync(level));
		};
		CheckBox automatic = new() {Name = "DebugAutoRefresh", Content = NativeSettingsResources.Get("debug.autoRefresh"), IsChecked = viewModel.AutoRefresh};
		automatic.IsCheckedChanged += (_, _) => viewModel.AutoRefresh = automatic.IsChecked == true;
		logs.Children.Add(ActionGroup(new TextBlock {Text = NativeSettingsResources.Get("debug.minimumLevel"), VerticalAlignment = VerticalAlignment.Center}, _debugMinimum, automatic));
		_debugHealth = new TextBlock {TextWrapping = TextWrapping.Wrap, Foreground = Brush("SettingsSecondaryBrush")};
		logs.Children.Add(_debugHealth);
		_debugRows.Clear();
		ListBox logItems = new()
		{
			Name = "DebugLogList", Height = 240, ItemsSource = _debugRows,
			ItemsPanel = new FuncTemplate<Panel?>(() => new VirtualizingStackPanel()),
			ItemTemplate = new FuncDataTemplate<DebugLogItem>((item, _) => new TextBlock
			{
				Text = item?.DisplayText, FontFamily = new FontFamily("Consolas"), TextWrapping = TextWrapping.Wrap,
				Foreground = item?.Level is "error" or "fatal" ? Brush("SettingsErrorBrush") : Brush("SettingsPrimaryBrush"),
			}),
		};
		ScrollViewer.SetHorizontalScrollBarVisibility(logItems, Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled);
		ScrollViewer.SetVerticalScrollBarVisibility(logItems, Avalonia.Controls.Primitives.ScrollBarVisibility.Visible);
		_debugLogItems = logItems;
		_debugRenderedLogs = null;
		_debugEmpty = Empty(NativeSettingsResources.Get("debug.noLogs"));
		_debugEmpty.HorizontalAlignment = HorizontalAlignment.Center;
		_debugEmpty.VerticalAlignment = VerticalAlignment.Center;
		_debugEmpty.IsHitTestVisible = false;
		logs.Children.Add(new Grid {Children = {logItems, _debugEmpty}});
		root.Children.Add(WrapCard(logs));

		StartDebugRefresh();
		UpdateDebug(viewModel);
	}


	private void UpdateDebug(DebugSettingsViewModel viewModel)
	{
		if (_busyText is not null) _busyText.Opacity = viewModel.IsBusy ? 1 : 0;
		if (_errorText is not null)
		{
			_errorText.Text = viewModel.ErrorMessage;
			_errorText.IsVisible = !string.IsNullOrWhiteSpace(viewModel.ErrorMessage);
		}
		if (_debugCopyLogs is not null) _debugCopyLogs.IsEnabled = viewModel.FilteredLogs.Count > 0;
		if (_debugCopyDiagnostic is not null) _debugCopyDiagnostic.IsEnabled = viewModel.Diagnostic.Count > 0;
		if (_debugFilter is not null)
			_debugFilter.SelectedIndex = Array.IndexOf(DebugLevels, viewModel.LevelFilter);
		if (_debugMinimum is not null) _debugMinimum.SelectedItem = viewModel.MinimumLevel;
		if (_debugHealth is not null) _debugHealth.Text = viewModel.HealthText;
		UpdatePerformance(viewModel);

		if (_debugDiagnostic is not null)
		{
			foreach (string key in _debugDiagnosticRows.Keys.Where(key => key.StartsWith("live2d_", StringComparison.Ordinal) || !viewModel.Diagnostic.ContainsKey(key)).ToArray())
			{
				_debugDiagnostic.Children.Remove(_debugDiagnosticRows[key]);
				_debugDiagnosticRows.Remove(key);
			}
			foreach ((string key, string value) in viewModel.Diagnostic)
			{
				if (key.StartsWith("live2d_", StringComparison.Ordinal)) continue;
				if (!_debugDiagnosticRows.TryGetValue(key, out TextBlock? row))
				{
					row = new TextBlock {Foreground = Brush("SettingsSecondaryBrush"), TextWrapping = TextWrapping.Wrap};
					_debugDiagnosticRows.Add(key, row);
					_debugDiagnostic.Children.Add(row);
				}
				row.Text = $"{key}: {value}";
			}
		}

		IReadOnlyList<DebugLogItem> logs = viewModel.FilteredLogs;
		if (_debugEmpty is not null) _debugEmpty.IsVisible = logs.Count == 0;
		if (_debugLogItems is null || (_debugRenderedLogs is not null && _debugRenderedLogs.SequenceEqual(logs))) return;
		// 日志容器及其 ScrollViewer 始终保留；没有变化的日志无需重新布局。
		ScrollViewer? scroll = _debugLogItems.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
		bool atBottom = scroll is not null && scroll.Offset.Y >= scroll.Extent.Height - scroll.Viewport.Height - 2;
		DebugLogItem? selected = _debugLogItems.SelectedItem as DebugLogItem;
		_debugRenderedLogs = logs.ToArray();
		// 按序号增删，保留未变化的条目和选择，避免替换 ItemsSource 导致跳动。
		HashSet<long> sequences = logs.Select(item => item.Sequence).ToHashSet();
		for (int index = _debugRows.Count - 1; index >= 0; index--)
			if (!sequences.Contains(_debugRows[index].Sequence)) _debugRows.RemoveAt(index);
		for (int index = 0; index < logs.Count; index++)
			if (index >= _debugRows.Count || _debugRows[index] != logs[index]) _debugRows.Insert(index, logs[index]);
		if (selected is not null && sequences.Contains(selected.Sequence)) _debugLogItems.SelectedItem = selected;
		if (atBottom && logs.Count > 0) _debugLogItems.ScrollIntoView(logs[^1]);
	}

	private void UpdatePerformance(DebugSettingsViewModel viewModel)
	{
		Live2DPerformancePresentation presentation = Live2DPerformanceText.Present(viewModel.Performance);
		if (_performanceStatus is not null)
		{
			_performanceStatus.Text = presentation.Status;
			_performanceStatus.Foreground = Brush(presentation.BrushKey);
		}
		if (_performanceDetail is not null)
		{
			_performanceDetail.Text = presentation.Detail ?? "";
			_performanceDetail.IsVisible = !string.IsNullOrWhiteSpace(presentation.Detail);
		}
		if (_performanceCopy is not null) _performanceCopy.IsEnabled = presentation.CopyText.Length > 0;
		UpdateExecution(viewModel);
		if (_performanceRows is null) return;
		HashSet<string> keys = presentation.Rows.Select(row => row.Key).ToHashSet(StringComparer.Ordinal);
		foreach (string key in _performanceRowText.Keys.Where(key => !keys.Contains(key)).ToArray())
		{
			_performanceRows.Children.Remove(_performanceRowText[key]);
			_performanceRowText.Remove(key);
		}
		for (int index = 0; index < presentation.Rows.Count; index++)
		{
			Live2DPerformanceRow row = presentation.Rows[index];
			if (!_performanceRowText.TryGetValue(row.Key, out TextBlock? text))
			{
				text = new TextBlock
				{
					Name = "PerfRow_" + row.Key,
					Foreground = Brush("SettingsSecondaryBrush"),
					TextWrapping = TextWrapping.Wrap,
				};
				_performanceRowText.Add(row.Key, text);
				_performanceRows.Children.Add(text);
			}
			text.Text = $"{row.Label}: {row.Value}";
			if (_performanceRows.Children.IndexOf(text) != index)
			{
				_performanceRows.Children.Remove(text);
				_performanceRows.Children.Insert(index, text);
			}
		}
	}

	private void UpdateExecution(DebugSettingsViewModel viewModel)
	{
		AgentExecutionPresentation presentation = AgentExecutionText.Present(viewModel.Execution);
		if (_executionSummary is not null) _executionSummary.Text = presentation.Summary;
		if (_executionCopy is not null) _executionCopy.IsEnabled = presentation.Lines.Count > 0;
		if (_executionEmpty is not null) _executionEmpty.IsVisible = presentation.Lines.Count == 0;
		if (_executionItems is null || presentation.Lines.Count == _executionRows.Count
			&& presentation.Lines.Select(line => line.Sequence).SequenceEqual(_executionRows.Select(line => line.Sequence))
			&& viewModel.Execution.Revision == _executionRevision)
		{
			_executionRevision = viewModel.Execution.Revision;
			return;
		}
		_executionRevision = viewModel.Execution.Revision;
		HashSet<long> sequences = presentation.Lines.Select(line => line.Sequence).ToHashSet();
		for (int index = _executionRows.Count - 1; index >= 0; index--)
			if (!sequences.Contains(_executionRows[index].Sequence)) _executionRows.RemoveAt(index);
		for (int index = 0; index < presentation.Lines.Count; index++)
		{
			AgentExecutionLine line = presentation.Lines[index];
			if (index >= _executionRows.Count || _executionRows[index].Sequence != line.Sequence)
				_executionRows.Insert(index, line);
			else if (_executionRows[index].Text != line.Text)
				_executionRows[index] = line;
		}
	}

	private bool IsDebugPanelVisible()
	{
		if (_disposed || !IsVisible) return false;
		if (TopLevel.GetTopLevel(this) is not Window {IsVisible: true}) return false;
		return !this.GetSelfAndVisualAncestors().Any(visual => !visual.IsVisible);
	}

	private void StartDebugRefresh()
	{
		if (_disposed || _viewModel is not DebugSettingsViewModel || TopLevel.GetTopLevel(this) is null) return;
		if (IsDebugPanelVisible() && _viewModel is DebugSettingsViewModel visible)
		{
			visible.RefreshPerformance();
			visible.RefreshExecution();
		}
		if (_executionSignal is null && _viewModel is DebugSettingsViewModel debug)
		{
			_executionSignal = QueueExecutionRefresh;
			debug.ExecutionChanged += _executionSignal;
		}
		if (_debugRefreshTimer is not null) return;
		_debugRefreshLifetime = new CancellationTokenSource();
		CancellationToken token = _debugRefreshLifetime.Token;
		_debugRefreshTimer = new DispatcherTimer {Interval = DebugRefreshInterval};
		_debugRefreshTimer.Tick += (_, _) => OnDebugRefreshTick(token);
		_debugRefreshTimer.Start();
	}

	private void OnDebugRefreshTick(CancellationToken token)
	{
		if (_disposed || _viewModel is not DebugSettingsViewModel debug || !IsDebugPanelVisible() || token.IsCancellationRequested) return;
		debug.RefreshPerformance();
		debug.RefreshExecution();
		if (!debug.AutoRefresh) return;
		_ = RunAsync(() => debug.RefreshLogsAsync(token));
	}

	private void QueueExecutionRefresh()
	{
		// Changed 可能来自后台线程；可见性检查只在 UI 线程回调里做。
		if (_disposed) return;
		if (Interlocked.Exchange(ref _executionRefreshQueued, 1) == 1) return;
		Dispatcher.UIThread.Post(() =>
		{
			_executionRefreshQueued = 0;
			if (_disposed || _debugRefreshLifetime is { IsCancellationRequested: true }) return;
			if (!IsDebugPanelVisible() || _viewModel is not DebugSettingsViewModel debug) return;
			debug.RefreshExecution();
		}, DispatcherPriority.Background);
	}

	private void StopDebugRefresh()
	{
		_debugRefreshTimer?.Stop(); _debugRefreshTimer = null;
		_debugRefreshLifetime?.Cancel(); _debugRefreshLifetime?.Dispose(); _debugRefreshLifetime = null;
		if (_executionSignal is not null && _viewModel is DebugSettingsViewModel debug)
			debug.ExecutionChanged -= _executionSignal;
		_executionSignal = null;
		_executionRefreshQueued = 0;
	}
}
