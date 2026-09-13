using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;

namespace Nori.Desktop.Chat;

public sealed partial class ChatView
{
	private readonly Grid _root = new();
	private readonly Grid _main = new() { RowDefinitions = new RowDefinitions("Auto,*,Auto,Auto"), ClipToBounds = true };
	private readonly StackPanel _messageList = new() { Spacing = 10, Margin = new Thickness(20, 16) };
	private readonly ScrollViewer _scroll = new() { Name = "ChatHistory", HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
	private readonly ChatComposer _composer = new()
	{
		Name = "ChatComposer", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 40, MaxHeight = 120,
		Padding = new Thickness(12, 9), VerticalContentAlignment = VerticalAlignment.Center,
	};
	private readonly TextBlock _modelLabel = Text("", 11.5);
	private readonly TextBlock _usageLabel = Text("", 11.5);
	private readonly TextBlock _cacheLabel = Text("", 11.5);
	private readonly TextBlock _toolsLabel = Text("", 11.5);
	private readonly TextBlock _statusText = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, MaxHeight = 72 };
	private readonly TextBlock _toolStatus = Text("", 12);
	private readonly TextBlock _voiceTime = Text("", 11.5);
	private Border _statusBar = null!;
	private Control _empty = null!;
	private TextBlock _emptyTitle = null!;
	private TextBlock _emptyDescription = null!;
	private Control _prompts = null!;
	private Button _settings = null!;
	private Button _send = null!;
	private Button _stop = null!;
	private Button _microphone = null!;
	private Button _clear = null!;
	private Button _retry = null!;
	private Button _older = null!;
	private Button _latest = null!;

	private void InstallResources()
	{
		Resources["ChatDeepBrush"] = ChatPalette.Deep; Resources["ChatPanelBrush"] = ChatPalette.Panel;
		Resources["ChatPrimaryBrush"] = ChatPalette.Primary; Resources["ChatBodyBrush"] = ChatPalette.Body;
		Resources["ChatMutedBrush"] = ChatPalette.Muted; Resources["ChatLineBrush"] = ChatPalette.Line;
		Resources["ChatAccentBrush"] = ChatPalette.Accent; Resources["ChatOnTealBrush"] = ChatPalette.OnTeal;
		Resources["ChatDangerBrush"] = ChatPalette.Danger;
		Background = ChatPalette.Background; Foreground = ChatPalette.Primary;
		FontFamily = new FontFamily("Microsoft YaHei UI, PingFang SC, Noto Sans CJK SC, sans-serif"); FontSize = 13;
	}

	private void BuildShell()
	{
		var header = new WrapPanel { Name = "ChatHeader", Orientation = Orientation.Horizontal };
		var identity = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Margin = new Thickness(0, 2, 16, 4), VerticalAlignment = VerticalAlignment.Center };
		identity.Children.Add(Local(() => T("和 Nori 对话", "AI Companion Chat"), 14, true));
		_modelLabel.Foreground = ChatPalette.Accent; _modelLabel.MaxWidth = 190; _modelLabel.TextWrapping = TextWrapping.NoWrap; _modelLabel.TextTrimming = TextTrimming.CharacterEllipsis;
		identity.Children.Add(new Border { Background = ChatPalette.Overlay, BorderBrush = ChatPalette.Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(999), Padding = new Thickness(9, 4), Child = _modelLabel });
		header.Children.Add(identity);
		foreach (TextBlock label in new[] { _usageLabel, _cacheLabel, _toolsLabel })
		{
			label.Foreground = ChatPalette.Muted; label.Margin = new Thickness(0, 0, 16, 4); label.VerticalAlignment = VerticalAlignment.Center;
			header.Children.Add(label);
		}
		_clear = Button(() => T("清空记录", "Clear history"), () => { _confirmOpen = true; RenderDialog(); return Task.CompletedTask; }, "ChatClear", "trash");
		_clear.Margin = new Thickness(0, 0, 8, 4); header.Children.Add(_clear);
		var home = Button(() => T("主页 · 自动化与插件卡片", "Home · automation & plugin cards"), () => { _service.OpenMain(); return Task.CompletedTask; }, "ChatOpenMain", "main", iconOnly: true);
		header.Children.Add(home);
		var settings = Button(() => T("设置", "Settings"), () => { _service.OpenSettings(); return Task.CompletedTask; }, "ChatOpenSettings", "settings", iconOnly: true);
		settings.Margin = new Thickness(6, 0, 0, 0); header.Children.Add(settings);
		_main.Children.Add(new Border { Background = ChatPalette.Deep, BorderBrush = ChatPalette.Line, BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(18, 10, 18, 6), Child = header });

