using System.Globalization;
using System.Text.Json;
using Cronos;
using Nori.Core.Configuration;
using Nori.Core.Logging;
using Nori.Core.Tools;

namespace Nori.Core.Proactive;

/// <summary>
/// 主动交互与定时提醒调度器
///
/// 职责:
/// - 持久化提醒的到期触发 (重启后自动恢复)
/// - 日常时段问候 (早安 / 午餐 / 晚安, 可配置开关)
/// - 挂机检测触发主动关怀 (依赖平台空闲时长, 不可用时静默跳过)
///
/// 触发的台词通过 ProactiveMessage 事件交给宿主: 由伴侣视窗播放动作/表情,
/// 开启自动朗读时由语音服务朗读。
/// </summary>
public sealed class ProactiveScheduler : IDisposable
{
	/// <summary>默认挂机阈值 (分钟)</summary>
	public const int DefaultIdleMinutes = 15;

	private readonly ReminderStore _store;
	private readonly ConfigStore _config;
	private readonly FileLogger _logger;
	private readonly Func<double?> _idleSecondsProvider;

	private readonly Lock _gate = new();
	private System.Threading.Timer? _tickTimer;

	private readonly GreetingSlot[] _greetingSlots =
	[
		new("proactive-greeting-morning", "30 8 * * *", "早安！新的一天也要元气满满哦~", "Good morning! Let’s have an energetic day~", "wave", "Smile"),
		new("proactive-greeting-lunch", "0 12 * * *", "到午饭时间啦！不要饿肚子，去吃点好吃的吧~", "It’s lunchtime! Please grab something tasty~", "smile", "Smile"),
		new("proactive-greeting-night", "0 23 * * *", "夜深了，工作再忙也要注意身体，早点休息吧~", "It’s late. Even when work is busy, please get some rest~", "think", "Sleepy"),
	];
	/// <summary>当前挂机 session 是否已经触发过关怀。</summary>
	private int _idleSessionFired;

	/// <summary>主动发声事件</summary>
	public event Action<ProactiveMessage>? Message;

	public ProactiveScheduler(ReminderStore store, ConfigStore config, FileLogger logger, Func<double?> idleSecondsProvider)
	{
		_store = store;
		_config = config;
		_logger = logger;
		_idleSecondsProvider = idleSecondsProvider;
	}

	/// <summary>启动调度循环 (30s 粒度)</summary>
	public void Start()
	{
		lock (_gate)
		{
			_tickTimer ??= new System.Threading.Timer(_ => SafeTick(), null, 5000, 30_000);
		}
	}

	/// <summary>提醒内容最大长度。</summary>
	public const int MaxReminderContentLength = 200;

	/// <summary>提醒时间允许的最大提前量 (分钟)。</summary>
	public const double MaxReminderDelayMinutes = 60 * 24 * 30;

	/// <summary>重复规则 JSON 最大长度。</summary>
	public const int MaxReminderRecurrenceJsonLength = 256;

	/// <summary>时区 ID 最大长度。</summary>
	public const int MaxReminderTimezoneLength = 128;

	/// <summary>设置一个提醒 (如 30 分钟后提醒喝水)。</summary>
	public ReminderItem AddReminder(
		string content,
		double delayMinutes,
		bool repeatDaily = false,
		string timezone = "UTC",
		string? recurrenceJson = null)
	{
		string normalizedContent = NormalizeContent(content);
		long triggerAt = TriggerAtAfter(delayMinutes);
		(string normalizedTimezone, string? normalizedRecurrence) = NormalizeSchedule(repeatDaily, timezone, recurrenceJson);
		return _store.Add(normalizedContent, triggerAt, repeatDaily, normalizedTimezone, normalizedRecurrence);
	}

	/// <summary>列出所有仍处于待处理或领取中的提醒。</summary>
	public IReadOnlyList<ReminderItem> ListReminders() => _store.List();

	/// <summary>读取提醒状态，终态也会返回。</summary>
	public ReminderItem? GetReminder(string id) => _store.Get(RequireId(id));

