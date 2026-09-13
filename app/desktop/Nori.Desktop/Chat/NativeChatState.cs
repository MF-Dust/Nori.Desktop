using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;

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
		string type = NativeChatJson.S(payload, "type"), sessionId = NativeChatJson.S(payload, "sessionId");
		if (type == "state" && sessionId.Length == 0)
		{
			// 自动朗读的 idle/speaking 不属于正在生成的新一轮。
			if (!Sending) { AgentState = NativeChatJson.S(payload, "state"); Changed?.Invoke(); }
			return;
		}
		if (Sending && SessionId is null && sessionId.Length > 0) { _earlyEvents.Add(payload.Clone()); return; }
		if (!Sending || sessionId != SessionId) return;
		LastActivity = DateTimeOffset.UtcNow;
		switch (type)
		{
			case "chunk": _response?.Append(NativeChatJson.S(payload, "chunk")); break;
			case "state": AgentState = NativeChatJson.S(payload, "state"); break;
			case "tool-executing": ExecutingTool = NativeChatJson.S(payload, "toolName"); break;
			case "tool-executed":
				ExecutingTool = "";
				if (!NativeChatJson.B(payload, "success")) Error = NativeChatJson.S(payload, "error");
				break;
			case "usage": Metrics = payload.Clone(); break;
			case "approval-request":
				string id = NativeChatJson.S(payload, "requestId");
				if (id.Length == 0 || _resolvedApprovals.Contains(id) || Approvals.Any(item => item.RequestId == id)) break;
				Approvals.Add(new NativeChatApproval(payload.Clone())); AgentState = "waiting_approval"; break;
			case "approval-extended":
				ExtendApproval(NativeChatJson.S(payload, "requestId"), NativeChatJson.Deadline(payload)); break;
			case "approval-result":
				ResolveApproval(NativeChatJson.S(payload, "requestId"));
				if (NativeChatJson.S(payload, "reason") == "timeout") Status = "approval-timeout";
				break;
			case "complete":
				// 最终正文即使为空也必须替换流式预览，不能残留已被服务端删掉的文本。
				_response?.Replace(NativeChatJson.S(NativeChatJson.P(payload, "message"), "text")); Finish(false); break;
			case "cancelled": Status = "cancelled"; Finish(true); break;
			case "error": Error = NativeChatJson.S(payload, "error"); Finish(true); break;
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

	internal void RequestCancel() { if (!Sending) return; CancelRequested = true; Status = "stopping"; Changed?.Invoke(); }
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
		JsonElement[] rows = page.EnumerateArray().OrderBy(row => NativeChatJson.Id(row, "id")).ToArray();
		var older = new List<NativeChatMessage>();
		foreach (JsonElement row in rows)
		{
			long id = NativeChatJson.Id(row, "id"); string role = NativeChatJson.S(row, "role");
			if (id <= 0 || role is not ("user" or "assistant") || !_historyIds.Add(id)) continue;
			older.Add(new NativeChatMessage(id.ToString(System.Globalization.CultureInfo.InvariantCulture), role, NativeChatJson.S(row, "content")));
			OldestId = OldestId == 0 ? id : Math.Min(OldestId, id);
		}
		for (int index = 0; index < older.Count; index++) Messages.Insert(index, older[index]);
		HasMoreHistory = rows.Length >= limit; Changed?.Invoke(); return true;
	}
	internal void InvalidateHistory() { _generation++; _historyRequest++; LoadingHistory = false; }
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
	internal string RequestId { get; } = NativeChatJson.S(payload, "requestId");
	internal string ToolName { get; } = NativeChatJson.S(payload, "toolName");
	internal string Description { get; } = NativeChatJson.S(payload, "description");
	internal string PermissionLevel { get; } = NativeChatJson.S(payload, "permissionLevel");
	internal JsonElement Arguments { get; } = NativeChatJson.P(payload, "arguments");
	// 缺失或损坏的服务端截止时间只允许拒绝，不用本地时间虚构授权窗口。
	internal DateTimeOffset Deadline { get; set; } = NativeChatJson.Deadline(payload);
	internal int RemainingSeconds(DateTimeOffset now) => (int)Math.Clamp(Math.Ceiling((Deadline - now).TotalSeconds), 0, int.MaxValue);
}

internal static class NativeChatJson
{
	internal static JsonElement P(JsonElement value, string key) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out JsonElement result) ? result : default;
	internal static string S(JsonElement value, string key, string fallback = "") => P(value, key).ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ? fallback : P(value, key).ToString();
	internal static bool B(JsonElement value, string key) => P(value, key).ValueKind == JsonValueKind.True;
	internal static double N(JsonElement value, string key) => P(value, key).ValueKind == JsonValueKind.Number && P(value, key).TryGetDouble(out double number) ? number : 0;
	internal static long Id(JsonElement value, string key) => P(value, key).ValueKind == JsonValueKind.Number && P(value, key).TryGetInt64(out long number) ? number : 0;
	internal static DateTimeOffset Deadline(JsonElement value) => DateTimeOffset.TryParse(S(value, "deadlineUtc"), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out DateTimeOffset deadline) ? deadline : DateTimeOffset.MinValue;
}
