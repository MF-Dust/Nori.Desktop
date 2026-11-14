using Nori.Live2D;

namespace Nori.Desktop.Live2D;

/// <summary>自主动作在现有优先级上的角色。互动与显式播放仍走 Force，不进这里。</summary>
internal enum AutonomousMotionRole
{
	Idle,
	Voice,
}

/// <summary>帧内观察到的伴侣活动。只用于这一帧的决定，不保存转移历史。</summary>
internal enum AutonomousActivity
{
	Idle,
	Interacting,
	Speaking,
	Busy,
	Suspended,
}

/// <summary>一次可播放的模型动作。下标是模型动作组里的原始下标。</summary>
internal readonly record struct AutonomousMotionSlot(string Group, int Index, bool EyesClosed);

/// <summary>传给规划器的一帧输入。字符串沿用调用方已有实例，本帧不新分配。</summary>
internal readonly record struct AutonomousFrame(
	double Now,
	bool Enabled,
	bool RandomMotions,
	string Frequency,
	bool Suspended,
	bool Speaking,
	bool MotionFinished,
	MotionPriority CurrentPriority,
	string? CurrentGroup,
	bool IdleAnimationEnabled);

/// <summary>这一帧要不要开始动作，以及当前活动归到哪一档。</summary>
internal readonly record struct AutonomousDecision(
	AutonomousActivity Activity,
	AutonomousMotionSlot? Motion,
	MotionPriority Priority)
{
	public bool HasMotion => Motion is not null;
}

/// <summary>
/// 行为频率。配置里只接受这三个词，读到别的写法时回退到自然。
/// 稀疏、自然、活跃对应两次待机之间的间隔。
/// </summary>
internal static class AutonomousPacing
{
	public const string Sparse = "sparse";
	public const string Normal = "normal";
	public const string Lively = "lively";

	/// <summary>语音动作之间的最短间隔，避免一句很短的口型把说话动作连着重开。</summary>
	public const double VoiceGapSeconds = 0.45;

	public static string Normalize(string? value) =>
		TryNormalize(value, strict: false, out string normalized) ? normalized : Normal;

	public static bool TryNormalize(string? value, bool strict, out string normalized)
	{
		normalized = value?.Trim().ToLowerInvariant() switch
		{
			Sparse => Sparse,
			Normal => Normal,
			Lively => Lively,
			"low" or "slow" when !strict => Sparse,
			"medium" or "default" when !strict => Normal,
			"high" or "fast" when !strict => Lively,
			_ => "",
		};
		if (normalized.Length > 0) return true;
		normalized = Normal;
		return false;
	}

	public static double CooldownSeconds(string? value) => Normalize(value) switch
	{
		Sparse => 16,
		Lively => 4,
		_ => 8,
	};
}

/// <summary>
/// 在现有帧循环里决定要不要开始一个自主动作。
/// 不创建计时器，也不在挑选过程中分配；候选只在换模型时绑定一次。
/// 显式动作和点击使用 Force，本规划器最高只申请 Normal，因此打断不了它们。
/// </summary>
internal sealed class AutonomousMotionPlanner
{
	private AutonomousMotionSlot[] _idle = [];
	private AutonomousMotionSlot[] _voice = [];
	private long _generation = -1;
	private int _recentIdleA = -1;
	private int _recentIdleB = -1;
	private int _recentVoiceA = -1;
	private int _recentVoiceB = -1;
	private double _nextIdleAt;
	private double _nextVoiceAt;
	private bool _resumeHold;
	private Func<int, int> _next = static count => Random.Shared.Next(count);

	public long Generation => _generation;

	internal bool ResumeHeld => _resumeHold;

	/// <summary>测试注入均匀随机序列。参数是候选个数，返回值必须落在范围内。</summary>
	internal void UseRandom(Func<int, int> next) => _next = next ?? throw new ArgumentNullException(nameof(next));

