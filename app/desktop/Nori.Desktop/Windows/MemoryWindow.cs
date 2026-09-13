using System.Globalization;
using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Threading;
using Nori.Desktop.Bridge;
using Nori.Desktop.Memory;

namespace Nori.Desktop.Windows;

/// <summary>独立的原生记忆管理窗口，保留各分区草稿及未完成写入。</summary>
public sealed partial class MemoryWindow : Window
{
	private static readonly string[] Sections = ["overview", "memories", "atoms", "knowledge", "archive", "transfer", "debugger", "advanced"];
	private static readonly string[] Kinds = ["general", "factual", "preference", "relational", "planned", "identity"];
	private readonly MemoryService _service;
	private readonly CancellationTokenSource _lifetime = new();
	private readonly Dictionary<string, Control> _pages = [];
	private readonly Dictionary<string, Button> _navigation = [];
	private readonly Dictionary<string, MemorySettingDraft> _drafts = [];
	private readonly List<Action> _localize = [];
	private readonly List<Action<JsonElement>> _snapshotBindings = [];
	private readonly HashSet<Task> _operations = [];
	private readonly ContentControl _presenter = new() { MaxWidth = 960, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch };
	private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
	private readonly TextBlock _heading = new() { FontSize = 26, FontWeight = FontWeight.SemiBold };
	private readonly TextBlock _description = new() { FontSize = 13, TextWrapping = TextWrapping.Wrap };
	private readonly ScrollViewer _scroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
	private JsonElement _snapshot;
	private string _language = "zh-CN";
	private string _section = "overview";
	private long _snapshotRequest;
	private long _pageRequest;
	private long _stateRevision;
	private bool _applying;
	private bool _closing;
	private bool _prepared;
	private bool _preparing;
	private bool _refreshQueued;
	private int _saveBarrierDepth;
	private readonly Dictionary<Control, bool> _saveBarrierSurfaces = [];

	/// <summary>创建具有八个分区的深色原生记忆窗口。</summary>
	public MemoryWindow(AppServices services)
	{
		_service = new MemoryService(services, this);
		Width = 960; Height = 640; MinWidth = 720; MinHeight = 480;
		RequestedThemeVariant = ThemeVariant.Dark;
		WindowStartupLocation = WindowStartupLocation.CenterScreen;
		Styles.Add(new StyleInclude(new Uri("avares://Nori.Desktop/")) { Source = new Uri("avares://Nori.Desktop/Settings/SettingsTheme.axaml") });
		BuildShell();
		BuildPages();
		ApplyLanguage();
		Navigate("overview");
		_service.StateChanged += OnStateChanged;
		Opened += (_, _) => QueueRefresh();
		PropertyChanged += (_, args) =>
		{
			if (args.Property != IsVisibleProperty) return;
			if (IsVisible) QueueRefresh();
			else { _pageRequest++; _snapshotRequest++; _detailRequest++; _debugRequest++; }
		};
		Closing += OnClosing;
	}

	/// <summary>允许宿主在完成退出收尾后销毁窗口。</summary>
	public bool AllowClose { get; set; }

	/// <summary>切换分区；各页输入控件与草稿保留在窗口中。</summary>
	public void Navigate(string? page)
	{
		_section = page is null ? _section : Sections.Contains(page) ? page : "overview";
		_pageRequest++;
		_detailRequest++;
		_presenter.Content = _pages[_section];
		_heading.Text = L("tabs." + _section);
		_description.Text = PageDescription();
		foreach (var pair in _navigation) pair.Value.Classes.Set("selected", pair.Key == _section);
		_scroll.Offset = default;
		if (IsVisible) _ = RefreshAsync();
	}

	/// <summary>刷新当前可见分区，旧查询结果不会覆盖新查询或活动编辑。</summary>
	public async Task RefreshAsync()
	{
		if (_prepared || _preparing || !IsVisible) return;
		long request = ++_snapshotRequest;
		long revision = _stateRevision;
		_status.Text = L("detail.loading");
		try
		{
			JsonElement snapshot = await _service.GetSnapshotAsync(_lifetime.Token);
			if (!IsVisible || request != _snapshotRequest || revision != _stateRevision) return;
			_snapshot = snapshot;
			string language = S(P(snapshot, "general"), "language", _language);
			if (language != _language) { _language = language; ApplyLanguage(); }
			_applying = true;
			try { foreach (var bind in _snapshotBindings) bind(P(snapshot, "memory")); }
			finally { _applying = false; }
			await RefreshPageAsync();
		}
		catch (OperationCanceledException) { }
		catch (Exception ex) { if (request == _snapshotRequest && IsVisible) ShowError(ex); }
	}