	/// <summary>按绝对 Unix 毫秒时间更新提醒。</summary>
	public ReminderItem UpdateReminder(
		string id,
		string? content = null,
		long? triggerAt = null,
		bool? repeatDaily = null,
		string? timezone = null,
		string? recurrenceJson = null)
	{
		ReminderItem existing = RequireExisting(id);
		EnsureEditable(existing, "更新");
		string normalizedContent = content is null ? existing.Content : NormalizeContent(content);
		long normalizedTriggerAt = triggerAt ?? existing.TriggerAt;
		if (triggerAt is not null) ValidateTriggerAt(normalizedTriggerAt);
		bool normalizedRepeatDaily = repeatDaily ?? existing.RepeatDaily;
		string normalizedTimezone = NormalizeTimezone(timezone ?? existing.Timezone);
		string? candidateRecurrence = recurrenceJson ?? existing.RecurrenceJson;
		if (repeatDaily == false && recurrenceJson is null) candidateRecurrence = null;
		if (repeatDaily == false && recurrenceJson is not null && !string.IsNullOrWhiteSpace(recurrenceJson))
			throw new InvalidOperationException("非重复提醒不能设置重复规则");
		(string _, string? normalizedRecurrence) = NormalizeSchedule(normalizedRepeatDaily, normalizedTimezone, candidateRecurrence);
		if (!_store.Update(existing.Id, normalizedContent, normalizedTriggerAt, normalizedRepeatDaily, normalizedTimezone, normalizedRecurrence))
			throw new InvalidOperationException("提醒已被其他操作变更，请刷新后重试");
		return _store.Get(existing.Id) ?? throw new InvalidOperationException("提醒更新后无法读取");
	}

	/// <summary>按延迟分钟数更新提醒。</summary>
	public ReminderItem UpdateReminderAfter(
		string id,
		string? content,
		double delayMinutes,
		bool? repeatDaily = null,
		string? timezone = null,
		string? recurrenceJson = null)
	{
		return UpdateReminder(id, content, TriggerAtAfter(delayMinutes), repeatDaily, timezone, recurrenceJson);
	}

	/// <summary>推迟提醒指定分钟数。</summary>
	public ReminderItem SnoozeReminder(string id, double delayMinutes)
	{
		long snoozedUntil = TriggerAtAfter(delayMinutes);
		return SnoozeReminderUntil(id, snoozedUntil);
	}

	/// <summary>推迟提醒到指定 Unix 毫秒时间。</summary>
	public ReminderItem SnoozeReminderUntil(string id, long snoozedUntil)
	{
		ReminderItem existing = RequireExisting(id);
		EnsureEditable(existing, "推迟");
		ValidateFutureTime(snoozedUntil);
		if (!_store.Snooze(existing.Id, snoozedUntil))
			throw new InvalidOperationException("提醒已被其他操作变更，请刷新后重试");
		return _store.Get(existing.Id) ?? throw new InvalidOperationException("提醒推迟后无法读取");
	}

	/// <summary>用户明确完成提醒；重复提醒也会永久停止。</summary>
	public bool CompleteReminder(string id)
	{
		ReminderItem existing = RequireExisting(id);
		if (existing.Status is "completed" or "cancelled" or "fired") return false;
		if (!_store.Complete(existing.Id)) throw new InvalidOperationException("提醒已被其他操作变更，请刷新后重试");
		return true;
	}

	/// <summary>用户明确取消提醒；保留取消状态以便状态查询。</summary>
	public bool CancelReminder(string id)
	{
		string normalizedId = RequireId(id);
		ReminderItem? existing = _store.Get(normalizedId);
		if (existing is null || existing.Status is "completed" or "cancelled" or "fired") return false;
		return _store.Cancel(normalizedId);
	}

	private static string NormalizeContent(string content)
	{
		if (string.IsNullOrWhiteSpace(content)) throw new InvalidOperationException("提醒内容不能为空");
		string trimmed = content.Trim();
		if (trimmed.Length > MaxReminderContentLength) throw new InvalidOperationException("提醒内容不能超过 200 个字符");
		return trimmed;
	}

	private static string RequireId(string id)
	{
		if (string.IsNullOrWhiteSpace(id)) throw new InvalidOperationException("提醒 ID 不能为空");
		string trimmed = id.Trim();
		if (trimmed.Length > 128) throw new InvalidOperationException("提醒 ID 超出范围");
		return trimmed;
	}

	private ReminderItem RequireExisting(string id)
	{
		string normalizedId = RequireId(id);
		return _store.Get(normalizedId) ?? throw new InvalidOperationException("未找到提醒");
	}

	private static void EnsureEditable(ReminderItem item, string operation)
	{
		if (item.Status == "claimed") throw new InvalidOperationException($"提醒正在投递，暂不能{operation}");
		if (item.Status is not "pending") throw new InvalidOperationException("提醒已结束，无法修改");
	}

	private static long TriggerAtAfter(double delayMinutes)
	{
		if (!double.IsFinite(delayMinutes) || delayMinutes <= 0 || delayMinutes > MaxReminderDelayMinutes)
			throw new InvalidOperationException("提醒延迟超出范围");
		try
		{
			return DateTimeOffset.UtcNow.AddMinutes(delayMinutes).ToUnixTimeMilliseconds();
		}
		catch (ArgumentOutOfRangeException)
		{
			throw new InvalidOperationException("提醒延迟超出范围");
		}
	}

