using Microsoft.Data.Sqlite;
using Nori.Core.Automation;
using Nori.Core.Data;
using Nori.Core.Tests.TestSupport;

namespace Nori.Core.Tests;

/// <summary>自动化审计迁移、留存和脱敏边界测试。</summary>
public sealed class AutomationAuditTests : IDisposable
{
	private readonly TempDatabase _tempDatabase = new("nori-automation-audit");

	[Fact]
	public void v7迁移创建审计表并保留迁移备份()
	{
		using (NoriDatabase initial = NoriDatabase.Open(_tempDatabase.Path))
		{
			initial.Locked(connection =>
			{
				using SqliteCommand command = connection.CreateCommand();
				command.CommandText = "DROP TABLE automation_audit; PRAGMA user_version = 6;";
				command.ExecuteNonQuery();
			});
		}

		using NoriDatabase migrated = NoriDatabase.Open(_tempDatabase.Path);
		long version = migrated.Locked(connection =>
		{
			using SqliteCommand command = connection.CreateCommand();
			command.CommandText = "PRAGMA user_version;";
			return Convert.ToInt64(command.ExecuteScalar());
		});
		long tableCount = migrated.Locked(connection =>
		{
			using SqliteCommand command = connection.CreateCommand();
			command.CommandText = "SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name = 'automation_audit';";
			return Convert.ToInt64(command.ExecuteScalar());
		});

		Assert.Equal(NoriDatabase.DatabaseSchemaVersion, version);
		Assert.Equal(1, tableCount);
		Assert.Single(Directory.GetFiles(Path.GetDirectoryName(_tempDatabase.Path)!, $"{Path.GetFileName(_tempDatabase.Path)}.pre-migration-*.bak"));
	}

	[Fact]
	public void 审计只保存固定字段并将自由失败文本降级()
	{
		using NoriDatabase database = NoriDatabase.Open(_tempDatabase.Path);
		AutomationAuditRepository repository = new(database);
		Guid taskId = Guid.NewGuid();
		repository.Record(new AutomationAuditEvent(
			DateTimeOffset.UtcNow,
			taskId,
			AutomationAuditTaskKind.Browser,
			AutomationAuditEventCategory.Fill,
			AutomationAuditOutcome.Failed,
			"https://example.test/?token=secret 输入正文"));

		AutomationAuditRecord record = Assert.Single(repository.List());
		Assert.Equal(taskId, record.TaskId);
		Assert.Equal(AutomationAuditTaskKind.Browser, record.TaskKind);
		Assert.Equal(AutomationAuditEventCategory.Fill, record.Category);
		Assert.Equal(AutomationAuditOutcome.Failed, record.Outcome);
		Assert.Equal("execution_failed", record.FailureCode);

		string raw = database.Locked(connection =>
		{
			using SqliteCommand command = connection.CreateCommand();
			command.CommandText = "SELECT task_kind || '|' || event_category || '|' || outcome || '|' || coalesce(failure_code, '') FROM automation_audit;";
			return command.ExecuteScalar()?.ToString() ?? "";
		});
		Assert.DoesNotContain("example.test", raw, StringComparison.Ordinal);
		Assert.DoesNotContain("token=secret", raw, StringComparison.Ordinal);
		Assert.DoesNotContain("输入正文", raw, StringComparison.Ordinal);
	}

	[Fact]
	public void 审计保留最多五百条并清除三十天前记录()
	{
		MutableTimeProvider clock = new(new DateTimeOffset(2026, 1, 31, 0, 0, 0, TimeSpan.Zero));
		using NoriDatabase database = NoriDatabase.Open(_tempDatabase.Path);
		AutomationAuditRepository repository = new(database, clock);
		repository.Record(new AutomationAuditEvent(
			clock.GetUtcNow() - AutomationAuditRepository.Retention - TimeSpan.FromSeconds(1),
			Guid.NewGuid(),
			AutomationAuditTaskKind.Desktop,
			AutomationAuditEventCategory.Task,
			AutomationAuditOutcome.Succeeded));
		for (int index = 0; index < AutomationAuditRepository.MaximumRecords + 12; index++)
		{
			repository.Record(new AutomationAuditEvent(
				clock.GetUtcNow().AddMilliseconds(index),
				Guid.NewGuid(),
				AutomationAuditTaskKind.Browser,
				AutomationAuditEventCategory.ReadVisibleText,
				AutomationAuditOutcome.Succeeded,
				Duration: TimeSpan.FromMilliseconds(index)));
		}

		long count = database.Locked(connection =>
		{
			using SqliteCommand command = connection.CreateCommand();
			command.CommandText = "SELECT count(*) FROM automation_audit;";
			return Convert.ToInt64(command.ExecuteScalar());
		});
		IReadOnlyList<AutomationAuditRecord> listed = repository.List(1000);

		Assert.Equal(AutomationAuditRepository.MaximumRecords, count);
		Assert.Equal(AutomationAuditRepository.MaximumQueryRecords, listed.Count);
		Assert.All(listed, record => Assert.True(record.Timestamp >= clock.GetUtcNow() - AutomationAuditRepository.Retention));
	}

	public void Dispose()
	{
		try
		{
			foreach (string backup in Directory.GetFiles(Path.GetDirectoryName(_tempDatabase.Path)!, $"{Path.GetFileName(_tempDatabase.Path)}.pre-migration-*.bak")) File.Delete(backup);
		}
		catch (IOException)
		{
		}
		_tempDatabase.Dispose();
	}
}
