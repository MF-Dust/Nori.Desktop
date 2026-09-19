using System.Text.Json;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using Nori.Core.Data;
using Nori.Core.Platform;
using Nori.Core.WebView;
using Nori.Desktop.Bridge;

namespace Nori.Desktop.Windows;

/// <summary>
/// 承载前端页面的窗口
///
/// 四个窗口共用这一个类, 差异全部来自 WindowDefinition. 每个窗口内含一个
/// NativeWebView, 加载同一份 Vue bundle, 靠 URL 上的 ?window=&lt;label&gt; 决定显示哪个页面.
/// </summary>
public sealed class NoriWindow : Window, IBridgeSource
{
	/// <summary>窗口标签</summary>
	public string Label { get; }

	/// <summary>当前窗口实际获得的系统背景模糊状态。</summary>
	internal bool BackdropActive { get; private set; }

	internal void SetBackdropActive(bool active)
	{
		if (BackdropActive == active) return;
		BackdropActive = active;
		PostEvent("nori:window-backdrop", new { active });
	}

	/// <summary>底层 Avalonia 窗口 (即自身)</summary>
	public Window? Self => this;

	/// <summary>
	/// 是否允许真正关闭
	///
	/// 平时关闭窗口只隐藏 (与 Tauri 版一致, 关窗不退应用), 只有窗口调度显式销毁时才放行
	/// </summary>
	public bool AllowClose { get; set; }

	private readonly NativeWebView _webView;
	private readonly NoriBridge _bridge;
	private readonly WebViewScriptDispatcher _scriptDispatcher;
	private readonly DispatcherTimer _metricsTimer;
	private readonly AppStoragePaths _storagePaths;

	public NoriWindow(WindowDefinition definition, NoriBridge bridge, string url, AppStoragePaths storagePaths)
	{
		Label = definition.Label;
		_bridge = bridge;
		_storagePaths = storagePaths ?? throw new ArgumentNullException(nameof(storagePaths));

		Title = definition.Title;
		Width = definition.Width;
		Height = definition.Height;
		if (definition.MinWidth is { } minWidth) MinWidth = minWidth;
		if (definition.MinHeight is { } minHeight) MinHeight = minHeight;
		CanResize = definition.CanResize;
		Topmost = definition.Topmost;
		ShowInTaskbar = definition.ShowInTaskbar;
		WindowDecorations = WindowDecorations.None;
		Background = Brushes.Transparent;
		TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
		WindowStartupLocation = WindowStartupLocation.CenterScreen;
		Icon = LoadIcon();

		_webView = new NativeWebView
		{
			Background = Brushes.Transparent,
		};
		_webView.EnvironmentRequested += OnEnvironmentRequested;
		_webView.WebMessageReceived += OnWebMessageReceived;
		_webView.NavigationCompleted += OnNavigationCompleted;
		// 原生 WebView 会覆盖 Avalonia 命中测试；留出可命中的宿主边缘。
		Content = Label == WindowLabels.AudioHost ? _webView : new Border
		{
			Padding = CanResize ? new Thickness(5) : default, Background = Brushes.Transparent, Child = _webView,
		};
		if (Label != WindowLabels.AudioHost) NativeWindowChrome.EnableBorderlessResize(this);
		_scriptDispatcher = new WebViewScriptDispatcher(script => _webView.InvokeScript(script));

		PropertyChanged += (_, args) =>
		{
			if (args.Property != WindowStateProperty && args.Property != CanResizeProperty) return;
			if (Content is Border frame) frame.Padding = CanResize && WindowState == WindowState.Normal ? new Thickness(5) : default;
			if (Label != WindowLabels.AudioHost) PostWindowState();
		};
		_webView.Source = new Uri(url);

		// 窗口移动 / DPI 变化时主动推度量, 前端据此免掉每帧的位置与缩放往返。
		// 拖动时 PositionChanged 每个像素都触发, 用 50ms 定时器合帧:
		// 移动中至多 ~20Hz, 停下后必补发一次最终值。
		_metricsTimer = new DispatcherTimer {Interval = TimeSpan.FromMilliseconds(50)};
		_metricsTimer.Tick += (_, _) =>
		{
			_metricsTimer.Stop();
			PostMetrics();
		};
		PositionChanged += (_, _) =>
		{
			if (_scriptDispatcher.IsClosed) return;
			if (!_metricsTimer.IsEnabled) _metricsTimer.Start();
		};
		ScalingChanged += (_, _) =>
		{
			if (!_scriptDispatcher.IsClosed) PostMetrics();
		};
		// 窗口真正销毁时停掉度量定时器并停止一切脚本派发 (隐藏不关闭的窗口不受影响)。
		Closed += (_, _) =>
		{
			_metricsTimer.Stop();
			_scriptDispatcher.Close();
		};
	}

