using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using static Nori.Desktop.SnapshotJson;

namespace Nori.Desktop.Chat;

/// <summary>后端事件的原生视图投影；终结事件以外的通知不会释放活动会话。</summary>
internal sealed class NativeChatState
{
	private readonly List<JsonElement> _earlyEvents = [];
	private readonly HashSet<long> _historyIds = [];
	private readonly HashSet<string> _resolvedApprovals = [];
	private NativeChatMessage? _response;
	private NativeChatMessage? _inputBubble;
	private string _activeInput = "";
	private long _generation;
	private long _historyRequest;

	internal ObservableCollection<NativeChatMessage> Messages { get; } = [];
	internal List<NativeChatApproval> Approvals { get; } = [];
	internal event Action? Changed;
	internal string Draft { get; set; } = "";
	internal string FailedInput { get; private set; } = "";
	internal string Error { get; private set; } = "";
	internal string Status { get; private set; } = "";
	internal string AgentState { get; private set; } = "idle";
	internal string ExecutingTool { get; private set; } = "";
	internal string? SessionId { get; private set; }
	internal bool Sending { get; private set; }
	internal bool CancelRequested { get; private set; }
	internal bool LoadingHistory { get; private set; }
	internal bool HasMoreHistory { get; private set; }
	internal long OldestId { get; private set; }
	internal JsonElement Metrics { get; private set; }
	internal DateTimeOffset LastActivity { get; private set; }

	internal bool BeginSend(string text)
	{
		text = text.Trim();
		if (Sending || text.Length == 0) return false;
		InvalidateHistory();
		Error = ""; Status = ""; FailedInput = ""; _activeInput = text;
		if (Draft.Trim() == text) Draft = "";
		Sending = true; CancelRequested = false; SessionId = null; AgentState = "thinking";
		LastActivity = DateTimeOffset.UtcNow; _earlyEvents.Clear(); _resolvedApprovals.Clear();
		_inputBubble = new NativeChatMessage($"user-{_generation}", "user", text); Messages.Add(_inputBubble);
		_response = new NativeChatMessage($"pending-{_generation}", "assistant", "") { Streaming = true };
		Messages.Add(_response); Changed?.Invoke(); return true;
	}

	internal void AttachSession(string sessionId)
	{
		if (!Sending) return;
		if (string.IsNullOrWhiteSpace(sessionId)) throw new InvalidOperationException("聊天服务没有返回会话标识");
		SessionId = sessionId;
		JsonElement[] buffered = _earlyEvents.ToArray(); _earlyEvents.Clear();
		foreach (JsonElement payload in buffered) ApplyEvent(payload);
		Changed?.Invoke();
	}

	internal void StartFailed(Exception exception)
	{
		if (!Sending || SessionId is not null) return;
		Error = exception.Message; _earlyEvents.Clear();
		if (_inputBubble is not null) Messages.Remove(_inputBubble);
		Finish(true); Changed?.Invoke();
	}

	internal void ApplyEvent(JsonElement payload)
	{
		string type = S(payload, "type"), sessionId = S(payload, "sessionId");
		if (type == "state" && sessionId.Length == 0)
		{
			// 自动朗读的 idle/speaking 不属于正在生成的新一轮。
			if (!Sending) { AgentState = S(payload, "state"); Changed?.Invoke(); }
			return;
		}
		if (Sending && SessionId is null && sessionId.Length > 0) { _earlyEvents.Add(payload.Clone()); return; }
		if (!Sending || sessionId != SessionId) return;
		LastActivity = DateTimeOffset.UtcNow;
		switch (type)
		{
			case "chunk": _response?.Append(S(payload, "chunk")); break;
			case "state": AgentState = S(payload, "state"); break;
			case "tool-executing": ExecutingTool = S(payload, "toolName"); break;
			case "tool-executed":
				ExecutingTool = "";
				if (!B(payload, "success")) Error = S(payload, "error");
				break;
			case "usage": Metrics = payload.Clone(); break;
			case "approval-request":
				string id = S(payload, "requestId");
				if (id.Length == 0 || _resolvedApprovals.Contains(id) || Approvals.Any(item => item.RequestId == id)) break;
				Approvals.Add(new NativeChatApproval(payload.Clone())); AgentState = "waiting_approval"; break;
			case "approval-extended":
				ExtendApproval(S(payload, "requestId"), NativeChatJson.Deadline(payload)); break;
			case "approval-result":
				ResolveApproval(S(payload, "requestId"));
				if (S(payload, "reason") == "timeout") Status = "approval-timeout";
				break;
			case "complete":
				// 最终正文即使为空也必须替换流式预览，不能残留已被服务端删掉的文本。
				_response?.Replace(S(P(payload, "message"), "text")); Finish(false); break;
			case "cancelled": Status = "cancelled"; Finish(true); break;
			case "error": Error = S(payload, "error"); Finish(true); break;
			default: break;
		}
		Changed?.Invoke();
	}

	private void Finish(bool restore)
	{
		if (restore && _activeInput.Length > 0)
		{
			FailedInput = _activeInput;
			if (string.IsNullOrWhiteSpace(Draft)) Draft = _activeInput;
		}
		if (_response is not null)
		{
			_response.Streaming = false; _response.Touch();
			if (_response.Content.Length == 0) Messages.Remove(_response);
		}
		Sending = false; CancelRequested = false; SessionId = null; _activeInput = ""; _response = null; _inputBubble = null;
		ExecutingTool = ""; AgentState = "idle"; Approvals.Clear(); _earlyEvents.Clear();
	}

