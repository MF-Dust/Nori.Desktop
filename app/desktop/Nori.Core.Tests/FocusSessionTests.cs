using Nori.Core.Cloud;
using Nori.Core.Configuration;
using Nori.Core.Data;
using Nori.Core.Focus;
using Nori.Core.Logging;
using Nori.Core.Proactive;

namespace Nori.Core.Tests;

public sealed class FocusSessionTests : IDisposable
{
	private readonly string _root = Directory.CreateTempSubdirectory("nori-focus-").FullName;

	[Fact]
	public void 生产时钟按Stopwatch频率换算且忽略回拨()
	{
		SystemFocusClock clock = new();
		Assert.Equal(1d, clock.SecondsBetween(0, System.Diagnostics.Stopwatch.Frequency), precision: 6);
		long now = clock.Now;
		Assert.Equal(0d, clock.SecondsBetween(now, now));
		Assert.Equal(0d, clock.SecondsBetween(now, now - 1));
		Assert.Equal(TimeSpan.FromMilliseconds(1), FocusSession.DueAfter(0.0001));
		Assert.Equal(TimeSpan.Zero, FocusSession.DueAfter(double.NaN));
		Assert.Equal(TimeSpan.Zero, FocusSession.DueAfter(double.NegativeInfinity));
		Assert.Equal(TimeSpan.FromMilliseconds(int.MaxValue), FocusSession.DueAfter(int.MaxValue));
	}

	[Fact]
	public void 单调时钟回拨不减少已用时间也不会把同一段算两次()
	{
		ManualFocusClock clock = new();
		using FocusSession session = Create(clock);
		session.Start(25, notifyOnComplete: true, silentReminders: true);
		clock.Now = 10_000;
		Assert.Equal(10, session.View().ElapsedSeconds);
		Assert.Equal(25 * 60 - 10, session.View().RemainingSeconds);

		clock.Now = 1_000;
		Assert.Equal(10, session.View().ElapsedSeconds);
		clock.Now = 10_000;
		Assert.Equal(10, session.View().ElapsedSeconds);
		clock.Now = 15_000;
		Assert.Equal(15, session.View().ElapsedSeconds);

		session.Pause();
		clock.Now = 500_000;
		Assert.Equal(15, session.View().ElapsedSeconds);
		Assert.Equal("paused", session.View().Phase);
		Assert.False(session.IsScheduleArmed);

		session.Resume();
		clock.Now = 508_000;
		Assert.Equal(23, session.View().ElapsedSeconds);
		Assert.Equal("running", session.View().Phase);
	}

	[Fact]
	public void 暂停继续与提前结束不重复计入完成()
	{
		ManualFocusClock clock = new();
		using FocusSession session = Create(clock);
		session.Start(1, notifyOnComplete: true, silentReminders: true);
		clock.Now = 10_000;
		session.Pause();
		Assert.Equal(10, session.View().ElapsedSeconds);
		Assert.Throws<InvalidOperationException>(() => session.Start(1, true, true));
		session.Resume();
		clock.Now = 16_000;
		session.End();

		FocusView view = session.View();
		Assert.Equal("idle", view.Phase);
		Assert.Equal(0, view.CompletedCount);
		Assert.Equal(16, view.TotalSeconds);
		FocusRecord recent = Assert.Single(view.Recent);
		Assert.False(recent.Completed);
		Assert.Equal(16, recent.Seconds);
		Assert.Null(session.ConsumeCompletionNotice(false));
		Assert.Throws<InvalidOperationException>(() => session.End());
	}

	[Fact]
	public void 完成只通知一次并在重新加载后保持统计()
	{
		ManualFocusClock clock = new();
		string path = StatePath("complete");
		using (FocusSession session = new(path, clock, background: true))
		{
			session.Start(1, notifyOnComplete: true, silentReminders: true);
			clock.Now = 60_000;
			session.Evaluate();
			session.Evaluate();
			FocusView view = session.View();
			Assert.Equal("completed", view.Phase);
			Assert.Equal(1, view.CompletedCount);
			Assert.Equal(60, view.TotalSeconds);
			Assert.Equal(0, view.RemainingSeconds);
			Assert.Equal("这段专注结束了。", session.ConsumeCompletionNotice(false));
			Assert.Null(session.ConsumeCompletionNotice(true));
			Assert.Equal("完成 01:00", FocusText.Recent(view, english: false));
		}

		using FocusSession reloaded = new(path, new ManualFocusClock(), background: true);
		Assert.Equal("completed", reloaded.View().Phase);
		Assert.Equal(1, reloaded.View().CompletedCount);
		Assert.Equal(60, reloaded.View().TotalSeconds);
		Assert.Null(reloaded.ConsumeCompletionNotice(false));
		Assert.False(reloaded.IsScheduleArmed);
	}

