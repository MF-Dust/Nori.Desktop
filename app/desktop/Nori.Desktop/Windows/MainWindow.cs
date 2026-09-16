using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Styling;
using Avalonia.Threading;
using Nori.Core.Configuration;
using Nori.Core.Logging;
using Nori.Core.Platform;
using Nori.Desktop.Account;
using Nori.Desktop.Bridge;
using Nori.Desktop.Chat;
using Nori.Desktop.Main;
using Nori.Desktop.Ui;

namespace Nori.Desktop.Windows;

/// <summary>
/// 原生主界面。迁移的最后一块 —— 它一走，WebView 在主路径上就没有了。
///
/// 结构照搬 Vue 版，因为那一版的分法是对的：左边一条侧边栏，上面是「主页」这个
/// **页面**，下面是四个**启动器**（点了开另一个窗口，不是切页）。两者形状不同不是
/// 装饰：把启动器画成标签页会让人以为点了会在当前窗口里换内容，而它们其实各自
/// 开窗，这正是当初那次改动要解决的问题。
///
/// 数据直接读服务，不经快照 JSON：快照是给 WebView 跨进程用的，原生窗口就在同一个
/// 进程里，绕一圈只会多一层可能漂的形状。
/// </summary>
public sealed class MainWindow : Window
{
	private readonly AppServices _services;
	private readonly HomeView _home;
	private readonly StackPanel _launchers = new() {Spacing = 2};
	private readonly StackPanel _navigation = new();
	private readonly Border _sidebar = new();
	private readonly Button _collapse = new();
	private readonly TextBlock _hints = new()
	{
		Foreground = ChatPalette.Faint, FontSize = 11,
		TextWrapping = TextWrapping.Wrap, MaxWidth = 640,
	};

	/// <summary>侧边栏是否收起。持久化，下次启动保持上次的选择。</summary>
	private bool _collapsed;

	/// <summary>
	/// 侧边栏底色。
	///
	/// 比内容区（<see cref="ChatPalette.Background"/>）再深一档：侧边栏是外壳不是
	/// 内容，两者同色时整个窗口读作一块平面，左右分区只能靠那条 1px 边线撑着。
	/// </summary>
	private static readonly IBrush SidebarGround = new ImmutableSolidColorBrush(Color.Parse("#12161c"));

	/// <summary>当前页那一行的底色与高亮条。高亮条的渐变方向与品牌标记一致，自上而下。</summary>
	private static readonly IBrush SidebarActive = new ImmutableSolidColorBrush(Color.FromArgb(30, 94, 234, 212));

	private static IBrush SidebarBar => new LinearGradientBrush
	{
		StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
		EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
		GradientStops =
		[
			new GradientStop(Color.Parse("#7de3ff"), 0),
			new GradientStop(Color.Parse("#5eead4"), 1),
		],
	};

	/// <summary>悬停底色。白色 4%，与聊天页的 Overlay 同一个值。</summary>
	private static readonly IBrush SidebarHover = ChatPalette.Overlay;

	/// <summary>待办标记的颜色。与首页告警段取同一个值（Vue 版的 --warning）。</summary>
	private static readonly IBrush SidebarBadge = new ImmutableSolidColorBrush(Color.Parse("#e8b168"));

	/// <summary>启动器上那两颗点的控件名。取用一律按名字，不按 Children 下标。</summary>
	private const string LauncherOpenDot = "launcher-open";
	private const string LauncherBadgeDot = "launcher-badge";

	/// <summary>展开与收起两种宽度。收起后只留图标列，图标的横坐标保持不变。</summary>
	private const double SidebarWide = 184;
	private const double SidebarNarrow = 60;

	/// <summary>
	/// 四个启动器。图标、取标签的函数、以及它对应的窗口。
	///
	/// 顺序即显示顺序，测试也按这个顺序断言（对话 / 模型 / 记忆 / 设置）。
	/// </summary>
	private static readonly (string Icon, Func<bool, string> Label, string Window)[] Launchers =
	[
		("bot", english => english ? "Chat" : "对话", WindowLabels.Chat),
		("package", english => english ? "Models" : "模型", WindowLabels.Models),
		("memory", english => english ? "Memory" : "记忆", WindowLabels.Memory),
		("settings", english => english ? "Settings" : "设置", WindowLabels.Settings),
	];

