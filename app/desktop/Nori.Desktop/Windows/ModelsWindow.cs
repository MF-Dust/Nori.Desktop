using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Nori.Desktop.Bridge;
using Nori.Desktop.Memory;
using Nori.Desktop.Models;

namespace Nori.Desktop.Windows;

/// <summary>独立深色模型管理窗口；预览不改变桌宠当前模型，隐藏前可靠保存草稿。</summary>
public sealed partial class ModelsWindow : Window
{
	private static readonly (string Id, string Name, string Image)[] Models = [("arg-nori", "ARG Nori", "ARGNori.webp"), ("nori", "Nori", "Nori.webp")];
	private readonly ModelService _service;
	private readonly CancellationTokenSource _lifetime = new();
	private readonly Dictionary<string, MemorySettingDraft> _drafts = [];
	private readonly Dictionary<string, JsonElement> _metadata = [];
	private readonly List<Action> _localize = [];
	private readonly List<Action> _adjustLocalize = [];
	private readonly List<Action> _bindings = [];
	private readonly List<Action> _adjustBindings = [];
	private readonly HashSet<Task> _operations = [];
	private readonly Grid _root = new() { ColumnDefinitions = new ColumnDefinitions("200,*") };
	private readonly ContentControl _presenter = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
	private readonly TextBlock _status = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
	private readonly TextBlock _heading = new() { FontSize = 26, FontWeight = FontWeight.SemiBold };
	private readonly TextBlock _description = new() { FontSize = 13, TextWrapping = TextWrapping.Wrap };
	private readonly StackPanel _header = new() { Spacing = 6, MaxWidth = 820, Margin = new Thickness(0, 0, 8, 0) };
	private readonly Dictionary<string, Button> _navigation = [];
	private Border _sidebar = null!;
	private Control _library = null!;
	private Control _behaviors = null!;
	private string _language = "zh-CN";
	private string _section = "library";
	private string? _adjustFor;
	private string _adjustTab = "display";
	private JsonElement _snapshot;
	private long _request;
	private long _revision;
	private long _adjustRequest;
	private bool _applying;
	private bool _buildingAdjust;
	private bool _refreshQueued;
	private bool _closing;
	private bool _prepared;
	private bool _preparing;
	private bool _importing;
	private int _barrierDepth;
	private bool _barrierWasEnabled;

	/// <summary>创建与原生设置和记忆窗口一致的模型管理外壳。</summary>
	public ModelsWindow(AppServices services)
	{
		_service = new ModelService(services, this);
		Width = 960; Height = 640; MinWidth = 720; MinHeight = 480;
		RequestedThemeVariant = ThemeVariant.Dark;
		Classes.Add("models-window");
		WindowStartupLocation = WindowStartupLocation.CenterScreen;
		Styles.Add(new StyleInclude(new Uri("avares://Nori.Desktop/")) { Source = new Uri("avares://Nori.Desktop/Settings/SettingsTheme.axaml") });
		BuildShell();
		BuildLibrary();
		_behaviors = Scroller(BuildBehaviorPage());
		InitializePreview(services);
		Localize(() => { _overlay.LocalReactionLabel = T("本地", "Local"); _overlay.InvalidateVisual(); });
		ApplyLanguage(); Navigate("library");
		_service.StateChanged += OnStateChanged;
		Opened += (_, _) => QueueRefresh();
		PropertyChanged += (_, args) =>
		{
			if (args.Property != IsVisibleProperty) return;
			if (IsVisible) { SetPreviewActive(true); QueueRefresh(); }
			else { _request++; _adjustRequest++; SetPreviewActive(false); _service.CancelBackgroundOperations(); }
		};
		Closing += OnClosing;
		KeyDown += async (_, args) =>
		{
			if (args.Key != Key.Escape || args.Handled || _adjustFor is null) return;
			if (_overlay.IsVisible && _overlay.HandleKey(Key.Escape)) { args.Handled = true; return; }
			args.Handled = true; await CloseAdjustAsync();
		};
	}

	/// <summary>仅在宿主完成退出保存后允许真正关闭。</summary>
	public bool AllowClose { get; set; }
	internal string? CurrentModelId => _adjustFor;
	internal Control PageContent => (Control)_presenter.Content!;
	internal ModelRegionOverlay RegionOverlay => _overlay;
	private string SelectedModel => S(P(_snapshot, "models"), "selected");
	private bool AiAvailable => B(P(_snapshot, "ai"), "configured") && !B(P(_snapshot, "app"), "safeMode");

