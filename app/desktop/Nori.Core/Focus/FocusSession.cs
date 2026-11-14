using System.Text.Json;

namespace Nori.Core.Focus;

/// <summary>专注阶段。进程退出时进行中的一场会落成暂停，不会靠墙钟把离开的时间算进去。</summary>
public enum FocusPhase
{
	/// <summary>没有进行中的专注。</summary>
	Idle,

	/// <summary>计时中。</summary>
	Running,

	/// <summary>用户暂停，或进程退出后等待继续。</summary>
	Paused,

	/// <summary>计时走完，等待用户开始下一场或结束查看。</summary>
	Completed,
}

/// <summary>一条只留在本机的专注记录。</summary>
public sealed record FocusRecord(long EndedAtUnixMs, int Seconds, bool Completed);

/// <summary>给设置页和快照看的当前专注状态。</summary>
public sealed record FocusView(
	string Phase,
	int DurationSeconds,
	int ElapsedSeconds,
	int RemainingSeconds,
	bool NotifyOnComplete,
	bool SilentReminders,
	int CompletedCount,
	int TotalSeconds,
	IReadOnlyList<FocusRecord> Recent);

/// <summary>
/// 专注计时。
///
/// 已用时长只累加单调时钟的正向间隔。暂停、进程退出和时钟回拨都不把时间减回去，
/// 也不会把同一段重复计算。记录写在包内 data/core/focus，不进配置库，因此也不进云端存档白名单。
/// 安全模式可以读到上次的记录并修正已经走完的一场，但不能把计时重新跑起来。
/// </summary>
public sealed class FocusSession : IDisposable
{
	/// <summary>最短专注，分钟。</summary>
	public const int MinMinutes = 1;

	/// <summary>最长专注，分钟。三小时足够覆盖一次学习，也避开定时器的毫秒上限。</summary>
	public const int MaxMinutes = 180;

	/// <summary>最近记录保留条数。</summary>
	public const int RecentLimit = 8;

	/// <summary>状态文件上限。超过就当作损坏，从空闲开始，不拖垮启动。</summary>
	public const int MaxStateBytes = 64 * 1024;

	private static readonly JsonSerializerOptions JsonOptions = new()
	{
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		PropertyNameCaseInsensitive = true,
		WriteIndented = false,
	};

	private readonly string _path;
	private readonly IFocusClock _clock;
	private readonly bool _background;
	private readonly Lock _gate = new();
	private System.Threading.Timer? _timer;
	private bool _armed;
	private FocusPhase _phase = FocusPhase.Idle;
	private int _durationSeconds;
	private double _accumulatedSeconds;
	private long _runStamp;
	private bool _notifyOnComplete = true;
	private bool _silentReminders = true;
	private bool _completionNotified;
	private bool _pendingNotice;
	private int _completedCount;
	private double _totalSeconds;
	private List<FocusRecord> _recent = [];
	private bool _disposed;

	/// <summary>阶段、累计时长或完成通知发生变化。</summary>
	public event Action? Changed;

	/// <summary>创建会话并读入上次留下的记录。读不到或内容损坏时从空闲开始，不抛到启动流程。</summary>
	public FocusSession(string statePath, IFocusClock clock, bool background)
	{
		if (string.IsNullOrWhiteSpace(statePath)) throw new ArgumentException("专注记录路径不能为空", nameof(statePath));
		_path = statePath;
		_clock = clock ?? throw new ArgumentNullException(nameof(clock));
		_background = background;
		Load();
	}

	/// <summary>当前是否挡住主动关怀。暂停也算还在专注，完成后不再挡住。</summary>
	public FocusPresence Presence
	{
		get
		{
			lock (_gate)
			{
				bool active = _phase is FocusPhase.Running or FocusPhase.Paused;
				return new FocusPresence(active, active && _silentReminders);
			}
		}
	}

	/// <summary>是否已经排了到点回调。安全模式和暂停都不会排。</summary>
	internal bool IsScheduleArmed
	{
		get { lock (_gate) return _armed; }
	}