    /// <summary>上一次画侧边栏时的状态签名；相同就不重建，见 <see cref="Refresh"/>。</summary>
	private string _sidebarSignature = "";

	/// <summary>
	/// 侧边栏收起状态的配置键。
	///
	/// 用的是网页版那一个（快照里的 <c>general.sidebarCollapsed</c>、
	/// <c>settings_update_general</c> 写的也是它），不是新起一个：同一个偏好两个键的话，
	/// 两边各记各的，而且只有它在 <see cref="Nori.Core.Cloud.CloudSaveScope"/> 白名单里
	/// —— 新键不会跟着账户同步到另一台机器。
	/// </summary>
	private const string KeySidebarCollapsed = "ui_sidebar_collapsed";

	private DispatcherTimer? _refresh;

	/// <summary>仅宿主退出流程可允许真正关闭。</summary>
	public bool AllowClose { get; set; }

	public MainWindow(WindowDefinition definition, AppServices services)
	{
		_services = services;
		Title = definition.Title;
		Width = definition.Width; Height = definition.Height;
		if (definition.MinWidth is {} minWidth) MinWidth = minWidth;
		if (definition.MinHeight is {} minHeight) MinHeight = minHeight;
		WindowStartupLocation = WindowStartupLocation.CenterScreen;
		RequestedThemeVariant = ThemeVariant.Dark;
		Styles.Add(new StyleInclude(new Uri("avares://Nori.Desktop/"))
		{
			Source = new Uri("avares://Nori.Desktop/Settings/SettingsTheme.axaml"),
		});
		Background = ChatPalette.Background;
		WindowDecorations = PlatformServices.Current.Capabilities.SupportsWindowDrag
			? WindowDecorations.None
			: WindowDecorations.Full;

		_home = new HomeView(services, Refresh);
		_collapsed = _services.Config.GetBoolOr(KeySidebarCollapsed, false);
		Content = BuildChrome();
		Refresh();

		Opened += (_, _) => StartRefreshing();
		Closing += (_, args) =>
		{
			if (AllowClose) return;
			args.Cancel = true;
			// 主界面关掉只是收起：托盘还在，伴侣可能还在桌面上。
			Hide();
		};
		PropertyChanged += (_, args) =>
		{
			if (args.Property != IsVisibleProperty) return;
			if (IsVisible) StartRefreshing();
			else StopRefreshing();
		};
	}

	private bool IsEnglish() =>
		_services.Config.GetStringOr(ConfigStore.KeyLanguage, "zh-CN")
			.StartsWith("en", StringComparison.OrdinalIgnoreCase);

	private Control BuildChrome()
	{
		Border header = BuildHeader();
		Border sidebar = BuildSidebar();

		ScrollViewer body = new()
		{
			HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
			Padding = new Thickness(22, 18),
			Content = _home,
		};

		Border footer = new()
		{
			Background = ChatPalette.Deep,
			BorderBrush = ChatPalette.Panel, BorderThickness = new Thickness(0, 1, 0, 0),
			Padding = new Thickness(16, 8),
			Child = _hints,
		};

		DockPanel right = new() {LastChildFill = true};
		DockPanel.SetDock(footer, Dock.Bottom);
		right.Children.Add(footer);
		right.Children.Add(body);

		DockPanel shell = new() {LastChildFill = true};
		DockPanel.SetDock(header, Dock.Top);
		DockPanel.SetDock(sidebar, Dock.Left);
		shell.Children.Add(header);
		shell.Children.Add(sidebar);
		shell.Children.Add(right);
		return shell;
	}

