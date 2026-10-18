using System.Text;
using System.Text.Json;
using Nori.Desktop.Chat;
using static Nori.Desktop.SnapshotJson;

namespace Nori.Desktop.QuickChat;

internal enum QuickChatBubblePhase
{
	Entering,
	Visible,
	Exiting,
}

/// <summary>悬浮对话显示分段；Message 保留完整源消息，同源分段共享首次接收时间。</summary>
internal sealed class QuickChatBubble(NativeChatMessage message, int segmentIndex, string segmentText, DateTimeOffset receivedAt)
{
	internal NativeChatMessage Message { get; } = message;
	internal string SourceKey => Message.Key;
	internal string Key => segmentIndex == 0 ? Message.Key : $"{Message.Key}:segment:{segmentIndex}";
	internal string SegmentText { get; set; } = segmentText;
	internal DateTimeOffset ReceivedAt { get; } = receivedAt;
	internal QuickChatBubblePhase Phase { get; set; } = QuickChatBubblePhase.Entering;
	internal DateTimeOffset? ExitStartedAt { get; set; }
}

/// <summary>QuickChat 对完整原生聊天状态的轻量投影，负责短时气泡、草稿和错误状态。</summary>
internal sealed class QuickChatState
{
	internal const int MaximumCodePoints = 100;
	internal const int MaximumBubbles = 3;
	internal static readonly TimeSpan BubbleLifetime = TimeSpan.FromSeconds(30);
	internal static readonly TimeSpan ExitDuration = TimeSpan.FromMilliseconds(350);

	private readonly Dictionary<string, DateTimeOffset?> _received = [];
	private readonly List<QuickChatBubble> _bubbles = [];
	private bool _historyInitialized;
	private string _pendingInput = "";

	internal NativeChatState Chat { get; } = new();
	internal IReadOnlyList<QuickChatBubble> Bubbles => _bubbles;
	internal bool HistoryInitialized => _historyInitialized;
	internal bool Configured { get; private set; }
	internal bool SafeMode { get; private set; }
	internal string Language { get; private set; } = "zh-CN";
	internal event Action? Changed;

	internal QuickChatState()
	{
		Chat.Changed += OnChatChanged;
	}

	internal string Draft
	{
		get => Chat.Draft;
		set
		{
			string normalized = LimitCodePoints(value, MaximumCodePoints);
			if (Chat.Draft == normalized) return;
			Chat.Draft = normalized;
			Changed?.Invoke();
		}
	}

	internal void ApplySnapshot(JsonElement snapshot)
	{
		JsonElement chat = P(snapshot, "chat");
		Configured = chat.ValueKind == JsonValueKind.Object
			? B(chat, "configured")
			: B(P(snapshot, "ai"), "configured");
		SafeMode = B(P(snapshot, "app"), "safeMode");
		Language = S(P(snapshot, "general"), "language", Language);
		Changed?.Invoke();
	}

	internal bool AcceptInitialHistory((long Generation, long Request) ticket, JsonElement page, int limit)
	{
		bool accepted = Chat.AcceptHistory(ticket, page, limit);
		if (!accepted) return false;
		_historyInitialized = true;
		Synchronize(DateTimeOffset.UtcNow, silentNewMessages: true);
		foreach (NativeChatMessage message in Chat.Messages.ToArray()) Chat.Messages.Remove(message);
		_received.Clear();
		return true;
	}

	internal void MarkHistoryInitialized()
	{
		if (_historyInitialized) return;
		_historyInitialized = true;
		Synchronize(DateTimeOffset.UtcNow, silentNewMessages: true);
		Changed?.Invoke();
	}

	internal bool BeginSend(string text, DateTimeOffset now)
	{
		string normalized = LimitCodePoints(text, MaximumCodePoints);
		if (!Chat.BeginSend(normalized)) return false;
		_pendingInput = normalized.Trim();
		Chat.Draft = _pendingInput;
		Synchronize(now, silentNewMessages: false);
		return true;
	}

	internal void AcceptSession(string sessionId, DateTimeOffset? now = null)
	{
		Chat.AttachSession(sessionId);
		// 启动响应可能晚于完整事件流；回放后再投影，且失败恢复的输入不能被“已接收”路径清空。
		if (Chat.FailedInput.Length == 0 && Chat.Draft.Trim() == _pendingInput) Chat.Draft = "";
		_pendingInput = "";
		Synchronize(now ?? DateTimeOffset.UtcNow, silentNewMessages: false);
		Changed?.Invoke();
	}

	internal void ApplyEvent(JsonElement payload, DateTimeOffset now)
	{
		Chat.ApplyEvent(payload);
		Synchronize(now, silentNewMessages: false);
	}

