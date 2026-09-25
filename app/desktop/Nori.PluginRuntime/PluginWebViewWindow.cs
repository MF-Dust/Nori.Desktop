using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using Nori.Core.Logging;
using Nori.Core.Platform;
using Nori.Core.WebView;

namespace Nori.PluginRuntime;

/// <summary>
/// 承载插件 Web 视图的独立窗口
///
/// 独立于 WindowDefinition.All 与 NoriWindow, 专为动态插件页面设计.
/// 提供原生透明度、跨平台标题栏适配与隔离的 NativeWebView 运行环境.
/// </summary>
internal sealed class PluginWebViewWindow : Window, IPluginWebViewWindow, IPluginBridgeSource
{
	/// <summary>所属插件 ID</summary>
	public string PluginId { get; }

	/// <summary>窗口 ID</summary>
	public string WindowId { get; }

	/// <summary>窗口全局标签 (plugin:{pluginId}:{windowId})</summary>
	public string Label { get; }

	/// <summary>脱敏的插件描述符摘要</summary>
	public PluginDescriptorSummary Descriptor { get; }

	/// <summary>关联的独立插件通信桥</summary>
	internal PluginBridge Bridge { get; }

	string IPluginWebViewWindow.Id => WindowId;
	string? IPluginWebViewWindow.Title => Title;
	bool IPluginBridgeSource.IsVisible => Volatile.Read(ref _visible) == 1;

	private readonly NativeWebView _webView;
	private readonly WebViewScriptDispatcher _scriptDispatcher;
	private readonly string _webViewDataRoot;
	private readonly Func<Uri, bool>? _navigationPolicy;
	private int _visible;
	private CancellationTokenRegistration _revocationRegistration;
	private int _isClosingOrClosed;

	/// <summary>
	/// 当窗口被关闭时触发通知 (供 PluginWindowHost 从活动窗口字典中移除)
	/// </summary>
	public event Action<PluginWebViewWindow>? WindowClosed;

	public PluginWebViewWindow(
		PluginDescriptorSummary descriptor,
		PluginWebViewOptions options,
		PluginBridge? bridge = null,
		CancellationToken revocationToken = default,
		string? webViewDataRoot = null,
		FileLogger? logger = null,
		Uri? entryPoint = null,
		Func<Uri, bool>? navigationPolicy = null)
	{
		ArgumentNullException.ThrowIfNull(descriptor);
		ArgumentNullException.ThrowIfNull(options);

		PluginWindowHost.ValidatePluginId(descriptor.Id, nameof(descriptor.Id));
		PluginWindowHost.ValidateId(options.Id, nameof(options.Id));

		PluginId = descriptor.Id;
		WindowId = options.Id;
		Label = PluginWindowHost.BuildLabel(PluginId, WindowId);
		Descriptor = descriptor;
		_webViewDataRoot = Path.GetFullPath(webViewDataRoot ?? Path.Combine(AppContext.BaseDirectory, "webview_plugins"));
		_navigationPolicy = navigationPolicy;

		Title = options.Title;
		Width = options.Width;
		Height = options.Height;
		if (options.MinWidth is { } minWidth) MinWidth = minWidth;
		if (options.MinHeight is { } minHeight) MinHeight = minHeight;
		CanResize = options.CanResize;
		Topmost = options.Topmost;
		ShowInTaskbar = options.ShowInTaskbar;

		// 标题栏与边框策略: 与主程序保持一致, 支持原生拖动时使用无边框透明
		WindowDecorations = PlatformServices.Current.Capabilities.SupportsWindowDrag
			? WindowDecorations.None
			: WindowDecorations.Full;
		Background = Brushes.Transparent;
		TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
		WindowStartupLocation = WindowStartupLocation.CenterScreen;
		Icon = LoadIcon();

		Bridge = bridge ?? new PluginBridge(
			PluginId,
			WindowId,
			descriptor,
			closeSelfHandler: ct => CloseAsync(ct),
			commandHandler: options.CommandHandler,
			logger: logger);

		_webView = new NativeWebView
		{
			Background = Brushes.Transparent,
		};
		_webView.EnvironmentRequested += OnEnvironmentRequested;
		_webView.WebMessageReceived += OnWebMessageReceived;
		_webView.NewWindowRequested += (_, args) => args.Handled = true;
		_webView.NavigationStarted += (_, args) =>
		{
			if (args.Request is not { } request || _navigationPolicy?.Invoke(request) == false) args.Cancel = true;
		};
		_webView.NavigationCompleted += OnNavigationCompleted;
		Content = _webView;
		_scriptDispatcher = new WebViewScriptDispatcher(script => _webView.InvokeScript(script));

		// 导航至已由宿主解析并限制在插件 web 根下的入口 URL。
		_webView.Source = entryPoint ?? new Uri(options.EntryPoint, UriKind.RelativeOrAbsolute);

		PropertyChanged += OnPropertyChanged;
		Closed += OnClosed;

		// 绑定插件租约撤销令牌: 当插件卸载或上下文销毁时，自动关闭该插件名下的全部窗口
		if (revocationToken.CanBeCanceled)
		{
			_revocationRegistration = revocationToken.Register(() =>
			{
				Dispatcher.UIThread.Post(() => _ = CloseAsync());
			});
		}
	}