	private Border BuildHeader()
	{
		TextBlock brand = new()
		{
			Text = "Nori", FontSize = 15, FontWeight = FontWeight.SemiBold,
			Foreground = ChatPalette.Primary, VerticalAlignment = VerticalAlignment.Center,
		};

		Button minimize = ChromeButton("─", () => WindowState = WindowState.Minimized);
		Button close = ChromeButton("✕", Hide);

		Border header = new()
		{
			Height = 44,
			Background = ChatPalette.Deep,
			BorderBrush = ChatPalette.Panel, BorderThickness = new Thickness(0, 0, 0, 1),
			Padding = new Thickness(16, 0, 6, 0),
			Child = new DockPanel
			{
				LastChildFill = false,
				Children = {brand, close, minimize},
			},
		};
		DockPanel.SetDock(brand, Dock.Left);
		DockPanel.SetDock(close, Dock.Right);
		DockPanel.SetDock(minimize, Dock.Right);

		// 去掉系统边框之后，顶部这条就是拖动区。
		header.PointerPressed += (_, args) =>
		{
			if (args.GetCurrentPoint(header).Properties.IsLeftButtonPressed) BeginMoveDrag(args);
		};
		return header;
	}

	private static Button ChromeButton(string glyph, Action onClick)
	{
		Button button = new()
		{
			Content = glyph, Width = 34, Height = 26,
			Background = Brushes.Transparent, Foreground = ChatPalette.Muted,
			BorderThickness = default,
			VerticalAlignment = VerticalAlignment.Center,
		};
		button.Click += (_, _) => onClick();
		return button;
	}

	/// <summary>
	/// 侧边栏。
	///
	/// 「主页」和下面四个**形状不同**：前者是当前窗口里的页面，带左侧高亮条与选中底色；
	/// 后者点了各自开窗，所以没有高亮条、没有选中态，只有一个「它已经开着」的圆点。
	/// 这条区分是上一轮专门改过的，别再把它们画成一样。
	///
	/// 几何上五行共用一套：高亮条槽位 3px → 图标 16px → 标签 → 状态点。高亮条即使不
	/// 显示也占位，图标因此在五行里共用一个横坐标；收起时这个横坐标不变，宽度变化不会
	/// 让图标横移。
	///
	/// 结构在这里建一次，内容每次 <see cref="Refresh"/> 重填 —— 界面语言可在运行时切换，
	/// 建一次填一次的话「主页」和分组标题会停在启动时的语言。
	/// </summary>
	private Border BuildSidebar()
	{
		_collapse.Background = Brushes.Transparent;
		_collapse.BorderThickness = default;
		_collapse.Padding = new Thickness(8, 0);
		_collapse.Height = 34;
		_collapse.Margin = new Thickness(8, 4, 8, 8);
		_collapse.CornerRadius = new CornerRadius(8);
		_collapse.HorizontalAlignment = HorizontalAlignment.Stretch;
		_collapse.HorizontalContentAlignment = HorizontalAlignment.Stretch;
		_collapse.Cursor = new Cursor(StandardCursorType.Hand);
		_collapse.Click += (_, _) => SetCollapsed(!_collapsed);
		Hoverable(_collapse);

		DockPanel column = new() {LastChildFill = true};
		DockPanel.SetDock(_collapse, Dock.Bottom);
		column.Children.Add(_collapse);
		column.Children.Add(_navigation);

		_sidebar.Width = _collapsed ? SidebarNarrow : SidebarWide;
		_sidebar.Background = SidebarGround;
		_sidebar.BorderBrush = ChatPalette.Line;
		_sidebar.BorderThickness = new Thickness(0, 0, 1, 0);
		_sidebar.Child = column;
		// 宽度过渡只在用户点击收起时发生，属于对操作的回应；系统关闭动效时不加。
		if (MotionPreference.AllowAnimation)
		{
			_sidebar.Transitions =
			[
				new DoubleTransition
				{
					Property = WidthProperty,
					Duration = TimeSpan.FromMilliseconds(160),
					Easing = new CubicEaseOut(),
				},
			];
		}
		return _sidebar;
	}