	/// <summary>异步刷新目录、元数据和运行时能力；旧查询不覆盖活动编辑。</summary>
	public async Task RefreshAsync()
	{
		if (_prepared || _preparing || !IsVisible || _barrierDepth > 0) return;
		long request = ++_request, revision = _revision;
		try
		{
			JsonElement snapshot = await _service.GetSnapshotAsync(_lifetime.Token);
			string selected = S(P(snapshot, "models"), "selected");
			var metadata = new Dictionary<string, JsonElement>();
			foreach (string id in new[] { selected, _adjustFor ?? "" }.Where(id => id.Length > 0).Distinct())
				if (Installed(snapshot, id)) metadata[id] = await _service.ExecuteAsync("model_get_meta", new { modelId = id }, _lifetime.Token);
			if (!IsVisible || request != _request || revision != _revision || _barrierDepth > 0) return;
			_snapshot = snapshot;
			foreach (var pair in metadata) { _metadata[pair.Key] = pair.Value; AcceptModelSnapshot(pair.Key, pair.Value); }
			foreach (string key in BehaviorKeys)
				if (_drafts.TryGetValue(BehaviorKey(key), out var draft)) draft.AcceptSnapshot(B(P(snapshot, "behaviors"), key));
			string language = S(P(snapshot, "general"), "language", _language);
			if (_language != language) { _language = language; ApplyLanguage(); }
			UpdateSelectedDisplay();
			ApplyBindings();
			UpdatePreview();
		}
		catch (OperationCanceledException) { }
		catch (Exception ex) { if (request == _request && IsVisible) ShowError(ex); }
	}

	/// <summary>等待导入等已开始的操作，并逐字段保存；任何失败都保留原草稿。</summary>
	public async Task<bool> FlushPendingSavesAsync()
	{
		_overlay.EndGesture();
		BeginBarrier();
		try
		{
			_service.CancelBackgroundOperations();
			while (_operations.Count > 0)
			{
				Task[] pending = _operations.ToArray();
				await Task.WhenAll(pending);
				_operations.ExceptWith(pending);
			}
			bool success = true;
			foreach (MemorySettingDraft draft in _drafts.Values.ToArray()) success &= await draft.FlushAsync();
			if (!success) ShowSaveState();
			return success;
		}
		finally { EndBarrier(); }
	}

	/// <summary>退出前保存并等待宿主写入完成；失败时不销毁窗口与预览草稿。</summary>
	public async Task PrepareShutdownAsync()
	{
		if (_prepared) return;
		_preparing = true;
		BeginBarrier();
		try
		{
			if (!await FlushPendingSavesAsync()) throw new InvalidOperationException(T("模型设置保存失败，请重试。", "Model settings could not be saved. Please retry."));
			await _service.WaitForPendingOperationsAsync();
			_prepared = true;
			_lifetime.Cancel();
			_service.StateChanged -= OnStateChanged;
			DisposePreview();
			_service.Dispose();
		}
		finally { _preparing = false; EndBarrier(); }
	}