		var stream = new Grid { ClipToBounds = true };
		var flow = new StackPanel { Spacing = 0 };
		_older = Button(() => _state.LoadingHistory ? T("正在加载…", "Loading…") : T("加载更早的消息", "Load earlier messages"), () => LoadHistoryAsync(true), "ChatLoadOlder");
		_older.HorizontalAlignment = HorizontalAlignment.Center; _older.Margin = new Thickness(12, 12, 12, 0);
		flow.Children.Add(_older); flow.Children.Add(_messageList);
		_toolStatus.Margin = new Thickness(20, 0, 20, 12); _toolStatus.Foreground = ChatPalette.Accent; flow.Children.Add(_toolStatus);
		AutomationProperties.SetLiveSetting(_toolStatus, AutomationLiveSetting.Polite); AutomationProperties.SetLiveSetting(_statusText, AutomationLiveSetting.Polite);
		_scroll.Content = flow; _scroll.ScrollChanged += (_, _) => OnScrollChanged();
		_scroll.SizeChanged += (_, _) => ResizeBubbles();
		AutomationProperties.SetName(_scroll, T("对话记录", "Conversation history")); stream.Children.Add(_scroll);
		_empty = BuildEmpty(); stream.Children.Add(_empty);
		_latest = Button(() => _unread > 0 ? T($"有新消息 ({_unread})", $"New messages ({_unread})") : T("回到最新消息", "Back to latest"), () => { ScrollToLatest(); return Task.CompletedTask; }, "ChatBackToLatest", "down");
		_latest.HorizontalAlignment = HorizontalAlignment.Right; _latest.VerticalAlignment = VerticalAlignment.Bottom; _latest.Margin = new Thickness(24, 12); stream.Children.Add(_latest);
		Grid.SetRow(stream, 1); _main.Children.Add(stream);

