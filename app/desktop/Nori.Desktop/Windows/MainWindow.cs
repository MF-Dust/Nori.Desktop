using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Nori.Core.Configuration;
using Nori.Core.Platform;
using Nori.Desktop.Bridge;
using Nori.Desktop.Chat;
using Nori.Desktop.Main;

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
	private readonly Button _collapse = new();
	private readonly TextBlock _hints = new()
	{
		Foreground = ChatPalette.Faint, FontSize = 11,
		TextWrapping = TextWrapping.Wrap, MaxWidth = 640,
	};

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
	/// 「主页」和下面四个**形状不同**：前者是当前窗口里的页面，带左侧竖条与选中态；
	/// 后者点了各自开窗，所以没有竖条、没有选中态，只有一个「它已经开着」的圆点。
	/// 这条区分是上一轮专门改过的，别再把它们画成一样。
	/// </summary>
	private Border BuildSidebar()
	{
		bool english = IsEnglish();

		Border home = new()
		{
			Background = ChatPalette.Panel,
			CornerRadius = new CornerRadius(8),
			Padding = new Thickness(12, 9),
			Margin = new Thickness(8, 8, 8, 4),
			Child = new StackPanel
			{
				Orientation = Orientation.Horizontal, Spacing = 8,
				Children =
				{
					new Rectangle {Width = 3, Height = 16, Fill = ChatPalette.Accent, RadiusX = 2, RadiusY = 2},
					new TextBlock
					{
						Text = english ? "Home" : "主页",
						Foreground = ChatPalette.Primary, FontSize = 13, FontWeight = FontWeight.SemiBold,
						VerticalAlignment = VerticalAlignment.Center,
					},
				},
			},
		};

		return new Border
		{
			Width = 176,
			Background = ChatPalette.Deep,
			BorderBrush = ChatPalette.Panel, BorderThickness = new Thickness(0, 0, 1, 0),
			Child = new StackPanel
			{
				Children =
				{
					home,
					new TextBlock
					{
						Text = english ? "Open" : "打开",
						Foreground = ChatPalette.Faint, FontSize = 11,
						Margin = new Thickness(20, 10, 0, 6),
					},
					_launchers,
				},
			},
		};
	}

	/// <summary>
	/// 一个启动器。点了开另一个窗口；已经开着时右侧亮一个圆点。
	///
	/// 圆点只表示「这个窗口开着」，没有第二种含义 —— 上一轮试过给关闭态也画一个暗点，
	/// 在 6 像素的圆上那个颜色根本看不出来，等于一个看不见的提示。
	/// </summary>
	private Control LauncherEntry(string label, string windowLabel)
	{
		bool open = _services.Windows?.IsWindowVisible(windowLabel) ?? false;

		Ellipse dot = new()
		{
			Width = 6, Height = 6,
			Fill = ChatPalette.Teal,
			IsVisible = open,
			VerticalAlignment = VerticalAlignment.Center,
		};

		Button button = new()
		{
			Background = Brushes.Transparent,
			BorderThickness = default,
			Padding = new Thickness(20, 8),
			HorizontalAlignment = HorizontalAlignment.Stretch,
			HorizontalContentAlignment = HorizontalAlignment.Stretch,
			Content = new DockPanel
			{
				LastChildFill = false,
				Children =
				{
					new TextBlock
					{
						Text = label, Foreground = ChatPalette.Body, FontSize = 13,
						VerticalAlignment = VerticalAlignment.Center,
					},
					dot,
				},
			},
		};
		DockPanel.SetDock(dot, Dock.Right);
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
	/// </summary>
	private void Refresh()
	{
		bool english = IsEnglish();

		_launchers.Children.Clear();
		foreach ((string label, string window) in new[]
		{
			(english ? "Chat" : "对话", WindowLabels.Chat),
			(english ? "Models" : "模型", WindowLabels.Models),
			(english ? "Memory" : "记忆", WindowLabels.Memory),
			(english ? "Settings" : "设置", WindowLabels.Settings),
		})
		{
			_launchers.Children.Add(LauncherEntry(label, window));
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
			.Select(button => ((button.Content as DockPanel)?.Children[0] as TextBlock)?.Text ?? "")];

	/// <summary>哪几个启动器亮着圆点。</summary>
	internal IReadOnlyList<bool> LauncherDotsForTests =>
		[.. _launchers.Children.OfType<Button>()
			.Select(button => (button.Content as DockPanel)?.Children[1].IsVisible ?? false)];

	/// <summary>底部那条平台提示；没有提示时为空串。</summary>
	internal string HintsForTests => _hints.IsVisible ? _hints.Text ?? "" : "";

	/// <summary>手工触发一次重画。</summary>
	internal void RefreshForTests() => Refresh();
}
