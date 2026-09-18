using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using Nori.Core.Configuration;
using Nori.Core.Logging;
using Nori.Core.Platform;
using Nori.Desktop.Bridge;
using Nori.Desktop.Chat;

namespace Nori.Desktop.Windows;

/// <summary>
/// 原生初始化窗口。
///
/// 从 WebView 迁过来的第一个：它自足（不碰音频、不碰插件、不需要 Vue 的任何服务），
/// 迁过来之后启动路径上就少一次 WebView 冷启动 —— 那正是用户第一眼等待的那几百毫秒。
///
/// 流程与原来一致：
/// 进入 → 跑一次初始化 → 打开主界面 → 自己隐藏。等不到信号时留一条 10 秒的自救出口，
/// 不让用户永远看着转圈。
/// </summary>
public sealed class InitWindow : Window
{
	/// <summary>等宿主初始化信号的上限。与原 Vue 版一致。</summary>
	internal const int TimeoutMilliseconds = 10_000;

	private readonly AppServices _services;
	private readonly InitView _view;
	private DispatcherTimer? _watchdog;
	private bool _started;

	/// <summary>仅宿主退出流程可允许真正关闭。</summary>
	public bool AllowClose { get; set; }

	public InitWindow(WindowDefinition definition, AppServices services)
	{
		_services = services;
		Title = definition.Title;
		Width = definition.Width; Height = definition.Height;
		CanResize = definition.CanResize;
		// 与 NoriWindow 同一套判断：能原生拖动就去掉系统边框（整个应用都是自绘 chrome，
		// 少设这一行就会在一堆无边框窗口里冒出一个系统标题栏）；不能拖的平台退回
		// 系统边框，不留一个既拖不动也没有提示的窗口。
		WindowDecorations = PlatformServices.Current.Capabilities.SupportsWindowDrag
			? WindowDecorations.None
			: WindowDecorations.Full;
		WindowStartupLocation = WindowStartupLocation.CenterScreen;
		RequestedThemeVariant = ThemeVariant.Dark;
		Styles.Add(new StyleInclude(new Uri("avares://Nori.Desktop/"))
		{
			Source = new Uri("avares://Nori.Desktop/Settings/SettingsTheme.axaml"),
		});
		Background = ChatPalette.Background;

		_view = new InitView(IsEnglish(), () => _ = RetryAsync(), () => _services.Windows.Shutdown());
		Content = _view;

		// 去掉系统边框之后要自己接拖动。启动画面整面都可拖 —— 它没有标题栏，
		// 用户会下意识按住任意位置挪。
		PointerPressed += (_, args) =>
		{
			if (args.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(args);
		};

		// **推到下一帧再起跑。** 在 Opened 处理器里同步走完「进主界面」会连带
		// Hide 掉自己，而那时窗口还在完成显示流程，屏幕上会留下一个不重绘的空壳。
		Opened += (_, _) => Dispatcher.UIThread.Post(() => _ = BeginAsync());
		PropertyChanged += (_, args) =>
		{
			if (args.Property != IsVisibleProperty) return;
			// 首次运行路径下这个窗口是隐藏启动的，向导完成后宿主 Show 它 —— 变可见
			// 就是原来那条 nori:init-start 广播的等价信号，不必再走一次事件总线。
			if (IsVisible) Dispatcher.UIThread.Post(() => _ = BeginAsync());
			else _view.StopAnimation();
		};
		Closing += (_, args) =>
		{
			if (AllowClose) return;
			args.Cancel = true;
			Hide();
		};
	}

	private bool IsEnglish() =>
		_services.Config.GetStringOr(ConfigStore.KeyLanguage, "zh-CN")
			.StartsWith("en", StringComparison.OrdinalIgnoreCase);

	/// <summary>
	/// 起跑。
	///
	/// 可见、或宿主已经置位了「该开始了」，就直接走；两样都没有说明这一轮不该由
	/// 我们发起（例如首次运行向导还开着），只留一条超时出口。
	/// </summary>
	private async Task BeginAsync()
	{
		if (_started) return;
		_view.SetLanguage(IsEnglish());
		_view.StartAnimation();

		if (_services.Runtime is not { } runtime) return;
		if (runtime.ConsumeInitStartPending() || IsVisible)
		{
			await EnterAsync();
			return;
		}
		ArmWatchdog();
	}

	private void ArmWatchdog()
	{
		_watchdog?.Stop();
		_watchdog = new DispatcherTimer {Interval = TimeSpan.FromMilliseconds(TimeoutMilliseconds)};
		_watchdog.Tick += (_, _) =>
		{
			_watchdog?.Stop();
			if (!_started) _view.ShowTimeout();
		};
		_watchdog.Start();
	}

	/// <summary>超时面板上的手动重试。</summary>
	private async Task RetryAsync()
	{
		_view.SetRetrying(true);
		try { await EnterAsync(); }
		finally { _view.SetRetrying(false); }
	}

	private async Task EnterAsync()
	{
		if (_started) return;
		_started = true;
		_watchdog?.Stop();
		try
		{
			if (_services.Runtime is not { } runtime) throw new InvalidOperationException("运行时尚未就绪");
			await runtime.EnterMainFromInitAsync();
		}
		catch (Exception failure)
		{
			// 主窗口没能打开时不能静默：退回超时面板让用户重试。
			_started = false;
			_services.Logger.Write(LogSource.Backend, "warn",
				$"初始化进入主界面失败：{failure.GetType().Name}");
			_view.ShowTimeout(_view.EnterFailedText);
		}
	}

	// ── 测试用的几个口子 ────────────────────────────────────────────────
	//
	// 这一页没有可点的中间态可供断言：动效是定时器画的、超时是等出来的。
	// 与其在测试里等 10 秒，不如把这三样露出来。

	/// <summary>是否已经发起过进入主界面。</summary>
	internal bool HasStartedForTests => _started;

	/// <summary>直接切到超时面板，不等那 10 秒。</summary>
	internal void ShowTimeoutForTests()
	{
		_view.SetLanguage(IsEnglish());
		_view.ShowTimeout();
	}

	/// <summary>超时面板上那颗按钮的文案。</summary>
	internal string RetryLabelForTests => _view.RetryLabel;

	protected override void OnClosed(EventArgs e)
	{
		_watchdog?.Stop();
		_view.StopAnimation();
		base.OnClosed(e);
	}
}

/// <summary>
/// 初始化窗口的正文：一个会呼吸的标志、一条状态、以及等不到信号时的自救面板。
///
/// 动效用一个定时器驱动而不是 Avalonia 的 Animation：这一页只在启动的几百毫秒里
/// 存在，定时器起停的时机完全握在手里，窗口一隐藏就停，不会留一个空转的动画。
/// </summary>
internal sealed class InitView : Panel
{
	/// <summary>光环直径。窗口只有 320 高，留给状态文字和超时卡片的余量得够。</summary>
	private const double RingOuterSize = 150;