	/// <summary>开始一场新的专注。已有进行中或暂停的一场时拒绝，避免覆盖还没结束的计时。</summary>
	public void Start(int minutes, bool notifyOnComplete, bool silentReminders)
	{
		Action? changed = null;
		lock (_gate)
		{
			ThrowIfDisposed();
			EnsureInteractive();
			if (minutes < MinMinutes || minutes > MaxMinutes)
				throw new InvalidOperationException("专注时长需要在 1 到 180 分钟之间。");
			if (_phase is FocusPhase.Running or FocusPhase.Paused)
				throw new InvalidOperationException("已经有一场专注在进行。");
			_phase = FocusPhase.Running;
			_durationSeconds = minutes * 60;
			_accumulatedSeconds = 0;
			_runStamp = _clock.Now;
			_notifyOnComplete = notifyOnComplete;
			_silentReminders = silentReminders;
			_completionNotified = false;
			_pendingNotice = false;
			Arm(_durationSeconds);
			Save();
			changed = Changed;
		}
		Notify(changed);
	}

	/// <summary>暂停。已用时长停在这一刻，之后的时钟变化不再计入。</summary>
	public void Pause()
	{
		Action? changed = null;
		lock (_gate)
		{
			ThrowIfDisposed();
			if (_phase != FocusPhase.Running) throw new InvalidOperationException("当前没有进行中的专注。");
			CommitElapsed();
			_phase = FocusPhase.Paused;
			_runStamp = 0;
			Disarm();
			Save();
			changed = Changed;
		}
		Notify(changed);
	}

	/// <summary>从暂停继续。安全模式不会把后台计时重新跑起来。</summary>
	public void Resume()
	{
		Action? changed = null;
		lock (_gate)
		{
			ThrowIfDisposed();
			EnsureInteractive();
			if (_phase != FocusPhase.Paused) throw new InvalidOperationException("当前没有暂停的专注。");
			_phase = FocusPhase.Running;
			_runStamp = _clock.Now;
			Arm(Math.Max(0, _durationSeconds - _accumulatedSeconds));
			Save();
			changed = Changed;
		}
		Notify(changed);
		Evaluate();
	}

	/// <summary>用户结束。未走完的时长记入累计，但不算完成，也不发完成通知。</summary>
	public void End()
	{
		Action? changed = null;
		lock (_gate)
		{
			ThrowIfDisposed();
			if (_phase is FocusPhase.Idle) throw new InvalidOperationException("当前没有进行中的专注。");
			if (_phase is FocusPhase.Running or FocusPhase.Paused)
			{
				double elapsed = Math.Min(CommitElapsed(), _durationSeconds);
				if (elapsed >= 1)
				{
					_totalSeconds += elapsed;
					AddRecent((int)Math.Round(elapsed), completed: false);
				}
			}
			ClearActive();
			Save();
			changed = Changed;
		}
		Notify(changed);
	}

	/// <summary>到点时收尾。测试也可以直接推进时钟后调用。提前醒来会按剩余时间再约一次。</summary>
	public void Evaluate()
	{
		Action? changed = null;
		lock (_gate)
		{
			if (_disposed || _phase != FocusPhase.Running) return;
			if (CommitElapsed() + 0.001 < _durationSeconds)
			{
				Arm(Math.Max(0, _durationSeconds - _accumulatedSeconds));
				return;
			}
			CompleteUnlocked();
			changed = Changed;
		}
		Notify(changed);
	}

	/// <summary>把进行中的已用时长写盘。给既有 30 秒调度顺带调用，崩溃时最多丢掉这一段还没写下来的时间。</summary>
	public void Checkpoint()
	{
		Action? changed = null;
		lock (_gate)
		{
			if (_disposed || _phase != FocusPhase.Running) return;
			if (CommitElapsed() + 0.001 >= _durationSeconds)
			{
				CompleteUnlocked();
				changed = Changed;
			}
			else
			{
				Save();
			}
		}
		Notify(changed);
	}

	/// <summary>取走一次完成通知。安全模式只保留标记，等下次正常启动再交给用户。</summary>
	public string? ConsumeCompletionNotice(bool english)
	{
		lock (_gate)
		{
			if (_disposed || !_background || !_pendingNotice) return null;
			_pendingNotice = false;
			_completionNotified = true;
			Save();
			return FocusText.Completion(english);
		}
	}

	/// <summary>当前状态的只读副本。</summary>
	public FocusView View()
	{
		lock (_gate)
		{
			if (_phase == FocusPhase.Running) CommitElapsed();
			return ViewUnlocked();
		}
	}

	/// <summary>到点回调要等多久。负值和过大的剩余时间都收进定时器能表达的范围。</summary>
	internal static TimeSpan DueAfter(double remainingSeconds)
	{
		double dueMs = double.IsFinite(remainingSeconds) ? Math.Ceiling(remainingSeconds * 1000d) : 0;
		if (dueMs < 0) dueMs = 0;
		if (dueMs > int.MaxValue) dueMs = int.MaxValue;
		return TimeSpan.FromMilliseconds(dueMs);
	}