	[Fact]
	public void 关闭完成通知时到点不发通知()
	{
		ManualFocusClock clock = new();
		using FocusSession session = Create(clock);
		session.Start(1, notifyOnComplete: false, silentReminders: false);
		clock.Now = 60_000;
		session.Checkpoint();
		Assert.Equal("completed", session.View().Phase);
		Assert.Equal(1, session.View().CompletedCount);
		Assert.False(session.View().NotifyOnComplete);
		Assert.Null(session.ConsumeCompletionNotice(false));
	}

	[Fact]
	public void 退出后重启保持暂停且不把离开的时间算进去()
	{
		ManualFocusClock clock = new();
		string path = StatePath("restart");
		using (FocusSession session = new(path, clock, background: true))
		{
			session.Start(25, notifyOnComplete: true, silentReminders: true);
			clock.Now = 30_000;
		}

		ManualFocusClock restarted = new() { Now = 9_000_000 };
		using FocusSession second = new(path, restarted, background: true);
		Assert.Equal("paused", second.View().Phase);
		Assert.Equal(30, second.View().ElapsedSeconds);
		Assert.False(second.IsScheduleArmed);
		Assert.True(second.Presence.Active);
		second.Resume();
		restarted.Now += 5_000;
		Assert.Equal(35, second.View().ElapsedSeconds);
		Assert.Equal(0, second.View().CompletedCount);
	}

	[Fact]
	public void 到点前退出会在下次加载时补记完成但不在安全模式发通知()
	{
		ManualFocusClock clock = new();
		string path = StatePath("overdue");
		using (FocusSession session = new(path, clock, background: true))
		{
			session.Start(1, notifyOnComplete: true, silentReminders: true);
			clock.Now = 60_000;
		}

		using (FocusSession safe = new(path, new ManualFocusClock(), background: false))
		{
			Assert.Equal("completed", safe.View().Phase);
			Assert.Equal(1, safe.View().CompletedCount);
			Assert.Null(safe.ConsumeCompletionNotice(false));
			Assert.False(safe.IsScheduleArmed);
			Assert.Throws<InvalidOperationException>(() => safe.Start(1, true, true));
			Assert.Throws<InvalidOperationException>(() => safe.Resume());
		}

		using FocusSession normal = new(path, new ManualFocusClock(), background: true);
		Assert.Equal(1, normal.View().CompletedCount);
		Assert.Equal("This focus session is complete.", normal.ConsumeCompletionNotice(true));
		Assert.Null(normal.ConsumeCompletionNotice(true));
	}

	[Fact]
	public void 没有记录或记录损坏时从空闲开始且不进入云端存档()
	{
		using FocusSession missing = Create(new ManualFocusClock());
		Assert.Equal("idle", missing.View().Phase);
		Assert.Equal(0, missing.View().CompletedCount);
		Assert.False(File.Exists(StatePath("missing")));

		string corrupt = StatePath("corrupt");
		Directory.CreateDirectory(Path.GetDirectoryName(corrupt)!);
		File.WriteAllText(corrupt, "{");
		using FocusSession broken = new(corrupt, new ManualFocusClock(), background: true);
		Assert.Equal("idle", broken.View().Phase);
		Assert.Equal("{", File.ReadAllText(corrupt));

		Assert.DoesNotContain(CloudSaveScope.ConfigKeys, key => key.Contains("focus", StringComparison.OrdinalIgnoreCase));
		Assert.DoesNotContain(CloudSaveScope.Tables, table => table.Contains("focus", StringComparison.OrdinalIgnoreCase));
	}