	private readonly Panel _halo;
	private readonly Image _logo;
	private readonly Ellipse _outerRing;
	private readonly Ellipse _innerRing;
	private readonly Ellipse _glow;
	private readonly TextBlock _status;
	private readonly StackPanel _statusCapsule;
	private readonly Border _timeoutCard;
	private readonly TextBlock _timeoutTitle;
	private readonly TextBlock _timeoutBody;
	private readonly Button _retry;
	private readonly TextBlock _retryError;

	private readonly RotateTransform _outerRotation = new();
	private readonly RotateTransform _innerRotation = new();
	private readonly ScaleTransform _breathe = new(1, 1);

	private DispatcherTimer? _ticker;
	private double _phase;
	private bool _english;

	internal InitView(bool english, Action onRetry, Action onClose)
	{
		_english = english;

		_outerRing = new Ellipse
		{
			Width = RingOuterSize, Height = RingOuterSize,
			Stroke = ChatPalette.Accent, StrokeThickness = 1, StrokeDashArray = [4, 6],
			Opacity = 0.40, RenderTransform = _outerRotation,
			RenderTransformOrigin = RelativePoint.Center,
		};
		_innerRing = new Ellipse
		{
			Width = RingOuterSize - 26, Height = RingOuterSize - 26,
			Stroke = ChatPalette.Teal, StrokeThickness = 1, StrokeDashArray = [1, 5],
			Opacity = 0.30, RenderTransform = _innerRotation,
			RenderTransformOrigin = RelativePoint.Center,
		};
		_glow = new Ellipse
		{
			Width = RingOuterSize - 52, Height = RingOuterSize - 52,
			Fill = new RadialGradientBrush
			{
				GradientStops =
				[
					new GradientStop(Color.Parse("#3a7de3ff"), 0),
					new GradientStop(Color.Parse("#1a7de3ff"), 0.45),
					new GradientStop(Colors.Transparent, 0.75),
				],
			},
		};
		_logo = new Image
		{
			Width = 84, Height = 84,
			Stretch = Stretch.Uniform,
			RenderTransform = _breathe,
			RenderTransformOrigin = RelativePoint.Center,
			Source = LoadLogo(),
		};

		_halo = new Panel
		{
			Width = RingOuterSize, Height = RingOuterSize,
			Children = {_outerRing, _innerRing, _glow, _logo},
		};

		_status = new TextBlock
		{
			Foreground = ChatPalette.Primary, FontSize = 14, FontWeight = FontWeight.SemiBold,
			VerticalAlignment = VerticalAlignment.Center,
		};
		_statusCapsule = new StackPanel
		{
			Orientation = Orientation.Horizontal, Spacing = 10,
			HorizontalAlignment = HorizontalAlignment.Center,
			Children = {_status},
		};

		_timeoutTitle = new TextBlock
		{
			Foreground = ChatPalette.Primary, FontSize = 15, FontWeight = FontWeight.SemiBold,
			HorizontalAlignment = HorizontalAlignment.Center,
		};
		_timeoutBody = new TextBlock
		{
			Foreground = ChatPalette.Muted, FontSize = 12, TextWrapping = TextWrapping.Wrap,
			MaxWidth = 360, TextAlignment = TextAlignment.Center,
		};
		_retry = new Button
		{
			HorizontalAlignment = HorizontalAlignment.Center,
			Padding = new Thickness(18, 7),
			Background = ChatPalette.Teal, Foreground = ChatPalette.OnTeal,
			CornerRadius = new CornerRadius(8),
		};
		_retry.Click += (_, _) => onRetry();
		_retryError = new TextBlock
		{
			Foreground = ChatPalette.Danger, FontSize = 12,
			HorizontalAlignment = HorizontalAlignment.Center, IsVisible = false,
		};
		_timeoutCard = new Border
		{
			IsVisible = false,
			Background = ChatPalette.Panel,
			BorderBrush = ChatPalette.Faint, BorderThickness = new Thickness(1),
			CornerRadius = new CornerRadius(12),
			Padding = new Thickness(22, 18),
			HorizontalAlignment = HorizontalAlignment.Center,
			Child = new StackPanel
			{
				Spacing = 10, HorizontalAlignment = HorizontalAlignment.Center,
				Children = {_timeoutTitle, _timeoutBody, _retry, _retryError},
			},
		};

		Button close = new()
		{
			Content = "✕",
			Width = 34, Height = 26,
			HorizontalAlignment = HorizontalAlignment.Right,
			VerticalAlignment = VerticalAlignment.Top,
			Margin = new Thickness(0, 6, 8, 0),
			Background = Brushes.Transparent, Foreground = ChatPalette.Muted,
			BorderThickness = default,
		};
		close.Click += (_, _) => onClose();

		Children.Add(new StackPanel
		{
			Spacing = 18,
			HorizontalAlignment = HorizontalAlignment.Center,
			VerticalAlignment = VerticalAlignment.Center,
			Children = {_halo, _statusCapsule, _timeoutCard},
		});
		Children.Add(close);

		ApplyText();
	}

