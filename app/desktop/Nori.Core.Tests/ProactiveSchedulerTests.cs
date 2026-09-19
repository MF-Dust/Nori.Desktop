using Nori.Core.Configuration;
using Nori.Core.Data;
using Nori.Core.Logging;
using Nori.Core.Proactive;

namespace Nori.Core.Tests;

public sealed class ProactiveSchedulerTests
{
	[Fact]
	public void 调度器限制提醒内容时间重复规则和时区()
	{
		string path = NewPath();
		try
		{
			using NoriDatabase database = NoriDatabase.Open(path);
			ConfigStore config = new(database);
			config.InitDefaults("Dev");
			using FileLogger logger = new(Path.Combine(Path.GetDirectoryName(path)!, "logs"));
			using ProactiveScheduler scheduler = new(new ReminderStore(database), config, logger, () => null);

			Assert.Throws<InvalidOperationException>(() => scheduler.AddReminder(new string('x', 201), 15));
			Assert.Throws<InvalidOperationException>(() => scheduler.AddReminder("无穷延迟", double.PositiveInfinity));
			Assert.Throws<InvalidOperationException>(() => scheduler.AddReminder("零延迟", 0));
			Assert.Throws<InvalidOperationException>(() => scheduler.AddReminder("无效时区", 15, false, "Not/AZone"));
			Assert.Throws<InvalidOperationException>(() => scheduler.AddReminder("无效重复", 15, true, "UTC", "{\"type\":\"weekly\"}"));
			Assert.Throws<InvalidOperationException>(() => scheduler.AddReminder("伪造规则", 15, false, "UTC", "{\"type\":\"daily\"}"));

			ReminderItem added = scheduler.AddReminder("每日提醒", 15, true, "UTC");
			Assert.Equal("{\"type\":\"daily\"}", added.RecurrenceJson);
			Assert.True(added.RepeatDaily);
			ReminderItem updated = scheduler.UpdateReminder(added.Id, "更新后的提醒", DateTimeOffset.UtcNow.AddMinutes(20).ToUnixTimeMilliseconds(), false, "UTC", null);
			Assert.Equal("更新后的提醒", updated.Content);
			Assert.False(updated.RepeatDaily);
			Assert.Null(updated.RecurrenceJson);
		}
		finally { DeleteDatabase(path); }
	}

	[Fact]
	public void 调度器推迟和完成会停止当前领取()
	{
		string path = NewPath();
		try
		{
			using NoriDatabase database = NoriDatabase.Open(path);
			ConfigStore config = new(database);
			config.InitDefaults("Dev");
			using FileLogger logger = new(Path.Combine(Path.GetDirectoryName(path)!, "logs"));
			ReminderStore store = new(database);
			using ProactiveScheduler scheduler = new(store, config, logger, () => null);
			ReminderItem added = scheduler.AddReminder("推迟测试", 15);
			ReminderItem snoozed = scheduler.SnoozeReminder(added.Id, 30);
			Assert.NotNull(snoozed.SnoozedUntil);
			Assert.Empty(store.TakeDue(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
			Assert.True(scheduler.CompleteReminder(added.Id));
			Assert.Equal("completed", scheduler.GetReminder(added.Id)!.Status);
			Assert.False(scheduler.CompleteReminder(added.Id));
			Assert.Empty(store.TakeDue(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 86_400_000));
		}
		finally { DeleteDatabase(path); }
	}

	[Fact]
	public void 更新绝对时间超过上限会拒绝()
	{
		string path = NewPath();
		try
		{
			using NoriDatabase database = NoriDatabase.Open(path);
			ConfigStore config = new(database);
			config.InitDefaults("Dev");
			using FileLogger logger = new(Path.Combine(Path.GetDirectoryName(path)!, "logs"));
			using ProactiveScheduler scheduler = new(new ReminderStore(database), config, logger, () => null);
			ReminderItem added = scheduler.AddReminder("时间边界", 15);
			long tooFar = DateTimeOffset.UtcNow.AddDays(31).ToUnixTimeMilliseconds();
			Assert.Throws<InvalidOperationException>(() => scheduler.UpdateReminder(added.Id, triggerAt: tooFar));
			Assert.Throws<InvalidOperationException>(() => scheduler.SnoozeReminderUntil(added.Id, tooFar));
		}
		finally { DeleteDatabase(path); }
	}

	private static string NewPath() => Path.Combine(Path.GetTempPath(), $"nori-proactive-{Guid.NewGuid():N}.db");

	private static void DeleteDatabase(string path)
	{
		try
		{
			File.Delete(path);
			File.Delete($"{path}-wal");
			File.Delete($"{path}-shm");
			foreach (string backup in Directory.GetFiles(Path.GetDirectoryName(path)!, $"{Path.GetFileName(path)}.pre-migration-*.bak")) File.Delete(backup);
			Directory.Delete(Path.Combine(Path.GetDirectoryName(path)!, "logs"), true);
		}
		catch (IOException) { }
	}
}
