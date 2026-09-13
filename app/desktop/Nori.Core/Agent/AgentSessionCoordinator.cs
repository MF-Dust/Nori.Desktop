namespace Nori.Core.Agent;

/// <summary>Agent 已有活动会话时的明确错误。</summary>
public sealed class AgentSessionBusyException(string sessionId)
	: InvalidOperationException($"已有 Agent 会话正在运行: {sessionId}");

/// <summary>
/// 核心层的单活动会话闸门。调用方必须持有返回的 lease 直到任务结束；不会自动取消或覆盖旧会话，
/// 避免新的请求在旧请求仍可能执行工具时造成重复副作用。
/// </summary>
public sealed class AgentSessionCoordinator : IDisposable
{
	private readonly object _gate = new();
	private ActiveSession? _active;
	private bool _disposed;

	/// <summary>当前活动会话 ID；没有活动会话时为 null。</summary>
	public string? ActiveSessionId
	{
		get
		{
			lock (_gate) return _active?.Id;
		}
	}

	/// <summary>尝试开始一个活动会话；已有会话时 fail-closed 拒绝。</summary>
	public AgentSessionLease Start(string sessionId, CancellationToken cancellationToken = default)
	{
		if (string.IsNullOrWhiteSpace(sessionId)) throw new ArgumentException("会话 ID 不能为空", nameof(sessionId));
		lock (_gate)
		{
			ObjectDisposedException.ThrowIf(_disposed, this);
			if (_active is not null) throw new AgentSessionBusyException(_active.Id);
			CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
			ActiveSession active = new(sessionId, linked);
			_active = active;
			return new AgentSessionLease(this, active);
		}
	}

	/// <summary>取消指定活动会话。</summary>
	public bool Cancel(string sessionId)
	{
		lock (_gate)
		{
			if (_active is null || !string.Equals(_active.Id, sessionId, StringComparison.Ordinal)) return false;
			_active.Cancellation.Cancel();
			return true;
		}
	}

	internal void Complete(ActiveSession active)
	{
		lock (_gate)
		{
			if (ReferenceEquals(_active, active)) _active = null;
			active.Cancellation.Dispose();
		}
	}

	public void Dispose()
	{
		lock (_gate)
		{
			if (_disposed) return;
			_disposed = true;
			_active?.Cancellation.Cancel();
			_active = null;
		}
	}

	internal sealed class ActiveSession(string id, CancellationTokenSource cancellation)
	{
		public string Id { get; } = id;
		public CancellationTokenSource Cancellation { get; } = cancellation;
	}
}

/// <summary>Agent 活动会话的生命周期 lease。</summary>
public sealed class AgentSessionLease : IDisposable
{
	private readonly AgentSessionCoordinator _owner;
	private readonly AgentSessionCoordinator.ActiveSession _active;
	private int _disposed;

	internal AgentSessionLease(AgentSessionCoordinator owner, AgentSessionCoordinator.ActiveSession active)
	{
		_owner = owner;
		_active = active;
	}

	internal bool IsOwnedBy(AgentSessionCoordinator owner) => ReferenceEquals(_owner, owner) && Volatile.Read(ref _disposed) == 0;

	/// <summary>包含调用方取消信号的会话 token。</summary>
	public CancellationToken CancellationToken => _active.Cancellation.Token;

	/// <summary>会话 ID。</summary>
	public string SessionId => _active.Id;

	public void Dispose()
	{
		if (Interlocked.Exchange(ref _disposed, 1) == 0) _owner.Complete(_active);
	}
}