	private static void ValidateTriggerAt(long triggerAt)
	{
		long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
		long earliest = now - (long)TimeSpan.FromMinutes(1).TotalMilliseconds;
		long latest = now + (long)TimeSpan.FromMinutes(MaxReminderDelayMinutes).TotalMilliseconds;
		if (triggerAt < earliest || triggerAt > latest) throw new InvalidOperationException("提醒时间超出范围");
	}

	private static void ValidateFutureTime(long timestamp)
	{
		long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
		long latest = now + (long)TimeSpan.FromMinutes(MaxReminderDelayMinutes).TotalMilliseconds;
		if (timestamp <= now || timestamp > latest) throw new InvalidOperationException("推迟时间超出范围");
	}

	private static (string Timezone, string? Recurrence) NormalizeSchedule(bool repeatDaily, string? timezone, string? recurrenceJson)
	{
		string normalizedTimezone = NormalizeTimezone(timezone);
		string? normalizedRecurrence = NormalizeRecurrence(repeatDaily, recurrenceJson);
		return (normalizedTimezone, normalizedRecurrence);
	}

	private static string NormalizeTimezone(string? timezone)
	{
		string value = string.IsNullOrWhiteSpace(timezone) ? "UTC" : timezone.Trim();
		if (value.Length > MaxReminderTimezoneLength || value.Any(char.IsControl))
			throw new InvalidOperationException("提醒时区超出范围");
		try
		{
			_ = TimeZoneInfo.FindSystemTimeZoneById(value);
		}
		catch (TimeZoneNotFoundException)
		{
			throw new InvalidOperationException("提醒时区无效");
		}
		catch (InvalidTimeZoneException)
		{
			throw new InvalidOperationException("提醒时区无效");
		}
		catch (ArgumentException)
		{
			throw new InvalidOperationException("提醒时区无效");
		}
		return value;
	}

	private static string? NormalizeRecurrence(bool repeatDaily, string? recurrenceJson)
	{
		if (!repeatDaily)
		{
			if (string.IsNullOrWhiteSpace(recurrenceJson)) return null;
			throw new InvalidOperationException("非重复提醒不能设置重复规则");
		}
		if (string.IsNullOrWhiteSpace(recurrenceJson)) return "{\"type\":\"daily\"}";
		string json = recurrenceJson.Trim();
		if (json.Length > MaxReminderRecurrenceJsonLength) throw new InvalidOperationException("重复规则超出范围");
		try
		{
			using JsonDocument document = JsonDocument.Parse(json);
			JsonElement root = document.RootElement;
			if (root.ValueKind != JsonValueKind.Object
				|| !root.TryGetProperty("type", out JsonElement type)
				|| type.ValueKind != JsonValueKind.String
				|| !string.Equals(type.GetString(), "daily", StringComparison.OrdinalIgnoreCase)
				|| root.EnumerateObject().Any(property => !property.NameEquals("type"))
				) throw new InvalidOperationException("重复规则无效");
		}
		catch (JsonException)
		{
			throw new InvalidOperationException("重复规则无效");
		}
		return "{\"type\":\"daily\"}";
	}


	/// <summary>手动触发一次主动发言 (供调试)</summary>
	public void SayNow(string text, string motion = "wave", string expression = "Smile") =>
		Message?.Invoke(new ProactiveMessage(text, motion, expression));

	private void SafeTick()
	{
		try
		{
			Tick();
		}
		catch (Exception)
		{
			try
			{
				_logger.Write(LogSource.Backend, "warn", "主动交互调度异常");
			}
			catch
			{
				// 日志失败保持静默
			}
		}
	}

	private void Tick()
	{
		DateTimeOffset now = DateTimeOffset.UtcNow;
		long nowMs = now.ToUnixTimeMilliseconds();
		FireDueReminders(nowMs);
		CheckDailyGreetings(now);
		CheckIdle();
	}