	/// <summary>
	/// 显示并聚焦窗口 (确保在 UI 线程执行)
	/// </summary>
	public async Task ShowAsync(CancellationToken cancellationToken = default)
	{
		if (Dispatcher.UIThread.CheckAccess())
		{
			Show();
			Activate();
			return;
		}

		await Dispatcher.UIThread.InvokeAsync(() =>
		{
			Show();
			Activate();
		});
	}

	/// <summary>
	/// 隐藏窗口 (确保在 UI 线程执行)
	/// </summary>
	public async Task HideAsync(CancellationToken cancellationToken = default)
	{
		if (Dispatcher.UIThread.CheckAccess())
		{
			Hide();
			return;
		}

		await Dispatcher.UIThread.InvokeAsync(Hide);
	}

	/// <summary>
	/// 关闭窗口并释放关联资源
	/// </summary>
	public async Task CloseAsync(CancellationToken cancellationToken = default)
	{
		if (Interlocked.Exchange(ref _isClosingOrClosed, 1) != 0) return;

		_revocationRegistration.Dispose();
		await Bridge.DisposeAsync().ConfigureAwait(false);

		if (Dispatcher.UIThread.CheckAccess())
		{
			Close();
			return;
		}

		await Dispatcher.UIThread.InvokeAsync(Close);
	}

	public async ValueTask DisposeAsync()
	{
		await CloseAsync().ConfigureAwait(false);
	}

	/// <summary>
	/// 向插件页面推送事件 (kind='event' 信封): 宿主侧动作 (如 AI 调用) 由此通知页面。
	/// </summary>
	public Task SendEventAsync(string eventName, JsonNode? payload, CancellationToken cancellationToken = default)
	{
		if (string.IsNullOrWhiteSpace(eventName)) throw new ArgumentException("事件名无效", nameof(eventName));
		if (Volatile.Read(ref _isClosingOrClosed) != 0) return Task.CompletedTask;

		JsonObject envelope = new()
		{
			["kind"] = "event",
			["event"] = eventName,
			["payload"] = payload?.DeepClone(),
		};
		Dispatch($"window.__noriPlugin&&window.__noriPlugin.dispatch({JsonSerializer.Serialize(envelope, PluginRuntimeJson.Options)})");
		return Task.CompletedTask;
	}

	/// <summary>
	/// 向插件页面回推调用结果
	/// </summary>
	public void PostResult(long id, object? value, string? error)
	{
		string envelope = JsonSerializer.Serialize(error is null
			? new PluginBridgeResult { Kind = "resolve", Id = id, Value = value }
			: new PluginBridgeResult { Kind = "reject", Id = id, Error = error }, PluginRuntimeJson.Options);
		Dispatch(envelope);
	}

	/// <summary>
	/// 将 JSON 信封发送至插件 Web 视图的 __noriPlugin 命名空间
	///
	/// 未 ready 时排队, 窗口销毁后丢弃, 派发任务由 dispatcher 统一观察。
	/// </summary>
	private void Dispatch(string envelopeJson)
	{
		string script = $"window.__noriPlugin&&window.__noriPlugin.dispatch({JsonSerializer.Serialize(envelopeJson, PluginRuntimeJson.Options)})";
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
		// 每个插件使用独立子目录隔离 WebView 存储
		wv2.UserDataFolder = Path.Combine(_webViewDataRoot, PluginId);
	}

	private void OnWebMessageReceived(object? sender, WebMessageReceivedEventArgs e)
	{
		if (e.Body is not { Length: > 0 } body) return;
		Bridge.Handle(this, body);
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
	}

	private void OnPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs args)
	{
		if (args.Property == Visual.IsVisibleProperty)
			Volatile.Write(ref _visible, base.IsVisible ? 1 : 0);
	}

	private void OnClosed(object? sender, EventArgs e)
	{
		Closed -= OnClosed;
		PropertyChanged -= OnPropertyChanged;
		_scriptDispatcher.Close();
		Volatile.Write(ref _visible, 0);
		_revocationRegistration.Dispose();
		WindowClosed?.Invoke(this);
	}

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