		var status = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 12 };
		_statusText.VerticalAlignment = VerticalAlignment.Center; status.Children.Add(_statusText);
		_retry = Button(() => !_loadedHistory ? T("重新加载历史", "Reload history") : T("重试上一条", "Retry last message"), () => !_loadedHistory ? LoadHistoryAsync(false) : SendAsync(_state.FailedInput), "ChatRetry");
		Grid.SetColumn(_retry, 1); status.Children.Add(_retry);
		_statusBar = new Border { Name = "ChatStatus", BorderBrush = ChatPalette.Line, BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(18, 6), Child = status };
		Grid.SetRow(_statusBar, 2); _main.Children.Add(_statusBar);

		var composer = new Grid { ColumnDefinitions = new ColumnDefinitions("40,*,40"), ColumnSpacing = 12 };
		_microphone = Button(() => VoiceLabel(), ToggleVoiceAsync, "ChatMicrophone", "mic", iconOnly: true);
		var microphoneContent = new StackPanel { Spacing = 1, HorizontalAlignment = HorizontalAlignment.Center };
		microphoneContent.Children.Add(Icon("mic", ChatPalette.Muted)); _voiceTime.HorizontalAlignment = HorizontalAlignment.Center; microphoneContent.Children.Add(_voiceTime);
		_microphone.Content = microphoneContent; _microphone.Height = 40; _microphone.Padding = new Thickness(0); _microphone.VerticalAlignment = VerticalAlignment.Bottom; composer.Children.Add(_microphone);
		_composer.Classes.Add("chat-composer"); Grid.SetColumn(_composer, 1); composer.Children.Add(_composer);
		var sendSlot = new Grid { VerticalAlignment = VerticalAlignment.Bottom };
		_send = Button(() => T("发送消息", "Send message"), () => SendAsync(), "ChatSend", "send", primary: true, iconOnly: true);
		_stop = Button(() => T("停止生成", "Stop generating"), StopGenerationAsync, "ChatStop", "stop", iconOnly: true);
		_stop.Classes.Add("stop"); _send.Height = 40; _stop.Height = 40;
		sendSlot.Children.Add(_send); sendSlot.Children.Add(_stop); Grid.SetColumn(sendSlot, 2); composer.Children.Add(sendSlot);
		var footer = new Border { Name = "ChatComposeBar", Background = ChatPalette.Deep, BorderBrush = ChatPalette.Line, BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(18, 14), Child = composer };
		Grid.SetRow(footer, 3); _main.Children.Add(footer);
		_root.Children.Add(_main);
		Content = new Border { Background = ChatPalette.Background, BorderBrush = ChatPalette.Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(16), ClipToBounds = true, Child = _root };
		Localize(() =>
		{
			_composer.PlaceholderText = T("输入消息, 回车发送", "Type a message, press Enter to send…");
			AutomationProperties.SetName(_composer, T("消息输入，Enter 发送，Shift+Enter 换行", "Message. Enter to send, Shift+Enter for a new line"));
			AutomationProperties.SetName(_scroll, T("对话记录", "Conversation history"));
		});
	}

	private Control BuildEmpty()
	{
		var body = new StackPanel { Spacing = 18, MaxWidth = 480, Margin = new Thickness(24, 16), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
		var symbol = new Border { Width = 48, Height = 48, CornerRadius = new CornerRadius(24), Background = ChatPalette.Deep, BorderBrush = ChatPalette.Line, BorderThickness = new Thickness(1), Child = Icon("sparkles", ChatPalette.Accent, 24), HorizontalAlignment = HorizontalAlignment.Center };
		body.Children.Add(symbol);
		_emptyTitle = Text("", 18, true); _emptyTitle.TextAlignment = TextAlignment.Center; body.Children.Add(_emptyTitle);
		_emptyDescription = Text("", 13); _emptyDescription.Foreground = ChatPalette.Muted; _emptyDescription.TextAlignment = TextAlignment.Center; body.Children.Add(_emptyDescription);
		var prompts = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), RowDefinitions = new RowDefinitions("Auto,Auto"), ColumnSpacing = 12, RowSpacing = 12 };
		Func<string>[] suggestions =
		[
			() => T("你好呀，做个自我介绍吧！", "Hi! Could you introduce yourself?"),
			() => T("今天天气怎么样？适合摸鱼吗？", "How is the weather today? Good for slacking off?"),
			() => T("在桌面上做个可爱的动作~", "Show me a cute motion on the desktop~"),
			() => T("你现在掌握了哪些技能和工具？", "Which skills and tools do you have right now?"),
		];
		for (int index = 0; index < suggestions.Length; index++)
		{
			Func<string> suggestion = suggestions[index];
			Button prompt = Button(suggestion, () => SendAsync(suggestion()), "ChatPrompt_" + index, index == 0 ? "sparkles" : "main");
			prompt.MinHeight = 48; prompt.HorizontalAlignment = HorizontalAlignment.Stretch; prompt.HorizontalContentAlignment = HorizontalAlignment.Stretch;
			Grid.SetColumn(prompt, index % 2); Grid.SetRow(prompt, index / 2); prompts.Children.Add(prompt);
		}
		_prompts = prompts; body.Children.Add(prompts);
		_settings = Button(() => T("前往设置", "Go to settings"), () => { _service.OpenSettings(); return Task.CompletedTask; }, "ChatConfigure", "settings", primary: true);
		_settings.HorizontalAlignment = HorizontalAlignment.Center; body.Children.Add(_settings);
		return new ScrollViewer { Content = body, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
	}

	private void RenderHeader()
	{
		string model = NativeChatJson.S(NativeChatJson.P(_snapshot, "chat"), "model", NativeChatJson.S(NativeChatJson.P(_snapshot, "ai"), "model"));
		if (NativeChatJson.S(NativeChatJson.P(_snapshot, "chat"), "backend") == "luolicore") model = NativeChatJson.S(_state.Metrics, "model", "LuoLiCore");
		_modelLabel.Text = model.Length > 0 ? model : T("未知模型", "Unknown model"); ToolTip.SetTip(_modelLabel, _modelLabel.Text);
		double total = NativeChatJson.N(_state.Metrics, "totalTokens"), duration = NativeChatJson.N(_state.Metrics, "durationMs");
		double speed = duration > 0 ? NativeChatJson.N(_state.Metrics, "completionTokens") / (duration / 1000) : 0;
		_usageLabel.Text = T("上下文 ", "Context ") + total.ToString("N0") + T(" 词元", " tokens") + (duration > 0 ? $"  {speed:0.#} t/s" : "");
		_usageLabel.Foreground = total > 0 ? ChatPalette.Accent : ChatPalette.Muted;
		_cacheLabel.Text = T("缓存命中 ", "Cache hit ") + NativeChatJson.N(_state.Metrics, "cacheHitRate").ToString("0.#") + "%";
		_cacheLabel.Foreground = NativeChatJson.N(_state.Metrics, "cachedTokens") > 0 ? ChatPalette.Accent : ChatPalette.Muted;
		int toolCount = NativeChatJson.P(_snapshot, "tools") is { ValueKind: System.Text.Json.JsonValueKind.Array } tools ? tools.EnumerateArray().Count(item => NativeChatJson.B(item, "enabled")) : 0;
		_toolsLabel.Text = $"{NativeChatJson.N(_snapshot, "enabledSkillsCount"):0}" + T(" 技能 / ", " skills / ") + toolCount + T(" 工具", " tools");
		_empty.IsVisible = _state.Messages.Count == 0;
		_prompts.IsVisible = _configured && !_safeMode;
		_settings.IsVisible = !_configured || _safeMode;
		_emptyTitle.Text = _safeMode ? T("安全模式：对话已暂停", "Safe mode: chat is paused") : !_configured ? T("尚未配置 AI API", "AI API not configured") : T("和 Nori 开始对话吧", "Start chatting with Nori");
		_emptyDescription.Text = _safeMode ? T("仍可查看和清空本地历史。重新正常启动后即可对话。", "You can still read and clear local history. Restart normally to chat.") : !_configured ? T("配置后即可开始对话，历史记录仍保存在本机。", "Configure a provider to chat. Your history remains on this device.") : T("你可以随时向 Nori 提问、互动，或者点击下方的建议话题：", "Ask anything, or pick one of the suggestions below:");
		_older.IsVisible = _state.HasMoreHistory; SetButtonLabel(_older, _state.LoadingHistory ? T("正在加载…", "Loading…") : T("加载更早的消息", "Load earlier messages"));
		SetButtonLabel(_retry, !_loadedHistory ? T("重新加载历史", "Reload history") : T("重试上一条", "Retry last message"));
	}

	private void RenderStatus()
	{
		string status = _state.Status switch
		{
			"cancelled" => T("已停止生成", "Generation stopped"),
			"stopping" => T("正在停止，等待后端确认…", "Stopping; waiting for the host…"),
			"approval-timeout" => T("工具授权已超时，已自动拒绝", "Tool approval timed out and was denied"),
			"transcript" => T("语音已转写到输入框，确认后回车发送", "Transcript added to the input box; press Enter to send"),
			_ => _state.Status,
		};
		if (status.Length == 0 && !_loadedHistory) status = T("正在加载本地对话历史…", "Loading local conversation history…");
		if (status.Length == 0 && _safeMode) status = T("安全模式已禁用对话和语音，仍可管理本地历史。", "Safe mode disables chat and voice. Local history remains available.");
		if (_state.Error.Length > 0) status = T("操作失败：", "Operation failed: ") + _state.Error;
		_statusText.Text = status; _statusText.Foreground = _state.Error.Length > 0 ? ChatPalette.Danger : ChatPalette.Muted;
		_statusBar.IsVisible = status.Length > 0;
		_retry.IsVisible = _state.FailedInput.Length > 0 || (!_loadedHistory && !_state.LoadingHistory);
		_toolStatus.Text = _state.ExecutingTool.Length > 0 ? T("正在执行工具：", "Running tool: ") + _state.ExecutingTool : _state.Sending ? T("Nori 正在回复…", "Nori is replying…") : _state.AgentState == "speaking" ? T("正在朗读…", "Speaking…") : "";
		_toolStatus.IsVisible = !string.IsNullOrEmpty(_toolStatus.Text);
		AutomationProperties.SetName(_statusBar, status);
	}

	private void UpdateActions()
	{
		bool ready = _configured && _loadedHistory && !_safeMode && !_preparing && !_clearing;
		_send.IsVisible = !_state.Sending; _stop.IsVisible = _state.Sending;
		_send.IsEnabled = ready && !_state.Sending && !string.IsNullOrWhiteSpace(_state.Draft);
		_stop.IsEnabled = !_preparing && !_state.CancelRequested;
		_clear.IsEnabled = !_preparing && !_clearing && !_state.Sending;
		_retry.IsEnabled = !_loadedHistory ? !_state.LoadingHistory && !_preparing : ready && !_state.Sending;
		_older.IsEnabled = !_preparing && !_state.LoadingHistory && !_clearing;
		_prompts.IsEnabled = ready && !_state.Sending;
		_composer.IsReadOnly = _preparing;
		_microphone.IsEnabled = !_safeMode && !_preparing && _voiceState is "idle" or "recording";
		UpdateVoiceLabel();
	}
	private string VoiceLabel() => _voiceState switch
	{
		"recording" => T("点击结束语音输入", "Stop voice input"),
		"transcribing" => T("正在识别语音…", "Transcribing…"),
		"starting" => T("正在启动录音…", "Starting recording…"),
		_ => T("点击开始语音输入", "Start voice input"),
	};
	private void UpdateVoiceLabel()
	{
		AutomationProperties.SetName(_microphone, VoiceLabel()); ToolTip.SetTip(_microphone, VoiceLabel());
		_voiceTime.IsVisible = _voiceState == "recording";
		_voiceTime.Foreground = ChatPalette.Danger;
		_voiceTime.Text = (DateTimeOffset.UtcNow - _recordStarted).ToString(@"mm\:ss", System.Globalization.CultureInfo.InvariantCulture);
	}

	private string T(string chinese, string english) => _language.StartsWith("en", StringComparison.OrdinalIgnoreCase) ? english : chinese;
	private void Localize(Action action) { _localize.Add(action); action(); }
	private void ApplyLanguage()
	{
		foreach (Action action in _localize) action();
		foreach (MessageVisual visual in _messageViews.Values) visual.Localize();
		QueueRender();
	}
	private static TextBlock Text(string text, double size = 13, bool bold = false) => new() { Text = text, FontSize = size, FontWeight = bold ? FontWeight.SemiBold : FontWeight.Normal, TextWrapping = TextWrapping.Wrap, Foreground = ChatPalette.Primary };
	private TextBlock Local(Func<string> text, double size = 13, bool bold = false)
	{
		TextBlock block = Text(text(), size, bold); Localize(() => block.Text = text()); return block;
	}
	private Button Button(Func<string> label, Func<Task> action, string name, string? icon = null, bool primary = false, bool iconOnly = false)
	{
		var button = new Button { Name = name, MinHeight = 30, Padding = new Thickness(iconOnly ? 8 : 12, 6), VerticalAlignment = VerticalAlignment.Center };
		button.Classes.Add("chat-button"); if (primary) button.Classes.Add("primary");
		TextBlock? text = iconOnly ? null : Text(label(), 12); if (text is not null) text.ClearValue(TextBlock.ForegroundProperty);
		if (icon is not null)
		{
			var row = new Grid { ColumnDefinitions = new ColumnDefinitions(iconOnly ? "16" : "16,*"), ColumnSpacing = 8 };
			row.Children.Add(Icon(icon, primary || icon == "stop" ? ChatPalette.OnTeal : ChatPalette.Muted));
			if (text is not null) { Grid.SetColumn(text, 1); row.Children.Add(text); }
			button.Content = row;
		}
		else button.Content = text;
		Localize(() => { if (text is not null) text.Text = label(); AutomationProperties.SetName(button, label()); ToolTip.SetTip(button, label()); });
		button.Click += (_, _) =>
		{
			try { Run(action()); }
			catch (Exception exception) { ReportFailure(exception); }
		};
		return button;
	}
	private static void SetButtonLabel(Button button, string label)
	{
		if (button.Content is TextBlock text) text.Text = label;
		AutomationProperties.SetName(button, label); ToolTip.SetTip(button, label);
	}
	private static Control Icon(string name, IBrush brush, double size = 16)
	{
		string data = name switch
		{
			"send" => "M22 2 15 22 11 13 2 9 22 2 M22 2 11 13",
			"mic" => "M9 5 A3 3 0 0 1 15 5 L15 12 A3 3 0 0 1 9 12 Z M5 10 L5 12 A7 7 0 0 0 19 12 L19 10 M12 19 L12 22 M8 22 L16 22",
			"stop" => "M6 6 L18 6 18 18 6 18 Z",
			"copy" => "M9 9 L21 9 21 21 9 21 Z M15 5 L15 3 3 3 3 15 5 15",
			"check" => "M4 12 L9 17 20 6",
			"trash" => "M3 6 L21 6 M8 6 L8 3 16 3 16 6 M5 6 L6 21 18 21 19 6 M10 10 L10 17 M14 10 L14 17",
			"down" => "M12 4 L12 20 M5 13 L12 20 19 13",
			"retry" => "M20 7 L20 3 M20 7 L16 7 M20 7 A9 9 0 1 0 21 15",
			"sparkles" => "M12 3 L14.5 9.5 21 12 14.5 14.5 12 21 9.5 14.5 3 12 9.5 9.5 Z M21 1 L21 5 M19 3 L23 3",
			"settings" => "M12 8 A4 4 0 1 0 12 16 A4 4 0 1 0 12 8 M12 2 L14.5 2 15.75 5 19.5 5 22 8 19.5 11 22 14 19.5 18 15.75 18 14.5 22 9.5 22 8.25 18 4.5 18 2 14 4.5 11 2 8 4.5 5 8.25 5 9.5 2 Z",
			"close" => "M6 6 L18 18 M18 6 L6 18",
			_ => "M3 10 L12 3 21 10 M5 9 L5 21 10 21 10 15 14 15 14 21 19 21 19 9",
		};
		return new Avalonia.Controls.Shapes.Path { Data = Geometry.Parse(data), Stroke = brush, StrokeThickness = 1.7, StrokeLineCap = PenLineCap.Round, StrokeJoin = PenLineJoin.Round, Width = size, Height = size, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
	}
}