	/// <summary>停止计时。进行中的一场按暂停写入，重启后从这里继续，而不是把关机时间算进去。</summary>
	public void Dispose()
	{
		System.Threading.Timer? timer;
		lock (_gate)
		{
			if (_disposed) return;
			if (_phase == FocusPhase.Running)
			{
				CommitElapsed();
				_phase = FocusPhase.Paused;
				_runStamp = 0;
				Save();
			}
			_disposed = true;
			_armed = false;
			timer = _timer;
			_timer = null;
		}
		timer?.Dispose();
	}

	private void EnsureInteractive()
	{
		if (!_background) throw new InvalidOperationException("安全模式下不能开始或继续专注。");
	}

	private void CompleteUnlocked()
	{
		_accumulatedSeconds = _durationSeconds;
		_phase = FocusPhase.Completed;
		_runStamp = 0;
		_totalSeconds += _durationSeconds;
		_completedCount++;
		AddRecent(_durationSeconds, completed: true);
		if (_notifyOnComplete && !_completionNotified) _pendingNotice = true;
		Disarm();
		Save();
	}

	private void ClearActive()
	{
		_phase = FocusPhase.Idle;
		_durationSeconds = 0;
		_accumulatedSeconds = 0;
		_runStamp = 0;
		_pendingNotice = false;
		Disarm();
	}

	/// <summary>
	/// 把单调时钟自上次提交以来的正向间隔累加进去。
	/// 终点不晚于起点时保持原值，也不把起点往回拨，避免时钟先回退再追上时把同一段算两次。
	/// </summary>
	private double CommitElapsed()
	{
		if (_phase == FocusPhase.Running)
		{
			long now = _clock.Now;
			double delta = _clock.SecondsBetween(_runStamp, now);
			if (delta > 0)
			{
				_accumulatedSeconds += delta;
				_runStamp = now;
			}
		}
		return Math.Max(0, _accumulatedSeconds);
	}

	private FocusView ViewUnlocked()
	{
		double elapsed = _phase == FocusPhase.Completed ? _durationSeconds : _accumulatedSeconds;
		int elapsedSeconds = (int)Math.Floor(Math.Max(0, elapsed));
		int remaining = _phase is FocusPhase.Idle or FocusPhase.Completed
			? 0
			: (int)Math.Ceiling(Math.Max(0, _durationSeconds - elapsed));
		return new FocusView(
			PhaseName(_phase),
			_durationSeconds,
			elapsedSeconds,
			remaining,
			_notifyOnComplete,
			_silentReminders,
			_completedCount,
			(int)Math.Round(_totalSeconds, MidpointRounding.AwayFromZero),
			_recent.ToArray());
	}