	/// <summary>重填侧边栏。五行加一条分组标题，全部按当前语言与当前状态重建。</summary>
	private void FillSidebar(bool english)
	{
		_navigation.Children.Clear();

		Border home = new()
		{
			Height = 36,
			Margin = new Thickness(8, 8, 8, 2),
			CornerRadius = new CornerRadius(8),
			Padding = new Thickness(8, 0),
			Background = SidebarActive,
			Child = Row("home", english ? "Home" : "主页", active: true),
		};
		_navigation.Children.Add(home);

		// 分组标题。收起后没有地方放文字，换成一条分隔线 —— 分组关系仍在，只是不写名字。
		_navigation.Children.Add(_collapsed
			? new Border
			{
				Height = 1, Background = ChatPalette.Line,
				Margin = new Thickness(16, 12, 16, 10),
			}
			: new TextBlock
			{
				Text = english ? "Open" : "打开",
				Foreground = ChatPalette.Faint, FontSize = 10,
				FontWeight = FontWeight.SemiBold, LetterSpacing = 0.8,
				Margin = new Thickness(19, 14, 0, 6),
			});

		_navigation.Children.Add(_launchers);

		_collapse.Content = Row(_collapsed ? "chevron-right" : "chevron-left",
				_collapsed
					? english ? "Expand" : "展开侧栏"
					: english ? "Collapse" : "收起侧栏",
			active: false);
		ToolTip.SetTip(_collapse, _collapsed ? english ? "Expand" : "展开侧栏" : null);
	}

	/// <summary>收起或展开。写入配置，下次启动沿用。</summary>
	private void SetCollapsed(bool collapsed)
	{
		if (_collapsed == collapsed) return;
		_collapsed = collapsed;
		try
		{
			// 写成 "1"/"0"：settings_update_general 写这个键时用的就是这个形状，
			// 两处写法不一致的话读取端要兼容两种，而其中一种迟早会漏。
			_services.Config.Set(KeySidebarCollapsed, new ConfigValue.Text(collapsed ? "1" : "0"));
			// 设置窗口与网页端读的是快照里的 general.sidebarCollapsed，不发通知它们不跟。
			_services.Runtime?.InvalidateSnapshot("general");
		}
		catch (Exception failure)
		{
			// 写不进去只影响下次启动的初值，当前这次照常收起。
			_services.Logger.Write(LogSource.Backend, "warn", $"侧边栏状态写入失败：{failure.Message}");
		}
		_sidebar.Width = collapsed ? SidebarNarrow : SidebarWide;
		Refresh();
	}

	/// <summary>
	/// 一行的内容。
	///
	/// 返回 DockPanel 而不是别的容器：读取端（含测试）按类型在这一层里找标签与状态点。
	/// </summary>
	private DockPanel Row(string icon, string label, bool active, params Control[] trailing)
	{
		Border bar = new()
		{
			Width = 3, Height = 16,
			CornerRadius = new CornerRadius(2),
			Background = SidebarBar,
			// 不显示时保留槽位，否则五行的图标各在各的横坐标上。
			Opacity = active ? 1 : 0,
			Margin = new Thickness(0, 0, 9, 0),
			VerticalAlignment = VerticalAlignment.Center,
		};

		Control glyph = LineIcon.Build(icon, active ? ChatPalette.Teal : ChatPalette.Muted, 16);
		glyph.Margin = new Thickness(0, 0, 10, 0);

		TextBlock text = new()
		{
			Text = label, FontSize = 13,
			FontWeight = active ? FontWeight.SemiBold : FontWeight.Medium,
			Foreground = active ? ChatPalette.Primary : ChatPalette.Body,
			IsVisible = !_collapsed,
			VerticalAlignment = VerticalAlignment.Center,
		};

		DockPanel row = new() {LastChildFill = false, VerticalAlignment = VerticalAlignment.Center};
		DockPanel.SetDock(bar, Dock.Left);
		DockPanel.SetDock(glyph, Dock.Left);
		DockPanel.SetDock(text, Dock.Left);
		row.Children.Add(bar);
		row.Children.Add(glyph);
		row.Children.Add(text);
		foreach (Control control in trailing)
		{
			DockPanel.SetDock(control, Dock.Right);
			row.Children.Add(control);
		}
		return row;
	}

