using System.Diagnostics;
using Nori.Desktop.Appearance;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Nori.Core.Configuration;
using Nori.Core.Live2D;
using Nori.Core.Logging;
using Nori.Core.Platform;
using Nori.Core.Resources;
using Nori.Desktop.Bridge;
using Nori.Desktop.Chat;
using Nori.Desktop.Main;

namespace Nori.Desktop.Windows;

/// <summary>原生主界面：主页常驻，侧栏入口打开各自的独立窗口。</summary>
public sealed class MainWindow : Window
{
	private readonly AppServices _services;
	private readonly HomeView _home;
	private readonly Border _sidebar = new();
	private readonly TextBlock _homeLabel = Label(14, ChatPalette.Teal);
	private readonly TextBlock _groupLabel = Label(12, ChatPalette.Faint);
	private readonly TextBlock _pageTitle = Label(24, ChatPalette.Primary);
	private readonly TextBlock _pageSubtitle = Label(12);
	private readonly TextBlock _brandCaption = Label(12);
	private readonly TextBlock _hints = Label(12);
	private readonly TextBlock _error = Label(12, ChatPalette.Danger);
	private readonly TextBlock _petStatus = Label(12);
	private readonly Ellipse _petDot = new() {Width = 6, Height = 6};
	private readonly Button _collapse = new() {Name = "CollapseSidebar", BorderThickness = default};
	private readonly Button _petToggle = new() {Name = "TogglePet"};
	private readonly Button _exit = new();
	private NativeWindowChrome _windowChrome = null!;
	private readonly List<LauncherEntry> _launchers = [];
	private static readonly long RefreshIntervalTicks = (long)(Stopwatch.Frequency * 0.4);
	private MainRefreshData _lastData;
	private long _nextAllowed;
	private int _dirty;
	private int _pumpQueued;
	private bool _stateHooked;
	private bool _collapsed;
	private bool _savingSidebar;
	private volatile bool _updatesEnabled;
	private volatile bool _closed;
	private bool _english;
	private (bool English, bool Collapsed)? _chromeState;

	private readonly record struct MainRefreshData(bool English, bool Collapsed, HomeRefreshData Home);

	private sealed record LauncherEntry(string Window, Button Button, TextBlock Label, Ellipse Dot, Ellipse Badge);

	/// <summary>仅宿主退出流程可允许真正关闭。</summary>
	public bool AllowClose { get; set; }

	public MainWindow(WindowDefinition definition, AppServices services)
	{
		_services = services;
		Title = definition.Title;
		Width = definition.Width;
		Height = definition.Height;
		MinWidth = definition.MinWidth ?? 720;
		MinHeight = definition.MinHeight ?? 480;
		CanResize = definition.CanResize;
		NativeWindowSizing.ConstrainOnFirstOpen(this, NativeWindowSizing.DefaultSize);
		WindowStartupLocation = WindowStartupLocation.CenterScreen;
		RequestedThemeVariant = ThemeVariant.Dark;
		FontFamily = NoriTypography.System;
		Background = ChatPalette.Background;
		Foreground = ChatPalette.Body;
		Resources["MainText"] = ChatPalette.Body;
		Resources["MainLine"] = ChatPalette.Line;
		Resources["MainHover"] = ChatPalette.Overlay;
		Resources["MainAccent"] = ChatPalette.Teal;
		Resources["MainOnAccent"] = ChatPalette.OnTeal;
		Styles.Add(new StyleInclude(new Uri("avares://Nori.Desktop/"))
		{
			Source = new Uri("avares://Nori.Desktop/Main/MainTheme.axaml"),
		});
		WindowDecorations = WindowDecorations.None;
		_lastData = ReadMainRefreshData();
		_english = _lastData.English;
		_collapsed = _lastData.Collapsed;
		_home = new HomeView(services, RefreshFromCache);
		Content = BuildChrome();
		ApplyMainRefresh(_lastData);
		Opened += (_, _) => StartRefreshing();
		Closing += (_, args) =>
		{
			if (AllowClose) return;
			args.Cancel = true;
			Hide();
		};
		PropertyChanged += (_, args) =>
		{
			if (args.Property != IsVisibleProperty) return;
			if (IsVisible) StartRefreshing();
			else StopRefreshing();
		};
	}