	/// <summary>「打开主界面失败」那句，交给窗口在重试失败时用。</summary>
	internal string EnterFailedText => _english
		? "Failed to open the main window, please retry"
		: "打开主界面失败, 请重试";

	internal void SetLanguage(bool english)
	{
		if (_english == english) return;
		_english = english;
		ApplyText();
	}

	/// <summary>
	/// 文案。
	///
	/// **这几句眼下在两处各有一份**：这里，以及 Vue 侧 locales 的 views.init —— 那个
	/// 视图已经不再被任何窗口加载，但摘掉它要连带动 router、WINDOW_ROUTES、两份
	/// locales 和一条约定测试，单开一次提交做。改文案时两边一起改，别只改一边。
	/// </summary>
	private void ApplyText()
	{
		_status.Text = _english ? "Initializing Live2D model..." : "正在初始化 Live2D 模型...";
		_timeoutTitle.Text = _english ? "Initialization timed out" : "初始化等待超时";
		_timeoutBody.Text = _english
			? "No initialization signal from the host. You can open the main window directly."
			: "没有收到宿主的初始化信号, 可以直接进入主界面";
		_retry.Content = _english ? "Open main window" : "进入主界面";
	}

	/// <summary>按钮文案，供测试断言语言切换。</summary>
	internal string RetryLabel => _retry.Content as string ?? "";