	private void AddRecent(int seconds, bool completed)
	{
		int stored = Math.Clamp(seconds, 0, MaxMinutes * 60);
		_recent.Insert(0, new FocusRecord(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), stored, completed));
		if (_recent.Count > RecentLimit) _recent.RemoveRange(RecentLimit, _recent.Count - RecentLimit);
	}

	private void Arm(double remainingSeconds)
	{
		if (!_background || _disposed) return;
		_timer ??= new System.Threading.Timer(_ =>
		{
			try { Evaluate(); }
			catch
			{
				// 计时线程不能把异常带出线程池。下一次检查点仍会收尾。
			}
		}, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
		_timer.Change(DueAfter(remainingSeconds), Timeout.InfiniteTimeSpan);
		_armed = true;
	}

	private void Disarm()
	{
		_armed = false;
		_timer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
	}

	private void Load()
	{
		StoredFocus stored;
		try
		{
			if (!File.Exists(_path)) return;
			FileInfo info = new(_path);
			if (info.Length <= 0 || info.Length > MaxStateBytes) return;
			stored = JsonSerializer.Deserialize<StoredFocus>(File.ReadAllText(_path), JsonOptions) ?? new StoredFocus();
		}
		catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
		{
			return;
		}

		_durationSeconds = Math.Clamp(stored.DurationSeconds, 0, MaxMinutes * 60);
		_accumulatedSeconds = double.IsFinite(stored.AccumulatedSeconds) ? Math.Max(0, stored.AccumulatedSeconds) : 0;
		if (_accumulatedSeconds > _durationSeconds) _accumulatedSeconds = _durationSeconds;
		_notifyOnComplete = stored.NotifyOnComplete;
		_silentReminders = stored.SilentReminders;
		_completionNotified = stored.CompletionNotified;
		_completedCount = Math.Max(0, stored.CompletedCount);
		_totalSeconds = double.IsFinite(stored.TotalSeconds) ? Math.Max(0, stored.TotalSeconds) : 0;
		_recent = stored.Recent?.Where(item => item is not null)
			.Select(item => new FocusRecord(item.EndedAtUnixMs, Math.Clamp(item.Seconds, 0, MaxMinutes * 60), item.Completed))
			.Take(RecentLimit)
			.ToList() ?? [];
		FocusPhase storedPhase = ParsePhase(stored.Phase);
		bool durationValid = _durationSeconds >= MinMinutes * 60 && _durationSeconds <= MaxMinutes * 60;
		if (!durationValid)
		{
			_phase = FocusPhase.Idle;
			_durationSeconds = 0;
			_accumulatedSeconds = 0;
			_pendingNotice = false;
			if (storedPhase != FocusPhase.Idle) Save();
			return;
		}

		// 单调时钟过不了进程边界。上次没暂停就退出的，恢复成暂停，已写盘的时长保留。
		_phase = storedPhase == FocusPhase.Running ? FocusPhase.Paused : storedPhase;
		_runStamp = 0;
		if (_phase == FocusPhase.Paused && _accumulatedSeconds + 0.001 >= _durationSeconds)
		{
			// 到点前崩溃或退出时，补上这一场的完成统计。通知仍等正常模式来取。
			CompleteUnlocked();
			return;
		}
		if (_phase == FocusPhase.Completed && _notifyOnComplete && !_completionNotified) _pendingNotice = true;
		if (_phase != storedPhase) Save();
	}

	private void Save()
	{
		try
		{
			string? directory = Path.GetDirectoryName(_path);
			if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
			StoredFocus stored = new()
			{
				Version = 1,
				Phase = PhaseName(_phase),
				DurationSeconds = _durationSeconds,
				AccumulatedSeconds = _phase == FocusPhase.Running ? CommitElapsed() : _accumulatedSeconds,
				NotifyOnComplete = _notifyOnComplete,
				SilentReminders = _silentReminders,
				CompletionNotified = _completionNotified,
				CompletedCount = _completedCount,
				TotalSeconds = _totalSeconds,
				Recent = _recent.Select(item => new StoredRecord
				{
					EndedAtUnixMs = item.EndedAtUnixMs,
					Seconds = item.Seconds,
					Completed = item.Completed,
				}).ToList(),
			};
			string json = JsonSerializer.Serialize(stored, JsonOptions);
			string temporary = _path + ".tmp";
			File.WriteAllText(temporary, json);
			File.Move(temporary, _path, overwrite: true);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			// 这一轮写盘失败时内存状态仍在；下次暂停、继续或检查点会再试。
		}
	}

	private void ThrowIfDisposed()
	{
		if (_disposed) throw new InvalidOperationException("专注计时已经关闭。");
	}

	private static void Notify(Action? changed)
	{
		if (changed is null) return;
		foreach (Delegate handler in changed.GetInvocationList())
		{
			try { ((Action)handler)(); }
			catch
			{
				// 订阅方失败不能回滚已经写盘的计时，也不能挡住随后的提醒。
			}
		}
	}

	private static FocusPhase ParsePhase(string? phase) => phase switch
	{
		"running" => FocusPhase.Running,
		"paused" => FocusPhase.Paused,
		"completed" => FocusPhase.Completed,
		_ => FocusPhase.Idle,
	};

	private static string PhaseName(FocusPhase phase) => phase switch
	{
		FocusPhase.Running => "running",
		FocusPhase.Paused => "paused",
		FocusPhase.Completed => "completed",
		_ => "idle",
	};

	private sealed class StoredFocus
	{
		public int Version { get; set; } = 1;
		public string Phase { get; set; } = "idle";
		public int DurationSeconds { get; set; }
		public double AccumulatedSeconds { get; set; }
		public bool NotifyOnComplete { get; set; } = true;
		public bool SilentReminders { get; set; } = true;
		public bool CompletionNotified { get; set; }
		public int CompletedCount { get; set; }
		public double TotalSeconds { get; set; }
		public List<StoredRecord> Recent { get; set; } = [];
	}

	private sealed class StoredRecord
	{
		public long EndedAtUnixMs { get; set; }
		public int Seconds { get; set; }
		public bool Completed { get; set; }
	}
}
