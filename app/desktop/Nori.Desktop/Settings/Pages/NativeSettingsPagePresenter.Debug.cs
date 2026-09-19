using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Controls.Templates;
using Avalonia.Threading;
using Avalonia.VisualTree;
using System.Collections.ObjectModel;

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
	private TextBlock? _debugGcResult;
	private Expander? _debugDanger;
	private Button? _debugCopyLogs;
	private Button? _debugCopyDiagnostic;
	private readonly List<Button> _debugCrashButtons = [];

	private void BuildDebug(StackPanel root, DebugSettingsViewModel viewModel)
	{
		root.Children.Add(new TextBlock {Text = NativeSettingsResources.Get("debug.warning"), Foreground = Brush("SettingsSecondaryBrush"), TextWrapping = TextWrapping.Wrap});
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

		StackPanel tools = CardBody(NativeSettingsResources.Get("debug.crash"), null);
		tools.Children.Add(Button(NativeSettingsResources.Get("debug.gc"), () => _ = RunAsync(async () =>
		{
			long released = await viewModel.CollectGarbageAsync().ConfigureAwait(true);
			await NativeSettingsDialogs.ShowMessageAsync(Owner(), NativeSettingsResources.Get("debug.gc"), $"{NativeSettingsResources.Get("debug.released")}: {released}").ConfigureAwait(true);
		})));
		tools.Children.Add(Button(NativeSettingsResources.Get("debug.testLog"), () => _ = RunAsync(() => viewModel.WriteTestLogAsync())));
		_debugGcResult = new TextBlock {Foreground = Brush("SettingsAccentBrush"), TextWrapping = TextWrapping.Wrap};
		tools.Children.Add(_debugGcResult);
		root.Children.Add(WrapCard(tools));
		StackPanel danger = new() {Spacing = 10, Margin = new Avalonia.Thickness(0, 12, 0, 0)};
		_debugCrashButtons.Clear();
		bool crashEnabled = viewModel.CrashTestsAvailable;
		_debugCrashButtons.Add(Button(NativeSettingsResources.Get("debug.uiCrash"), () => _ = RunAsync(() => RunCrashAsync(viewModel, "ui_thread", false)), danger: true, enabled: crashEnabled));
		_debugCrashButtons.Add(Button(NativeSettingsResources.Get("debug.backgroundCrash"), () => _ = RunAsync(() => RunCrashAsync(viewModel, "background_thread", true)), danger: true, enabled: crashEnabled));
		_debugCrashButtons.Add(Button(NativeSettingsResources.Get("debug.taskCrash"), () => _ = RunAsync(() => RunCrashAsync(viewModel, "unobserved_task", false)), danger: true, enabled: crashEnabled));
		foreach (Button button in _debugCrashButtons) danger.Children.Add(button);
		danger.Children.Add(Button(NativeSettingsResources.Get("debug.settingsError"), () => _ = RunAsync(() =>
			Task.FromException(new InvalidOperationException(NativeSettingsResources.Get("debug.settingsErrorResult")))), danger: true));
		_debugDanger = new Expander {Header = NativeSettingsResources.Get("debug.danger"), Content = danger, IsExpanded = false, IsVisible = crashEnabled};
		root.Children.Add(_debugDanger);
		UpdateDebug(viewModel);
		StartDebugRefresh();
	}


	private void UpdateDebug(DebugSettingsViewModel viewModel)
	{
		if (_busyText is not null) _busyText.Opacity = viewModel.IsBusy ? 1 : 0;
		if (_errorText is not null)
		{
			_errorText.Text = viewModel.ErrorMessage;
			_errorText.IsVisible = !string.IsNullOrWhiteSpace(viewModel.ErrorMessage);
		}
		if (_debugDanger is not null) _debugDanger.IsVisible = viewModel.CrashTestsAvailable;
		if (_debugCopyLogs is not null) _debugCopyLogs.IsEnabled = viewModel.FilteredLogs.Count > 0;
		if (_debugCopyDiagnostic is not null) _debugCopyDiagnostic.IsEnabled = viewModel.Diagnostic.Count > 0;
		if (_debugGcResult is not null)
			_debugGcResult.Text = viewModel.ReleasedBytes is long released ? $"{NativeSettingsResources.Get("debug.released")}: {released:N0}" : "";
		foreach (Button button in _debugCrashButtons)
			button.IsEnabled = viewModel.CrashTestsAvailable && !viewModel.IsBusy;
		if (_debugFilter is not null)
			_debugFilter.SelectedIndex = Array.IndexOf(DebugLevels, viewModel.LevelFilter);
		if (_debugMinimum is not null) _debugMinimum.SelectedItem = viewModel.MinimumLevel;
		if (_debugHealth is not null) _debugHealth.Text = viewModel.HealthText;

		if (_debugDiagnostic is not null)
		{
			foreach (string key in _debugDiagnosticRows.Keys.Except(viewModel.Diagnostic.Keys).ToArray())
			{
				_debugDiagnostic.Children.Remove(_debugDiagnosticRows[key]);
				_debugDiagnosticRows.Remove(key);
			}
			foreach ((string key, string value) in viewModel.Diagnostic)
			{
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

	private void StartDebugRefresh()
	{
		if (_disposed || _viewModel is not DebugSettingsViewModel || TopLevel.GetTopLevel(this) is null || _debugRefreshTimer is not null) return;
		_debugRefreshLifetime = new CancellationTokenSource();
		CancellationToken token = _debugRefreshLifetime.Token;
		_debugRefreshTimer = new DispatcherTimer {Interval = TimeSpan.FromSeconds(2)};
		_debugRefreshTimer.Tick += async (_, _) =>
		{
			if (_viewModel is not DebugSettingsViewModel {AutoRefresh: true} debug
				|| TopLevel.GetTopLevel(this) is not Window {IsVisible: true}
				|| this.GetSelfAndVisualAncestors().Any(visual => !visual.IsVisible)) return;
			await RunAsync(() => debug.RefreshLogsAsync(token));
		};
		_debugRefreshTimer.Start();
	}

	private void StopDebugRefresh()
	{
		_debugRefreshTimer?.Stop(); _debugRefreshTimer = null;
		_debugRefreshLifetime?.Cancel(); _debugRefreshLifetime?.Dispose(); _debugRefreshLifetime = null;
	}
}