	[Fact]
	public void 专注分流问候挂机关怀提醒和用户聊天且不改保存的开关()
	{
		string databasePath = Path.Combine(_root, "routing.db");
		using NoriDatabase database = NoriDatabase.Open(databasePath);
		ConfigStore config = new(database);
		config.InitDefaults("test");
		config.Set("language", new ConfigValue.Text("zh-CN"));
		config.Set("proactive_daily_greeting", new ConfigValue.Boolean(true));
		config.Set("proactive_idle_enabled", new ConfigValue.Boolean(true));
		config.Set("proactive_idle_minutes", new ConfigValue.Integer(1));
		string idleBefore = config.GetStringOr("proactive_idle_enabled", "");
		string greetingBefore = config.GetStringOr("proactive_daily_greeting", "");
		using FileLogger logger = new(Path.Combine(_root, "logs"));
		ManualFocusClock clock = new();
		using FocusSession focus = new(StatePath("routing"), clock, background: true);
		ReminderStore store = new(database);
		double? idle = 120;
		using ProactiveScheduler scheduler = new(store, config, logger, () => idle, () => focus.Presence, focus.Checkpoint);
		List<ProactiveMessage> messages = [];
		scheduler.Message += messages.Add;

		DateTime localDate = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TimeZoneInfo.Local).Date;
		DateTimeOffset morning = new(TimeZoneInfo.ConvertTimeToUtc(localDate.AddHours(8).AddMinutes(30), TimeZoneInfo.Local), TimeSpan.Zero);
		ReminderItem due = store.Add("喝水", morning.ToUnixTimeMilliseconds() - 1);
		ReminderItem later = store.Add("明天的书", morning.AddDays(2).ToUnixTimeMilliseconds(), true, "UTC", "{\"type\":\"daily\"}");
		focus.Start(25, notifyOnComplete: false, silentReminders: true);
		scheduler.Tick(morning.UtcDateTime);

		ProactiveMessage reminder = Assert.Single(messages);
		Assert.Contains("喝水", reminder.Text, StringComparison.Ordinal);
		Assert.False(reminder.AllowSpeech);
		Assert.False(reminder.AllowAnimation);
		Assert.Equal("fired", scheduler.GetReminder(due.Id)!.Status);
		Assert.Equal("{\"type\":\"daily\"}", scheduler.GetReminder(later.Id)!.RecurrenceJson);
		Assert.Equal("pending", scheduler.GetReminder(later.Id)!.Status);
		scheduler.Tick(morning.UtcDateTime);
		Assert.Single(messages);

		focus.End();
		Assert.False(focus.Presence.Active);
		idle = 0;
		scheduler.Tick(morning.UtcDateTime);
		Assert.Equal(2, messages.Count);
		Assert.Contains("早安", messages[1].Text, StringComparison.Ordinal);
		Assert.True(messages[1].Care);
		Assert.False(reminder.Care);
		Assert.True(messages[1].AllowSpeech);
		Assert.True(messages[1].AllowAnimation);

		messages.Clear();
		DateTimeOffset laterHour = morning.AddHours(1);
		scheduler.Tick(laterHour.UtcDateTime);
		idle = 120;
		focus.Start(25, notifyOnComplete: true, silentReminders: false);
		scheduler.Tick(laterHour.UtcDateTime);
		Assert.Empty(messages);
		focus.End();
		scheduler.Tick(laterHour.UtcDateTime);
		Assert.Single(messages);
		Assert.True(messages[0].AllowSpeech);