	private bool IsEnglish() => _english;

	private Control BuildChrome()
	{
		_pageTitle.FontWeight = FontWeight.SemiBold;
		_error.IsVisible = false;
		_hints.IsVisible = false;
		ScrollViewer body = new()
		{
			Name = "HomeScroll", HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
			Padding = new Thickness(24, 22),
			Content = new StackPanel
			{
				MaxWidth = 1120, Spacing = 20, HorizontalAlignment = HorizontalAlignment.Stretch,
				Children = {new StackPanel {Spacing = 5, Children = {_pageTitle, _pageSubtitle}}, _error, _home},
			},
		};
		Grid workspace = new() {ColumnDefinitions = new ColumnDefinitions("Auto,*")};
		workspace.Children.Add(BuildSidebar());
		Grid.SetColumn(body, 1);
		workspace.Children.Add(body);
		Grid shell = new() {RowDefinitions = new RowDefinitions("52,*,Auto")};
		shell.Children.Add(BuildHeader());
		Grid.SetRow(workspace, 1);
		shell.Children.Add(workspace);
		Control footer = BuildFooter();
		Grid.SetRow(footer, 2);
		shell.Children.Add(footer);
		return new Border {BorderBrush = ChatPalette.Line, BorderThickness = new Thickness(1), Child = shell};
	}

	private Border BuildHeader()
	{
		StackPanel brand = new()
		{
			Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center,
			Children =
			{
				MainVisual.Icon("sparkles", 23, ChatPalette.Teal),
				new TextBlock {Text = "Nori", FontSize = 18, FontWeight = FontWeight.SemiBold, Foreground = ChatPalette.Primary},
				new Border {Width = 1, Height = 14, Background = ChatPalette.Line, Margin = new Thickness(4, 0)},
				_brandCaption,
			},
		};
		_windowChrome = new NativeWindowChrome(this, IsEnglish, brand) { Height = 52 };
		return _windowChrome;
	}

	private Border BuildSidebar()
	{
		_homeLabel.FontWeight = FontWeight.SemiBold;
		Border home = new()
		{
			Background = ChatPalette.Panel, CornerRadius = new CornerRadius(8), Padding = new Thickness(13, 12),
			BorderBrush = ChatPalette.Teal, BorderThickness = new Thickness(2, 0, 0, 0),
			Child = new StackPanel {Orientation = Orientation.Horizontal, Spacing = 12, Children = {MainVisual.Icon("home", 20, ChatPalette.Teal), _homeLabel}},
		};
		_groupLabel.Margin = new Thickness(14, 20, 0, 6);
		StackPanel navigation = new() {Spacing = 5, Children = {home, _groupLabel}};
		foreach (string window in new[] {WindowLabels.Chat, WindowLabels.Models, WindowLabels.Memory, WindowLabels.Settings})
		{
			TextBlock label = Label(13, ChatPalette.Body);
			Ellipse dot = new() {Width = 6, Height = 6, Fill = ChatPalette.Teal, IsVisible = false};
			Ellipse badge = new() {Width = 6, Height = 6, Fill = ChatPalette.Accent, IsVisible = false};
			Grid row = new() {ColumnDefinitions = new ColumnDefinitions("20,*,Auto"), ColumnSpacing = 8};
			row.Children.Add(MainVisual.Icon(window));
			Grid.SetColumn(label, 1);
			row.Children.Add(label);
			StackPanel dots = new() {Orientation = Orientation.Horizontal, Spacing = 5, VerticalAlignment = VerticalAlignment.Center, Children = {badge, dot}};
			Grid.SetColumn(dots, 2);
			row.Children.Add(dots);
			Button button = new()
			{
				Name = $"Launcher_{window}", Content = row, Height = 44,
				Padding = new Thickness(12, 8), BorderThickness = default,
				HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
			};
			button.Click += (_, _) => RunAction(() => _services.Windows.Show(window));
			_launchers.Add(new LauncherEntry(window, button, label, dot, badge));
			navigation.Children.Add(button);
		}
		_collapse.HorizontalAlignment = HorizontalAlignment.Stretch;
		_collapse.Click += async (_, _) => await ToggleSidebarAsync();
		Grid contents = new() {RowDefinitions = new RowDefinitions("*,Auto"), Margin = new Thickness(10, 18, 10, 14)};
		contents.Children.Add(navigation);
		Grid.SetRow(_collapse, 1);
		contents.Children.Add(_collapse);
		_sidebar.Background = ChatPalette.Deep;
		_sidebar.BorderBrush = ChatPalette.Line;
		_sidebar.BorderThickness = new Thickness(0, 0, 1, 0);
		_sidebar.Child = contents;
		return _sidebar;
	}