	/// <summary>悬停底色。显式赋值会盖掉主题的悬停样式，所以两端都自己给。</summary>
	private static void Hoverable(Button button)
	{
		button.PointerEntered += (_, _) => button.Background = SidebarHover;
		button.PointerExited += (_, _) => button.Background = Brushes.Transparent;
	}

	/// <summary>
	/// 一个启动器。点了开另一个窗口；已经开着时右侧亮一个圆点。
	///
	/// 圆点只表示「这个窗口开着」，没有第二种含义 —— 上一轮试过给关闭态也画一个暗点，
	/// 在 6 像素的圆上那个颜色根本看不出来，等于一个看不见的提示。
	///
	/// <paramref name="badge"/> 是另一种指示：该窗口里存在待配置项（目前只有设置页的
	/// 模型服务未配置），用告警色的圆点，与开关状态的青色点区分。
	/// </summary>
	private Control LauncherEntry(string icon, string label, string windowLabel, bool badge = false)
	{
		bool open = _services.Windows?.IsWindowVisible(windowLabel) ?? false;

		// 两颗点都按名字取用，不按位置：读取端（含测试）此前按 Children 下标取，
		// 在这一行里插入图标就会静默取到另一个控件。
		Ellipse dot = new()
		{
			Name = LauncherOpenDot,
			Width = 6, Height = 6,
			Fill = ChatPalette.Teal,
			IsVisible = open,
			VerticalAlignment = VerticalAlignment.Center,
		};

		Ellipse attention = new()
		{
			Name = LauncherBadgeDot,
			Width = 6, Height = 6,
			Fill = SidebarBadge,
			IsVisible = badge,
			Margin = new Thickness(0, 0, open ? 8 : 0, 0),
			VerticalAlignment = VerticalAlignment.Center,
		};

		Button button = new()
		{
			Height = 36,
			Background = Brushes.Transparent,
			BorderThickness = default,
			CornerRadius = new CornerRadius(8),
			Padding = new Thickness(8, 0),
			Margin = new Thickness(8, 0),
			HorizontalAlignment = HorizontalAlignment.Stretch,
			HorizontalContentAlignment = HorizontalAlignment.Stretch,
			Cursor = new Cursor(StandardCursorType.Hand),
			// 与「主页」同一套行内几何：高亮条槽位（这里不显示）→ 图标 → 标签 → 状态点。
			Content = Row(icon, label, active: false, dot, attention),
		};
		Hoverable(button);
		// 收起后标签不显示，名称改由 ToolTip 承担。
		ToolTip.SetTip(button, _collapsed ? label : null);
		button.Click += (_, _) =>
		{
			_services.Windows?.Show(windowLabel);
			Refresh();
		};
		return button;
	}

	/// <summary>
	/// 重画那些会变的部分。
	///
	/// 整幅重建而不是绑定：这一页上会变的东西不多（四个窗口的开关状态、首页那几个
	/// 计数），而绑定要为每一项维护一个通知源，代价比重建大。
	///
	/// **但重建要有条件**：这个方法由一个 2 秒的定时器驱动，无条件重建会每 2 秒把
	/// 侧边栏那五行换成新控件 —— 指针停在某一行上时悬停底色被重置，而且每次都在丢弃
	/// 并重建一批控件。所以先比一遍状态签名，没变就什么也不做。
	/// </summary>
	private void Refresh()
	{
		bool english = IsEnglish();
		// 设置项带一个待办标记：模型服务未配置时，对话窗口打开也无法出结果，
		// 这个条件必须在主界面上可见。
		bool providerReady = _services.AiSettings.Read().Chat.IsConfigured;

		string signature = string.Join('|',
			english ? "en" : "zh",
			_collapsed ? "c" : "e",
			providerReady ? "ai" : "-",
			string.Concat(Launchers.Select(entry =>
				_services.Windows?.IsWindowVisible(entry.Window) == true ? '1' : '0')));

		if (signature != _sidebarSignature)
		{
			_sidebarSignature = signature;
			_launchers.Children.Clear();
			foreach ((string icon, Func<bool, string> label, string window) in Launchers)
			{
				_launchers.Children.Add(LauncherEntry(icon, label(english), window,
					badge: window == WindowLabels.Settings && !providerReady));
			}
			FillSidebar(english);
		}

		_home.Refresh(english);

		// 平台能力缺失时要说出来：托盘不可用的桌面环境里，用户找不到常驻入口。
		List<string> hints = [];
		PlatformCapabilities capabilities = PlatformServices.Current.Capabilities;
		if (_services.Runtime is {TrayAvailable: false})
			hints.Add(english ? "System tray unavailable; use this window to reach Nori." : "系统托盘不可用，只能从这个窗口找到 Nori。");
		if (!capabilities.SupportsHitThrough)
			hints.Add(english ? "Click-through is unavailable on this platform." : "本平台不支持点击穿透。");
		_hints.Text = hints.Count > 0 ? string.Join("  ", hints) : "";
		_hints.IsVisible = hints.Count > 0;
	}

