using Nori.Core.Agent;
using Nori.Core.Security;
using Nori.Core.Telemetry;
using Nori.Core.Voice;
using Nori.Desktop.Bridge;
using Nori.Desktop.Windows;

namespace Nori.Desktop.Runtime;

public sealed partial class AppRuntime
{
	// ===================================================================
	// 聊天会话
	// ===================================================================

	/// <summary>
	/// 启动一次 Agent 会话; 返回 sessionId 供取消/授权关联
	/// </summary>
	public string StartChat(IBridgeSource source, string text)
	{
		if (Volatile.Read(ref _disposed) != 0) throw new InvalidOperationException("应用正在退出");
		if (Services.SafeMode) throw new InvalidOperationException("安全模式已禁用联网和外部服务");
		Nori.Desktop.Chat.NativeChatService.ValidateSourceCommand(source, "chat_start");
		if (source is not INativeChatSource && source.Label != WindowLabels.Main)
			throw new InvalidOperationException("来源窗口无权发起对话");
		if (string.IsNullOrWhiteSpace(text)) throw new InvalidOperationException("消息内容不能为空");
		string sessionId = $"agent-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds():x}-{Interlocked.Increment(ref _sessionCounter):x}";
		CancellationToken lifetimeToken = _lifetimeCts.Token;
		AgentSessionState session = new(source, lifetimeToken);
		AgentSessionLease lease;
		try
		{
			session.Cts.Token.ThrowIfCancellationRequested();
			// 在返回 sessionId 前预留引擎闸门，消除后台线程尚未启动时的清空/重入窗口。
			lease = Engine.ReserveSession(sessionId, session.Cts.Token);
		}
		catch { session.Dispose(); throw; }
		_sessions[sessionId] = session;

		AgentCallbacks callbacks = new()
		{
			OnState = state => PostAgentEvent(session.Source, new {type = "state", sessionId, state = state.ToString().ToLowerInvariant()}),
			OnTextChunk = chunk => PostAgentEvent(session.Source, new {type = "chunk", sessionId, chunk}),
			OnToolExecuting = (name, args) => PostAgentEvent(session.Source, new {type = "tool-executing", sessionId, toolName = name, arguments = args}),
			OnToolExecuted = (name, result, error) => PostAgentEvent(session.Source, new
			{
				type = "tool-executed",
				sessionId,
				toolName = name,
				result = ToJsonNode(result),
				success = error is null,
				error = error is null ? null : SensitiveDataRedactor.Redact(error),
			}),
			OnUsage = usage => PostAgentEvent(session.Source, new
			{
				type = "usage", sessionId,
				promptTokens = usage.PromptTokens, completionTokens = usage.CompletionTokens,
				totalTokens = usage.TotalTokens, cachedTokens = usage.CachedTokens,
				cacheHitRate = usage.CacheHitRate, durationMs = usage.DurationMs, model = usage.Model,
			}),
			RequestApproval = request => RequestApprovalAsync(session.Source, sessionId, request, session.Cts.Token),
		};

		Task worker = Task.Run(async () =>
		{
			object terminal;
			ProtocolMessage? final = null;
			try
			{
				using ITelemetryTransaction operation = Services.Telemetry.StartTransaction("agent.run");
				// 聊天优先于伴侣轻量互动，但两者不共用聊天历史。
				CancelPetInteractionRequest(true);
				CancelPetInteractionPresentation();
				await RefreshMcpToolsAsync(session.Cts.Token);
				final = await Engine.RunAsync(text, sessionId, callbacks, session.Cts.Token, lease);
				if (!Services.SafeMode) _reflectionWorker.TryEnqueue();
				terminal = new
				{
					type = "complete", sessionId,
					message = new {text = final.Text, emotion = final.Emotion, expression = final.Expression, action = final.Action},
				};
			}
			catch (OperationCanceledException) { terminal = new {type = "cancelled", sessionId}; }
			catch (Exception exception)
			{
				terminal = new {type = "error", sessionId, error = SensitiveDataRedactor.Redact(exception.Message)};
			}
			finally
			{
				lease.Dispose();
				_sessions.TryRemove(sessionId, out _);
				session.Dispose();
				// 「本轮记住」记的就是这一轮。轮结束必须忘掉，否则下一轮会继承上一轮的同意。
				Permissions.ForgetTurn(sessionId);
			}
			// 终结事件意味着引擎与落库已结束，清空/下一轮不再被自动朗读占用。
			PostAgentEvent(session.Source, terminal);
			NotifyChatHistoryChanged();
			if (final is not null) await AutoSpeakAsync(final.Text, final.Emotion, lifetimeToken);
		});
		session.Worker = worker;
		TrackTask(worker);
		return sessionId;
	}
	internal long ChatHistoryRevision => Interlocked.Read(ref _chatHistoryRevision);
	internal void NotifyChatHistoryChanged()
	{
		Interlocked.Increment(ref _chatHistoryRevision);
		InvalidateSnapshot();
	}

	private async Task AutoSpeakAsync(string text, string? messageEmotion, CancellationToken ct)
	{
		if (string.IsNullOrWhiteSpace(text)) return;
		bool autoTts = ParseBoolFlag(Services.Config.GetStringOr("tts_auto_play", "")) ?? false;
		if (!autoTts || Services.SafeMode || ct.IsCancellationRequested) return;

		// 情绪自动推断：优先本条 AI 回复自带情绪，否则用全局情绪状态机当前值；
		// 都没有 (或为 neutral) 时不传情绪，让 TTS 用音色自带情绪。
		string? emotion = string.IsNullOrWhiteSpace(messageEmotion) ? Emotion.CurrentType : messageEmotion.Trim();
		TtsSynthesizeOptions speechOptions = new() {EmotionText = emotion};
		try
		{
			await Voice.SpeakAsync(text, speechOptions, ct);
		}
		catch
		{
			/* 自动朗读失败不阻断完成事件 */
		}
	}

	/// <summary>取消指定来源窗口的会话</summary>
	public bool CancelChat(IBridgeSource source, string sessionId)
	{
		if (!_sessions.TryGetValue(sessionId, out AgentSessionState? session) || !IsSameSource(source, session.Source)) return false;
		try { session.Cts.Cancel(); }
		catch (ObjectDisposedException) { return false; }
		return true;
	}

	private static bool IsSameSource(IBridgeSource source, IBridgeSource owner) =>
		source is INativeChatSource || owner is INativeChatSource
			? ReferenceEquals(source, owner)
			: source.Label == owner.Label;

	/// <summary>会话是否仍在运行</summary>
	public bool IsSessionActive(string sessionId) => _sessions.ContainsKey(sessionId);

	// ===================================================================
	// 前端音频宿主回报
	// ===================================================================

	/// <summary>前端回报一段音频播放结束 (或失败)</summary>
	public void ReportPlaybackFinished(string token, string? error) =>
		_webViewPlayback?.ReportPlaybackFinished(token, error);

	/// <summary>前端回报实时播放音量 (0~1), 驱动伴侣口型</summary>
	public void ReportAudioLevel(double level) => _webViewPlayback?.ReportLevel(level);

	/// <summary>音频宿主完成监听器安装后的就绪握手。</summary>
	public void MarkAudioHostReady() => _audioChannel.MarkReady();

	/// <summary>前端回报 MediaRecorder 已获权并开始。</summary>
	public void ReportRecordingReady(string token) => _webViewRecorder?.ReportRecordingReady(token);

	/// <summary>前端回报麦克风权限、录音或上传失败。</summary>
	public void ReportRecordingFailed(string token, string? error) => _webViewRecorder?.ReportRecordingFailed(token, error);
}