	internal void RequestCancel()
	{
		if (!Sending) return;
		CancelRequested = true;
		Status = "stopping";
		Changed?.Invoke();
	}
	internal void CancelFailed(Exception exception) { CancelRequested = false; SetError(exception.Message); }
	internal void SetError(string error) { Error = error; Changed?.Invoke(); }
	internal void SetStatus(string status) { Error = ""; Status = status; Changed?.Invoke(); }
	internal void ResolveApproval(string requestId)
	{
		_resolvedApprovals.Add(requestId); Approvals.RemoveAll(item => item.RequestId == requestId); Changed?.Invoke();
	}
	internal void ExtendApproval(string requestId, DateTimeOffset deadline)
	{
		NativeChatApproval? approval = Approvals.Find(item => item.RequestId == requestId);
		if (approval is not null) approval.Deadline = deadline;
		Changed?.Invoke();
	}

	internal (long Generation, long Request) BeginHistory()
	{
		LoadingHistory = true; Changed?.Invoke(); return (_generation, ++_historyRequest);
	}
	internal void EndHistory((long Generation, long Request) ticket)
	{
		if (ticket != (_generation, _historyRequest)) return;
		LoadingHistory = false; Changed?.Invoke();
	}
	internal bool AcceptHistory((long Generation, long Request) ticket, JsonElement page, int limit)
	{
		if (ticket != (_generation, _historyRequest) || page.ValueKind != JsonValueKind.Array) return false;
		JsonElement[] rows = page.EnumerateArray().OrderBy(row => Id(row, "id")).ToArray();
		var older = new List<NativeChatMessage>();
		foreach (JsonElement row in rows)
		{
			long id = Id(row, "id"); string role = S(row, "role");
			if (id <= 0 || role is not ("user" or "assistant") || !_historyIds.Add(id)) continue;
			older.Add(new NativeChatMessage(id.ToString(System.Globalization.CultureInfo.InvariantCulture), role, S(row, "content")));
			OldestId = OldestId == 0 ? id : Math.Min(OldestId, id);
		}
		for (int index = 0; index < older.Count; index++) Messages.Insert(index, older[index]);
		HasMoreHistory = rows.Length >= limit; Changed?.Invoke(); return true;
	}
	internal void InvalidateHistory() { _generation++; _historyRequest++; LoadingHistory = false; }

	/// <summary>合并其他原生表面新增的真实历史，保留已经加载的较早页与草稿。</summary>
	internal void MergeLatestHistory(JsonElement page)
	{
		if (Sending || page.ValueKind != JsonValueKind.Array) return;
		if (page.GetArrayLength() == 0) { Clear(""); return; }
		foreach (JsonElement row in page.EnumerateArray().OrderBy(row => Id(row, "id")))
		{
			long id = Id(row, "id");
			string role = S(row, "role"), text = S(row, "content");
			if (id <= 0 || role is not ("user" or "assistant") || !_historyIds.Add(id)) continue;
			NativeChatMessage? local = Messages.FirstOrDefault(message =>
				!long.TryParse(message.Key, out _) && message.Role == role && message.Content == text);
			if (local is not null) Messages.Remove(local);
			int index = 0;
			while (index < Messages.Count
				&& long.TryParse(Messages[index].Key, out long existingId) && existingId < id) index++;
			Messages.Insert(index, new NativeChatMessage(id.ToString(System.Globalization.CultureInfo.InvariantCulture), role, text));
			OldestId = OldestId == 0 ? id : Math.Min(OldestId, id);
		}
		Changed?.Invoke();
	}
	internal void Clear(string note)
	{
		if (Sending) throw new InvalidOperationException("请先停止生成，再清空记录");
		InvalidateHistory(); Messages.Clear(); _historyIds.Clear(); OldestId = 0; HasMoreHistory = false;
		Metrics = default; Error = ""; Status = note; FailedInput = ""; Changed?.Invoke();
	}
}

internal sealed class NativeChatMessage(string key, string role, string text)
{
	private readonly StringBuilder _text = new(text);
	private string? _cached = text;
	internal string Key { get; } = key;
	internal string Role { get; } = role;
	internal bool Streaming { get; set; }
	internal string Content => _cached ??= _text.ToString();
	internal event Action? Changed;
	internal void Append(string text) { _text.Append(text); _cached = null; Touch(); }
	internal void Replace(string text) { _text.Clear().Append(text); _cached = text; Touch(); }
	internal void Touch() => Changed?.Invoke();
}

internal sealed class NativeChatApproval(JsonElement payload)
{
	internal string RequestId { get; } = S(payload, "requestId");
	internal string ToolName { get; } = S(payload, "toolName");
	internal string Description { get; } = S(payload, "description");
	internal string PermissionLevel { get; } = S(payload, "permissionLevel");
	internal JsonElement Arguments { get; } = P(payload, "arguments");
	// 缺失或损坏的服务端截止时间只允许拒绝，不用本地时间虚构授权窗口。
	internal DateTimeOffset Deadline { get; set; } = NativeChatJson.Deadline(payload);
	internal int RemainingSeconds(DateTimeOffset now) => (int)Math.Clamp(Math.Ceiling((Deadline - now).TotalSeconds), 0, int.MaxValue);
}

internal static class NativeChatJson
{
	internal static DateTimeOffset Deadline(JsonElement value) => DateTimeOffset.TryParse(S(value, "deadlineUtc"), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out DateTimeOffset deadline) ? deadline : DateTimeOffset.MinValue;
}
