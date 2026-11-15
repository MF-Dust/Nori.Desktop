using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace Nori.Core.Agent;

/// <summary>执行时间线上的状态。终态不会被迟到事件改写成另一种结果。</summary>
[JsonConverter(typeof(JsonStringEnumConverter<AgentExecutionStatus>))]
public enum AgentExecutionStatus
{
	Pending = 0,
	Running = 1,
	Success = 2,
	Failed = 3,
	Cancelled = 4,
	Denied = 5,
}

/// <summary>时间线事件种类。这是观察记录，不是第二套执行器。</summary>
[JsonConverter(typeof(JsonStringEnumConverter<AgentExecutionKind>))]
public enum AgentExecutionKind
{
	Request = 0,
	Model = 1,
	Tool = 2,
	Permission = 3,
	Session = 4,
}

/// <summary>提供方实际回报的用量。没有回报时保持 null，不补命中率。</summary>
public sealed record AgentExecutionUsage(
	int PromptTokens,
	int CompletionTokens,
	int TotalTokens,
	int CachedTokens,
	double? CacheHitRate,
	string? Model)
{
	/// <summary>只在调用方真的收到用量时转换。提示词为空时不计算命中率。</summary>
	public static AgentExecutionUsage? From(AgentTraceUsage? usage)
	{
		if (usage is null) return null;
		double? rate = usage.PromptTokens > 0 && double.IsFinite(usage.CacheHitRate) ? usage.CacheHitRate : null;
		return new AgentExecutionUsage(
			usage.PromptTokens,
			usage.CompletionTokens,
			usage.TotalTokens,
			usage.CachedTokens,
			rate,
			usage.Model);
	}
}

/// <summary>一条脱敏后的执行事件。没有提示词、对话、参数、结果或异常正文的字段。</summary>
public sealed record AgentExecutionEvent(
	long Sequence,
	string RequestId,
	string SessionId,
	int? Round,
	string? ToolCallId,
	AgentExecutionKind Kind,
	string? ToolName,
	DateTimeOffset OccurredAtUtc,
	long? DurationMs,
	AgentExecutionStatus Status,
	string? FailureCategory,
	bool Deduplicated,
	bool SideEffectsCommitted,
	bool Late,
	string? PermissionLevel,
	AgentExecutionUsage? Usage);

/// <summary>当前内存里还能看到的合计。只统计仍保留的事件。</summary>
public sealed record AgentExecutionSummary(
	int RealExecutions,
	int Deduplicated,
	long? ModelDurationMs,
	int? PromptTokens,
	int? CompletionTokens,
	int? TotalTokens,
	int? CachedTokens,
	int ReportedUsageRounds)
{
	public static AgentExecutionSummary Empty { get; } = new(0, 0, null, null, null, null, null, 0);
}

/// <summary>一份不可变快照。</summary>
public sealed record AgentExecutionSnapshot(
	long Revision,
	IReadOnlyList<AgentExecutionEvent> Events,
	AgentExecutionSummary Summary)
{
	public static AgentExecutionSnapshot Empty { get; } = new(0, [], AgentExecutionSummary.Empty);
}

/// <summary>
/// 有界的纯内存执行记录。
/// 只观察 <see cref="AgentEngine"/> 已经在做的请求、工具和授权，不决定能不能执行，
/// 也不改变 <see cref="Nori.Core.Tools.ToolExecutionTracker"/> 的去重。
/// </summary>
public sealed class AgentExecutionJournal : IDisposable
{
	public const int DefaultCapacity = 256;
	public const int DefaultMaxEventBytes = 512;
	public const int DefaultMaxRetainedBytes = 48 * 1024;

	private readonly object _gate = new();
	private readonly Queue<StoredEvent> _events = [];
	private readonly Dictionary<string, Dictionary<string, CallSlot>> _slots = new(StringComparer.Ordinal);
	private readonly int _capacity;
	private readonly int _maxEventBytes;
	private readonly int _maxRetainedBytes;
	private long _sequence;
	private long _revision;
	private int _bytes;
	private bool _disposed;