	internal void ShowTimeout(string? error = null)
	{
		// 光环收掉：320 高的窗口放不下「光环 + 整张卡片」，而这一刻页面的任务已经
		// 从「等着」变成「请你点一下」，动效留着只会把卡片挤出可视区域。
		_halo.IsVisible = false;
		StopAnimation();
		_statusCapsule.IsVisible = false;
		_timeoutCard.IsVisible = true;
		_retryError.Text = error ?? "";
		_retryError.IsVisible = error is {Length: > 0};
	}

	internal void SetRetrying(bool retrying)
	{
		_retry.IsEnabled = !retrying;
		if (retrying) _retryError.IsVisible = false;
	}

	/// <summary>起转。窗口可见时才应该转 —— 隐藏着空转一个定时器没有意义。</summary>
	internal void StartAnimation()
	{
		if (_ticker is not null) return;
		_ticker = new DispatcherTimer {Interval = TimeSpan.FromMilliseconds(33)};
		_ticker.Tick += (_, _) =>
		{
			_phase += 0.033;
			// 两圈反向转，周期取原来 CSS 上的 14s 与 22s。
			_outerRotation.Angle = _phase / 14 * 360 % 360;
			_innerRotation.Angle = 360 - _phase / 22 * 360 % 360;
			// 呼吸：4 秒一个来回，幅度 3%。
			double scale = 1 + 0.03 * Math.Sin(_phase / 4 * 2 * Math.PI);
			_breathe.ScaleX = scale;
			_breathe.ScaleY = scale;
			_glow.Opacity = 0.65 + 0.25 * Math.Sin(_phase / 3 * 2 * Math.PI);
		};
		_ticker.Start();
	}

	internal void StopAnimation()
	{
		_ticker?.Stop();
		_ticker = null;
	}

	/// <summary>标志图。取不到就不画 —— 一个缺图的启动画面仍然能用。</summary>
	private static Bitmap? LoadLogo()
	{
		try
		{
			return new Bitmap(AssetLoader.Open(new Uri("avares://Nori.Desktop/Assets/logo.png")));
		}
		catch
		{
			return null;
		}
	}
}