	/// <summary>
	/// 窗口可见时才轮询。
	///
	/// 别的窗口的显隐没有事件通到这里（WindowManager 的 VisibilityChanged 是给快照
	/// 用的），而侧边栏那几个圆点要跟着变。两秒一次对一个静态页面足够，
	/// 也不会在窗口收起之后继续空转。
	/// </summary>
	private void StartRefreshing()
	{
		if (_refresh is not null) return;
		_refresh = new DispatcherTimer {Interval = TimeSpan.FromSeconds(2)};
		_refresh.Tick += (_, _) => Refresh();
		_refresh.Start();
		Refresh();
	}

	private void StopRefreshing()
	{
		_refresh?.Stop();
		_refresh = null;
	}

	protected override void OnClosed(EventArgs e)
	{
		StopRefreshing();
		base.OnClosed(e);
	}

	// ── 测试用 ─────────────────────────────────────────────────────────────

	/// <summary>侧边栏上四个启动器的标签，按顺序。</summary>
	internal IReadOnlyList<string> LauncherLabelsForTests =>
		[.. _launchers.Children.OfType<Button>()
			.Select(button => (button.Content as DockPanel)?.Children.OfType<TextBlock>().FirstOrDefault()?.Text ?? "")];

	/// <summary>哪几个启动器亮着「窗口开着」那颗点。按名字取，不按位置。</summary>
	internal IReadOnlyList<bool> LauncherDotsForTests =>
		[.. _launchers.Children.OfType<Button>().Select(button => DotVisible(button, LauncherOpenDot))];

	/// <summary>哪几个启动器亮着待办标记。</summary>
	internal IReadOnlyList<bool> LauncherBadgesForTests =>
		[.. _launchers.Children.OfType<Button>().Select(button => DotVisible(button, LauncherBadgeDot))];

	private static bool DotVisible(Button button, string name) =>
		(button.Content as DockPanel)?.Children
			.OfType<Ellipse>().FirstOrDefault(shape => shape.Name == name)?.IsVisible ?? false;

	/// <summary>底部那条平台提示；没有提示时为空串。</summary>
	internal string HintsForTests => _hints.IsVisible ? _hints.Text ?? "" : "";

	/// <summary>侧边栏当前宽度。</summary>
	internal double SidebarWidthForTests => _sidebar.Width;

	/// <summary>四个启动器的控件实例。用来判断有没有发生重建。</summary>
	internal IReadOnlyList<Control> LauncherControlsForTests => [.. _launchers.Children.OfType<Control>()];

	/// <summary>首页当前的顶层控件实例。同上。</summary>
	internal IReadOnlyList<Control> HomeChildrenForTests => _home.ChildrenForTests;

	/// <summary>启动器标签此刻是否显示。收起后只留图标。</summary>
	internal IReadOnlyList<bool> LauncherLabelsVisibleForTests =>
		[.. _launchers.Children.OfType<Button>()
			.Select(button => (button.Content as DockPanel)?.Children
				.OfType<TextBlock>().FirstOrDefault()?.IsVisible ?? false)];

	/// <summary>手工收起或展开。</summary>
	internal void SetCollapsedForTests(bool collapsed) => SetCollapsed(collapsed);

	/// <summary>手工触发一次重画。</summary>
	internal void RefreshForTests() => Refresh();
}