	/// <summary>等待所有已经开始的操作，并保存各个独立字段。</summary>
	public async Task<bool> FlushPendingSavesAsync()
	{
		BeginSaveBarrier();
		try
		{
			_service.CancelBackgroundOperations();
			while (_operations.Count > 0) await Task.WhenAll(_operations.ToArray());
			if (_editorDirty?.Invoke() == true)
			{
				if (!await ConfirmAsync("detail.unsavedTitle", "detail.unsavedDesc", _editor)) return false;
				_discardEditor?.Invoke();
			}
			bool success = true;
			foreach (MemorySettingDraft draft in _drafts.Values) success &= await draft.FlushAsync();
			return success;
		}
		finally { EndSaveBarrier(); }
	}

	private void BeginSaveBarrier()
	{
		_saveBarrierDepth++;
		DisableSaveSurface(Content as Control);
		DisableSaveSurface(_editor?.Content as Control);
	}

	private void DisableSaveSurface(Control? surface)
	{
		if (_saveBarrierDepth == 0 || surface is null || _saveBarrierSurfaces.ContainsKey(surface)) return;
		_saveBarrierSurfaces[surface] = surface.IsEnabled;
		// 仅禁用窗口内容树，独立确认窗口仍然可以继续编辑或放弃草稿。
		surface.IsEnabled = false;
	}

	private void EndSaveBarrier()
	{
		if (--_saveBarrierDepth != 0) return;
		if (!_prepared)
			foreach (var surface in _saveBarrierSurfaces) surface.Key.IsEnabled = surface.Value;
		_saveBarrierSurfaces.Clear();
	}

	/// <summary>退出时等待写入；保存失败时抛出并保留窗口草稿。</summary>
	public async Task PrepareShutdownAsync()
	{
		if (_prepared) return;
		BeginSaveBarrier();
		_preparing = true;
		try
		{
			if (!await FlushPendingSavesAsync()) throw new InvalidOperationException(L("toast.saveFailed"));
			await _service.WaitForPendingOperationsAsync();
			_prepared = true;
			_lifetime.Cancel();
			_service.StateChanged -= OnStateChanged;
			_service.Dispose();
		}
		finally { _preparing = false; EndSaveBarrier(); }
	}

	/// <summary>将宿主关闭或保存失败显示在窗口中。</summary>
	public void ReportHostFailure(Exception exception)
	{
		ShowError(exception);
		if (!IsVisible) Show();
		Activate();
	}

	private async void OnClosing(object? sender, WindowClosingEventArgs args)
	{
		if (AllowClose) return;
		args.Cancel = true;
		if (_closing) return;
		_closing = true;
		try { if (await FlushPendingSavesAsync()) Hide(); }
		catch (Exception ex) { ShowError(ex); }
		finally { _closing = false; }
	}

	protected override void OnClosed(EventArgs e)
	{
		_lifetime.Cancel();
		_service.StateChanged -= OnStateChanged;
		_service.Dispose();
		base.OnClosed(e);
	}

	private void OnStateChanged() => Dispatcher.UIThread.Post(QueueRefresh);
	private void QueueRefresh()
	{
		if (_prepared || _preparing || !IsVisible || _refreshQueued) return;
		_refreshQueued = true;
		Dispatcher.UIThread.Post(async () => { _refreshQueued = false; await RefreshAsync(); }, DispatcherPriority.Background);
	}

