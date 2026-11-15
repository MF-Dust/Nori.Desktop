using Nori.Core.Configuration;
using Nori.Core.Data;
using Nori.Core.Focus;
using Nori.Core.Logging;
using Nori.Core.Proactive;

namespace Nori.Core.Tests;

/// <summary>专注、已排队的主动消息和用户原配置放在同一条路径上检查。</summary>
public sealed class CompanionIntegrationTests : IDisposable
{
	private readonly string _root = Directory.CreateTempSubdirectory("nori-companion-").FullName;

	[Fact]
	public void 已排队的关怀在专注后丢掉且静音提醒不会朗读()
	{
		string databasePath = Path.Combine(_root, "routing.db");
		using NoriDatabase database = NoriDatabase.Open(databasePath);
		ConfigStore config = new(database);
		config.InitDefaults("test");
		config.Set("language", new ConfigValue.Text("zh-CN"));
		config.Set("proactive_daily_greeting", new ConfigValue.Boolean(true));
		config.Set("proactive_idle_enabled", new ConfigValue.Boolean(true));
		config.Set("proactive_idle_minutes", new ConfigValue.Integer(1));
		string greetingBefore = config.GetStringOr("proactive_daily_greeting", "");
		string idleBefore = config.GetStringOr("proactive_idle_enabled", "");
		using FileLogger logger = new(Path.Combine(_root, "logs"));
		LockedFocusClock clock = new();
		string statePath = Path.Combine(_root, "focus", "state.json");
		using FocusSession focus = new(statePath, clock, background: true);
		ReminderStore store = new(database);
		double? idle = 120;
		using ProactiveScheduler scheduler = new(store, config, logger, () => idle, () => focus.Presence, focus.Checkpoint);
		List<ProactiveMessage> messages = [];
		scheduler.Message += messages.Add;

		ProactiveMessage queuedCare = new("已经排好的问候", "wave", "Smile") { Care = true, AllowSpeech = true, AllowAnimation = true };
		ProactiveMessage queuedReminder = new("提醒时间到了：喝水", "wave", "Surprised");
		DateTime localDate = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TimeZoneInfo.Local).Date;
		DateTimeOffset morning = new(TimeZoneInfo.ConvertTimeToUtc(localDate.AddHours(8).AddMinutes(30), TimeZoneInfo.Local), TimeSpan.Zero);
		ReminderItem due = store.Add("喝水", morning.ToUnixTimeMilliseconds() - 1);

		focus.Start(25, notifyOnComplete: false, silentReminders: true);
		FocusPresence pausedLike = focus.Presence;
		Assert.True(pausedLike.Active);
		Assert.False(FocusCompanionPolicy.ShouldDeliver(queuedCare.Care, pausedLike));
		Assert.True(FocusCompanionPolicy.ShouldDeliver(queuedReminder.Care, pausedLike));
		Assert.False(FocusCompanionPolicy.StillAllowsSpeech(queuedReminder.AllowSpeech, pausedLike));
		Assert.False(FocusCompanionPolicy.StillAllowsAnimation(queuedReminder.AllowAnimation, pausedLike));
		Assert.False(FocusCompanionPolicy.ShouldSpeakProactive(
			FocusCompanionPolicy.StillAllowsSpeech(queuedReminder.AllowSpeech, pausedLike),
			autoTts: true));
		Assert.True(FocusCompanionPolicy.ShouldShowProactiveText(allowSpeech: false));
		Assert.True(FocusCompanionPolicy.ShouldSpeakUserReply(autoTts: true, safeMode: false));
		Assert.False(FocusCompanionPolicy.ShouldSpeakUserReply(autoTts: false, safeMode: false));

		scheduler.Tick(morning.UtcDateTime);
		ProactiveMessage reminder = Assert.Single(messages);
		Assert.Contains("喝水", reminder.Text, StringComparison.Ordinal);
		Assert.False(reminder.Care);
		Assert.False(reminder.AllowSpeech);
		Assert.False(reminder.AllowAnimation);
		Assert.False(FocusCompanionPolicy.ShouldSpeakProactive(reminder.AllowSpeech, autoTts: true));
		Assert.Equal("fired", scheduler.GetReminder(due.Id)!.Status);
		scheduler.Tick(morning.UtcDateTime);
		Assert.Single(messages);

