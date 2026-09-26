using Nori.Desktop.Appearance;
using System.Text.Json;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Threading;
using Nori.Core.Configuration;
using Nori.Desktop.Chat;

namespace Nori.Desktop.QuickChat;

/// <summary>贴近桌宠显示的原生轻量对话界面；完整历史与复杂操作交给独立聊天窗口。</summary>
public sealed class QuickChatView : UserControl, IDisposable
{
	private static readonly FontFamily ConversationFont = NoriTypography.Conversation;
	private static readonly SplineEasing ComposerEase = new() { X1 = 0.25, Y1 = 0.1, X2 = 0.25, Y2 = 1 };
	private readonly NativeChatService _service;
	private readonly Func<string, object?, CancellationToken, Task<JsonElement>> _execute;
	private readonly Func<DateTimeOffset> _now;
	private readonly Func<bool> _reduceMotion;
	private readonly CancellationTokenSource _lifetime = new();
	private readonly HashSet<Task> _operations = [];
	private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromMilliseconds(16) };
	private readonly QuickChatState _state = new();
	private readonly StackPanel _bubblePanel = new() { Name = "QuickChatBubbles", Spacing = 8, Margin = new Thickness(0, 0, 0, 12) };
	private readonly Grid _content = new() { RowDefinitions = new RowDefinitions("*,Auto,Auto,Auto,Auto"), HorizontalAlignment = HorizontalAlignment.Stretch };
	private readonly ScrollViewer _bubbleScroll = new()
	{
		Name = "QuickChatBubbleScroll",
		HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
		VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
	};
	private readonly QuickChatComposer _composer = new()
	{
		Name = "QuickChatComposer",
		AcceptsReturn = false,
		TextWrapping = TextWrapping.NoWrap,
		HorizontalAlignment = HorizontalAlignment.Stretch,
	};
	private readonly TextBlock _placeholder = new()
	{
		Name = "QuickChatPlaceholder",
		FontSize = 14,
		FontWeight = FontWeight.Medium,
		FontFamily = ConversationFont,
		VerticalAlignment = VerticalAlignment.Center,
		IsHitTestVisible = false,
		TextTrimming = TextTrimming.CharacterEllipsis,
	};
	private readonly TextBlock _shortcutText = new()
	{
		FontSize = 12,
		FontWeight = FontWeight.Medium,
		FontFamily = ConversationFont,
		LineHeight = 16,
		VerticalAlignment = VerticalAlignment.Center,
	};
	private readonly Border _shortcut = new()
	{
		Name = "QuickChatShortcutHint",
		Margin = new Thickness(8, 0, 0, 0),
		Opacity = 0.7,
		VerticalAlignment = VerticalAlignment.Center,
		IsHitTestVisible = false,
	};
	private readonly StackPanel _placeholderRow = new()
	{
		Orientation = Orientation.Horizontal,
		VerticalAlignment = VerticalAlignment.Center,
		IsHitTestVisible = false,
	};
	private readonly TextBlock _activity = Text("", 12, FontWeight.Medium, QuickChatPalette.MintLight);
	private readonly Button _send = new()
	{
		Name = "QuickChatSend",
		HorizontalAlignment = HorizontalAlignment.Right,
		VerticalAlignment = VerticalAlignment.Center,
		RenderTransformOrigin = RelativePoint.Center,
	};
	private readonly Border _sendGlow = new()
	{
		Name = "QuickChatSendGlow",
		Width = 28,
		Height = 28,
		CornerRadius = new CornerRadius(8),
		HorizontalAlignment = HorizontalAlignment.Right,
		VerticalAlignment = VerticalAlignment.Center,
	};
	private readonly Border _composerShell = new()
	{
		Name = "QuickChatComposeBar",
		MinWidth = 200,
		MaxWidth = 280,
		Height = 46,
		Padding = new Thickness(0),
		CornerRadius = new CornerRadius(12),
		BorderThickness = new Thickness(1),
		HorizontalAlignment = HorizontalAlignment.Stretch,
		RenderTransformOrigin = RelativePoint.Center,
	};
	private readonly ExperimentalAcrylicMaterial _composerMaterial = new()
	{
		BackgroundSource = AcrylicBackgroundSource.Digger,
		TintColor = QuickChatPalette.Tint("chat-composer", 255),
		TintOpacity = 0.9,
		MaterialOpacity = 1,
		FallbackColor = QuickChatPalette.Tint("chat-composer", 255),
	};
	private readonly ExperimentalAcrylicBorder _composerFrost = new()
	{
		Name = "QuickChatComposerFrost",
		CornerRadius = new CornerRadius(11),
		Padding = new Thickness(0),
		ClipToBounds = true,
	};
	private readonly Border _composerTint = new()
	{
		Name = "QuickChatComposerTint",
		CornerRadius = new CornerRadius(11),
		IsHitTestVisible = false,
	};
	private readonly Border _error = new()
	{
		Name = "QuickChatError",
		Padding = new Thickness(12, 8),
		CornerRadius = new CornerRadius(12),
		Background = QuickChatPalette.Error,
		Margin = new Thickness(0, 0, 0, 8),
		IsVisible = false,
	};
	private readonly Border _approval = new()
	{
		Name = "QuickChatApproval",
		Padding = new Thickness(12, 10),
		CornerRadius = new CornerRadius(12),
		Background = QuickChatPalette.Error,
		BorderBrush = QuickChatPalette.PlayerBorder,
		BorderThickness = new Thickness(1),
		Margin = new Thickness(0, 0, 0, 8),
		IsVisible = false,
	};
	private readonly TextBlock _approvalTitle = Text("", 12, FontWeight.SemiBold, QuickChatPalette.PlayerText);
	private readonly TextBlock _approvalDescription = Text("", 12, FontWeight.Normal, QuickChatPalette.PlayerText);
	private readonly TextBlock _approvalCountdown = Text("", 12, FontWeight.Normal, QuickChatPalette.Placeholder);
	private readonly TextBlock _approvalArguments = Text("", 12, FontWeight.Normal, QuickChatPalette.PlayerText);
	private readonly Button _deny = ApprovalButton("QuickChatApprovalDeny", "deny");
	private readonly Button _allow = ApprovalButton("QuickChatApprovalAllow", "allow");
	private readonly Button _details = ApprovalButton("QuickChatApprovalDetails");
	private readonly Button _openChat = ApprovalButton("QuickChatApprovalOpenChat");
	private readonly Avalonia.Controls.Shapes.Path _sendIcon = new()
	{
		Data = Geometry.Parse("M8 12.6667 L8 3.3333 M3.3333 8 L8 3.3333 12.6667 8"),
		StrokeThickness = 1.6667,
		StrokeLineCap = PenLineCap.Round,
		StrokeJoin = PenLineJoin.Round,
		Width = 16,
		Height = 16,
		Stretch = Stretch.None,
		HorizontalAlignment = HorizontalAlignment.Center,
		VerticalAlignment = VerticalAlignment.Center,
	};
	private readonly Dictionary<string, BubbleVisual> _bubbleViews = new(StringComparer.Ordinal);
	private readonly ScaleTransform _composerScale = new(1, 1);
	private readonly ScaleTransform _sendScale = new(1, 1);
	private bool _hostVisible;
	private bool _loadedHistory;
	private bool _refreshQueued;
	private bool _renderingDraft;
	private bool _preparing;
	private bool _approvalBusy;
	private bool _disposed;
	private long _snapshotRequest;
	private Task? _startOperation;
	private bool _argumentsVisible;
	private DateTimeOffset _focusStarted;
	private double _focusFrom = 1;
	private double _focusTo = 1;
	private bool _focusAnimating;
	private DateTimeOffset _sendMotionStarted;
	private double _sendScaleFrom = 1;
	private double _sendScaleTo = 1;
	private bool _sendAnimating;
	private bool? _motionReduced;

	/// <summary>建立 QuickChat 与专用原生聊天来源之间的连接。</summary>
	public QuickChatView(NativeChatService service, Func<bool>? reduceMotion = null)
		: this(service, service.ExecuteAsync, () => DateTimeOffset.UtcNow, reduceMotion ?? (() => false)) { }

	internal QuickChatView(
		NativeChatService service,
		Func<string, object?, CancellationToken, Task<JsonElement>> execute,
		Func<DateTimeOffset> now,
		Func<bool> reduceMotion)
	{
		_service = service ?? throw new ArgumentNullException(nameof(service));
		_execute = execute ?? throw new ArgumentNullException(nameof(execute));
		_now = now ?? throw new ArgumentNullException(nameof(now));
		_reduceMotion = reduceMotion ?? throw new ArgumentNullException(nameof(reduceMotion));
		MinWidth = 200;
		MaxWidth = 280;
		// 聚焦缩放和多层阴影需要越过内容边界，窗口外层已预留安全边距。
		ClipToBounds = false;
		FontFamily = ConversationFont;
		FontSize = 14;
		Styles.Add(new StyleInclude(new Uri("avares://Nori.Desktop/")) { Source = new Uri("avares://Nori.Desktop/QuickChat/QuickChatTheme.axaml") });
		BuildContent();
		_state.Changed += QueueRender;
		_service.EventReceived += OnHostEvent;
		_service.StateChanged += OnHostStateChanged;
		_composer.PropertyChanged += OnComposerPropertyChanged;
		_composer.SendRequested += OnSendRequested;
		_composer.EscapeRequested += ClearComposerFocus;
		_composer.GotFocus += (_, _) => SetComposerFocus(true);
		_composer.LostFocus += (_, _) => SetComposerFocus(false);
		_send.Click += (_, _) => Run(SendAsync());
		_send.PointerEntered += (_, _) => { if (_send.IsEnabled) SetSendScale(1.05); };
		_send.PointerExited += (_, _) => SetSendScale(1);
		_send.PointerPressed += (_, _) => { if (_send.IsEnabled) SetSendScale(0.95); };
		_send.PointerReleased += (_, _) => SetSendScale(_send.IsEnabled && _send.IsPointerOver ? 1.05 : 1);
		_deny.Click += (_, _) => Run(DecideApprovalAsync(false));
		_allow.Click += (_, _) => Run(DecideApprovalAsync(true));
		_details.Click += (_, _) => { _argumentsVisible = !_argumentsVisible; RenderApproval(); DesiredLayoutChanged?.Invoke(); };
		_openChat.Click += (_, _) => _service.OpenChat();
		_clock.Tick += (_, _) => OnClock();
		SizeChanged += (_, _) => ResizeBubbles();
		Render();
	}

	internal QuickChatState State => _state;
	internal QuickChatComposer Composer => _composer;
	internal bool HistoryLoaded => _loadedHistory;
	internal IReadOnlyList<Control> InteractiveControls =>
		[_composerShell, .. _bubbleViews.Values.Select(item => (Control)item.Root), _approval, _error];
	internal event Action? DesiredLayoutChanged;

	/// <summary>把键盘焦点放入输入框。</summary>
	public void FocusComposer()
	{
		if (!_disposed) _composer.Focus();
	}

	/// <summary>同步宿主可见性；隐藏不会丢弃草稿或取消正在生成的回复。</summary>
	public void SetHostVisible(bool visible)
	{
		if (_disposed) return;
		_hostVisible = visible;
		if (visible)
		{
			_clock.Start();
			_state.Tick(_now());
			QueueRefresh();
			Render();
		}
		else _clock.Stop();
	}

	/// <summary>刷新语言与聊天可用性；首批历史只登记为已见，不弹出旧气泡。</summary>
	public async Task RefreshAsync()
	{
		if (_disposed || _preparing) return;
		long request = ++_snapshotRequest;
		try
		{
			JsonElement snapshot = await _service.GetSnapshotAsync(_lifetime.Token).ConfigureAwait(true);
			if (_disposed || request != _snapshotRequest) return;
			_state.ApplySnapshot(snapshot);
			if (!_loadedHistory && !_state.Chat.LoadingHistory && !_state.Chat.Sending)
			{
				await LoadInitialHistoryAsync().ConfigureAwait(true);
			}
		}
		catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
		catch (Exception exception)
		{
			if (!_disposed && request == _snapshotRequest) _state.Chat.SetError(exception.Message);
		}
		Render();
	}

	internal void ApplySnapshot(JsonElement snapshot)
	{
		_state.ApplySnapshot(snapshot);
		Render();
	}

	internal Task SendAsync(string? text = null)
	{
		if (!_state.Configured || !_loadedHistory || _state.SafeMode || _preparing || _approval.IsVisible) return Task.CompletedTask;
		text ??= _state.Draft;
		DateTimeOffset now = _now();
		if (!_state.BeginSend(text, now)) return Task.CompletedTask;
		Render();
		Task operation = StartCoreAsync(QuickChatState.LimitCodePoints(text, QuickChatState.MaximumCodePoints).Trim());
		_startOperation = operation.IsCompleted ? null : operation;
		return operation;
	}

	private async Task StartCoreAsync(string text)
	{
		try
		{
			JsonElement result = await ExecuteAsync("chat_start", new { text }, _lifetime.Token).ConfigureAwait(true);
			_state.AcceptSession(result.ValueKind == JsonValueKind.String ? result.GetString() ?? "" : NativeChatJson.S(result, "sessionId"), _now());
		}
		catch (Exception exception) { _state.StartFailed(exception, _now()); }
		finally { _startOperation = null; Render(); }
	}

	private async Task LoadInitialHistoryAsync()
	{
		var ticket = _state.Chat.BeginHistory();
		try
		{
			JsonElement page = await ExecuteAsync("chat_history_page", new { limit = 50, beforeId = 0 }, _lifetime.Token).ConfigureAwait(true);
			if (_state.AcceptInitialHistory(ticket, page, 50)) _loadedHistory = true;
		}
		finally
		{
			_state.Chat.EndHistory(ticket);
			Render();
		}
	}

	private async Task DecideApprovalAsync(bool approved)
	{
		NativeChatApproval? approval = _state.Chat.Approvals.FirstOrDefault();
		if (approval is null || _approvalBusy || _preparing || (approved && approval.RemainingSeconds(_now()) <= 0)) return;
		_approvalBusy = true;
		RenderApproval();
		try
		{
			await ExecuteAsync("approval_respond", new { requestId = approval.RequestId, approved }, _lifetime.Token).ConfigureAwait(true);
			_state.Chat.ResolveApproval(approval.RequestId);
		}
		finally
		{
			_approvalBusy = false;
			Render();
		}
	}

	/// <summary>退出前拒绝待决授权并取消本来源的活动会话，随后解除视图订阅。</summary>
	public async Task PrepareShutdownAsync()
	{
		if (_disposed || _preparing) return;
		_preparing = true;
		Render();
		try
		{
			if (_startOperation is not null) await _startOperation.ConfigureAwait(true);
			foreach (NativeChatApproval approval in _state.Chat.Approvals.ToArray())
			{
				await ExecuteAsync("approval_respond", new { requestId = approval.RequestId, approved = false }).ConfigureAwait(true);
				_state.Chat.ResolveApproval(approval.RequestId);
			}
			if (_state.Chat.SessionId is { } sessionId) await ExecuteAsync("chat_cancel", new { sessionId }).ConfigureAwait(true);
			while (_operations.Count > 0) await Task.WhenAll(_operations.ToArray()).ConfigureAwait(true);
		}
		finally { Dispose(); }
	}

	private void BuildContent()
	{
		var errorText = Text("", 12, FontWeight.Medium, QuickChatPalette.PlayerText);
		errorText.Name = "QuickChatErrorText";
		_error.Child = errorText;

		var approvalActions = new WrapPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
		foreach (Button button in new[] { _details, _openChat, _deny, _allow }) button.Margin = new Thickness(3, 3, 0, 0);
		approvalActions.Children.Add(_details);
		approvalActions.Children.Add(_openChat);
		approvalActions.Children.Add(_deny);
		approvalActions.Children.Add(_allow);
		var argumentScroll = new ScrollViewer
		{
			Name = "QuickChatApprovalArguments",
			MaxHeight = 80,
			HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
			VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
			Content = _approvalArguments,
			IsVisible = false,
		};
		var approvalBody = new StackPanel { Spacing = 4 };
		approvalBody.Children.Add(_approvalTitle);
		approvalBody.Children.Add(_approvalDescription);
		approvalBody.Children.Add(_approvalCountdown);
		approvalBody.Children.Add(argumentScroll);
		approvalBody.Children.Add(approvalActions);
		_approval.Child = approvalBody;

		var inputLayer = new Grid { ColumnDefinitions = new ColumnDefinitions("*,28"), ColumnSpacing = 8, Margin = new Thickness(12, 8) };
		var textLayer = new Grid { ClipToBounds = true };
		_placeholderRow.Children.Add(_placeholder);
		var shortcutKeys = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
		shortcutKeys.Children.Add(KeyCap(_shortcutText));
		shortcutKeys.Children.Add(Text("+", 12, FontWeight.Medium, QuickChatPalette.Mint));
		TextBlock k = Text("K", 12, FontWeight.Medium, QuickChatPalette.Mint);
		k.LineHeight = 16;
		shortcutKeys.Children.Add(KeyCap(k));
		_shortcut.Child = shortcutKeys;
		_placeholderRow.Children.Add(_shortcut);
		textLayer.Children.Add(_placeholderRow);
		textLayer.Children.Add(_composer);
		inputLayer.Children.Add(textLayer);
		_send.Classes.Add("quick-send");
		_send.Content = _sendIcon;
		_send.RenderTransform = _sendScale;
		Grid.SetColumn(_send, 1);
		_sendGlow.Child = _send;
		Grid.SetColumn(_sendGlow, 1);
		inputLayer.Children.Add(_sendGlow);
		_composer.Classes.Add("quick-composer");
		// Devolutions 通过独立 AdornerLayer 绘制焦点环；只禁用该环，保留输入框自己的薄荷高亮。
		_composer.Classes.Add("no-focus-border");
		_composer.FontFamily = ConversationFont;
		_composer.LineHeight = 20;
		var frostContent = new Grid();
		frostContent.Children.Add(_composerTint);
		frostContent.Children.Add(inputLayer);
		_composerFrost.Material = _composerMaterial;
		_composerFrost.Child = frostContent;
		_composerShell.Child = _composerFrost;
		_composerShell.RenderTransform = _composerScale;

		_bubbleScroll.Content = _bubblePanel;
		_content.Children.Add(_bubbleScroll);
		Grid.SetRow(_error, 1); _content.Children.Add(_error);
		Grid.SetRow(_approval, 2); _content.Children.Add(_approval);
		_activity.Margin = new Thickness(4, 0, 4, 6);
		_activity.IsVisible = false;
		Grid.SetRow(_activity, 3); _content.Children.Add(_activity);
		Grid.SetRow(_composerShell, 4); _content.Children.Add(_composerShell);
		Content = _content;
		AutomationProperties.SetLiveSetting(_bubblePanel, AutomationLiveSetting.Polite);
		AutomationProperties.SetLiveSetting(_activity, AutomationLiveSetting.Polite);
	}

	private void Render()
	{
		if (_disposed) return;
		if (_composer.Text != _state.Draft)
		{
			_renderingDraft = true;
			_composer.Text = _state.Draft;
			_composer.CaretIndex = _state.Draft.Length;
			_renderingDraft = false;
		}
		RenderBubbles();
		RenderError();
		RenderApproval();
		RenderComposer();
		DesiredLayoutChanged?.Invoke();
	}

	private void RenderBubbles()
	{
		HashSet<string> present = _state.Bubbles.Select(item => item.Key).ToHashSet(StringComparer.Ordinal);
		foreach (string key in _bubbleViews.Keys.Where(key => !present.Contains(key)).ToArray())
		{
			BubbleVisual visual = _bubbleViews[key];
			visual.Dispose();
			_bubbleViews.Remove(key);
			_bubblePanel.Children.Remove(visual.Root);
		}
		foreach (QuickChatBubble bubble in _state.Bubbles)
		{
			if (!_bubbleViews.TryGetValue(bubble.Key, out BubbleVisual? visual))
			{
				visual = new BubbleVisual(this, bubble);
				_bubbleViews.Add(bubble.Key, visual);
				_bubblePanel.Children.Add(visual.Root);
				_state.MarkEntered(bubble.Key);
			}
			visual.Render();
		}
		_bubblePanel.IsVisible = _bubbleViews.Count > 0;
		ResizeBubbles();
	}

	private void ResizeBubbles()
	{
		double available = Bounds.Width > 0 ? Bounds.Width : 280;
		foreach (BubbleVisual visual in _bubbleViews.Values) visual.Root.MaxWidth = Math.Max(80, available * 0.85);
	}

	private void RenderError()
	{
		string error = _state.Chat.Error;
		_error.IsVisible = error.Length > 0;
		if (_error.Child is TextBlock text) text.Text = T("操作失败：", "Something went wrong: ") + error;
		AutomationProperties.SetName(_error, error);
	}

	private void RenderApproval()
	{
		NativeChatApproval? approval = _state.Chat.Approvals.FirstOrDefault();
		_approval.IsVisible = approval is not null;
		if (approval is null) return;
		int seconds = approval.RemainingSeconds(_now());
		_approvalTitle.Text = T("需要允许工具操作", "Tool approval needed") + $" · {approval.ToolName}";
		_approvalDescription.Text = approval.Description.Length > 0 ? approval.Description : T("Nori 想执行一项工具操作。", "Nori wants to run a tool action.");
		_approvalCountdown.Text = seconds > 0 ? T($"{seconds} 秒后超时", $"Times out in {seconds}s") : T("已超时，等待宿主确认拒绝", "Expired; waiting for the host to deny");
		string arguments = approval.Arguments.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
			? "{}"
			: JsonSerializer.Serialize(approval.Arguments, new JsonSerializerOptions { WriteIndented = true });
		_approvalArguments.Text = arguments;
		if (_approvalArguments.Parent is ScrollViewer argumentScroll) argumentScroll.IsVisible = _argumentsVisible;
		_deny.Content = T("拒绝", "Deny");
		_allow.Content = T("允许", "Allow");
		_details.Content = _argumentsVisible ? T("收起", "Hide") : T("详情", "Details");
		_openChat.Content = T("完整对话", "Full chat");
		_deny.IsEnabled = !_approvalBusy && !_preparing;
		_allow.IsEnabled = !_approvalBusy && !_preparing && seconds > 0;
		_details.IsEnabled = !_preparing;
		_openChat.IsEnabled = !_preparing;
		AutomationProperties.SetName(_approval, _approvalTitle.Text);
	}

	private void RenderComposer()
	{
		UpdateMotionPreference();
		bool focused = _composer.IsKeyboardFocusWithin;
		bool filled = !string.IsNullOrWhiteSpace(_state.Draft);
		_placeholder.IsVisible = _state.Draft.Length == 0;
		_placeholder.Foreground = focused ? QuickChatPalette.FocusedPlaceholder : QuickChatPalette.Placeholder;
		_composerShell.Background = Brushes.Transparent;
		_composerTint.Background = focused ? FocusedComposerGradient() : ComposerGradient();
		_composerShell.BorderBrush = focused ? QuickChatPalette.ComposerFocusedBorder : QuickChatPalette.ComposerBorder;
		_composerShell.BoxShadow = ComposerShadows(focused);
		_composerMaterial.TintColor = focused ? QuickChatPalette.Tint("chat-ai-bg-end", 255) : QuickChatPalette.Tint("chat-composer", 255);
		_composerMaterial.TintOpacity = focused ? 0.18 : 0.9;
		_composerMaterial.MaterialOpacity = focused ? 0.15 : 1;
		_composerMaterial.FallbackColor = focused ? QuickChatPalette.Tint("chat-white", 242) : QuickChatPalette.Tint("chat-composer", 217);
		_send.Classes.Set("filled", filled);
		_sendGlow.BoxShadow = filled ? Shadows(ShadowValue(0, 2, 8, QuickChatPalette.Tint("chat-ai-bg-end", 128))) : default;
		_sendIcon.Stroke = filled ? QuickChatPalette.Dark : focused ? QuickChatPalette.DisabledArrow : QuickChatPalette.Placeholder;
		_send.IsEnabled = filled && _state.Configured && _loadedHistory && !_state.SafeMode && !_state.Chat.Sending && !_preparing && !_approval.IsVisible;
		if (!_send.IsEnabled && _sendScaleTo != 1) SetSendScale(1);
		_composer.IsReadOnly = _preparing;
		bool shortcutAvailable = _state.Configured && _loadedHistory && !_state.SafeMode && !_preparing;
		_shortcut.IsVisible = _state.Draft.Length == 0 && !focused && shortcutAvailable;
		_shortcutText.Text = OperatingSystem.IsMacOS() ? "⌘" : "Ctrl";
		_shortcutText.Foreground = QuickChatPalette.Mint;
		_placeholder.Text = _state.SafeMode
			? T("安全模式已暂停对话", "Chat is paused in safe mode")
			: !_state.Configured
				? T("请先配置 AI", "Configure AI first")
				: !_loadedHistory ? T("正在准备对话...", "Preparing chat...") : T("和 Nori 聊天...", "Talk to Nori...");
		_activity.Text = _state.Chat.Sending
			? _state.Chat.SessionId is null ? T("正在连接...", "Connecting...") : T("Nori 正在回复...", "Nori is replying...")
			: "";
		_activity.IsVisible = _activity.Text.Length > 0;
		AutomationProperties.SetName(_activity, _activity.Text);
		AutomationProperties.SetName(_composer, T("消息输入，回车发送，Escape 退出输入", "Message. Enter to send; Escape to leave the input"));
		AutomationProperties.SetName(_send, T("发送消息", "Send message"));
	}

	private void OnClock()
	{
		if (_disposed || !_hostVisible) return;
		DateTimeOffset now = _now();
		_state.Tick(now);
		UpdateMotion(now);
		RenderApproval();
	}

	private void UpdateMotion(DateTimeOffset now)
	{
		bool reduce = _reduceMotion();
		UpdateMotionPreference();
		foreach (BubbleVisual visual in _bubbleViews.Values) visual.UpdateMotion(now, reduce);
		if (_focusAnimating)
		{
			double progress = reduce ? 1 : Math.Clamp((now - _focusStarted).TotalMilliseconds / 200, 0, 1);
			double eased = ComposerEase.Ease(progress);
			double scale = _focusFrom + ((_focusTo - _focusFrom) * eased);
			_composerScale.ScaleX = scale;
			_composerScale.ScaleY = scale;
			if (progress >= 1) _focusAnimating = false;
		}
		UpdateSendMotion(now, reduce);
	}

	private void UpdateSendMotion(DateTimeOffset now, bool reduce)
	{
		if (!_sendAnimating) return;
		double progress = reduce ? 1 : Math.Clamp((now - _sendMotionStarted).TotalMilliseconds / 200, 0, 1);
		double scale = _sendScaleFrom + ((_sendScaleTo - _sendScaleFrom) * ComposerEase.Ease(progress));
		_sendScale.ScaleX = scale;
		_sendScale.ScaleY = scale;
		if (progress >= 1) _sendAnimating = false;
	}

	private void UpdateMotionPreference()
	{
		bool reduced = _reduceMotion();
		if (_motionReduced == reduced) return;
		_motionReduced = reduced;
		TimeSpan duration = TimeSpan.FromMilliseconds(200);
		_composerTint.Transitions = reduced ? null :
		[
			new BrushTransition { Property = Border.BackgroundProperty, Duration = duration, Easing = ComposerEase },
		];
		_composerShell.Transitions = reduced ? null :
		[
			new BrushTransition { Property = Border.BorderBrushProperty, Duration = duration, Easing = ComposerEase },
			new BoxShadowsTransition { Property = Border.BoxShadowProperty, Duration = duration, Easing = ComposerEase },
		];
	}

	private void SetSendScale(double scale)
	{
		if (_sendScaleTo == scale && (_sendAnimating || Math.Abs(_sendScale.ScaleX - scale) < 0.001)) return;
		_sendScaleFrom = _sendScale.ScaleX;
		_sendScaleTo = scale;
		_sendMotionStarted = _now();
		_sendAnimating = true;
		UpdateSendMotion(_sendMotionStarted, _reduceMotion());
	}

	private void SetComposerFocus(bool focused)
	{
		_focusFrom = _composerScale.ScaleX;
		_focusTo = focused ? 1.02 : 1;
		_focusStarted = _now();
		_focusAnimating = true;
		RenderComposer();
		UpdateMotion(_focusStarted);
	}

	private void ClearComposerFocus() => TopLevel.GetTopLevel(this)?.FocusManager?.Focus(null);

	private void OnComposerPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs args)
	{
		if (_disposed || _renderingDraft || args.Property != TextBox.TextProperty) return;
		string limited = QuickChatState.LimitCodePoints(_composer.Text, QuickChatState.MaximumCodePoints);
		_state.Draft = limited;
		if (_composer.Text != limited)
		{
			_renderingDraft = true;
			_composer.Text = limited;
			_composer.CaretIndex = limited.Length;
			_renderingDraft = false;
		}
		RenderComposer();
	}

	private void OnSendRequested() => Run(SendAsync());

	private void OnHostEvent(string name, JsonElement payload)
	{
		if (name != "nori:agent-event") return;
		JsonElement safePayload = payload.Clone();
		Dispatcher.UIThread.Post(() =>
		{
			if (_disposed) return;
			_state.ApplyEvent(safePayload, _now());
			Render();
		});
	}

	private void OnHostStateChanged() => Dispatcher.UIThread.Post(QueueRefresh);

	private void QueueRefresh()
	{
		if (_disposed || _preparing || !_hostVisible || _refreshQueued) return;
		_refreshQueued = true;
		Dispatcher.UIThread.Post(async () =>
		{
			_refreshQueued = false;
			await RefreshAsync();
		}, DispatcherPriority.Background);
	}

	private Task<JsonElement> ExecuteAsync(string command, object? args = null, CancellationToken cancellationToken = default) =>
		_execute(command, args, cancellationToken);

	[System.Diagnostics.CodeAnalysis.SuppressMessage("CodeQuality", "S3168", Justification = "UI 事件的受控 fire-and-forget 入口会统一收集异常。")]
	private async void Run(Task task)
	{
		_operations.Add(task);
		try { await task; }
		catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
		catch (Exception exception) { _state.Chat.SetError(exception.Message); }
		finally { _operations.Remove(task); Render(); }
	}

	private string T(string chinese, string english) => UiLanguage.IsEnglish(_state.Language) ? english : chinese;

	private static TextBlock Text(string value, double size, FontWeight weight, IBrush brush) => new()
	{
		Text = value,
		FontFamily = ConversationFont,
		FontSize = size,
		FontWeight = weight,
		Foreground = brush,
		TextWrapping = TextWrapping.Wrap,
	};

	private static Border KeyCap(TextBlock label) => new()
	{
		Padding = new Thickness(3, 0),
		CornerRadius = new CornerRadius(3),
		BorderThickness = new Thickness(1),
		BorderBrush = new SolidColorBrush(QuickChatPalette.Tint("chat-ai-bg", 64)),
		Background = new SolidColorBrush(QuickChatPalette.Tint("chat-ai-bg", 48)),
		VerticalAlignment = VerticalAlignment.Center,
		Child = label,
	};

	private static Button ApprovalButton(string name, string? style = null)
	{
		var button = new Button { Name = name };
		button.Classes.Add("quick-approval");
		if (style is not null) button.Classes.Add(style);
		return button;
	}

	private static IBrush ComposerGradient() => Gradient(QuickChatPalette.Tint("chat-composer", 204), QuickChatPalette.Tint("chat-composer", 217));
	private static IBrush FocusedComposerGradient() => Gradient(QuickChatPalette.Tint("chat-white", 240), QuickChatPalette.Tint("chat-ai-bg-end", 64));
	private static IBrush AgentGradient() => new LinearGradientBrush
	{
		StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
		EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
		GradientStops =
		{
			new GradientStop(QuickChatPalette.Tint("chat-ai-bg", 224), 0),
			new GradientStop(QuickChatPalette.Tint("chat-ai-bg-end", 232), 0.5),
			new GradientStop(QuickChatPalette.Tint("chat-ai-bg", 224), 1),
		},
	};
	private static IBrush PlayerGradient() => Gradient(QuickChatPalette.Tint("chat-user-bg", 240), QuickChatPalette.Tint("chat-user-bg-end", 245));
	private static IBrush Gradient(Color start, Color end) => new LinearGradientBrush
	{
		StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
		EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
		GradientStops = { new GradientStop(start, 0), new GradientStop(end, 1) },
	};
	private static BoxShadows AgentShadows() => Shadows(
		ShadowValue(0, 4, 24, QuickChatPalette.Tint("chat-ai-bg-end", 89)),
		ShadowValue(0, 2, 8, QuickChatPalette.Tint("scrim", 15)),
		ShadowValue(0, 1, 0, QuickChatPalette.Tint("chat-white", 144), inset: true),
		ShadowValue(0, -1, 0, QuickChatPalette.Tint("scrim", 5), inset: true));
	private static BoxShadows PlayerShadows() => Shadows(
		ShadowValue(0, 4, 16, QuickChatPalette.Tint("scrim", 68)),
		ShadowValue(0, 2, 6, QuickChatPalette.Tint("scrim", 31)),
		ShadowValue(0, 1, 0, QuickChatPalette.Tint("chat-white", 15), inset: true));
	private static BoxShadows ComposerShadows(bool focused) => focused
		? Shadows(
			ShadowValue(0, 8, 32, QuickChatPalette.Tint("chat-ai-bg-end", 64)),
			ShadowValue(0, 2, 8, QuickChatPalette.Tint("scrim", 20)),
			ShadowValue(0, 1, 0, QuickChatPalette.Tint("chat-white", 176), inset: true),
			ShadowValue(0, -1, 0, QuickChatPalette.Tint("scrim", 5), inset: true))
		: Shadows(
			ShadowValue(0, 4, 16, QuickChatPalette.Tint("scrim", 68)),
			ShadowValue(0, 2, 4, QuickChatPalette.Tint("scrim", 38)),
			ShadowValue(0, 1, 0, QuickChatPalette.Tint("chat-white", 15), inset: true),
			ShadowValue(0, -1, 0, QuickChatPalette.Tint("scrim", 26), inset: true));
	private static BoxShadows Shadows(BoxShadow first, params BoxShadow[] remaining) => new(first, remaining);
	private static BoxShadow ShadowValue(double x, double y, double blur, Color color, bool inset = false) => new()
	{
		OffsetX = x,
		OffsetY = y,
		Blur = blur,
		Color = color,
		IsInset = inset,
	};

	private static double Ease(double progress)
	{
		progress = Math.Clamp(progress, 0, 1);
		double low = 0, high = 1;
		for (int index = 0; index < 12; index++)
		{
			double t = (low + high) / 2;
			double x = Bezier(t, 0.32, 0);
			if (x < progress) low = t; else high = t;
		}
		return Bezier((low + high) / 2, 0.72, 1);
	}

	private static double Bezier(double t, double first, double second)
	{
		double inverse = 1 - t;
		return (3 * inverse * inverse * t * first) + (3 * inverse * t * t * second) + (t * t * t);
	}

	/// <summary>只解除视图订阅；拥有 NativeChatService 的窗口负责随后释放服务来源。</summary>
	public void Dispose()
	{
		if (_disposed) return;
		_disposed = true;
		_lifetime.Cancel();
		_clock.Stop();
		_state.Changed -= QueueRender;
		_service.EventReceived -= OnHostEvent;
		_service.StateChanged -= OnHostStateChanged;
		_composer.PropertyChanged -= OnComposerPropertyChanged;
		foreach (BubbleVisual visual in _bubbleViews.Values) visual.Dispose();
		_bubbleViews.Clear();
		_lifetime.Dispose();
	}

	private void QueueRender()
	{
		if (_disposed) return;
		if (Dispatcher.UIThread.CheckAccess()) Render();
		else Dispatcher.UIThread.Post(Render);
	}

	private sealed class BubbleVisual : IDisposable
	{
		private readonly QuickChatView _owner;
		private readonly QuickChatBubble _bubble;
		private readonly TextBlock _text;
		private readonly ScaleTransform _scale = new(1, 1);
		private readonly TranslateTransform _translate = new(0, 0);
		private readonly DateTimeOffset _enteredAt;
		private readonly TransformGroup _transforms = new();
		internal Border Root { get; }

		internal BubbleVisual(QuickChatView owner, QuickChatBubble bubble)
		{
			_owner = owner;
			_bubble = bubble;
			_enteredAt = owner._now();
			bool player = bubble.Message.Role == "user";
			_text = Text(bubble.Message.Content, 14, FontWeight.Medium, player ? QuickChatPalette.PlayerText : QuickChatPalette.Dark);
			_text.TextWrapping = TextWrapping.WrapWithOverflow;
			_text.LineHeight = 22.75;
			_text.Opacity = player ? 0.95 : 0.85;
			_transforms.Children.Add(_scale);
			_transforms.Children.Add(_translate);
			Root = new Border
			{
				Name = player ? "QuickChatPlayerBubble" : "QuickChatAgentBubble",
				Padding = new Thickness(16, 10),
				CornerRadius = player ? new CornerRadius(16, 16, 4, 16) : new CornerRadius(16, 16, 16, 4),
				Background = player ? PlayerGradient() : AgentGradient(),
				BorderBrush = player ? QuickChatPalette.PlayerBorder : QuickChatPalette.BubbleBorder,
				BorderThickness = new Thickness(1),
				BoxShadow = player ? PlayerShadows() : AgentShadows(),
				Child = _text,
				HorizontalAlignment = player ? HorizontalAlignment.Right : HorizontalAlignment.Left,
				RenderTransformOrigin = RelativePoint.Center,
				RenderTransform = _transforms,
			};
			AutomationProperties.SetName(Root, player ? owner.T("你说", "You said") : "Nori");
			bubble.Message.Changed += OnMessageChanged;
			UpdateMotion(_enteredAt, owner._reduceMotion());
		}

		internal void Render()
		{
			if (_text.Text != _bubble.Message.Content) _text.Text = _bubble.Message.Content;
		}

		internal void UpdateMotion(DateTimeOffset now, bool reduceMotion)
		{
			double progress;
			if (_bubble.ExitStartedAt is { } exitStarted)
			{
				progress = reduceMotion ? 1 : Math.Clamp((now - exitStarted).TotalMilliseconds / 350, 0, 1);
				double eased = Ease(progress);
				Root.Opacity = 1 - eased;
				_translate.Y = -60 * eased;
				_scale.ScaleX = _scale.ScaleY = 1 - (0.15 * eased);
				Root.IsHitTestVisible = false;
			}
			else
			{
				progress = reduceMotion ? 1 : Math.Clamp((now - _enteredAt).TotalMilliseconds / 400, 0, 1);
				double eased = Ease(progress);
				Root.Opacity = eased;
				_translate.Y = 40 * (1 - eased);
				_scale.ScaleX = _scale.ScaleY = 0.9 + (0.1 * eased);
			}
		}

		private void OnMessageChanged() => _owner.QueueRender();

		public void Dispose() => _bubble.Message.Changed -= OnMessageChanged;
	}
}
