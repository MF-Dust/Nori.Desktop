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
/// 隐藏的音频宿主窗口。
///
/// 只承载 WebAudio 与 MediaRecorder，不进入用户可见的窗口列表。
/// </summary>
public sealed class NoriWindow : Window, IBridgeSource
{
	/// <summary>窗口标签</summary>
	public string Label { get; }

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
		};
		_webView.Source = new Uri(url);

		// 窗口真正销毁时停止一切脚本派发 (隐藏不关闭的窗口不受影响)。
		Closed += (_, _) => _scriptDispatcher.Close();
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
		if (Label != WindowLabels.AudioHost) return;
		// 原生控件必须先挂到窗口才会导航；导航后隐藏不会卸载音频页面。
		Dispatcher.UIThread.Post(Hide, DispatcherPriority.Background);
	}

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