		focus.Pause();
		Assert.True(focus.Presence.Active);
		Assert.False(FocusCompanionPolicy.ShouldDeliver(queuedCare.Care, focus.Presence));
		focus.Resume();
		focus.End();
		Assert.False(focus.Presence.Active);
		Assert.True(FocusCompanionPolicy.ShouldDeliver(queuedCare.Care, focus.Presence));
		Assert.False(FocusCompanionPolicy.StillAllowsSpeech(messageAllows: false, focus.Presence));
		Assert.False(FocusCompanionPolicy.ShouldSpeakProactive(allowSpeech: false, autoTts: true));
		Assert.Equal(greetingBefore, config.GetStringOr("proactive_daily_greeting", ""));
		Assert.Equal(idleBefore, config.GetStringOr("proactive_idle_enabled", ""));
	}

	[Fact]
	public void 退出后重新加载保留暂停且不把同一段时长记两次()
	{
		string databasePath = Path.Combine(_root, "exit.db");
		using NoriDatabase database = NoriDatabase.Open(databasePath);
		ConfigStore config = new(database);
		config.InitDefaults("test");
		config.Set("proactive_daily_greeting", new ConfigValue.Boolean(true));
		config.Set("proactive_idle_enabled", new ConfigValue.Boolean(false));
		string greetingBefore = config.GetStringOr("proactive_daily_greeting", "");
		string idleBefore = config.GetStringOr("proactive_idle_enabled", "");
		LockedFocusClock clock = new();
		string path = Path.Combine(_root, "exit", "state.json");
		using (FocusSession session = new(path, clock, background: true))
		{
			session.Start(25, notifyOnComplete: true, silentReminders: true);
			clock.Now = 5_000;
			session.Checkpoint();
		}

		using FocusSession restored = new(path, new LockedFocusClock(), background: true);
		FocusView paused = restored.View();
		Assert.Equal("paused", paused.Phase);
		Assert.Equal(5, paused.ElapsedSeconds);
		Assert.Equal(0, paused.CompletedCount);
		Assert.Equal(0, paused.TotalSeconds);
		Assert.False(restored.IsScheduleArmed);
		restored.End();
		FocusView ended = restored.View();
		Assert.Equal("idle", ended.Phase);
		Assert.Equal(0, ended.CompletedCount);
		Assert.Equal(5, ended.TotalSeconds);
		Assert.Null(restored.ConsumeCompletionNotice(false));
		Assert.Equal(greetingBefore, config.GetStringOr("proactive_daily_greeting", ""));
		Assert.Equal(idleBefore, config.GetStringOr("proactive_idle_enabled", ""));

		using FocusSession again = new(path, new LockedFocusClock(), background: true);
		Assert.Equal(5, again.View().TotalSeconds);
		Assert.Equal(0, again.View().CompletedCount);
	}

	[Fact]
	public void 到点后并发结束暂停和检查只记一次完成()
	{
		for (int round = 0; round < 8; round++)
		{
			LockedFocusClock clock = new();
			string path = Path.Combine(_root, "race-" + round.ToString(), "state.json");
			using FocusSession session = new(path, clock, background: true);
			session.Start(1, notifyOnComplete: true, silentReminders: true);
			clock.Now = 60_000;
			int notices = 0;
			Parallel.Invoke(
				() => TryFocus(() => session.End()),
				() => TryFocus(() => session.Pause()),
				() => TryFocus(() => session.Evaluate()),
				() => TryFocus(() => session.Checkpoint()),
				() => TryFocus(() => session.Resume()),
				() => TryFocus(() => session.End()),
				() => TryFocus(() => _ = session.View()),
				() => TryFocus(() =>
				{
					if (session.ConsumeCompletionNotice(false) is not null)
						Interlocked.Increment(ref notices);
				}));
			if (session.ConsumeCompletionNotice(false) is not null)
				Interlocked.Increment(ref notices);

			FocusView view = session.View();
			Assert.Equal(1, view.CompletedCount);
			Assert.Equal(60, view.TotalSeconds);
			Assert.Equal(1, notices);
			Assert.Null(session.ConsumeCompletionNotice(false));
			FocusRecord recent = Assert.Single(view.Recent);
			Assert.True(recent.Completed);
			Assert.Equal(60, recent.Seconds);
		}
	}

	public void Dispose()
	{
		if (Directory.Exists(_root)) Directory.Delete(_root, true);
	}

	private static void TryFocus(Action action)
	{
		try
		{
			action();
		}
		catch (InvalidOperationException)
		{
			// 另一条路径已经把这场收尾时，后到的暂停或继续会拒绝。
		}
	}

	private sealed class LockedFocusClock : IFocusClock
	{
		private long _now;

		public long Now
		{
			get => Volatile.Read(ref _now);
			set => Volatile.Write(ref _now, value);
		}

		public double SecondsBetween(long start, long end) => end <= start ? 0 : (end - start) / 1000d;
	}
}