	public AgentExecutionJournal(
		int capacity = DefaultCapacity,
		int maxEventBytes = DefaultMaxEventBytes,
		int maxRetainedBytes = DefaultMaxRetainedBytes)
	{
		if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity), "执行记录容量必须为正数");
		if (maxEventBytes < 32) throw new ArgumentOutOfRangeException(nameof(maxEventBytes), "单条执行记录至少要容纳短字段");
		if (maxRetainedBytes < maxEventBytes) throw new ArgumentOutOfRangeException(nameof(maxRetainedBytes), "总容量不能小于单条上限");
		_capacity = capacity;
		_maxEventBytes = maxEventBytes;
		_maxRetainedBytes = maxRetainedBytes;
	}

	/// <summary>新事件通知。可能发生在任意线程，订阅方不得在其中执行工具。</summary>
	public event Action? Changed;

	public int ListenerCount
	{
		get { lock (_gate) return Changed?.GetInvocationList().Length ?? 0; }
	}

	public AgentExecutionRequest Open(string sessionId)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
		AgentExecutionRequest request;
		Action? changed;
		lock (_gate)
		{
			if (_disposed) return AgentExecutionRequest.Inactive;
			string requestId = "req-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
			request = new AgentExecutionRequest(this, requestId, Text(sessionId, AgentTraceRecord.MaxSessionIdLength, 96));
			Append(request, null, null, AgentExecutionKind.Request, null, null, AgentExecutionStatus.Running,
				null, false, false, false, null, null, false);
			changed = Changed;
		}
		Signal(changed);
		return request;
	}

	public bool TryFinish(
		string requestId, int round, string callId, string? toolName, long durationMs,
		AgentExecutionStatus status, string? failureCategory, bool sideEffectsCommitted)
	{
		bool applied;
		Action? changed;
		lock (_gate)
		{
			if (_disposed || string.IsNullOrWhiteSpace(requestId)) return false;
			applied = Finish(requestId, "", round, callId, toolName, durationMs, status, failureCategory, sideEffectsCommitted);
			changed = Changed;
		}
		Signal(changed);
		return applied;
	}

	public AgentExecutionSnapshot Snapshot()
	{
		lock (_gate)
		{
			AgentExecutionEvent[] events = _events.Select(item => item.Event).ToArray();
			return new AgentExecutionSnapshot(_revision, events, Summarize(events));
		}
	}

	public void Dispose()
	{
		lock (_gate)
		{
			if (_disposed) return;
			_disposed = true;
			Changed = null;
		}
	}

	internal void Model(
		AgentExecutionRequest request, int round, long durationMs, AgentExecutionStatus status,
		string? failureCategory, AgentTraceUsage? usage)
	{
		Action? changed = null;
		lock (_gate)
		{
			if (_disposed || !Owns(request)) return;
			Append(request, round, null, AgentExecutionKind.Model, null, durationMs, status,
				failureCategory, false, false, false, null, AgentExecutionUsage.From(usage), false);
			changed = Changed;
		}
		Signal(changed);
	}

	internal bool ToolPending(AgentExecutionRequest request, int round, string callId, string? toolName)
	{
		bool created = false;
		Action? changed = null;
		lock (_gate)
		{
			if (_disposed || !Owns(request)) return false;
			string id = CallId(callId);
			if (Slot(request.Id, round, id) is not null)
			{
				Append(request, round, id, AgentExecutionKind.Tool, toolName, null, AgentExecutionStatus.Pending,
					"duplicate", true, false, false, null, null, false);
			}
			else
			{
				PutSlot(request.Id, round, id, new CallSlot());
				Append(request, round, id, AgentExecutionKind.Tool, toolName, null, AgentExecutionStatus.Pending,
					null, false, false, false, null, null, false);
				created = true;
			}
			changed = Changed;
		}
		Signal(changed);
		return created;
	}

	internal void ToolStarted(AgentExecutionRequest request, int round, string callId, string? toolName)
	{
		Action? changed = null;
		lock (_gate)
		{
			if (_disposed || !Owns(request)) return;
			string id = CallId(callId);
			CallSlot? slot = Slot(request.Id, round, id);
			if (slot is null)
			{
				PutSlot(request.Id, round, id, new CallSlot { Status = AgentExecutionStatus.Running, RealExecution = true });
				Append(request, round, id, AgentExecutionKind.Tool, toolName, null, AgentExecutionStatus.Running,
					null, false, false, false, null, null, false);
			}
			else if (slot.Terminal || slot.Suppressed || slot.RealExecution)
			{
				Append(request, round, id, AgentExecutionKind.Tool, toolName, null, AgentExecutionStatus.Running,
					slot.RealExecution ? "duplicate" : null, slot.RealExecution, false, slot.Terminal || slot.Suppressed, null, null, false);
			}
			else
			{
				slot.Status = AgentExecutionStatus.Running;
				slot.RealExecution = true;
				Append(request, round, id, AgentExecutionKind.Tool, toolName, null, AgentExecutionStatus.Running,
					null, false, false, false, null, null, false);
			}
			changed = Changed;
		}
		Signal(changed);
	}

	internal void Permission(
		AgentExecutionRequest request, int round, string callId, string? toolName, string? permissionLevel,
		AgentExecutionStatus status, string? category)
	{
		Action? changed = null;
		lock (_gate)
		{
			if (_disposed || !Owns(request)) return;
			string id = CallId(callId);
			CallSlot? slot = Slot(request.Id, round, id);
			bool late = slot is null || slot.Terminal || slot.PermissionTerminal;
			if (slot is not null && !late && status != AgentExecutionStatus.Pending) slot.PermissionTerminal = true;
			Append(request, round, id, AgentExecutionKind.Permission, toolName, null, status,
				category, false, false, late, permissionLevel, null, false);
			changed = Changed;
		}
		Signal(changed);
	}

	internal void Deduplicated(AgentExecutionRequest request, int round, string callId, string? toolName)
	{
		Action? changed = null;
		lock (_gate)
		{
			if (_disposed || !Owns(request)) return;
			string id = CallId(callId);
			if (Slot(request.Id, round, id) is { Terminal: false, RealExecution: false } slot) slot.Suppressed = true;
			Append(request, round, id, AgentExecutionKind.Tool, toolName, null, AgentExecutionStatus.Pending,
				"duplicate", true, false, false, null, null, false);
			changed = Changed;
		}
		Signal(changed);
	}

	internal void End(AgentExecutionRequest request, long durationMs, AgentExecutionStatus status, string? failureCategory)
	{
		Action? changed = null;
		lock (_gate)
		{
			if (_disposed || !Owns(request)) return;
			if (!request.TryMarkEnded())
			{
				Append(request, null, null, AgentExecutionKind.Session, null, durationMs, status,
					failureCategory, false, false, true, null, null, false);
			}
			else
			{
				Append(request, null, null, AgentExecutionKind.Session, null, durationMs, status,
					failureCategory, false, false, false, null, null, false);
				if (_slots.TryGetValue(request.Id, out Dictionary<string, CallSlot>? slots))
				{
					foreach ((string key, CallSlot slot) in slots.ToArray())
					{
						if (slot.Terminal || slot.Suppressed || slot.RealExecution || slot.Status != AgentExecutionStatus.Pending) continue;
						slot.Terminal = true;
						slot.Status = AgentExecutionStatus.Cancelled;
						(int round, string callId) = SplitSlotKey(key);
						Append(request, round, callId, AgentExecutionKind.Tool, null, null, AgentExecutionStatus.Cancelled,
							"not_started", false, false, false, null, null, false);
					}
				}
			}
			changed = Changed;
		}
		Signal(changed);
	}

	private bool Finish(
		string requestId, string sessionId, int round, string callId, string? toolName, long durationMs,
		AgentExecutionStatus status, string? failureCategory, bool sideEffectsCommitted)
	{
		string id = CallId(callId);
		CallSlot? slot = Slot(requestId, round, id);
		bool late = slot is null || slot.Terminal || slot.Suppressed;
		bool committed = !late && status == AgentExecutionStatus.Success && sideEffectsCommitted;
		if (!late && slot is not null)
		{
			slot.Terminal = true;
			slot.Status = status;
			slot.SideEffectsCommitted = committed;
		}
		Append(new AgentExecutionRequest(this, requestId, sessionId), round, id, AgentExecutionKind.Tool, toolName,
			Math.Max(0, durationMs), status, failureCategory, false, committed, late, null, null, true);
		return !late;
	}

	private bool Owns(AgentExecutionRequest request) => ReferenceEquals(request.Journal, this) && request.Id.Length > 0;

	private CallSlot? Slot(string requestId, int round, string callId)
	{
		if (!_slots.TryGetValue(requestId, out Dictionary<string, CallSlot>? slots)) return null;
		slots.TryGetValue(SlotKey(round, callId), out CallSlot? slot);
		return slot;
	}

	private void PutSlot(string requestId, int round, string callId, CallSlot slot)
	{
		if (!_slots.TryGetValue(requestId, out Dictionary<string, CallSlot>? slots))
		{
			slots = new Dictionary<string, CallSlot>(StringComparer.Ordinal);
			_slots[requestId] = slots;
		}
		slots[SlotKey(round, callId)] = slot;
	}

	private void Append(
		AgentExecutionRequest request, int? round, string? callId, AgentExecutionKind kind, string? toolName,
		long? durationMs, AgentExecutionStatus status, string? failureCategory, bool deduplicated,
		bool sideEffectsCommitted, bool late, string? permissionLevel, AgentExecutionUsage? usage, bool preserveSession)
	{
		string sessionId = preserveSession && request.SessionId.Length == 0
			? SessionIdOf(request.Id)
			: request.SessionId;
		AgentExecutionEvent fitted = Fit(new AgentExecutionEvent(
			++_sequence,
			request.Id,
			sessionId,
			round is >= 0 ? round : null,
			string.IsNullOrEmpty(callId) ? null : callId,
			kind,
			string.IsNullOrEmpty(toolName) ? null : Text(toolName, AgentTraceRecord.MaxToolNameLength, 120),
			DateTimeOffset.UtcNow,
			durationMs is null ? null : Math.Max(0, durationMs.Value),
			status,
			Category(failureCategory),
			deduplicated,
			sideEffectsCommitted,
			late,
			kind == AgentExecutionKind.Permission ? Level(permissionLevel) : null,
			FitUsage(usage)));
		int size = Size(fitted);
		_events.Enqueue(new StoredEvent(fitted, size));
		_bytes += size;
		_revision++;
		while (_events.Count > _capacity || (_events.Count > 1 && _bytes > _maxRetainedBytes))
		{
			StoredEvent old = _events.Dequeue();
			_bytes -= old.Bytes;
			if (!_events.Any(item => item.Event.RequestId == old.Event.RequestId)) _slots.Remove(old.Event.RequestId);
		}
	}

	private void Signal(Action? changed)
	{
		try { changed?.Invoke(); }
		catch { /* 观察者失败不能影响正在执行的工具。 */ }
	}

	private string SessionIdOf(string requestId)
	{
		foreach (StoredEvent item in _events)
		{
			if (item.Event.RequestId == requestId && item.Event.SessionId.Length > 0) return item.Event.SessionId;
		}
		return "";
	}

	private AgentExecutionEvent Fit(AgentExecutionEvent ev)
	{
		if (Size(ev) <= _maxEventBytes) return ev;
		string? tool = ev.ToolName;
		string? call = ev.ToolCallId;
		string session = ev.SessionId;
		while (Size(ev) > _maxEventBytes && (tool is { Length: > 0 } || call is { Length: > 0 } || session.Length > 0))
		{
			if (tool is { Length: > 0 }) tool = tool[..^1];
			else if (call is { Length: > 0 }) call = call[..^1];
			else session = session[..^1];
			ev = ev with { ToolName = NullIfEmpty(tool), ToolCallId = NullIfEmpty(call), SessionId = session };
		}
		return ev;
	}

	private AgentExecutionUsage? FitUsage(AgentExecutionUsage? usage)
	{
		if (usage is null) return null;
		string? model = string.IsNullOrEmpty(usage.Model) ? null : Text(usage.Model, AgentTraceRecord.MaxModelLength, 80);
		return usage with { Model = model };
	}

	private static AgentExecutionSummary Summarize(IReadOnlyList<AgentExecutionEvent> events)
	{
		int real = 0;
		int dedup = 0;
		long? modelMs = null;
		int? prompt = null;
		int? completion = null;
		int? total = null;
		int? cached = null;
		int rounds = 0;
		foreach (AgentExecutionEvent ev in events)
		{
			if (ev.Deduplicated) dedup++;
			if (ev.Kind == AgentExecutionKind.Tool && ev.Status == AgentExecutionStatus.Running && !ev.Deduplicated && !ev.Late)
				real++;
			if (ev.Kind == AgentExecutionKind.Model && ev.DurationMs is long duration)
				modelMs = (modelMs ?? 0) + duration;
			if (ev.Usage is not { } usage) continue;
			rounds++;
			prompt = (prompt ?? 0) + usage.PromptTokens;
			completion = (completion ?? 0) + usage.CompletionTokens;
			total = (total ?? 0) + usage.TotalTokens;
			cached = (cached ?? 0) + usage.CachedTokens;
		}
		return new AgentExecutionSummary(real, dedup, modelMs, prompt, completion, total, cached, rounds);
	}

	private static int Size(AgentExecutionEvent ev)
	{
		int size = Bytes(ev.RequestId) + Bytes(ev.SessionId) + Bytes(ev.ToolCallId) + Bytes(ev.ToolName)
			+ Bytes(ev.FailureCategory) + Bytes(ev.PermissionLevel);
		if (ev.Usage?.Model is { } model) size += Bytes(model);
		return Math.Max(1, size);
	}

	private static int Bytes(string? value) => string.IsNullOrEmpty(value) ? 0 : Encoding.UTF8.GetByteCount(value);

	private string? Category(string? value)
	{
		string text = Text(value, AgentTraceRecord.MaxFailureCategoryLength, 48);
		if (text.Length == 0) return null;
		foreach (char character in text)
		{
			if (char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' or ':') continue;
			return "unclassified";
		}
		return text;
	}

	private string Text(string? value, int maxChars, int maxBytes)
	{
		if (string.IsNullOrWhiteSpace(value)) return "";
		string stripped = Strip(value.Trim());
		string redacted = AgentTraceRecord.Sanitize(stripped, maxChars) ?? "";
		return CutBytes(redacted, Math.Min(maxBytes, _maxEventBytes));
	}

	private static string CallId(string? value)
	{
		if (string.IsNullOrWhiteSpace(value)) return "call";
		string trimmed = Strip(value.Trim());
		if (trimmed.Length is > 0 and <= 160 && SafeToken(trimmed)) return trimmed;
		byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(trimmed));
		return "id-" + Convert.ToHexString(hash)[..16];
	}

	private static string Level(string? value)
	{
		string text = (value ?? "").Trim().ToLowerInvariant();
		return text is "safe" or "confirm" or "dangerous" ? text : "unknown";
	}

	private static bool SafeToken(string value)
	{
		foreach (char character in value)
		{
			if (char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' or ':') continue;
			return false;
		}
		return true;
	}

	private static string Strip(string value)
	{
		StringBuilder builder = new(value.Length);
		foreach (char character in value)
		{
			if (character < ' ' || character == '\u007f') continue;
			if (character is >= '\u200b' and <= '\u200f' or >= '\u202a' and <= '\u202e' or >= '\u2066' and <= '\u2069' or '\ufeff')
				continue;
			builder.Append(character);
		}
		return builder.ToString().Trim();
	}

	private static string CutBytes(string value, int maxBytes)
	{
		if (Encoding.UTF8.GetByteCount(value) <= maxBytes) return value;
		StringBuilder builder = new(value.Length);
		int used = 0;
		foreach (char character in value)
		{
			int next = Encoding.UTF8.GetByteCount(character.ToString());
			if (used + next > maxBytes) break;
			builder.Append(character);
			used += next;
		}
		return builder.ToString();
	}

	private static string SlotKey(int round, string callId) =>
		round.ToString(CultureInfo.InvariantCulture) + "\u001f" + callId;

	private static (int Round, string CallId) SplitSlotKey(string key)
	{
		int split = key.IndexOf('\u001f');
		int round = int.Parse(key[..split], CultureInfo.InvariantCulture);
		return (round, key[(split + 1)..]);
	}

	private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;

	private sealed class CallSlot
	{
		public AgentExecutionStatus Status = AgentExecutionStatus.Pending;
		public bool RealExecution;
		public bool Terminal;
		public bool Suppressed;
		public bool PermissionTerminal;
		public bool SideEffectsCommitted;
	}

	private readonly record struct StoredEvent(AgentExecutionEvent Event, int Bytes);
}

