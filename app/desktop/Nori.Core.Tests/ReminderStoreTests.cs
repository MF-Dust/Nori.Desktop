using Microsoft.Data.Sqlite;
using Nori.Core.Data;
using Nori.Core.Proactive;

namespace Nori.Core.Tests;

public sealed class ReminderStoreTests
{
	[Fact]
	public void 逐级提醒迁移保留旧列并回填领取字段()
	{
		string path = NewPath();
		try
		{
			using (SqliteConnection connection = new($"Data Source={path}"))
			{
				connection.Open();
				using SqliteCommand command = connection.CreateCommand();
				command.CommandText = """
					CREATE TABLE reminders (id TEXT PRIMARY KEY, content TEXT NOT NULL, trigger_at INTEGER NOT NULL, repeat_daily INTEGER NOT NULL DEFAULT 0, created_at TEXT NOT NULL);
					INSERT INTO reminders (id, content, trigger_at, repeat_daily, created_at) VALUES ('legacy', '每日提醒', 123, 1, '2026-01-01T00:00:00Z');
					PRAGMA user_version = 3;
					""";
				command.ExecuteNonQuery();
			}
			using NoriDatabase database = NoriDatabase.Open(path);
			(string repeat, string status, string timezone, string? recurrence, string updated) row = database.Locked(connection =>
			{
				using SqliteCommand command = connection.CreateCommand();
				command.CommandText = "SELECT repeat_daily, status, timezone, recurrence_json, updated_at FROM reminders WHERE id = 'legacy'";
				using SqliteDataReader reader = command.ExecuteReader();
				Assert.True(reader.Read());
				return (reader.GetInt64(0).ToString(), reader.GetString(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetString(4));
			});
			Assert.Equal(NoriDatabase.DatabaseSchemaVersion, database.Locked(ReadVersion));
			Assert.Equal("1", row.repeat);
			Assert.Equal("pending", row.status);
			Assert.Equal("UTC", row.timezone);
			Assert.Equal("{\"type\":\"daily\"}", row.recurrence);
			Assert.Equal("2026-01-01T00:00:00Z", row.updated);
		}
		finally { DeleteDatabase(path); }
	}

	[Fact]
	public async Task 并发领取同一提醒只返回一份()
	{
		string path = NewPath();
		try
		{
			using NoriDatabase firstDatabase = NoriDatabase.Open(path);
			ReminderStore first = new(firstDatabase);
			first.Add("只投递一次", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 1);
			using NoriDatabase secondDatabase = NoriDatabase.Open(path);
			ReminderStore second = new(secondDatabase);
			long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
			IReadOnlyList<ReminderItem>[] results = await Task.WhenAll(
				Task.Run(() => first.TakeDue(now)), Task.Run(() => second.TakeDue(now)));
			Assert.Single(results.SelectMany(items => items));
			Assert.Contains(results, items => items.Count == 0);
			Assert.Equal("claimed", Assert.Single(results.SelectMany(items => items)).Status);
		}
		finally { DeleteDatabase(path); }
	}

	[Fact]
	public void 领取失败释放后可重试并确认后结束()
	{
		string path = NewPath();
		try
		{
			using NoriDatabase database = NoriDatabase.Open(path);
			ReminderStore store = new(database);
			ReminderItem added = store.Add("可恢复提醒", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 1);
			long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
			Assert.Single(store.TakeDue(now));
			Assert.Empty(store.TakeDue(now));
			Assert.True(store.ReleaseClaim(added.Id));
			Assert.Equal(added.Id, Assert.Single(store.TakeDue(now)).Id);
			Assert.True(store.MarkFired(added.Id));
			Assert.Empty(store.TakeDue(now));
			Assert.Empty(store.List());
		}
		finally { DeleteDatabase(path); }
	}

	[Fact]
	public void 过期领取租约可以重试()
	{
		string path = NewPath();
		try
		{
			using NoriDatabase database = NoriDatabase.Open(path);
			ReminderStore store = new(database);
			store.Add("崩溃后重试", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 1);
			long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
			Assert.Single(store.TakeDue(now));
			database.Locked(connection =>
			{
				using SqliteCommand command = connection.CreateCommand();
				command.CommandText = "UPDATE reminders SET claimed_at = '2000-01-01T00:00:00Z'";
				command.ExecuteNonQuery();
			});
			Assert.Single(store.TakeDue(now));
		}
		finally { DeleteDatabase(path); }
	}

	[Fact]
	public void 更新推迟后重启仍可领取并兼容每日重复()
	{
		string path = NewPath();
		try
		{
			long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
			ReminderItem added;
			long snoozedUntil = now + 60_000;
			using (NoriDatabase database = NoriDatabase.Open(path))
			{
				ReminderStore store = new(database);
				added = store.Add("旧版每日提醒", now - 1);
				Assert.True(store.Update(added.Id, "更新后的每日提醒", now - 1, true, "UTC", "{\"type\":\"daily\"}"));
				Assert.True(store.Snooze(added.Id, snoozedUntil));
				Assert.Empty(store.TakeDue(now + 30_000));
			}

			using (NoriDatabase reopened = NoriDatabase.Open(path))
			{
				ReminderStore store = new(reopened);
				ReminderItem saved = Assert.Single(store.List());
				Assert.Equal("更新后的每日提醒", saved.Content);
				Assert.True(saved.RepeatDaily);
				Assert.Equal(snoozedUntil, saved.SnoozedUntil);
				ReminderItem claimed = Assert.Single(store.TakeDue(snoozedUntil + 1));
				Assert.Equal("claimed", claimed.Status);
				Assert.Null(claimed.SnoozedUntil);
				Assert.True(store.MarkFired(added.Id));
				ReminderItem? repeated = store.Get(added.Id);
				Assert.NotNull(repeated);
				Assert.Equal("pending", repeated!.Status);
				Assert.Equal(now - 1 + 86_400_000, repeated.TriggerAt);
			}
		}
		finally { DeleteDatabase(path); }
	}

	[Fact]
	public void 每日提醒按时区跨夏令时保持本地时间()
	{
		TimeZoneInfo zone;
		try
		{
			zone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
		}
		catch (Exception exception) when (exception is TimeZoneNotFoundException or InvalidTimeZoneException)
		{
			return;
		}

		DateTimeOffset beforeSpring = new(2026, 3, 7, 8, 30, 0, TimeSpan.Zero); // 03:30 EST
		long springNext = ReminderStore.NextDailyTrigger(beforeSpring.ToUnixTimeMilliseconds(), "America/New_York");
		DateTimeOffset springLocal = DateTimeOffset.FromUnixTimeMilliseconds(springNext)
			.ToOffset(zone.GetUtcOffset(DateTimeOffset.FromUnixTimeMilliseconds(springNext)));
		Assert.Equal(new DateTime(2026, 3, 8, 3, 30, 0), springLocal.DateTime);
		Assert.Equal(23 * 60 * 60 * 1000, springNext - beforeSpring.ToUnixTimeMilliseconds());

		DateTimeOffset beforeFall = new(2026, 10, 31, 5, 30, 0, TimeSpan.Zero); // 01:30 EDT
		long fallNext = ReminderStore.NextDailyTrigger(beforeFall.ToUnixTimeMilliseconds(), "America/New_York");
		DateTimeOffset fallLocal = DateTimeOffset.FromUnixTimeMilliseconds(fallNext)
			.ToOffset(zone.GetUtcOffset(DateTimeOffset.FromUnixTimeMilliseconds(fallNext)));
		Assert.Equal(new DateTime(2026, 11, 1, 1, 30, 0), fallLocal.DateTime);
		Assert.Equal(25 * 60 * 60 * 1000, fallNext - beforeFall.ToUnixTimeMilliseconds());
	}

	[Fact]
	public void 完成或取消后不会再次进入到期领取()
	{
		string path = NewPath();
		try
		{
			using NoriDatabase database = NoriDatabase.Open(path);
			ReminderStore store = new(database);
			long due = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 1;
			ReminderItem completed = store.Add("完成后不再提醒", due);
			Assert.True(store.Complete(completed.Id));
			Assert.Equal("completed", store.Get(completed.Id)!.Status);
			Assert.Empty(store.TakeDue(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
			Assert.Empty(store.List());

			ReminderItem cancelled = store.Add("取消后不再提醒", due);
			Assert.True(store.Cancel(cancelled.Id));
			Assert.Equal("cancelled", store.Get(cancelled.Id)!.Status);
			Assert.False(store.MarkFired(cancelled.Id));
			Assert.Empty(store.TakeDue(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
		}
		finally { DeleteDatabase(path); }
	}

	[Fact]
	public void 领取中完成不会被旧投递确认复活()
	{
		string path = NewPath();
		try
		{
			using NoriDatabase database = NoriDatabase.Open(path);
			ReminderStore store = new(database);
			ReminderItem item = store.Add("领取中完成", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 1);
			Assert.Single(store.TakeDue(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
			Assert.True(store.Complete(item.Id));
			Assert.False(store.MarkFired(item.Id));
			Assert.Equal("completed", store.Get(item.Id)!.Status);
			Assert.Empty(store.TakeDue(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
		}
		finally { DeleteDatabase(path); }
	}

	private static long ReadVersion(SqliteConnection connection)
	{
		using SqliteCommand command = connection.CreateCommand();
		command.CommandText = "PRAGMA user_version";
		return Convert.ToInt64(command.ExecuteScalar());
	}

	private static string NewPath() => Path.Combine(Path.GetTempPath(), $"nori-reminder-{Guid.NewGuid():N}.db");

	private static void DeleteDatabase(string path)
	{
		try
		{
			File.Delete(path); File.Delete($"{path}-wal"); File.Delete($"{path}-shm");
			foreach (string backup in Directory.GetFiles(Path.GetDirectoryName(path)!, $"{Path.GetFileName(path)}.pre-migration-*.bak")) File.Delete(backup);
		}
		catch (IOException) { }
	}
}