	/// <summary>供测试按指定 UTC 时间推进一次调度。</summary>
	public void TickForTests(DateTime? nowUtc = null)
	{
		DateTimeOffset now = nowUtc is { } value
			? new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc))
			: DateTimeOffset.UtcNow;
		FireDueReminders(now.ToUnixTimeMilliseconds());
		CheckDailyGreetings(now);
		CheckIdle();
	}

	private void FireDueReminders(long nowMs)
	{
		foreach (ReminderItem reminder in _store.TakeDue(nowMs))
		{
			string text = IsEnglish()
				? $"Reminder time: {reminder.Content}"
				: $"提醒时间到了：{reminder.Content}";
			try
			{
				Message?.Invoke(new ProactiveMessage(text, "wave", "Surprised"));
				_store.MarkFired(reminder.Id);
			}
			catch
			{
				_store.ReleaseClaim(reminder.Id);
				throw;
			}
			try
			{
				_logger.Write(LogSource.Backend, "info", "定时提醒触发");
			}
			catch
			{
				// 忽略日志失败
			}
		}
	}

	private void CheckDailyGreetings(DateTimeOffset nowUtc)
	{
		bool enabled = ParseBool(_config.GetStringOr("proactive_daily_greeting", "true")) ?? true;
		if (!enabled) return;

		DateTimeOffset localNow = TimeZoneInfo.ConvertTime(nowUtc, TimeZoneInfo.Local);
		foreach (GreetingSlot slot in _greetingSlots)
		{
			DateTimeOffset from = localNow.AddMinutes(-15).AddTicks(-1);
			DateTimeOffset? occurrence = slot.Schedule.GetNextOccurrence(from, TimeZoneInfo.Local);
			if (occurrence is null || occurrence > localNow || localNow - occurrence > TimeSpan.FromMinutes(15)) continue;
			string occurrenceValue = occurrence.Value.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);
			if (!_store.TryClaimOccurrence(slot.Key, occurrenceValue)) continue;
			Message?.Invoke(new ProactiveMessage(slot.Text(IsEnglish()), slot.Motion, slot.Expression));
		}
	}

	private void CheckIdle()
	{
		bool enabled = ParseBool(_config.GetStringOr("proactive_idle_enabled", "true")) ?? true;
		if (!enabled) return;
		double thresholdSeconds = Math.Max(1,
			ReadNumberConfig(_config, "proactive_idle_minutes", DefaultIdleMinutes) * 60);

		double? idleSeconds = _idleSecondsProvider();
		if (idleSeconds is not { } idle || idle < thresholdSeconds)
		{
			// 任意一次真实活动都结束上一轮挂机 session。
			Interlocked.Exchange(ref _idleSessionFired, 0);
			return;
		}
		if (Interlocked.Exchange(ref _idleSessionFired, 1) != 0) return;

		string[] texts = IsEnglish()
			? [
				"It’s been a while since you checked in with Nori...",
				"Stretch break~ You’ve worked hard. Please rest your eyes!",
				"Yawn... I wonder what you’re busy with?",
			]
			: [
				"已经好久没有一起聊天啦...",
				"伸个懒腰~ 工作辛苦啦，记得休息一下眼睛哦！",
				"呼啊... 好困呀，你在忙什么呢？",
			];
		string[] motions = ["think", "smile", "wave"];
		string[] expressions = ["Sad", "Smile", "Sleepy"];
		int pick = Random.Shared.Next(texts.Length);
		Message?.Invoke(new ProactiveMessage(texts[pick], motions[pick], expressions[pick]));
	}

	private static bool? ParseBool(string raw) => raw switch
	{
		"1" => true,
		"0" => false,
		_ when raw.Equals("true", StringComparison.OrdinalIgnoreCase) => true,
		_ when raw.Equals("false", StringComparison.OrdinalIgnoreCase) => false,
		_ => null,
	};

	private static double ReadNumberConfig(ConfigStore config, string key, double fallback) => config.Get(key) switch
	{
		ConfigValue.Integer integer => integer.Value,
		// SQLite 的历史类型推断会把字符串 "1"/"0" 读成布尔值, 数值配置必须兼容。
		ConfigValue.Boolean boolean => boolean.Value ? 1 : 0,
		ConfigValue.Text text when double.TryParse(text.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) => value,
		_ => fallback,
	};

	private bool IsEnglish() => _config.GetStringOr(ConfigStore.KeyLanguage, "zh-CN").StartsWith("en", StringComparison.OrdinalIgnoreCase);

	public void Dispose()
	{
		System.Threading.Timer? timer;
		lock (_gate)
		{
			timer = _tickTimer;
			_tickTimer = null;
		}
		timer?.Dispose();
	}

	private sealed class GreetingSlot
	{
		public GreetingSlot(string key, string expression, string chineseText, string englishText, string motion, string face)
		{
			Key = key;
			Schedule = CronExpression.Parse(expression, CronFormat.Standard);
			ChineseText = chineseText;
			EnglishText = englishText;
			Motion = motion;
			Expression = face;
		}

		public string Key { get; }
		public CronExpression Schedule { get; }
		public string ChineseText { get; }
		public string EnglishText { get; }
		public string Motion { get; }
		public string Expression { get; }
		public string Text(bool english) => english ? EnglishText : ChineseText;
	}
}

/// <summary>主动发声消息</summary>
public sealed record ProactiveMessage(string Text, string Motion, string Expression);
