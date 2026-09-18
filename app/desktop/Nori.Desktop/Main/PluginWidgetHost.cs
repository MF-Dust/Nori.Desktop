using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using Nori.Core.Logging;
using Nori.Core.WebView;
using Nori.Desktop.Bridge;
using Nori.Desktop.Chat;
using Nori.PluginRuntime;

namespace Nori.Desktop.Main;

/// <summary>原生卡片入口；仅用户展开时创建受限 HTML 兼容视图，不随首页轮询重建。</summary>
internal sealed class PluginWidgetHost : StackPanel
{
	private readonly AppServices _services;
	private readonly Dictionary<string, WidgetCard> _cards = new(StringComparer.Ordinal);
	private readonly TextBlock _status = new() {Foreground = ChatPalette.Muted, FontSize = 12, IsVisible = false};
	private PluginRuntimeHost? _runtime;
	private Window? _owner;
	private bool _attached;
	private bool _reading;
	private bool _english;
	private int _revision;

	public PluginWidgetHost(AppServices services)
	{
		_services = services;
		Spacing = 10;
		Children.Add(_status);
		AttachedToVisualTree += (_, _) =>
		{
			_attached = true;
			_owner = TopLevel.GetTopLevel(this) as Window;
			if (_owner is not null) _owner.PropertyChanged += OnOwnerChanged;
			Refresh(_english);
		};
		DetachedFromVisualTree += (_, _) =>
		{
			_attached = false;
			_revision++;
			if (_owner is not null) _owner.PropertyChanged -= OnOwnerChanged;
			_owner = null;
			if (_runtime is not null) _runtime.ActivePluginsChanged -= OnPluginsChanged;
			_runtime = null;
			ClearCards();
		};
	}

	private void OnOwnerChanged(object? sender, AvaloniaPropertyChangedEventArgs args)
	{
		if (args.Property == IsVisibleProperty && _owner is {IsVisible: false})
			foreach (WidgetCard card in _cards.Values) card.IsExpanded = false;
	}

	public void Refresh(bool english)
	{
		_english = english;
		_status.Text = english ? "Plugin cards could not be loaded." : "插件卡片暂时无法加载。";
		foreach (WidgetCard card in _cards.Values) card.SetLanguage(english);
		if (!_attached || _services.SafeMode) return;
		if (!ReferenceEquals(_runtime, _services.PluginRuntime))
		{
			if (_runtime is not null) _runtime.ActivePluginsChanged -= OnPluginsChanged;
			_runtime = _services.PluginRuntime;
			if (_runtime is not null) _runtime.ActivePluginsChanged += OnPluginsChanged;
			_revision++;
			ClearCards();
		}
		if (!_reading) _ = RefreshAsync();
	}

	private void OnPluginsChanged() => Dispatcher.UIThread.Post(() =>
	{
		// 停用后撤销旧页面；同 ID 再激活也不能继承旧页面的未完成请求。
		_revision++;
		ClearCards();
		Refresh(_english);
	});

	private async Task RefreshAsync()
	{
		_reading = true;
		int revision = _revision;
		PluginRuntimeHost? runtime = _runtime;
		try
		{
			IReadOnlyList<PluginChatWidget> widgets = await Task.Run(() => runtime?.GetChatWidgets() ?? []);
			if (!_attached || revision != _revision) return;
			ApplyWidgets(widgets);
			_status.IsVisible = false;
		}
		catch (Exception failure)
		{
			if (_attached && revision == _revision) _status.IsVisible = true;
			_services.Logger.Write(LogSource.Backend, "warn", $"读取插件卡片失败：{failure.GetType().Name}");
		}
		finally
		{
			_reading = false;
			if (_attached && revision != _revision) Refresh(_english);
		}
	}

	internal void ApplyWidgets(IReadOnlyList<PluginChatWidget> widgets)
	{
		if (_services.SafeMode) { ClearCards(); return; }
		foreach (string id in _cards.Keys.Where(id => !widgets.Any(widget => widget.PluginId == id)).ToArray()) RemoveCard(id);
		foreach (PluginChatWidget widget in widgets)
		{
			if (_cards.TryGetValue(widget.PluginId, out WidgetCard? existing))
			{
				if (existing.Widget == widget) continue;
				RemoveCard(widget.PluginId);
			}
			WidgetCard card = new(widget, _services, _english);
			_cards.Add(widget.PluginId, card);
			Children.Add(card);
		}
	}

	private void RemoveCard(string id)
	{
		if (!_cards.Remove(id, out WidgetCard? card)) return;
		card.ClosePage();
		Children.Remove(card);
	}

	private void ClearCards()
	{
		foreach (string id in _cards.Keys.ToArray()) RemoveCard(id);
	}

	/// <summary>折叠、隐藏或撤销时释放页面；每次展开重新绑定插件身份和独立取消令牌。</summary>
	private sealed class WidgetCard : Expander
	{
		private readonly AppServices _services;
		private readonly DispatcherTimer _timeout = new() {Interval = TimeSpan.FromSeconds(15)};
		private readonly TextBlock _error = new() {Foreground = ChatPalette.Muted, FontSize = 12, TextWrapping = TextWrapping.Wrap};
		private readonly Button _retry = new() {HorizontalAlignment = HorizontalAlignment.Left};
		private readonly StackPanel _fallback;
		private NativeWebView? _webView;
		private WebViewScriptDispatcher? _scripts;
		private CancellationTokenSource? _lifetime;
		private int _pending;