	private void BuildShell()
	{
		var root = new Grid { ColumnDefinitions = new ColumnDefinitions("200,*") };
		var nav = new StackPanel { Spacing = 0, Margin = new Thickness(10, 20, 10, 12) };
		var brand = new Grid { ColumnDefinitions = new ColumnDefinitions("36,*"), ColumnSpacing = 10, Margin = new Thickness(6, 0, 6, 14) };
		var monogram = new Border { CornerRadius = new CornerRadius(11), Width = 36, Height = 36, Child = Text("N", 22, true) };
		((TextBlock)monogram.Child).HorizontalAlignment = HorizontalAlignment.Center;
		((TextBlock)monogram.Child).VerticalAlignment = VerticalAlignment.Center;
		SetBrush(monogram, Border.BackgroundProperty, "SettingsSelectionBrush");
		SetBrush(monogram.Child, TextBlock.ForegroundProperty, "SettingsAccentBrush");
		brand.Children.Add(monogram);
		var wordmark = Text("NORI", 11.5, true); wordmark.LetterSpacing = 2;
		SetBrush(wordmark, TextBlock.ForegroundProperty, "SettingsSecondaryBrush");
		var brandTitle = Text(T("记忆", "Memory"), 16, true);
		_localize.Add(() => brandTitle.Text = T("记忆", "Memory"));
		var brandLabels = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
		brandLabels.Children.Add(wordmark); brandLabels.Children.Add(brandTitle);
		Grid.SetColumn(brandLabels, 1); brand.Children.Add(brandLabels); nav.Children.Add(brand);
		foreach (string section in Sections)
		{
			if (section is "overview" or "transfer")
			{
				var group = Text("", 11.5, true); group.Margin = new Thickness(10, section == "overview" ? 0 : 10, 0, 6);
				_localize.Add(() => group.Text = section == "overview" ? T("记忆资料库", "LIBRARY") : T("管理与工具", "MANAGEMENT"));
				SetBrush(group, TextBlock.ForegroundProperty, "SettingsSecondaryBrush"); nav.Children.Add(group);
			}
			var button = new Button { Tag = section, CornerRadius = new CornerRadius(9), Padding = new Thickness(8, 4), Margin = new Thickness(0, 1) };
			button.Click += (_, _) => Navigate(section);
			button.Classes.Add("settings-nav");
			button.HorizontalAlignment = HorizontalAlignment.Stretch;
			button.HorizontalContentAlignment = HorizontalAlignment.Stretch;
			button.MinHeight = 36;
			var row = new Grid { ColumnDefinitions = new ColumnDefinitions("26,*"), ColumnSpacing = 9 };
			row.Children.Add(NavigationSymbol(section));
			var title = Label("tabs." + section); title.VerticalAlignment = VerticalAlignment.Center;
			Grid.SetColumn(title, 1); row.Children.Add(title); button.Content = row;
			_localize.Add(() => AutomationProperties.SetName(button, L("tabs." + section)));
			_navigation[section] = button;
			nav.Children.Add(button);
		}
		var sidebar = new Border { Child = new ScrollViewer { Content = nav, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }, BorderThickness = new Thickness(0, 0, 1, 0) };
		SetBrush(sidebar, Border.BackgroundProperty, "SettingsSidebarBrush");
		SetBrush(sidebar, Border.BorderBrushProperty, "SettingsBorderBrush");
		root.Children.Add(sidebar);
		var main = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto"), ClipToBounds = true };
		Grid.SetColumn(main, 1);
		var header = new StackPanel { Spacing = 6, MaxWidth = 960 };
		header.Children.Add(_heading);
		header.Children.Add(_description);
		SetBrush(_description, TextBlock.ForegroundProperty, "SettingsSecondaryBrush");
		var headerBorder = new Border { Child = header, Padding = new Thickness(24, 28, 24, 18), BorderThickness = new Thickness(0, 0, 0, 1) };
		SetBrush(headerBorder, Border.BorderBrushProperty, "SettingsBorderBrush");
		main.Children.Add(headerBorder);
		_scroll.Content = _presenter;
		_scroll.Margin = new Thickness(24, 20, 24, 24);
		_scroll.PropertyChanged += (_, args) =>
		{
			if (args.Property != ScrollViewer.ViewportProperty || _scroll.Viewport.Width <= 0) return;
			_presenter.Width = Math.Min(960, _scroll.Viewport.Width);
			if (_overviewStats is not null) _overviewStats.Columns = _scroll.Viewport.Width >= 600 ? 4 : 2;
		};
		Grid.SetRow(_scroll, 1); main.Children.Add(_scroll);
		var footer = new DockPanel { Margin = new Thickness(24, 10, 24, 12), LastChildFill = true };
		var refresh = Button("list.retryLoad", RefreshAsync); DockPanel.SetDock(refresh, Dock.Right); footer.Children.Add(refresh);
		_status.VerticalAlignment = VerticalAlignment.Center;
		footer.Children.Add(_status); Grid.SetRow(footer, 2); main.Children.Add(footer);
		root.Children.Add(main);
		Content = root;
	}