	private Control BuildFooter()
	{
		_petToggle.Classes.Add("primary");
		_petToggle.Click += (_, _) => RunAction(() =>
		{
			if (!_home.ModelReady) _services.Windows.Show(WindowLabels.Models);
			else if (_services.Windows.IsWindowVisible(WindowLabels.Pet)) _services.Windows.Hide(WindowLabels.Pet);
			else _services.Windows.Show(WindowLabels.Pet);
		});
		_exit.Click += (_, _) => _services.Windows.Shutdown();
		StackPanel status = new()
		{
			Orientation = Orientation.Horizontal, Spacing = 9,
			VerticalAlignment = VerticalAlignment.Center, Children = {_petDot, _petStatus},
		};
		Grid row = new() {ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 12};
		row.Children.Add(status);
		StackPanel actions = new() {Orientation = Orientation.Horizontal, Spacing = 8, Children = {_exit, _petToggle}};
		Grid.SetColumn(actions, 1);
		row.Children.Add(actions);
		return new Border
		{
			Padding = new Thickness(20, 10), Background = ChatPalette.Deep,
			BorderBrush = ChatPalette.Line, BorderThickness = new Thickness(0, 1, 0, 0),
			Child = new StackPanel {Spacing = 8, Children = {_hints, row}},
		};
	}

	private async Task ToggleSidebarAsync()
	{
		if (_savingSidebar) return;
		_savingSidebar = true;
		_collapsed = !_collapsed;
		ApplyMainRefresh(_lastData);
		try
		{
			bool value = _collapsed;
			await Task.Run(() => _services.Config.Set("ui_sidebar_collapsed", new ConfigValue.Boolean(value)), _services.ShutdownToken);
			_error.IsVisible = false;
		}
		catch (Exception failure)
		{
			if (!_closed && !_services.ShutdownToken.IsCancellationRequested) ShowError(failure);
		}
		finally
		{
			_savingSidebar = false;
			if (!_closed && !_services.ShutdownToken.IsCancellationRequested) QueueRefresh();
		}
	}

	private void RunAction(Action action)
	{
		try { action(); _error.IsVisible = false; }
		catch (Exception failure) { ShowError(failure); }
		RefreshFromCache();
	}

	private void ShowError(Exception failure)
	{
		_error.Text = IsEnglish() ? "Unable to complete this action. Please try again." : "操作未完成，请重试。";
		_error.IsVisible = true;
		_services.Logger.Write(LogSource.Backend, "warn", $"主界面操作失败：{failure.GetType().Name}");
	}