	/// <summary>挂接原生 WebView 后隐藏；不激活、不在任务栏留下窗口。</summary>
	internal void StartAudioHost()
	{
		if (Label != WindowLabels.AudioHost) throw new InvalidOperationException("不是音频宿主窗口");
		ShowActivated = false;
		ShowInTaskbar = false;
		WindowStartupLocation = WindowStartupLocation.Manual;
		Position = new PixelPoint(-32000, -32000);
		Opacity = 0;
		WindowDecorations = WindowDecorations.None;
		Show();
	}

	/// <summary>
	/// 把当前窗口度量推给页面 (物理像素)
	/// </summary>
	public void PostMetrics()
	{
		double scale = RenderScaling;
		PostEvent("nori:window-metrics", new
		{
			label = Label,
			x = Position.X,
			y = Position.Y,
			width = (int)Math.Round((FrameSize?.Width ?? Bounds.Width) * scale),
			height = (int)Math.Round((FrameSize?.Height ?? Bounds.Height) * scale),
			scaleFactor = scale,
		});
	}

	/// <summary>
	/// 向该窗口的页面推送一个事件
	/// </summary>
	public void PostEvent(string name, object? payload)
	{
		string envelope = JsonSerializer.Serialize(new
		{
			kind = "event",
			@event = name,
			payload,
		}, BridgeJson.Options);
		Dispatch(envelope);
	}

	/// <summary>
	/// 回复一次 invoke 调用
	/// </summary>
	public void PostResult(long id, object? value, string? error)
	{
		string envelope = JsonSerializer.Serialize(error is null
			? new BridgeResult {Kind = "resolve", Id = id, Value = value}
			: new BridgeResult {Kind = "reject", Id = id, Error = error}, BridgeJson.Options);
		Dispatch(envelope);
	}

	/// <summary>
	/// 把 JSON 信封送进页面
	///
	/// 再序列化一次成 JS 字符串字面量, 页面里 JSON.parse 回来 —— 双层编码, 杜绝转义问题。
	/// 未 ready 时排队, 窗口销毁后丢弃, 派发任务由 dispatcher 统一观察。
	/// </summary>
	private void Dispatch(string envelopeJson)
	{
		string script = $"window.__nori&&window.__nori.dispatch({JsonSerializer.Serialize(envelopeJson)})";
		if (!Dispatcher.UIThread.CheckAccess())
		{
			Dispatcher.UIThread.Post(() => Dispatch(envelopeJson));
			return;
		}
		_scriptDispatcher.Dispatch(script);
	}

	private void OnEnvironmentRequested(object? sender, WebViewEnvironmentRequestedEventArgs e)
	{
		if (e is not WindowsWebView2EnvironmentRequestedEventArgs wv2) return;
		// WebView 缓存固定放入可移动包根的 data/webview 分层目录。
		wv2.UserDataFolder = _storagePaths.WebViewHostCacheDirectory;
	}

	private void OnWebMessageReceived(object? sender, WebMessageReceivedEventArgs e)
	{
		if (e.Body is not {Length: > 0} body) return;
		_bridge.Handle(this, body);
	}

	private void OnNavigationCompleted(object? sender, WebViewNavigationCompletedEventArgs e)
	{
		if (!e.IsSuccess) return;
		if (!Dispatcher.UIThread.CheckAccess())
		{
			Dispatcher.UIThread.Post(() => OnNavigationCompleted(sender, e));
			return;
		}
		_scriptDispatcher.MarkReady();
		if (Label == WindowLabels.AudioHost)
		{
			// 原生控件必须先挂到窗口才会导航；导航后隐藏不会卸载音频页面。
			Dispatcher.UIThread.Post(Hide, DispatcherPriority.Background);
			return;
		}
		// 页面就绪后先给一份度量, 免得首次调用还要往返
		PostMetrics();
		PostEvent("nori:window-backdrop", new { active = BackdropActive });
		PostWindowState();
	}

	private void PostWindowState() => PostEvent("nori:window-state", new
	{
		maximized = WindowState == WindowState.Maximized, canResize = CanResize,
	});

	/// <summary>
	/// 窗口原生句柄, 供平台服务发起拖动
	/// </summary>
	public nint NativeHandle => TryGetPlatformHandle()?.Handle ?? 0;

	/// <summary>
	/// 加载窗口图标, 失败时返回 null 而不是让启动崩掉
	/// </summary>
	private static WindowIcon? LoadIcon()
	{
		try
		{
			return new WindowIcon(AssetLoader.Open(new Uri("avares://Nori.Desktop/Assets/icon.ico")));
		}
		catch (Exception exception) when (exception is FileNotFoundException or ArgumentException)
		{
			return null;
		}
	}
}