	private Border NavigationSymbol(string section)
	{
		(string color, string data) = section switch
		{
			"overview" => ("Blue", "M 4,4 L 10,4 10,10 4,10 Z M 14,4 L 20,4 20,10 14,10 Z M 4,14 L 10,14 10,20 4,20 Z M 14,14 L 20,14 20,20 14,20 Z"),
			"memories" => ("Purple", "M 4,5 Q 8,3 12,6 Q 16,3 20,5 L 20,20 Q 16,18 12,21 Q 8,18 4,20 Z M 12,6 L 12,21"),
			"atoms" => ("Orange", "M 12,3 L 21,8 21,17 12,22 3,17 3,8 Z M 3,8 L 12,13 21,8 M 12,13 L 12,22"),
			"knowledge" => ("Green", "M 6,3 L 15,3 20,8 20,21 6,21 Z M 15,3 L 15,8 20,8 M 9,12 L 16,12 M 9,16 L 16,16"),
			"archive" => ("Gray", "M 3,4 L 21,4 21,9 3,9 Z M 5,9 L 5,21 19,21 19,9 M 10,13 L 14,13"),
			"transfer" => ("Blue", "M 4,7 L 20,7 M 16,3 L 20,7 16,11 M 20,17 L 4,17 M 8,13 L 4,17 8,21"),
			"debugger" => ("Purple", "M 16,10 A 6,6 0 1 1 4,10 A 6,6 0 1 1 16,10 M 15,15 L 21,21 M 8,10 L 12,10 M 10,8 L 10,12"),
			_ => ("Gray", "M 4,6 L 20,6 M 4,12 L 20,12 M 4,18 L 20,18 M 8,3 L 8,9 M 16,9 L 16,15 M 10,15 L 10,21"),
		};
		var icon = new Avalonia.Controls.Shapes.Path { Data = Geometry.Parse(data) };
		icon.Classes.Add("settings-nav-icon");
		var symbol = new Border { Width = 26, Height = 26, CornerRadius = new CornerRadius(7), Child = new Viewbox { Width = 18, Height = 18, Child = icon } };
		symbol.Classes.Add("settings-nav-symbol");
		SetBrush(symbol, Border.BackgroundProperty, "SettingsIcon" + color + "Brush");
		return symbol;
	}

	private string PageDescription() => _section switch
	{
		"overview" => T("浏览记忆资产，管理 Nori 记住与回想的方式。", "Your memory library at a glance, and how Nori remembers."),
		"memories" => T("收藏偏好、重要事实与约定，让每一次相处都有迹可循。", "Review the preferences, facts and promises Nori remembers."),
		"atoms" => T("查看从长期记忆中提炼的事实，以及它们的来源。", "Explore the facts distilled from memories and their origins."),
		"knowledge" => T("管理 Memory.md 知识文件及其检索索引。", "Manage your Memory.md knowledge file and search index."),
		"archive" => T("暂存不再参与日常检索的记忆，随时恢复。", "Memories set aside from everyday recall, ready to restore."),
		"transfer" => T("安全导出记忆，或先预览再导入已有资料。", "Export your memories, or preview an import before applying it."),
		"debugger" => T("跟随一次检索，了解哪些记忆最终进入了上下文。", "Trace a recall to see which memories reach the final context."),
		_ => T("调整记忆形成、检索与归档策略。修改会自动保存。", "Tune reflection, recall and archiving. Changes save automatically."),
	};