		public PluginChatWidget Widget { get; }

		public WidgetCard(PluginChatWidget widget, AppServices services, bool english)
		{
			Widget = widget;
			_services = services;
			Header = new TextBlock {Text = widget.Title, FontSize = 13, Foreground = ChatPalette.Primary};
			HorizontalAlignment = HorizontalAlignment.Stretch;
			HorizontalContentAlignment = HorizontalAlignment.Stretch;
			Background = ChatPalette.Deep;
			_fallback = new StackPanel {Spacing = 8, Margin = new Thickness(12), Children = {_error, _retry}};
			SetLanguage(english);
			_retry.Click += (_, _) => OpenPage();
			_timeout.Tick += (_, _) => Fail(new TimeoutException("插件卡片加载超时"));
			PropertyChanged += (_, args) =>
			{
				if (args.Property != IsExpandedProperty) return;
				if (IsExpanded) OpenPage();
				else ClosePage();
			};
		}

		private void OpenPage()
		{
			ClosePage();
			try
			{
				string document = PluginWidgetBridge.CreateDocument(Widget);
				PluginRuntimeHost runtime = _services.PluginRuntime ?? throw new InvalidOperationException("插件运行时尚未就绪");
				PluginWidgetBridge bridge = new(Widget.PluginId, runtime.InvokePluginActionAsync);
				_lifetime = CancellationTokenSource.CreateLinkedTokenSource(_services.ShutdownToken);
				CancellationToken token = _lifetime.Token;
				NativeWebView webView = new() {Height = 220, Background = Brushes.Transparent};
				_webView = webView;
				WebViewScriptDispatcher scripts = new(script => webView.InvokeScript(script));
				_scripts = scripts;
				webView.EnvironmentRequested += (_, args) =>
				{
					if (args is WindowsWebView2EnvironmentRequestedEventArgs windows)
						windows.UserDataFolder = Path.Combine(_services.Paths.PluginsWebViewCacheDirectory, Widget.PluginId);
				};
				webView.NewWindowRequested += (_, args) => args.Handled = true;
				webView.AdapterCreated += (_, _) =>
				{
					if (token.IsCancellationRequested) return;
					try { webView.NavigateToString(document); }
					catch (Exception failure) { Fail(failure); }
				};
				webView.NavigationCompleted += (_, args) =>
				{
					if (token.IsCancellationRequested) return;
					if (args.IsSuccess) scripts.MarkReady();
					else Fail(new InvalidOperationException("插件卡片导航失败"));
				};
				webView.WebMessageReceived += (_, args) =>
				{
					if (token.IsCancellationRequested) return;
					if (args.Body == "widget-loaded") { _timeout.Stop(); return; }
					if (args.Body is not {Length: > 0 and <= 65_536} raw) return;
					if (Interlocked.Increment(ref _pending) > 8) { Interlocked.Decrement(ref _pending); return; }
					_ = Task.Run(() => InvokeAsync(bridge, scripts, raw, token));
				};
				_timeout.Start();
				Content = webView;
			}
			catch (Exception failure) { Fail(failure); }
		}

		public void SetLanguage(bool english)
		{
			_error.Text = english ? "This plugin card is unavailable. Home is still usable." : "此插件卡片暂时不可用，不影响主页其他功能。";
			_retry.Content = english ? "Retry" : "重试";
		}

		private async Task InvokeAsync(PluginWidgetBridge bridge, WebViewScriptDispatcher scripts, string raw, CancellationToken token)
		{
			try
			{
				token.ThrowIfCancellationRequested();
				using CancellationTokenSource request = CancellationTokenSource.CreateLinkedTokenSource(token);
				request.CancelAfter(TimeSpan.FromSeconds(15));
				string? reply = await bridge.InvokeAsync(raw, request.Token).WaitAsync(request.Token).ConfigureAwait(false);
				if (reply is null) return;
				Dispatcher.UIThread.Post(() =>
				{
					if (!token.IsCancellationRequested)
						scripts.Dispatch($"window.__noriWidgetReply&&window.__noriWidgetReply({JsonSerializer.Serialize(reply)})");
				});
			}
			catch (OperationCanceledException) { }
			catch (Exception failure)
			{
				_services.Logger.Write(LogSource.Backend, "warn", $"插件卡片动作失败 [{Widget.PluginId}]：{failure.GetType().Name}");
			}
			finally { Interlocked.Decrement(ref _pending); }
		}

		private void Fail(Exception failure)
		{
			ClosePage();
			_services.Logger.Write(LogSource.Backend, "warn", $"插件卡片加载失败 [{Widget.PluginId}]：{failure.GetType().Name}");
			Content = _fallback;
		}

		public void ClosePage()
		{
			_timeout.Stop();
			_lifetime?.Cancel();
			_scripts?.Close();
			try { _webView?.Stop(); }
			catch (Exception) { /* 原生适配器初始化失败时仍需撤销卡片通道。 */ }
			Content = null;
			_webView = null;
			_scripts = null;
			_lifetime?.Dispose();
			_lifetime = null;
		}
	}
}