		Assert.Equal(idleBefore, config.GetStringOr("proactive_idle_enabled", ""));
		Assert.Equal(greetingBefore, config.GetStringOr("proactive_daily_greeting", ""));
		Assert.True(FocusCompanionPolicy.AllowsUserChat());
		Assert.True(FocusCompanionPolicy.ShouldSpeakUserReply(autoTts: true, safeMode: false));
		Assert.False(FocusCompanionPolicy.ShouldSpeakUserReply(autoTts: true, safeMode: true));
		Assert.False(FocusCompanionPolicy.ShouldSpeakUserReply(autoTts: false, safeMode: false));
		Assert.True(FocusCompanionPolicy.AllowsAmbientMotion(userEnabled: true, focusQuiet: false));
		Assert.False(FocusCompanionPolicy.AllowsAmbientMotion(userEnabled: true, focusQuiet: true));
		Assert.False(FocusCompanionPolicy.AllowsAmbientMotion(userEnabled: false, focusQuiet: false));
	}

	[Fact]
	public void 到点后不经计时回调直接结束仍记为完成()
	{
		ManualFocusClock clock = new();
		using FocusSession session = Create(clock);
		session.Start(1, notifyOnComplete: true, silentReminders: true);
		clock.Now = 60_000;
		session.End();

		FocusView view = session.View();
		Assert.Equal("idle", view.Phase);
		Assert.Equal(1, view.CompletedCount);
		Assert.Equal(60, view.TotalSeconds);
		FocusRecord recent = Assert.Single(view.Recent);
		Assert.True(recent.Completed);
		Assert.Equal(60, recent.Seconds);
		Assert.Equal("这段专注结束了。", session.ConsumeCompletionNotice(false));
		Assert.Null(session.ConsumeCompletionNotice(false));
	}

	[Fact]
	public void 安全模式结束未通知的完成场后下次正常启动仍通知一次()
	{
		ManualFocusClock clock = new();
		string path = StatePath("safe-end-notice");
		using (FocusSession session = new(path, clock, background: true))
		{
			session.Start(1, notifyOnComplete: true, silentReminders: true);
			clock.Now = 60_000;
			session.Evaluate();
		}

		using (FocusSession safe = new(path, new ManualFocusClock(), background: false))
		{
			Assert.Equal("completed", safe.View().Phase);
			Assert.Null(safe.ConsumeCompletionNotice(false));
			safe.End();
			Assert.Equal("idle", safe.View().Phase);
			Assert.Null(safe.ConsumeCompletionNotice(false));
		}

		using (FocusSession normal = new(path, new ManualFocusClock(), background: true))
		{
			Assert.Equal("idle", normal.View().Phase);
			Assert.Equal(1, normal.View().CompletedCount);
			Assert.Equal("这段专注结束了。", normal.ConsumeCompletionNotice(false));
			Assert.Null(normal.ConsumeCompletionNotice(false));
		}

		using FocusSession again = new(path, new ManualFocusClock(), background: true);
		Assert.Null(again.ConsumeCompletionNotice(false));
	}

	[Fact]
	public void 到点后暂停会记成完成并只通知一次()
	{
		ManualFocusClock clock = new();
		using FocusSession session = Create(clock);
		session.Start(1, notifyOnComplete: true, silentReminders: true);
		clock.Now = 60_000;
		session.Pause();
		Assert.Equal("completed", session.View().Phase);
		Assert.Equal(1, session.View().CompletedCount);
		Assert.Equal(0, session.View().RemainingSeconds);
		Assert.Equal("这段专注结束了。", session.ConsumeCompletionNotice(false));
		Assert.Null(session.ConsumeCompletionNotice(false));
		Assert.False(FocusCompanionPolicy.StillAllowsSpeech(true, new FocusPresence(true, true)));
		Assert.False(FocusCompanionPolicy.StillAllowsAnimation(true, new FocusPresence(true, true)));
		Assert.True(FocusCompanionPolicy.StillAllowsSpeech(true, default));
		Assert.False(FocusCompanionPolicy.StillAllowsSpeech(false, default));
		Assert.False(FocusCompanionPolicy.ShouldDeliver(care: true, new FocusPresence(true, true)));
		Assert.False(FocusCompanionPolicy.ShouldDeliver(care: true, new FocusPresence(true, false)));
		Assert.True(FocusCompanionPolicy.ShouldDeliver(care: true, default));
		Assert.True(FocusCompanionPolicy.ShouldDeliver(care: false, new FocusPresence(true, true)));
	}

	[Fact]
	public void 专注期间回来再离开会在结束后重新挂机关怀()
	{
		string databasePath = Path.Combine(_root, "idle-reset.db");
		using NoriDatabase database = NoriDatabase.Open(databasePath);
		ConfigStore config = new(database);
		config.InitDefaults("test");
		config.Set("language", new ConfigValue.Text("zh-CN"));
		config.Set("proactive_daily_greeting", new ConfigValue.Boolean(false));
		config.Set("proactive_idle_enabled", new ConfigValue.Boolean(true));
		config.Set("proactive_idle_minutes", new ConfigValue.Integer(1));
		using FileLogger logger = new(Path.Combine(_root, "idle-logs"));
		using FocusSession focus = new(StatePath("idle-reset"), new ManualFocusClock(), background: true);
		double? idle = 120;
		using ProactiveScheduler scheduler = new(new ReminderStore(database), config, logger, () => idle, () => focus.Presence, focus.Checkpoint);
		List<ProactiveMessage> messages = [];
		scheduler.Message += messages.Add;
		DateTimeOffset now = DateTimeOffset.UnixEpoch.AddHours(3);

		scheduler.Tick(now.UtcDateTime);
		Assert.Single(messages);
		focus.Start(25, notifyOnComplete: false, silentReminders: true);
		idle = 0;
		scheduler.Tick(now.AddMinutes(1).UtcDateTime);
		idle = 120;
		scheduler.Tick(now.AddMinutes(2).UtcDateTime);
		Assert.Single(messages);
		focus.End();
		scheduler.Tick(now.AddMinutes(3).UtcDateTime);
		Assert.Equal(2, messages.Count);
		Assert.True(messages[1].AllowSpeech);
	}

	public void Dispose()
	{
		if (Directory.Exists(_root)) Directory.Delete(_root, true);
	}

	private FocusSession Create(ManualFocusClock clock) => new(StatePath("session"), clock, background: true);

	private string StatePath(string name) => Path.Combine(_root, name, "state.json");

	private sealed class ManualFocusClock : IFocusClock
	{
		public long Now { get; set; }

		public double SecondsBetween(long start, long end) => end <= start ? 0 : (end - start) / 1000d;
	}
}