	private string L(string key) => MemoryResources.Get(key, _language);
	private string T(string chinese, string english) => _language.StartsWith("en", StringComparison.OrdinalIgnoreCase) ? english : chinese;
	private void ApplyLanguage()
	{
		Title = L("header.title");
		_heading.Text = L("tabs." + _section);
		_description.Text = PageDescription();
		foreach (Action action in _localize) action();
		_editorLocalize?.Invoke();
		foreach (Action action in _confirmationLocalizers.Values.ToArray()) action();
	}
	private TextBlock Label(string key, double size = 13, bool bold = false)
	{
		TextBlock text = Text(L(key), size, bold);
		var reference = new WeakReference<TextBlock>(text);
		_localize.Add(() => { if (reference.TryGetTarget(out TextBlock? target)) target.Text = L(key); });
		return text;
	}
	private static TextBlock Text(string value, double size = 13, bool bold = false) => new()
	{
		Text = value, FontSize = size, FontWeight = bold ? FontWeight.SemiBold : FontWeight.Normal,
		TextWrapping = TextWrapping.Wrap,
	};
	private Button Button(string key, Func<Task> action, bool danger = false, Func<bool>? enabled = null)
	{
		var button = new Button { Content = L(key), MinHeight = 32, Padding = new Thickness(12, 6) };
		var reference = new WeakReference<Button>(button);
		_localize.Add(() => { if (reference.TryGetTarget(out Button? target)) { target.Content = L(key); AutomationProperties.SetName(target, L(key)); } });
		if (danger) button.Classes.Add("danger");
		button.Click += async (_, _) =>
		{
			button.IsEnabled = false;
			Task task = RunActionAsync(action);
			_operations.Add(task);
			try { await task; }
			finally { _operations.Remove(task); button.IsEnabled = enabled?.Invoke() ?? true; }
		};
		return button;
	}
	private async Task RunActionAsync(Func<Task> action)
	{
		try { _status.Text = T("正在处理…", "Working…"); await action(); if (_status.Text == T("正在处理…", "Working…")) Success(); }
		catch (OperationCanceledException) { Success(T("操作已取消", "Cancelled")); }
		catch (Exception ex) { ShowError(ex); }
	}
	private void ShowError(Exception ex)
	{
		_status.Text = T("操作失败", "Operation failed") + ": " + ex.Message;
		SetBrush(_status, TextBlock.ForegroundProperty, "SettingsErrorBrush");
	}
	private void Success(string? message = null)
	{
		_status.Text = message ?? T("操作已完成", "Completed");
		SetBrush(_status, TextBlock.ForegroundProperty, "SettingsAccentBrush");
	}
	private static StackPanel Stack(params Control[] children)
	{
		var stack = new StackPanel { Spacing = 12 };
		foreach (Control child in children) stack.Children.Add(child);
		return stack;
	}
	private static WrapPanel Row(params Control[] children)
	{
		var panel = new WrapPanel { Orientation = Orientation.Horizontal };
		foreach (Control child in children) { child.Margin = new Thickness(0, 0, 8, 8); panel.Children.Add(child); }
		return panel;
	}
	private Border Card(string key, params Control[] children)
	{
		var body = Stack();
		if (key.Length > 0) body.Children.Add(Label(key, 16, true));
		foreach (Control child in children) body.Children.Add(child);
		var border = new Border { Child = body, Padding = new Thickness(16), CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1) };
		SetBrush(border, Border.BackgroundProperty, "SettingsCardBrush");
		SetBrush(border, Border.BorderBrushProperty, "SettingsBorderBrush");
		return border;
	}
	private Control Field(string key, Control control) => Stack(Label(key, 12, true), control);
	private TextBox Input(string text = "", bool multiline = false) => new()
	{
		Text = text, AcceptsReturn = multiline, TextWrapping = TextWrapping.Wrap,
		MinHeight = multiline ? 78 : 32, HorizontalAlignment = HorizontalAlignment.Stretch,
	};
	private static JsonElement P(JsonElement value, string key) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out JsonElement item) ? item : default;
	private static string S(JsonElement value, string key, string fallback = "") => P(value, key).ValueKind is JsonValueKind.Undefined or JsonValueKind.Null ? fallback : P(value, key).ToString();
	private static double N(JsonElement value, string key, double fallback = 0) => P(value, key).TryGetDoubleSafe(fallback);
	private static IEnumerable<JsonElement> Items(JsonElement value) => value.ValueKind == JsonValueKind.Array ? value.EnumerateArray() : [];
	private static bool B(JsonElement value, string key) => P(value, key).ValueKind == JsonValueKind.True;
	private string Kind(string kind) => Kinds.Contains(kind) ? L("add.kind" + char.ToUpperInvariant(kind[0]) + kind[1..]) : kind;
	private string Status(string status) => status is "active" or "dormant" or "expired" or "archived" ? L("list." + status) : status;
	private string Source(string source) => source is "agent" or "manual" ? L("list.source" + char.ToUpperInvariant(source[0]) + source[1..]) : source;
	private static string Date(string value) => DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date) ? date.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) : value;
	private void SetBrush(Control control, AvaloniaProperty property, string key) => control.SetValue(property, Nori.Desktop.Settings.SettingsBrushes.Resolve(this, key));
}

internal static class MemoryJsonExtensions
{
	public static double TryGetDoubleSafe(this JsonElement value, double fallback) => value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double number) ? number : fallback;
}