	/// <summary>在窗口内显示宿主失败；保存失败时重新显示原草稿。</summary>
	public void ReportHostFailure(Exception exception)
	{
		ShowError(exception); if (!IsVisible) Show(); Activate();
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
		_lifetime.Cancel(); _service.StateChanged -= OnStateChanged; DisposePreview(); _service.Dispose();
		foreach (var bitmap in _thumbnails) bitmap.Dispose(); _thumbnails.Clear();
		base.OnClosed(e);
	}
	private void BeginBarrier()
	{
		if (_barrierDepth++ > 0) return;
		_request++; _barrierWasEnabled = _root.IsEnabled; _root.IsEnabled = false;
	}
	private void EndBarrier()
	{
		if (--_barrierDepth == 0 && !_prepared) _root.IsEnabled = _barrierWasEnabled;
	}
	private void OnStateChanged() => Dispatcher.UIThread.Post(QueueRefresh);
	private void QueueRefresh()
	{
		if (_prepared || _preparing || !IsVisible || _refreshQueued || _barrierDepth > 0) return;
		_refreshQueued = true;
		Dispatcher.UIThread.Post(async () => { _refreshQueued = false; await RefreshAsync(); }, DispatcherPriority.Background);
	}
	internal void Navigate(string section)
	{
		_section = section;
		_presenter.Content = section == "behaviors" ? _behaviors : _library;
		foreach (var pair in _navigation) pair.Value.Classes.Set("selected", pair.Key == section);
		UpdateHeading();
	}
	private void BuildShell()
	{
		var nav = new StackPanel { Spacing = 4, Margin = new Thickness(10, 20, 10, 12) };
		var monogram = new Border { Width = 36, Height = 36, CornerRadius = new CornerRadius(11), Child = Text("N", 22, true) };
		((TextBlock)monogram.Child).HorizontalAlignment = HorizontalAlignment.Center;
		((TextBlock)monogram.Child).VerticalAlignment = VerticalAlignment.Center;
		Brush(monogram, Border.BackgroundProperty, "SettingsSelectionBrush"); Brush(monogram.Child, TextBlock.ForegroundProperty, "SettingsAccentBrush");
		var brand = new Grid { ColumnDefinitions = new ColumnDefinitions("36,*"), ColumnSpacing = 10, Margin = new Thickness(6, 0, 6, 20) };
		brand.Children.Add(monogram);
		var name = Stack(Text("NORI", 11.5, true), Local(() => T("模型", "Models"), 16, true)); name.Spacing = 2;
		Grid.SetColumn(name, 1); brand.Children.Add(name); nav.Children.Add(brand);
		foreach (string section in new[] { "library", "behaviors" })
		{
			var icon = new Avalonia.Controls.Shapes.Path(); icon.Classes.Add("settings-nav-icon");
			var symbol = new Border
			{
				Width = 26, Height = 26, CornerRadius = new CornerRadius(7),
				Child = new Viewbox { Width = 18, Height = 18, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Child = icon },
			};
			symbol.Classes.Add("settings-nav-symbol");
			TextBlock label = Local(() => section == "library" ? T("模型资料库", "Model library") : T("伴侣交互行为", "Companion behavior"));
			label.VerticalAlignment = VerticalAlignment.Center;
			var content = new Grid { ColumnDefinitions = new ColumnDefinitions("26,*"), ColumnSpacing = 9 };
			content.Children.Add(symbol); Grid.SetColumn(label, 1); content.Children.Add(label);
			var button = new Button
			{
				Name = "ModelsNav_" + section, Tag = section, Content = content,
				MinHeight = 40, Padding = new Thickness(8, 6), Margin = new Thickness(0, 1), CornerRadius = new CornerRadius(9),
				HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
			};
			button.Classes.Add("settings-nav");
			Localize(() => Avalonia.Automation.AutomationProperties.SetName(button, label.Text));
			button.Click += (_, _) => Navigate(section);
			_navigation[section] = button; nav.Children.Add(button);
		}
		_sidebar = new Border { Child = new ScrollViewer { Content = nav, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }, BorderThickness = new Thickness(0, 0, 1, 0) };
		Brush(_sidebar, Border.BackgroundProperty, "SettingsSidebarBrush"); Brush(_sidebar, Border.BorderBrushProperty, "SettingsBorderBrush");
		_root.Children.Add(_sidebar);
		var main = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto"), ClipToBounds = true };
		Grid.SetColumn(main, 1);
		_header.Children.Add(_heading); _header.Children.Add(_description);
		Brush(_description, TextBlock.ForegroundProperty, "SettingsSecondaryBrush");
		var header = new Border { Child = _header, Padding = new Thickness(24, 24, 24, 18), BorderThickness = new Thickness(0, 0, 0, 1) };
		Brush(header, Border.BorderBrushProperty, "SettingsBorderBrush"); main.Children.Add(header);
		_presenter.Margin = new Thickness(24, 18, 24, 12); Grid.SetRow(_presenter, 1); main.Children.Add(_presenter);
		var footer = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 12, Margin = new Thickness(24, 8, 24, 12) };
		footer.Children.Add(_status);
		var retry = ActionButton(() => T("保存 / 刷新", "Save / Refresh"), async () => { if (await FlushPendingSavesAsync()) { await RefreshAsync(); Success(T("已保存", "Saved")); } });
		retry.Name = "ModelsRetry"; Grid.SetColumn(retry, 1); footer.Children.Add(retry); Grid.SetRow(footer, 2); main.Children.Add(footer);
		_root.Children.Add(main); Content = _root;
	}
	private void UpdateHeading()
	{
		_heading.Text = _section == "behaviors" ? T("伴侣交互行为", "Companion behavior") : T("模型资料库", "Model library");
		_description.Text = _section == "behaviors" ? T("所有模型共用这些行为设置，让 Nori 的回应更贴近你。", "These behaviors apply to every model. Make Nori’s responses your own.") : T("选择陪伴你的 Nori，调整外观与专属互动。", "Choose your Nori, then shape their appearance and interactions.");
	}
	private void ApplyLanguage()
	{
		Title = T("Nori · 模型", "Nori · Models"); UpdateHeading();
		bool previous = _applying; _applying = true;
		try { foreach (Action action in _localize.Concat(_adjustLocalize).ToArray()) action(); ApplyBindings(); }
		finally { _applying = previous; }
	}
	private void ApplyBindings()
	{
		bool previous = _applying; _applying = true;
		try { foreach (Action bind in _bindings.Concat(_adjustBindings).ToArray()) bind(); }
		finally { _applying = previous; }
	}
}
