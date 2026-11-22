using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Nori.Core.Configuration;
using Nori.Core.Voice.Realtime;
using Nori.Desktop.Appearance;
using Nori.Desktop.Settings;

namespace Nori.Desktop.Chat;

public sealed partial class ChatView
{
	private readonly DispatcherTimer _liveVoiceTimer = new() { Interval = TimeSpan.FromMilliseconds(80) };
	private readonly TextBlock _liveStatus = Text("", NoriMetrics.FontXs);
	private readonly TextBlock _liveTranscript = Text("", NoriMetrics.FontXs);
	private readonly ProgressBar _liveLevel = new() { Minimum = 0, Maximum = 1, Height = 4, Foreground = ChatPalette.Teal };
	private Border _liveBar = null!;
	private Button _liveToggle = null!;
	private Button _liveRetry = null!;
	private Button _liveInterrupt = null!;
	private Button _liveStopSpeech = null!;
	private Button _liveRestore = null!;
	private bool _liveCommandPending;
	private string _liveCommandError = "";

	private Control BuildLiveVoiceBar()
	{
		_liveStatus.Name = "ChatLiveVoiceStatus";
		_liveTranscript.Name = "ChatLiveVoiceTranscript";
		_liveLevel.Name = "ChatLiveVoiceLevel";
		_liveTranscript.MaxLines = _liveStatus.MaxLines = 2;
		_liveTranscript.TextTrimming = _liveStatus.TextTrimming = TextTrimming.CharacterEllipsis;
		_liveTranscript.Foreground = ChatPalette.Muted;
		AutomationProperties.SetLiveSetting(_liveStatus, AutomationLiveSetting.Polite);
		Localize(() => AutomationProperties.SetName(_liveLevel, T("麦克风输入电平", "Microphone input level")));
		_liveToggle = Button(LiveToggleLabel, () => RunLiveCommandAsync(LiveActive ? "realtime_voice_stop" : "realtime_voice_start"), "ChatLiveVoiceToggle");
		_liveRetry = Button(() => T("重试语音", "Retry voice"), () => RunLiveCommandAsync("realtime_voice_start", restart: LiveActive), "ChatLiveVoiceRetry");
		_liveInterrupt = Button(() => T("打断语音", "Interrupt voice"), () => RunLiveCommandAsync("realtime_voice_interrupt"), "ChatLiveVoiceInterrupt");
		_liveStopSpeech = Button(() => T("停止朗读", "Stop speaking"), () => RunLiveCommandAsync("tts_stop"), "ChatStopSpeaking");
		_liveRestore = Button(() => T("将转写追加到草稿", "Append transcript to draft"), () =>
		{
			_state.RestoreFailedVoiceInput(); FlushRender(); return Task.CompletedTask;
		}, "ChatRestoreVoiceInput");
		var actions = new WrapPanel { ItemSpacing = 6, LineSpacing = 4, Children = { _liveToggle, _liveRetry, _liveInterrupt, _liveStopSpeech, _liveRestore } };
		return _liveBar = new Border
		{
			Name = "ChatLiveVoice", IsVisible = false, Background = ChatPalette.Overlay, BorderBrush = ChatPalette.Line,
			BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(16, 5),
			Child = new StackPanel { Spacing = 4, Children = { _liveStatus, _liveTranscript, _liveLevel, actions } },
		};
	}

	private bool LiveActive => _state.LiveVoice?.Status.Phase is not (null or RealtimeVoicePhase.Idle or RealtimeVoicePhase.Error);
	private string LiveToggleLabel() => LiveActive ? T("停止实时语音", "Stop live voice") : T("开启实时语音", "Start live voice");

	// 仅在完整对话窗口可见时采样；不把电平/字幕接入通用快照刷新或消息流。
	private void RefreshLiveVoice()
	{
		if (_disposed || !_hostVisible) return;
		ApplyLiveVoiceSnapshot(_service.ReadLiveVoice());
	}

	internal void ApplyLiveVoiceSnapshot(Runtime.ChatVoiceSnapshot? snapshot)
	{
		Dispatcher.UIThread.VerifyAccess();
		if (Equals(_state.LiveVoice, snapshot)) return;
		_state.UpdateLiveVoice(snapshot);
		RenderLiveVoice();
	}

