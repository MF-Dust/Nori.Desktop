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
using Nori.Desktop.Account;
using Nori.Desktop.Bridge;
using Nori.Desktop.Chat;
using Nori.Desktop.Ui;

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
		Opened += (_, _) =>
		{
			_shownAt = DateTime.UtcNow;
			Dispatcher.UIThread.Post(() => _ = BeginAsync());
		};
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
		// 入场序列与初始化并行，不 await：await 会使动效计入启动耗时。
		_ = _view.PlayIntroAsync();

		// 视觉测试需要逐档截图。实际运行中初始化随即开始，入场序列通常不会走完
		// （符合预期），但那样无法取到各档位的渲染结果。
		if (HoldForTests) return;

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
			// 收尾必须排在打开主界面**之前**：主界面显示后本窗口即被隐藏，排在其后不会被渲染。
			await _view.PlayHandoffAsync();
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

	/// <summary>视觉测试用：停留在入场序列，不自动进入主界面。</summary>
	internal bool HoldForTests { get; set; }

	/// <summary>视觉测试用：执行一次收尾，不打开主界面。</summary>
	internal Task PlayHandoffForTests() => _view.PlayHandoffAsync();

	/// <summary>
	/// 视觉测试用：当前档位。
	///
	/// 按时间取样不可靠：入场序列各档由 <c>await Task.Delay</c> 推进，而测试在同一
	/// UI 线程上轮询，二者竞争调度，实际耗时可达标称值的两到三倍。取样改为按状态判断。
	/// </summary>
	internal HaloMood MoodForTests => _view.MoodForTests;

	/// <summary>视觉测试用：窗口显示到现在过了多少毫秒。</summary>
	internal double ElapsedSinceShownForTests =>
		_shownAt is {} at ? (DateTime.UtcNow - at).TotalMilliseconds : 0;

	private DateTime? _shownAt;

	/// <summary>视觉测试用：将入场序列置为终态，以便截取稳定的 Working 档。</summary>
	internal void SettleIntroForTests() => _view.SettleIntro();

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

	private readonly NoriHalo _halo;
	private readonly ScaleTransform _wake = new(1, 1);
	private readonly TextBlock _status;
	private readonly StackPanel _statusCapsule;
	private readonly Border _timeoutCard;
	private readonly TextBlock _timeoutTitle;
	private readonly TextBlock _timeoutBody;
	private readonly Button _retry;
	private readonly TextBlock _retryError;

	private bool _english;

	internal InitView(bool english, Action onRetry, Action onClose)
	{
		_english = english;

		// 品牌标记走共用控件。启动画面要的是「在忙」那一档 —— 双环反向转、呼吸、
		// 光晕脉动，跟原来逐帧一致。
		_halo = new NoriHalo(RingOuterSize) {Mood = HaloMood.Working};

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
			// 开场时整组一起放大到位。放在这一层而不是各控件各缩各的 —— 那样
			// 它们会各自从不同的地方长出来，读起来是三样东西而不是一屏。
			RenderTransform = _wake,
			RenderTransformOrigin = RelativePoint.Center,
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

	/// <summary>视觉测试用：光环当前在哪一档。</summary>
	internal HaloMood MoodForTests => _halo.Mood;

	/// <summary>起转。窗口可见时才应该转 —— 隐藏着空转一个定时器没有意义。</summary>
	internal void StartAnimation() => _halo.Start();

	internal void StopAnimation() => _halo.Stop();

	/*
	 * ── 启动序列 ──────────────────────────────────────────────────────────
	 *
	 * HaloMood 已定义 Dormant / Waking / Working / Connected 四档，此前这一页
	 * 固定使用 Working，不区分初始化的开始与结束。本段按四档依次切换：
	 *
	 *   Dormant ─► Waking ─► Working（初始化在此期间执行）─► Connected ─► 打开主界面
	 *
	 * 三条约束：
	 *
	 *   ① **不得增加启动耗时。**入场序列不被 await，与初始化并行执行；初始化先完成
	 *      时直接进入收尾，入场序列中止于当前档位。
	 *   ② 收尾固定 280ms。此前主界面打开是无过渡切换，这是本序列唯一主动占用的时间。
	 *   ③ 系统「减少动画」偏好开启时整段跳过，直接呈现终态。
	 */

	/// <summary>入场序列每一档的停留时长。</summary>
	private static readonly TimeSpan Beat = TimeSpan.FromMilliseconds(260);

	private bool _introDone;

	/// <summary>
	/// 将入场序列直接置为终态。
	///
	/// 初始化早于入场序列完成时调用。此时序列无需继续，但它设置的中间值（不透明度、
	/// 缩放）必须复位，否则界面停留在过渡中的数值上。
	/// </summary>
	internal void SettleIntro()
	{
		_introDone = true;
		Opacity = 1;
		_wake.ScaleX = _wake.ScaleY = 1;
		_statusCapsule.Opacity = 1;
		if (_halo.Mood != HaloMood.Connected) _halo.Mood = HaloMood.Working;
	}

	/// <summary>入场序列。调用方**不得** await —— 它不应阻塞初始化。</summary>
	internal async Task PlayIntroAsync()
	{
		if (!MotionPreference.AllowAnimation)
		{
			_halo.Mood = HaloMood.Working;
			_statusCapsule.Opacity = 1;
			_introDone = true;
			return;
		}

		// 整体淡入并轻微放大，作为单次入场，不对各元素分别做进入动效。
		_halo.Mood = HaloMood.Dormant;
		_statusCapsule.Opacity = 0;
		_wake.ScaleX = _wake.ScaleY = 0.94;
		Opacity = 0;

		await FadeAsync(Beat,
			value => Opacity = value,
			value => _wake.ScaleX = _wake.ScaleY = 0.94 + 0.06 * value);
		if (_introDone) return;

		// 唯一一次提亮：外环由中性灰转强调色，标志不透明度到 1。
		_halo.Mood = HaloMood.Waking;
		await Task.Delay(Beat);
		if (_introDone) return;

		// 进入 Working 后才显示状态文字：Dormant 档尚未开始初始化，提前显示与实际状态不符。
		_halo.Mood = HaloMood.Working;
		await FadeAsync(Beat, value => _statusCapsule.Opacity = value);
		_introDone = true;
	}

	/// <summary>
	/// 收尾：切到 Connected 档，停留 280ms 后打开主界面。
	///
	/// Connected 在账户窗口表示会话已建立，在此表示初始化完成，两处语义一致。
	/// </summary>
	internal async Task PlayHandoffAsync()
	{
		// 初始化可能早于入场序列完成。先复位序列设置的中间值，否则收尾会叠加在
		// 未完成的淡入与缩放上。
		SettleIntro();
		if (!MotionPreference.AllowAnimation) return;

		_halo.Mood = HaloMood.Connected;
		await Task.Delay(TimeSpan.FromMilliseconds(280));
	}

	/// <summary>
	/// 逐帧推进一个 0→1 的插值量。
	///
	/// 使用循环而非 Avalonia Transition：本序列可能被初始化完成中断，而交给属性系统的
	/// 过渡无法在中途接管。
	/// </summary>
	private static async Task FadeAsync(TimeSpan span, params Action<double>[] apply)
	{
		int frames = Math.Max(1, (int)(span.TotalMilliseconds / 16));
		for (int frame = 1; frame <= frames; frame++)
		{
			// ease-out：末段速度递减，终止时无突变。
			double eased = 1 - Math.Pow(1 - (double)frame / frames, 3);
			foreach (Action<double> set in apply) set(eased);
			await Task.Delay(16);
		}
	}
}
