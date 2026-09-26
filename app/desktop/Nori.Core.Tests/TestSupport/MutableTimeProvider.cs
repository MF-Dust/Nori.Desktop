namespace Nori.Core.Tests.TestSupport;

/// <summary>可手动推进 UTC 时间并触发到期计时器的测试时钟。</summary>
internal sealed class MutableTimeProvider : TimeProvider, IDisposable
{
	private readonly Lock _gate = new();
	private DateTimeOffset _utcNow;
	private readonly List<RegisteredTimer> _timers = [];

	public MutableTimeProvider(DateTimeOffset? start = null) => _utcNow = start ?? DateTimeOffset.UtcNow;

	public override DateTimeOffset GetUtcNow()
	{
		lock (_gate) return _utcNow;
	}

	public void Advance(TimeSpan amount)
	{
		lock (_gate)
		{
			_utcNow += amount;
			FireDueTimers();
		}
	}

	public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
	{
		lock (_gate)
		{
			RegisteredTimer timer = new(this, callback, state, _utcNow + dueTime, period);
			_timers.Add(timer);
			return timer;
		}
	}

	private void Unregister(RegisteredTimer timer)
	{
		lock (_gate) _timers.Remove(timer);
	}

	private void FireDueTimers()
	{
		foreach (RegisteredTimer timer in _timers.ToArray())
		{
			if (!timer.IsDue(_utcNow)) continue;
			timer.Invoke();
			if (timer.Period > TimeSpan.Zero) timer.Reschedule(_utcNow + timer.Period);
			else timer.Dispose();
		}
	}

	public void Dispose()
	{
		lock (_gate)
		{
			foreach (RegisteredTimer timer in _timers) timer.Dispose();
			_timers.Clear();
		}
	}

	private sealed class RegisteredTimer : ITimer
	{
		private readonly MutableTimeProvider _owner;
		private readonly TimerCallback _callback;
		private readonly object? _state;
		private DateTimeOffset _due;
		private bool _disposed;

		public RegisteredTimer(MutableTimeProvider owner, TimerCallback callback, object? state, DateTimeOffset due, TimeSpan period)
		{
			_owner = owner;
			_callback = callback;
			_state = state;
			_due = due;
			Period = period;
		}

		public TimeSpan Period { get; private set; }

		public bool Change(TimeSpan dueTime, TimeSpan period)
		{
			lock (_owner._gate)
			{
				if (_disposed) return false;
				_due = _owner._utcNow + dueTime;
				Period = period;
				return true;
			}
		}

		public void Dispose()
		{
			lock (_owner._gate)
			{
				if (_disposed) return;
				_disposed = true;
				_owner.Unregister(this);
			}
		}

		public ValueTask DisposeAsync()
		{
			Dispose();
			return ValueTask.CompletedTask;
		}

		public bool IsDue(DateTimeOffset now) => !_disposed && now >= _due;

		public void Invoke() => _callback(_state);

		public void Reschedule(DateTimeOffset due) => _due = due;
	}
}
