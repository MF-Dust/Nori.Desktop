using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Nori.Desktop.Settings.Pages;

public sealed partial class NativeSettingsPagePresenter
{
	private StackPanel? _debugDiagnostic;
	private readonly Dictionary<string, TextBlock> _debugDiagnosticRows = new(StringComparer.Ordinal);
	private StackPanel? _debugLogItems;
	private IReadOnlyList<DebugLogItem>? _debugRenderedLogs;
	private ComboBox? _debugFilter;
	private readonly List<Button> _debugCrashButtons = [];

	private void BuildDebug(StackPanel root, DebugSettingsViewModel viewModel)
	{
		root.Children.Add(new TextBlock {Text = NativeSettingsResources.Get("debug.warning"), Foreground = Brush("SettingsSecondaryBrush"), TextWrapping = TextWrapping.Wrap});
		StackPanel diagnostic = CardBody(NativeSettingsResources.Get("debug.diagnostic"), null);
		WrapPanel diagnosticActions = new() {Orientation = Orientation.Horizontal};
		diagnosticActions.Children.Add(Button(NativeSettingsResources.Get("debug.refresh"), () => _ = RunAsync(() => viewModel.RefreshDiagnosticAsync())));
		diagnosticActions.Children.Add(Button(NativeSettingsResources.Get("common.copy"), () => _ = RunAsync(() => viewModel.CopyDiagnosticAsync())));
		diagnosticActions.Children.Add(Button(NativeSettingsResources.Get("debug.export"), () => _ = RunAsync(() => ExportDiagnosticsAsync(viewModel))));
		diagnosticActions.Children.Add(Button(NativeSettingsResources.Get("debug.openFolder"), () => _ = RunAsync(() => viewModel.OpenLogFolderAsync())));
		diagnostic.Children.Add(diagnosticActions);
		_debugDiagnosticRows.Clear();
		_debugDiagnostic = new StackPanel {Spacing = 4};
		diagnostic.Children.Add(_debugDiagnostic);
		root.Children.Add(WrapCard(diagnostic));

		StackPanel logs = CardBody(NativeSettingsResources.Get("debug.logs"), null);
		Grid logToolbar = new() {ColumnDefinitions = new ColumnDefinitions("Auto,Auto,Auto,Auto"), ColumnSpacing = 8};
		ComboBox filter = new() {ItemsSource = new[] {NativeSettingsResources.Get("debug.all"), "error", "warn", "info"}, SelectedIndex = viewModel.LevelFilter switch {"error" => 1, "warn" => 2, "info" => 3, _ => 0}, MinWidth = 100};
		_debugFilter = filter;
		filter.SelectionChanged += (_, _) => viewModel.LevelFilter = filter.SelectedIndex switch {1 => "error", 2 => "warn", 3 => "info", _ => "all"};
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
		Grid.SetColumn(copy, 3);
		logToolbar.Children.Add(copy);
		logs.Children.Add(logToolbar);
		ScrollViewer logScroll = new() {Name = "DebugLogScroll", Height = 240, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Visible, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled};
		StackPanel logItems = new() {Spacing = 3};
		_debugLogItems = logItems;
		_debugRenderedLogs = null;
		logScroll.Content = logItems;
		logs.Children.Add(logScroll);
		root.Children.Add(WrapCard(logs));

		StackPanel tools = CardBody(NativeSettingsResources.Get("debug.crash"), null);
		tools.Children.Add(Button(NativeSettingsResources.Get("debug.gc"), () => _ = RunAsync(async () =>
		{
			long released = await viewModel.CollectGarbageAsync().ConfigureAwait(true);
			await NativeSettingsDialogs.ShowMessageAsync(Owner(), NativeSettingsResources.Get("debug.gc"), $"{NativeSettingsResources.Get("debug.released")}: {released}").ConfigureAwait(true);
		})));
		tools.Children.Add(Button(NativeSettingsResources.Get("debug.testLog"), () => _ = RunAsync(() => viewModel.WriteTestLogAsync())));
		_debugCrashButtons.Clear();
		bool crashEnabled = viewModel.CrashTestsAvailable;
		_debugCrashButtons.Add(Button(NativeSettingsResources.Get("debug.uiCrash"), () => _ = RunCrashAsync(viewModel, "ui_thread", false), danger: true, enabled: crashEnabled));
		_debugCrashButtons.Add(Button(NativeSettingsResources.Get("debug.backgroundCrash"), () => _ = RunCrashAsync(viewModel, "background_thread", true), danger: true, enabled: crashEnabled));
		_debugCrashButtons.Add(Button(NativeSettingsResources.Get("debug.taskCrash"), () => _ = RunCrashAsync(viewModel, "unobserved_task", true), danger: true, enabled: crashEnabled));
		foreach (Button button in _debugCrashButtons) tools.Children.Add(button);
		root.Children.Add(WrapCard(tools));
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
		foreach (Button button in _debugCrashButtons)
			button.IsEnabled = viewModel.CrashTestsAvailable && !viewModel.IsBusy;
		if (_debugFilter is not null)
			_debugFilter.SelectedIndex = viewModel.LevelFilter switch {"error" => 1, "warn" => 2, "info" => 3, _ => 0};

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
		if (_debugLogItems is null || (_debugRenderedLogs is not null && _debugRenderedLogs.SequenceEqual(logs))) return;
		// 日志容器及其 ScrollViewer 始终保留；没有变化的日志无需重新布局。
		_debugRenderedLogs = logs.ToArray();
		_debugLogItems.Children.Clear();
		foreach (DebugLogItem item in logs)
			_debugLogItems.Children.Add(new TextBlock
			{
				Text = $"[{item.Time}] [{item.Level}] [{item.Source}] {item.Message}",
				FontFamily = new FontFamily("Consolas"),
				TextWrapping = TextWrapping.Wrap,
				Foreground = item.Level.Equals("error", StringComparison.OrdinalIgnoreCase) ? Brush("SettingsErrorBrush") : Brush("SettingsSecondaryBrush"),
			});
		if (logs.Count == 0) _debugLogItems.Children.Add(Empty(NativeSettingsResources.Get("debug.noLogs")));
	}
}
