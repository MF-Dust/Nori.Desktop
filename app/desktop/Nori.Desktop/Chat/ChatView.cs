using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Threading;

namespace Nori.Desktop.Chat;

/// <summary>可复用的原生深色对话正文；窗口隐藏时保留草稿、历史、滚动位置和活动会话。</summary>
public sealed partial class ChatView : UserControl, IDisposable
{
	private readonly NativeChatService _service;
	private readonly Func<string, object?, CancellationToken, Task<JsonElement>> _execute;
	private readonly CancellationTokenSource _lifetime = new();
	private readonly NativeChatState _state = new();
	private readonly List<Action> _localize = [];
	private readonly DispatcherTimer _renderTimer = new() { Interval = TimeSpan.FromMilliseconds(40) };
	private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };
	private readonly HashSet<Task> _operations = [];
	private JsonElement _snapshot;
	private string _language = "zh-CN";
	private bool _configured;
	private bool _safeMode;
	private bool _hostVisible;
	private bool _loadedHistory;
	private bool _renderPending;
	private bool _refreshQueued;
	private bool _disposed;
	private bool _preparing;
	private bool _clearing;
	private long _snapshotRequest;
	private Task? _startOperation;
	private Task? _voiceOperation;
	private string _voiceState = "idle";
	private DateTimeOffset _recordStarted;

	/// <summary>连接宿主服务；订阅先于快照和第一条消息，避免启动事件竞态。</summary>
	public ChatView(NativeChatService service) : this(service, service.ExecuteAsync) { }

	internal ChatView(NativeChatService service, Func<string, object?, CancellationToken, Task<JsonElement>> execute)
	{
		_service = service; _execute = execute;
		InstallResources();
		Styles.Add(new StyleInclude(new Uri("avares://Nori.Desktop/")) { Source = new Uri("avares://Nori.Desktop/Chat/ChatTheme.axaml") });
		BuildShell(); BuildDialogs();
		_state.Changed += QueueRender; _state.Messages.CollectionChanged += OnMessagesChanged;
		_service.EventReceived += OnHostEvent; _service.StateChanged += OnHostStateChanged;
		_renderTimer.Tick += (_, _) => FlushRender();
		_clock.Tick += (_, _) => OnClock();
		_composer.PropertyChanged += OnComposerPropertyChanged;
		_composer.SendRequested += () => Run(SendAsync());
		KeyDown += (_, args) =>
		{
			if (args.Key != Key.Escape || args.Handled || !_modal.IsVisible) return;
			args.Handled = true;
			if (_state.Approvals.Count > 0) Run(DecideApprovalAsync(false));
			else if (!_clearing) { _confirmOpen = false; RenderDialog(); }
		};
		ApplyLanguage(); QueueRender(); FlushRender();
	}

	internal NativeChatState State => _state;
	internal ChatComposer Composer => _composer;
	internal string Language => _language;
	internal string VoiceState => _voiceState;
	internal bool HistoryLoaded => _loadedHistory;
	private Task<JsonElement> ExecuteAsync(string command, object? args = null, CancellationToken cancellationToken = default) => _execute(command, args, cancellationToken);
	internal event Action? LanguageChanged;

	/// <summary>刷新脱敏就绪信息与语言；初次历史加载不会覆盖后来开始的会话。</summary>
	public async Task RefreshAsync()
	{
		if (_disposed || _preparing) return;
		long request = ++_snapshotRequest;
		try
		{
			JsonElement snapshot = await _service.GetSnapshotAsync(_lifetime.Token);
			if (_disposed || request != _snapshotRequest) return;
			ApplySnapshot(snapshot);
			if (!_loadedHistory && !_state.LoadingHistory && !_state.Sending) await LoadHistoryAsync(false);
		}
		catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
		catch (Exception exception) { if (!_disposed && request == _snapshotRequest) ReportFailure(exception); }
	}

	internal void ApplySnapshot(JsonElement snapshot)
	{
		_snapshot = snapshot;
		JsonElement chat = NativeChatJson.P(snapshot, "chat");
		_configured = chat.ValueKind == JsonValueKind.Object ? NativeChatJson.B(chat, "configured") : NativeChatJson.B(NativeChatJson.P(snapshot, "ai"), "configured");
		_safeMode = NativeChatJson.B(NativeChatJson.P(snapshot, "app"), "safeMode");
		string language = NativeChatJson.S(NativeChatJson.P(snapshot, "general"), "language", _language);
		if (language != _language) { _language = language; ApplyLanguage(); LanguageChanged?.Invoke(); }
		QueueRender(); FlushRender();
	}

	internal void SetHostVisible(bool visible)
	{
		if (_disposed) return;
		_hostVisible = visible;
		if (visible)
		{
			_renderTimer.Start(); _clock.Start(); QueueRefresh(); FlushRender();
			if (_followLatest) ScrollToLatest();
		}
		else
		{
			_renderTimer.Stop();
			Run(StopRecordingAsync());
		}
	}

	/// <summary>普通关闭只停止录音并保留转写；不会取消仍在生成的对话。</summary>
	public async Task PrepareHideAsync()
	{
		await StopRecordingAsync();
	}

	/// <summary>退出时等待启动/转写请求，拒绝待决授权并请求取消；失败保留窗口供重试。</summary>
	public async Task PrepareShutdownAsync()
	{
		if (_disposed) return;
		_preparing = true; UpdateActions(); RenderDialog();
		try
		{
			if (_startOperation is not null) await _startOperation;
			await StopRecordingAsync();
			foreach (NativeChatApproval approval in _state.Approvals.ToArray())
			{
				await ExecuteAsync("approval_respond", new { requestId = approval.RequestId, approved = false });
				_state.ResolveApproval(approval.RequestId);
			}
			if (_state.SessionId is { } sessionId) await ExecuteAsync("chat_cancel", new { sessionId });
			while (_operations.Count > 0) await Task.WhenAll(_operations.ToArray());
			Dispose();
		}
		finally { _preparing = false; if (!_disposed) { UpdateActions(); RenderDialog(); } }
	}

	/// <summary>显示宿主可读失败而不清除草稿。</summary>
	public void ReportFailure(Exception exception)
	{
		if (!_disposed) { _state.SetError(exception.Message); FlushRender(); }
	}

	private void OnHostEvent(string name, JsonElement payload)
	{
		if (name != "nori:agent-event") return;
		JsonElement safePayload = payload.Clone();
		Dispatcher.UIThread.Post(() =>
		{
			if (_disposed) return;
			_state.ApplyEvent(safePayload);
			if (NativeChatJson.S(safePayload, "type") != "chunk") FlushRender();
		});
	}
	private void OnHostStateChanged() => Dispatcher.UIThread.Post(QueueRefresh);
	private void QueueRefresh()
	{
		if (_disposed || _preparing || !_hostVisible || _refreshQueued) return;
		_refreshQueued = true;
		Dispatcher.UIThread.Post(async () => { _refreshQueued = false; await RefreshAsync(); }, DispatcherPriority.Background);
	}
	private void OnComposerPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs args)
	{
		if (_disposed || args.Property != TextBox.TextProperty) return;
		// TextChanged 是延后派发的事件；草稿必须在 Text 属性变化时同步，避免快照或流式刷新覆盖刚输入的内容。
		_state.Draft = _composer.Text ?? ""; UpdateActions();
	}
	private void QueueRender() => _renderPending = true;
	internal void FlushRender()
	{
		if (_disposed) return;
		if (_renderPending)
		{
			_renderPending = false;
			if (_composer.Text != _state.Draft) { _composer.Text = _state.Draft; _composer.CaretIndex = _state.Draft.Length; }
			RenderHeader(); RenderStatus(); UpdateActions(); RenderDialog();
		}
		FlushMessages();
	}

	internal Task SendAsync(string? text = null)
	{
		if (!_configured || !_loadedHistory || _safeMode || _preparing || _clearing || _modal.IsVisible) return Task.CompletedTask;
		text ??= _state.Draft;
		if (!_state.BeginSend(text)) return Task.CompletedTask;
		ScrollToLatest(); FlushRender();
		Task operation = StartCoreAsync(text.Trim()); _startOperation = operation.IsCompleted ? null : operation; return operation;
	}
	private async Task StartCoreAsync(string text)
	{
		try
		{
			JsonElement result = await ExecuteAsync("chat_start", new { text }, _lifetime.Token);
			_state.AttachSession(result.ValueKind == JsonValueKind.String ? result.GetString() ?? "" : NativeChatJson.S(result, "sessionId"));
		}
		catch (Exception exception) { _state.StartFailed(exception); }
		finally { _startOperation = null; FlushRender(); }
	}
	private async Task StopGenerationAsync()
	{
		if (!_state.Sending || _state.CancelRequested) return;
		_state.RequestCancel(); FlushRender();
		try
		{
			if (_startOperation is not null) await _startOperation;
			if (_state.SessionId is { } sessionId) await ExecuteAsync("chat_cancel", new { sessionId }, _lifetime.Token);
		}
		catch (Exception exception) { _state.CancelFailed(exception); }
		finally { FlushRender(); }
	}
	private async Task LoadHistoryAsync(bool older)
	{
		if (_state.LoadingHistory || _clearing || (older && (!_state.HasMoreHistory || _state.OldestId == 0))) return;
		var ticket = _state.BeginHistory();
		try
		{
			JsonElement page = await ExecuteAsync("chat_history_page", new { limit = 50, beforeId = older ? _state.OldestId : 0 }, _lifetime.Token);
			double previousHeight = _scroll.Extent.Height, previousOffset = _scroll.Offset.Y;
			_loadingOlder = older;
			bool accepted;
			try { accepted = _state.AcceptHistory(ticket, page, 50); }
			finally { _loadingOlder = false; }
			if (!accepted) return;
			_loadedHistory = true; _preserveScroll = older; FlushRender();
			if (older)
			{
				Dispatcher.UIThread.Post(() =>
				{
					if (_disposed) return;
					_scroll.UpdateLayout(); _scroll.Offset = new Vector(0, previousOffset + Math.Max(0, _scroll.Extent.Height - previousHeight));
					_preserveScroll = false;
				}, DispatcherPriority.Loaded);
			}
			else ScrollToLatest();
		}
		finally { _state.EndHistory(ticket); FlushRender(); }
	}

	internal Task ToggleVoiceAsync()
	{
		if (_voiceOperation is not null) return _voiceOperation;
		if (_voiceState == "recording") return StopRecordingAsync();
		if (_safeMode || _preparing) return Task.CompletedTask;
		Task operation = StartRecordingCoreAsync(); _voiceOperation = operation.IsCompleted ? null : operation; return operation;
	}
	private async Task StartRecordingCoreAsync()
	{
		_voiceState = "starting"; UpdateActions();
		try
		{
			await ExecuteAsync("stt_start", cancellationToken: _lifetime.Token);
			_voiceState = "recording"; _recordStarted = DateTimeOffset.UtcNow;
		}
		catch { _voiceState = "idle"; throw; }
		finally { _voiceOperation = null; UpdateActions(); }
	}
	internal async Task StopRecordingAsync()
	{
		while (_voiceOperation is { } pending) await pending;
		if (_voiceState != "recording") return;
		Task operation = StopRecordingCoreAsync(); _voiceOperation = operation.IsCompleted ? null : operation; await operation;
	}
	private async Task StopRecordingCoreAsync()
	{
		_voiceState = "transcribing"; UpdateActions();
		try
		{
			JsonElement result = await ExecuteAsync("stt_stop", cancellationToken: _lifetime.Token);
			string transcript = NativeChatJson.S(result, "text");
			if (transcript.Length > 0)
			{
				_state.Draft = string.IsNullOrWhiteSpace(_state.Draft) ? transcript : _state.Draft + " " + transcript;
				_state.SetStatus("transcript");
			}
			_voiceState = "idle";
		}
		catch
		{
			// 无法区分录音停止失败和转写失败；再次停止是幂等的，不能假定麦克风已释放。
			_voiceState = "recording"; throw;
		}
		finally { _voiceOperation = null; QueueRender(); FlushRender(); }
	}
	private void OnClock()
	{
		if (_disposed) return;
		UpdateVoiceLabel(); UpdateApprovalCountdown();
	}
	private async void Run(Task task)
	{
		_operations.Add(task);
		try { await task; }
		catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
		catch (Exception exception) { ReportFailure(exception); }
		finally { _operations.Remove(task); }
	}

	/// <summary>仅真正销毁宿主时解除订阅；普通隐藏不得调用。</summary>
	public void Dispose()
	{
		if (_disposed) return;
		_disposed = true; _lifetime.Cancel(); _renderTimer.Stop(); _clock.Stop();
		_service.EventReceived -= OnHostEvent; _service.StateChanged -= OnHostStateChanged;
		_state.Changed -= QueueRender; _state.Messages.CollectionChanged -= OnMessagesChanged;
		_composer.PropertyChanged -= OnComposerPropertyChanged;
		foreach (MessageVisual visual in _messageViews.Values) visual.Dispose();
		_messageViews.Clear(); _dirtyMessages.Clear(); _lifetime.Dispose();
	}
}