	private void RefreshChrome(bool english)
	{
		if (_chromeState == (english, _collapsed)) return;
		_chromeState = (english, _collapsed);
		_sidebar.Width = _collapsed ? 74 : 174;
		_homeLabel.Text = english ? "Home" : "主页";
		_homeLabel.IsVisible = !_collapsed;
		_groupLabel.Text = english ? "WORKSPACE" : "工作空间";
		_groupLabel.IsVisible = !_collapsed;
		_pageTitle.Text = english ? "Home" : "主页";
		_pageSubtitle.Text = english ? "A little company, always close by." : "一点陪伴，随时在你身边。";
		_brandCaption.Text = english ? "Desktop companion" : "桌面伴侣";
		_windowChrome.RefreshLabels();
		string collapseText = _collapsed ? english ? "Expand sidebar" : "展开侧栏" : english ? "Collapse sidebar" : "折叠侧栏";
		_collapse.Content = new StackPanel
		{
			Orientation = Orientation.Horizontal, Spacing = 10,
			Children = {MainVisual.Icon(_collapsed ? "right" : "left", 16), new TextBlock {Text = collapseText, FontSize = 12, IsVisible = !_collapsed}},
		};
		NameControl(_collapse, collapseText);
		foreach (LauncherEntry launcher in _launchers)
		{
			string text = launcher.Window switch
			{
				WindowLabels.Chat => english ? "Chat" : "对话",
				WindowLabels.Models => english ? "Models" : "模型",
				WindowLabels.Memory => english ? "Memory" : "记忆",
				_ => english ? "Settings" : "设置",
			};
			launcher.Label.Text = text;
			launcher.Label.IsVisible = !_collapsed;
			launcher.Button.Padding = _collapsed ? new Thickness(6, 8) : new Thickness(12, 8);
			((Grid)launcher.Button.Content!).ColumnSpacing = _collapsed ? 0 : 8;
			NameControl(launcher.Button, text + (english ? " · opens a window" : " · 打开独立窗口"));
		}
	}

	private void RefreshFromCache()
	{
		if (_closed) return;
		ApplyMainRefresh(_lastData);
	}

	private MainRefreshData ReadMainRefreshData()
	{
		bool english = UiLanguage.IsEnglish(_services.Config);
		bool collapsed = _services.Config.GetStringOr("ui_sidebar_collapsed", "false") is "true" or "1";
		string modelId = _services.Config.GetStringOr(ConfigStore.KeySelectedModel, ConfigStore.DefaultModel);
		bool modelReady = false;
		try
		{
			modelReady = SupportedModelIds.Normalize(modelId) is not null
				&& _services.Resources.IsInstalled(ResourceType.Live2D, modelId);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ResourceException)
		{
			modelReady = false;
		}
		AiChatSettings chat = _services.AiSettings.Read().Chat;
		return new MainRefreshData(english, collapsed, new HomeRefreshData(modelId, modelReady, chat.IsConfigured, chat.Model));
	}

	private void ApplyMainRefresh(MainRefreshData data)
	{
		_lastData = data;
		_english = data.English;
		if (!_savingSidebar) _collapsed = data.Collapsed;
		bool english = _english;
		RefreshChrome(english);
		foreach (LauncherEntry launcher in _launchers)
		{
			launcher.Dot.IsVisible = _services.Windows.IsWindowVisible(launcher.Window);
			launcher.Badge.IsVisible = launcher.Window == WindowLabels.Settings && !data.Home.ChatConfigured;
		}
		_home.Refresh(english, data.Home);
		bool visible = _services.Windows.IsWindowVisible(WindowLabels.Pet);
		string model = data.Home.ModelId;
		string modelName = model switch {"nori" => "Nori", "arg-nori" => "ARG Nori", _ => model};
		_petStatus.Text = (visible ? english ? "On your desktop" : "伴侣已在桌面" : english ? "Resting" : "伴侣休息中") + "  ·  " + modelName;
		_petDot.Fill = visible ? ChatPalette.Teal : ChatPalette.Faint;
		_petToggle.Content = !_home.ModelReady ? english ? "Import appearance" : "导入形象"
			: visible ? english ? "Hide Nori" : "收起 Nori" : english ? "Summon Nori" : "唤出 Nori";
		_exit.Content = english ? "Quit" : "退出程序";
		_exit.IsVisible = _services.Runtime is {TrayAvailable: false};
		List<string> hints = [];
		PlatformCapabilities capabilities = PlatformServices.Current.Capabilities;
		if (_services.Runtime is {TrayAvailable: false})
			hints.Add(english ? "System tray unavailable; use this window to reach Nori." : "系统托盘不可用，只能从这个窗口找到 Nori。");
		if (!capabilities.SupportsHitThrough)
			hints.Add(english ? "Click-through is unavailable on this platform." : "本平台不支持点击穿透。");
		_hints.Text = string.Join("  ", hints);
		_hints.IsVisible = hints.Count > 0;
	}