	/// <summary>
	/// 按组名判断能不能用于自主动作。
	/// 待机组留给待机；说话、语音组留给口型期间；点击和其余组留给用户互动。
	/// </summary>
	public static bool TryClassify(string? group, out AutonomousMotionRole role)
	{
		role = AutonomousMotionRole.Idle;
		string normalized = Normalize(group);
		if (normalized.Length == 0) return false;
		if (normalized.StartsWith("idle", StringComparison.Ordinal))
		{
			role = AutonomousMotionRole.Idle;
			return true;
		}
		if (normalized.Contains("tap", StringComparison.Ordinal)
			|| normalized.Contains("touch", StringComparison.Ordinal)
			|| normalized.Contains("click", StringComparison.Ordinal))
		{
			return false;
		}
		if (normalized.Contains("talk", StringComparison.Ordinal)
			|| normalized.Contains("speak", StringComparison.Ordinal)
			|| normalized.Contains("voice", StringComparison.Ordinal)
			|| normalized == "chat")
		{
			role = AutonomousMotionRole.Voice;
			return true;
		}
		return false;
	}

	/// <summary>换模型或重新装载时替换候选，并清掉上一份模型的冷却和最近播放。</summary>
	public void Bind(long generation, IReadOnlyList<AutonomousMotionSlot> idle, IReadOnlyList<AutonomousMotionSlot> voice)
	{
		ArgumentNullException.ThrowIfNull(idle);
		ArgumentNullException.ThrowIfNull(voice);
		_generation = generation;
		_idle = idle.Count == 0 ? [] : idle.ToArray();
		_voice = voice.Count == 0 ? [] : voice.ToArray();
		_recentIdleA = _recentIdleB = _recentVoiceA = _recentVoiceB = -1;
		_nextIdleAt = 0;
		_nextVoiceAt = 0;
	}

	/// <summary>释放模型或丢弃运行时时清掉全部候选，避免之后再启动旧下标。</summary>
	public void Reset()
	{
		_generation = -1;
		_idle = [];
		_voice = [];
		_recentIdleA = _recentIdleB = _recentVoiceA = _recentVoiceB = -1;
		_nextIdleAt = 0;
		_nextVoiceAt = 0;
		_resumeHold = false;
	}

	/// <summary>窗口隐藏后调用。下一次恢复只延后待机，不保留任何到点任务。</summary>
	public void Hold() => _resumeHold = true;

	public AutonomousDecision Tick(in AutonomousFrame frame)
	{
		if (frame.Suspended || !frame.Enabled)
			return new AutonomousDecision(AutonomousActivity.Suspended, null, MotionPriority.None);

		if (_resumeHold)
		{
			_resumeHold = false;
			double resumeAt = frame.Now + AutonomousPacing.CooldownSeconds(frame.Frequency);
			if (resumeAt > _nextIdleAt) _nextIdleAt = resumeAt;
		}

		if (!frame.MotionFinished && frame.CurrentPriority >= MotionPriority.Force)
			return new AutonomousDecision(AutonomousActivity.Interacting, null, MotionPriority.None);

		if (frame.Speaking)
		{
			AutonomousMotionSlot? voice = SelectVoice(frame);
			return new AutonomousDecision(AutonomousActivity.Speaking, voice, voice is null ? MotionPriority.None : MotionPriority.Normal);
		}

		if (!frame.MotionFinished && frame.CurrentPriority >= MotionPriority.Normal)
			return new AutonomousDecision(AutonomousActivity.Busy, null, MotionPriority.None);

		AutonomousMotionSlot? idle = SelectIdle(frame);
		return new AutonomousDecision(AutonomousActivity.Idle, idle, idle is null ? MotionPriority.None : MotionPriority.Idle);
	}