	private void RenderLiveVoice()
	{
		var voice = _state.LiveVoice;
		var status = voice?.Status;
		bool canStart = status?.Enabled == true && !_safeMode;
		bool speaking = voice?.Speaking == true || status?.Phase == RealtimeVoicePhase.Speaking;
		bool canRestore = _state.FailedVoiceInput.Length > 0;
		string error = _liveCommandError.Length > 0 ? _liveCommandError : status?.Error ?? "";
		bool fallback = status is { Enabled: true, UsedBatchFallback: true };
		_liveBar.IsVisible = _service.SupportsLiveVoice && (canStart || LiveActive || speaking || canRestore || error.Length > 0 || fallback);
		_liveToggle.IsVisible = canStart || LiveActive;
		SetButtonLabel(_liveToggle, LiveToggleLabel());
		bool canRetryFallback = fallback && status?.Phase is RealtimeVoicePhase.Listening or RealtimeVoicePhase.Idle or RealtimeVoicePhase.Error;
		_liveRetry.IsVisible = canStart && ((!LiveActive && error.Length > 0) || canRetryFallback);
		_liveInterrupt.IsVisible = LiveActive && status?.InterruptEnabled == true
			&& status.Phase is RealtimeVoicePhase.Transcribing or RealtimeVoicePhase.Thinking or RealtimeVoicePhase.Speaking;
		_liveStopSpeech.IsVisible = speaking;
		_liveRestore.IsVisible = canRestore;
		_liveRestore.IsEnabled = !_preparing;
		_liveToggle.IsEnabled = _liveRetry.IsEnabled = _liveInterrupt.IsEnabled = _liveStopSpeech.IsEnabled = !_preparing && !_liveCommandPending;
		_liveLevel.IsVisible = status?.Phase == RealtimeVoicePhase.Listening && !status.CapturePaused;
		_liveLevel.Value = _liveLevel.IsVisible ? voice!.InputLevel : 0;
		string phase = status?.Phase switch
		{
			RealtimeVoicePhase.Listening when status.CapturePaused && speaking => T("正在朗读 · 监听已暂停", "Speaking · listening paused"),
			RealtimeVoicePhase.Listening when status.CapturePaused => T("监听已暂停", "Listening paused"),
			RealtimeVoicePhase.Listening => T("正在监听", "Listening"),
			RealtimeVoicePhase.Transcribing => T("正在识别", "Transcribing"),
			RealtimeVoicePhase.Thinking => T("正在思考", "Thinking"),
			RealtimeVoicePhase.Speaking => T("正在朗读 · 监听已暂停", "Speaking · listening paused"),
			RealtimeVoicePhase.Interrupting => T("正在打断", "Interrupting"),
			RealtimeVoicePhase.Error => T("语音连接或处理异常", "Voice connection or processing error"),
			_ => speaking ? T("正在朗读", "Speaking") : T("实时语音未开启", "Live voice is off"),
		};
		if (fallback) phase += " · " + T(status!.FallbackReason, "Streaming unavailable; using whole-utterance recognition.");
		if (error.Length > 0) phase += " · " + SettingsErrorText.Resolve(error, UiLanguage.IsEnglish(_language));
		_liveStatus.Text = phase;
		_liveStatus.Foreground = error.Length > 0 ? ChatPalette.Danger : ChatPalette.Primary;
		var subtitle = voice?.Transcript;
		_liveTranscript.Text = subtitle is { IsFinal: true }
			? T("识别已确认", "Transcript confirmed")
			: subtitle is { Text.Length: > 0 } ? T("识别中 · 临时字幕 · ", "Recognizing · live caption · ") + subtitle.Text : "";
		_liveTranscript.IsVisible = _liveTranscript.Text.Length > 0;
	}

	private async Task RunLiveCommandAsync(string command, bool restart = false)
	{
		if (_liveCommandPending || _preparing || _disposed) return;
		_liveCommandPending = true; _liveCommandError = ""; RenderLiveVoice();
		try
		{
			if (restart) await ExecuteAsync("realtime_voice_stop", cancellationToken: _lifetime.Token);
			await ExecuteAsync(command, cancellationToken: _lifetime.Token);
		}
		catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
		catch (Exception exception) { _liveCommandError = exception.Message; }
		finally
		{
			_liveCommandPending = false;
			if (!_disposed && _hostVisible)
			{
				_state.UpdateLiveVoice(_service.ReadLiveVoice());
				RenderLiveVoice();
			}
		}
	}
}