	private static TextBlock Label(double size, IBrush? foreground = null) => new()
	{
		FontSize = size, Foreground = foreground ?? ChatPalette.Muted,
		TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center,
	};


	private static void NameControl(Control control, string name)
	{
		AutomationProperties.SetName(control, name);
		ToolTip.SetTip(control, name);
	}

	private void StartRefreshing()
	{
		if (_closed || _updatesEnabled) return;
		_updatesEnabled = true;
		if (!_stateHooked && _services.Runtime is { } runtime)
		{
			runtime.StateChanged += OnRuntimeStateChanged;
			_stateHooked = true;
		}
		_nextAllowed = 0;
		QueueRefresh();
	}

	private void StopRefreshing()
	{
		_updatesEnabled = false;
		if (!_stateHooked) return;
		_stateHooked = false;
		if (_services.Runtime is { } runtime) runtime.StateChanged -= OnRuntimeStateChanged;
	}

	private void OnRuntimeStateChanged()
	{
		if (!_updatesEnabled || _closed) return;
		QueueRefresh();
	}

	private void QueueRefresh()
	{
		if (!_updatesEnabled || _closed) return;
		Interlocked.Exchange(ref _dirty, 1);
		if (Interlocked.CompareExchange(ref _pumpQueued, 1, 0) != 0) return;
		Dispatcher.UIThread.Post(() => _ = PumpRefreshAsync(), DispatcherPriority.Background);
	}

	private async Task PumpRefreshAsync()
	{
		try
		{
			while (_updatesEnabled && !_closed && Volatile.Read(ref _dirty) == 1)
			{
				long now = Stopwatch.GetTimestamp();
				if (now < _nextAllowed)
				{
					await Task.Delay(Stopwatch.GetElapsedTime(now, _nextAllowed), _services.ShutdownToken).ConfigureAwait(true);
					continue;
				}
				Interlocked.Exchange(ref _dirty, 0);
				MainRefreshData data;
				try
				{
					data = await Task.Run(ReadMainRefreshData, _services.ShutdownToken).ConfigureAwait(true);
				}
				catch (Exception exception) when (exception is not OperationCanceledException)
				{
					_services.Logger.Write(LogSource.Backend, "warn", $"刷新主界面失败：{exception.GetType().Name}");
					break;
				}
				if (!_updatesEnabled || _closed || !IsVisible) break;
				ApplyMainRefresh(data);
				_nextAllowed = Stopwatch.GetTimestamp() + RefreshIntervalTicks;
			}
		}
		catch (OperationCanceledException) when (_services.ShutdownToken.IsCancellationRequested)
		{
		}
		finally
		{
			Interlocked.Exchange(ref _pumpQueued, 0);
			if (_updatesEnabled && !_closed && Volatile.Read(ref _dirty) == 1) QueueRefresh();
		}
	}

	protected override void OnClosed(EventArgs e)
	{
		_closed = true;
		StopRefreshing();
		base.OnClosed(e);
	}

}