	/// <summary>只有播放器真正接受了这次开始，才记录冷却和最近播放。</summary>
	public void Commit(AutonomousMotionSlot motion, MotionPriority priority, double now, string frequency)
	{
		if (priority <= MotionPriority.Idle)
		{
			Remember(ref _recentIdleB, ref _recentIdleA, IndexOf(_idle, motion));
			_nextIdleAt = now + AutonomousPacing.CooldownSeconds(frequency);
			return;
		}
		Remember(ref _recentVoiceB, ref _recentVoiceA, IndexOf(_voice, motion));
		_nextVoiceAt = now + AutonomousPacing.VoiceGapSeconds;
	}

	private AutonomousMotionSlot? SelectVoice(in AutonomousFrame frame)
	{
		if (_voice.Length == 0) return null;
		if (!frame.MotionFinished && frame.CurrentPriority > MotionPriority.Idle) return null;
		if (frame.Now < _nextVoiceAt) return null;
		int index = Pick(_voice, frame.RandomMotions, frame.RandomMotions, ref _recentVoiceA, ref _recentVoiceB, avoidClosedEyes: false);
		return index < 0 ? null : _voice[index];
	}

	private AutonomousMotionSlot? SelectIdle(in AutonomousFrame frame)
	{
		if (!frame.IdleAnimationEnabled || _idle.Length == 0 || !frame.MotionFinished) return null;
		if (frame.Now < _nextIdleAt) return null;
		int index = Pick(_idle, frame.RandomMotions, frame.RandomMotions, ref _recentIdleA, ref _recentIdleB, avoidClosedEyes: true);
		return index < 0 ? null : _idle[index];
	}

	private int Pick(AutonomousMotionSlot[] pool, bool random, bool avoidRecent, ref int recentA, ref int recentB, bool avoidClosedEyes)
	{
		if (pool.Length == 0) return -1;
		bool filterClosed = avoidClosedEyes && HasOpenEyes(pool);
		int chosen = Select(pool, random, filterClosed, avoidRecent, recentA, recentB);
		if (chosen >= 0) return chosen;
		if (avoidRecent)
		{
			chosen = Select(pool, random, filterClosed, false, -1, -1);
			if (chosen >= 0) return chosen;
		}
		return Select(pool, random, false, false, -1, -1);
	}

	private int Select(AutonomousMotionSlot[] pool, bool random, bool filterClosed, bool avoidRecent, int recentA, int recentB)
	{
		int count = 0;
		int first = -1;
		for (int index = 0; index < pool.Length; index++)
		{
			if (filterClosed && pool[index].EyesClosed) continue;
			if (avoidRecent && (index == recentA || index == recentB)) continue;
			if (first < 0) first = index;
			count++;
		}
		if (count == 0 || first < 0) return -1;
		if (!random) return first;
		int nth = _next(count);
		if (nth < 0 || nth >= count) nth = 0;
		int seen = 0;
		for (int index = 0; index < pool.Length; index++)
		{
			if (filterClosed && pool[index].EyesClosed) continue;
			if (avoidRecent && (index == recentA || index == recentB)) continue;
			if (seen == nth) return index;
			seen++;
		}
		return first;
	}

	private static bool HasOpenEyes(AutonomousMotionSlot[] pool)
	{
		foreach (AutonomousMotionSlot slot in pool)
		{
			if (!slot.EyesClosed) return true;
		}
		return false;
	}

	private static int IndexOf(AutonomousMotionSlot[] pool, AutonomousMotionSlot motion)
	{
		for (int index = 0; index < pool.Length; index++)
		{
			if (pool[index].Index == motion.Index
				&& string.Equals(pool[index].Group, motion.Group, StringComparison.Ordinal))
				return index;
		}
		return -1;
	}

	private static void Remember(ref int older, ref int newer, int index)
	{
		if (index < 0) return;
		older = newer;
		newer = index;
	}

	private static string Normalize(string? value)
	{
		if (string.IsNullOrWhiteSpace(value)) return "";
		Span<char> buffer = stackalloc char[value.Length];
		int length = 0;
		foreach (char character in value)
		{
			if (!char.IsLetterOrDigit(character)) continue;
			buffer[length++] = char.ToLowerInvariant(character);
		}
		return new string(buffer[..length]);
	}
}