	internal void StartFailed(Exception exception, DateTimeOffset now)
	{
		Chat.StartFailed(exception);
		_pendingInput = "";
		Synchronize(now, silentNewMessages: false);
	}

	internal void Synchronize(DateTimeOffset now, bool silentNewMessages)
	{
		HashSet<string> present = [];
		foreach (NativeChatMessage message in Chat.Messages)
		{
			if (message.Content.Length == 0) continue;
			present.Add(message.Key);
			if (!_received.ContainsKey(message.Key)) _received[message.Key] = silentNewMessages ? null : now;
		}
		foreach (string key in _received.Keys.Where(key => !present.Contains(key)).ToArray())
		{
			_received.Remove(key);
		}

		Dictionary<string, QuickChatBubble> existing = _bubbles.ToDictionary(item => item.Key, StringComparer.Ordinal);
		var next = new List<QuickChatBubble>();
		foreach (NativeChatMessage message in Chat.Messages)
		{
			if (!_received.TryGetValue(message.Key, out DateTimeOffset? receivedAt) || receivedAt is null) continue;
			IReadOnlyList<string> segments = message.Role == "assistant"
				? ChatMarkdown.Split(message.Content, message.Streaming)
				: [message.Content];
			if (segments.Count == 0 && message.Content.Length > 0) segments = [message.Content];
			for (int index = 0; index < segments.Count; index++)
			{
				string key = index == 0 ? message.Key : $"{message.Key}:segment:{index}";
				if (existing.TryGetValue(key, out QuickChatBubble? current))
				{
					current.SegmentText = segments[index];
					next.Add(current);
				}
				else if (now - receivedAt.Value < BubbleLifetime)
				{
					next.Add(new QuickChatBubble(message, index, segments[index], receivedAt.Value));
				}
			}
		}
		foreach (QuickChatBubble exiting in _bubbles.Where(item => item.Phase == QuickChatBubblePhase.Exiting && next.All(nextItem => nextItem.Key != item.Key))) next.Add(exiting);
		string[] visibleSources = next.Select(item => item.SourceKey).Distinct(StringComparer.Ordinal).TakeLast(MaximumBubbles).ToArray();
		HashSet<string> retainedSources = visibleSources.ToHashSet(StringComparer.Ordinal);
		_bubbles.Clear();
		_bubbles.AddRange(next.Where(item => retainedSources.Contains(item.SourceKey)));
		Tick(now);
		PruneInvisibleMessages();
		Changed?.Invoke();
	}

	internal bool Tick(DateTimeOffset now)
	{
		bool changed = false;
		foreach (QuickChatBubble bubble in _bubbles)
		{
			if (bubble.Phase != QuickChatBubblePhase.Exiting && now - bubble.ReceivedAt >= BubbleLifetime)
			{
				bubble.Phase = QuickChatBubblePhase.Exiting;
				bubble.ExitStartedAt = now;
				changed = true;
			}
		}
		changed |= _bubbles.RemoveAll(item => item.ExitStartedAt is { } started && now - started >= ExitDuration) > 0;
		if (changed) PruneInvisibleMessages();
		if (changed) Changed?.Invoke();
		return changed;
	}

	internal void MarkEntered(string key)
	{
		QuickChatBubble? bubble = _bubbles.FirstOrDefault(item => item.Key == key);
		if (bubble is not null && bubble.Phase == QuickChatBubblePhase.Entering) bubble.Phase = QuickChatBubblePhase.Visible;
	}

	internal static string LimitCodePoints(string? text, int maximum)
	{
		if (string.IsNullOrEmpty(text) || maximum <= 0) return "";
		var builder = new StringBuilder(Math.Min(text.Length, maximum));
		int count = 0;
		foreach (Rune rune in text.EnumerateRunes())
		{
			if (count++ >= maximum) break;
			builder.Append(rune.ToString());
		}
		return builder.ToString();
	}

	internal static int CountCodePoints(string? text) => string.IsNullOrEmpty(text) ? 0 : text.EnumerateRunes().Count();

	private void OnChatChanged() => Changed?.Invoke();

	private void PruneInvisibleMessages()
	{
		HashSet<NativeChatMessage> active = Chat.Sending ? Chat.Messages.TakeLast(2).ToHashSet() : [];
		HashSet<string> visible = _bubbles.Select(item => item.SourceKey).ToHashSet(StringComparer.Ordinal);
		foreach (NativeChatMessage message in Chat.Messages.Where(message => !active.Contains(message) && !visible.Contains(message.Key)).ToArray())
		{
			Chat.Messages.Remove(message);
			_received.Remove(message.Key);
		}
	}
}