/// <summary>一次请求的记录入口。方法失败时不影响调用方的工具执行。</summary>
public sealed class AgentExecutionRequest
{
	private int _ended;

	internal AgentExecutionRequest(AgentExecutionJournal journal, string id, string sessionId)
	{
		Journal = journal;
		Id = id;
		SessionId = sessionId;
	}

	private AgentExecutionRequest()
	{
		Id = "";
		SessionId = "";
	}

	public static AgentExecutionRequest Inactive { get; } = new();

	internal AgentExecutionJournal? Journal { get; }
	public string Id { get; }
	internal string SessionId { get; }
	internal bool HasEnded => Volatile.Read(ref _ended) != 0;

	public void Model(int round, long durationMs, AgentExecutionStatus status, string? failureCategory, AgentTraceUsage? usage) =>
		Guard(() => Journal?.Model(this, round, durationMs, status, failureCategory, usage));

	public bool ToolPending(int round, string callId, string? toolName)
	{
		bool created = false;
		Guard(() => created = Journal?.ToolPending(this, round, callId, toolName) ?? false);
		return created;
	}

	public void ToolStarted(int round, string callId, string? toolName) =>
		Guard(() => Journal?.ToolStarted(this, round, callId, toolName));

	public void Permission(
		int round, string callId, string? toolName, string? permissionLevel, AgentExecutionStatus status, string? category) =>
		Guard(() => Journal?.Permission(this, round, callId, toolName, permissionLevel, status, category));

	public void Deduplicated(int round, string callId, string? toolName) =>
		Guard(() => Journal?.Deduplicated(this, round, callId, toolName));

	public bool FinishTool(
		int round, string callId, string? toolName, long durationMs, AgentExecutionStatus status,
		string? failureCategory, bool sideEffectsCommitted)
	{
		bool applied = false;
		Guard(() => applied = Journal?.TryFinish(Id, round, callId, toolName, durationMs, status, failureCategory, sideEffectsCommitted) ?? false);
		return applied;
	}

	public void End(long durationMs, AgentExecutionStatus status, string? failureCategory) =>
		Guard(() => Journal?.End(this, durationMs, status, failureCategory));

	internal bool TryMarkEnded() => Interlocked.Exchange(ref _ended, 1) == 0;

	private static void Guard(Action action)
	{
		try { action(); }
		catch { /* 记录失败不改变对话、授权或工具副作用。 */ }
	}
}
