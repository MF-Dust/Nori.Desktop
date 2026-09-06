using System.Globalization;
using Microsoft.Data.Sqlite;
using Nori.Core.Data;

namespace Nori.Core.Automation;

/// <summary>审计任务种类；只允许自动化域内的固定分类。</summary>
public enum AutomationAuditTaskKind
{
	/// <summary>受限 DOM 浏览器任务。</summary>
	Browser,
	/// <summary>桌面视觉任务。</summary>
	Desktop,
}

/// <summary>审计事件或动作分类；不接受自由文本。</summary>
public enum AutomationAuditEventCategory
{
	/// <summary>任务排队、启动或终态变化。</summary>
	Task,
	/// <summary>浏览器导航。</summary>
	Navigate,
	/// <summary>浏览器元素点击。</summary>
	Click,
	/// <summary>浏览器表单填写。</summary>
	Fill,
	/// <summary>浏览器滚动。</summary>
	Scroll,
	/// <summary>浏览器等待。</summary>
	Wait,
	/// <summary>浏览器可见文本读取。</summary>
	ReadVisibleText,
	/// <summary>浏览器安全页面保护。</summary>
	SafePage,
	/// <summary>自动化高风险动作审批。</summary>
	Approval,
}

/// <summary>审计结果；不包含异常消息或页面数据。</summary>
public enum AutomationAuditOutcome
{
	/// <summary>任务已排队。</summary>
	Queued,
	/// <summary>任务正在运行。</summary>
	Running,
	/// <summary>动作或任务成功。</summary>
	Succeeded,
	/// <summary>动作或任务失败。</summary>
	Failed,
	/// <summary>任务或审批已取消。</summary>
	Cancelled,
	/// <summary>安全策略拒绝操作。</summary>
	Rejected,
	/// <summary>审批请求已展示。</summary>
	Requested,
	/// <summary>用户已批准。</summary>
	Approved,
	/// <summary>用户已拒绝。</summary>
	Denied,
	/// <summary>审批或任务已经超时。</summary>
	TimedOut,
	/// <summary>任务因安全页面暂停。</summary>
	Paused,
}

/// <summary>写入审计库的固定字段事件。</summary>
public sealed record AutomationAuditEvent(
	DateTimeOffset Timestamp,
	Guid? TaskId,
	AutomationAuditTaskKind TaskKind,
	AutomationAuditEventCategory Category,
	AutomationAuditOutcome Outcome,
	string? FailureCode = null,
	TimeSpan? Duration = null);

/// <summary>读取审计库的脱敏记录。</summary>
public sealed record AutomationAuditRecord(
	string Id,
	Guid? TaskId,
	DateTimeOffset Timestamp,
	AutomationAuditTaskKind TaskKind,
	AutomationAuditEventCategory Category,
	AutomationAuditOutcome Outcome,
	string? FailureCode,
	long? DurationMilliseconds);

/// <summary>
/// 有界、脱敏的自动化审计仓储。
///
/// 仅持久化固定分类、稳定失败码和耗时；不接受 URL、选择器、文本、截图、提示词、参数、凭据或路径。
/// </summary>
public sealed class AutomationAuditRepository
{
	/// <summary>最多保留的记录数。</summary>
	public const int MaximumRecords = 500;

	/// <summary>记录最长保留时间。</summary>
	public static TimeSpan Retention { get; } = TimeSpan.FromDays(30);

	/// <summary>单次查询的最大记录数。</summary>
	public const int MaximumQueryRecords = 100;

	private readonly NoriDatabase _database;
	private readonly TimeProvider _timeProvider;

	/// <summary>创建审计仓储；时间源可注入以测试过期清理。</summary>
	public AutomationAuditRepository(NoriDatabase database, TimeProvider? timeProvider = null)
	{
		_database = database ?? throw new ArgumentNullException(nameof(database));
		_timeProvider = timeProvider ?? TimeProvider.System;
	}

	/// <summary>写入一条固定字段审计事件。</summary>
	public void Record(AutomationAuditEvent entry)
	{
		ArgumentNullException.ThrowIfNull(entry);
		DateTimeOffset timestamp = entry.Timestamp.ToUniversalTime();
		string? failureCode = NormalizeFailureCode(entry.FailureCode);
		long? durationMilliseconds = NormalizeDuration(entry.Duration);
		_database.Locked(connection =>
		{
			using SqliteTransaction transaction = connection.BeginTransaction();
			Prune(connection, transaction, _timeProvider.GetUtcNow());
			using (SqliteCommand command = connection.CreateCommand())
			{
				command.Transaction = transaction;
				command.CommandText = """
					INSERT INTO automation_audit
						(timestamp, task_id, task_kind, event_category, outcome, failure_code, duration_ms)
					VALUES ($timestamp, $taskId, $taskKind, $category, $outcome, $failureCode, $durationMilliseconds);
					""";
				command.Parameters.AddWithValue("$timestamp", ToStorage(timestamp));
				command.Parameters.AddWithValue("$taskId", entry.TaskId is { } taskId ? taskId.ToString("D") : DBNull.Value);
				command.Parameters.AddWithValue("$taskKind", ToStorage(entry.TaskKind));
				command.Parameters.AddWithValue("$category", ToStorage(entry.Category));
				command.Parameters.AddWithValue("$outcome", ToStorage(entry.Outcome));
				command.Parameters.AddWithValue("$failureCode", failureCode ?? (object)DBNull.Value);
				command.Parameters.AddWithValue("$durationMilliseconds", durationMilliseconds ?? (object)DBNull.Value);
				command.ExecuteNonQuery();
			}
			// 事件时间来自受控运行时；仍在插入后再次清理，避免过期记录短暂滞留。
			Prune(connection, transaction, _timeProvider.GetUtcNow());
			PruneOverflow(connection, transaction);
			transaction.Commit();
		});
	}

	/// <summary>按时间倒序读取有界的脱敏审计记录。</summary>
	public IReadOnlyList<AutomationAuditRecord> List(int limit = MaximumQueryRecords)
	{
		int boundedLimit = Math.Clamp(limit, 1, MaximumQueryRecords);
		return _database.Locked(connection =>
		{
			using (SqliteTransaction transaction = connection.BeginTransaction())
			{
				Prune(connection, transaction, _timeProvider.GetUtcNow());
				PruneOverflow(connection, transaction);
				transaction.Commit();
			}

			using SqliteCommand command = connection.CreateCommand();
			command.CommandText = """
				SELECT id, timestamp, task_id, task_kind, event_category, outcome, failure_code, duration_ms
				FROM automation_audit
				ORDER BY timestamp DESC, id DESC
				LIMIT $limit;
				""";
			command.Parameters.AddWithValue("$limit", boundedLimit);
			using SqliteDataReader reader = command.ExecuteReader();
			List<AutomationAuditRecord> records = [];
			while (reader.Read())
			{
				string? taskIdText = reader.IsDBNull(2) ? null : reader.GetString(2);
				Guid? taskId = Guid.TryParse(taskIdText, out Guid parsedTaskId) ? parsedTaskId : null;
				records.Add(new AutomationAuditRecord(
					reader.GetInt64(0).ToString(CultureInfo.InvariantCulture),
					taskId,
					DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
					FromTaskKind(reader.GetString(3)),
					FromCategory(reader.GetString(4)),
					FromOutcome(reader.GetString(5)),
					reader.IsDBNull(6) ? null : NormalizeFailureCode(reader.GetString(6)),
					reader.IsDBNull(7) ? null : reader.GetInt64(7)));
			}
			return (IReadOnlyList<AutomationAuditRecord>)records;
		});
	}

	private static void Prune(SqliteConnection connection, SqliteTransaction transaction, DateTimeOffset now)
	{
		using SqliteCommand command = connection.CreateCommand();
		command.Transaction = transaction;
		command.CommandText = "DELETE FROM automation_audit WHERE timestamp < $cutoff;";
		command.Parameters.AddWithValue("$cutoff", ToStorage(now.ToUniversalTime() - Retention));
		command.ExecuteNonQuery();
	}

	private static void PruneOverflow(SqliteConnection connection, SqliteTransaction transaction)
	{
		using SqliteCommand command = connection.CreateCommand();
		command.Transaction = transaction;
		command.CommandText = """
			DELETE FROM automation_audit
			WHERE id NOT IN (
				SELECT id FROM automation_audit ORDER BY timestamp DESC, id DESC LIMIT $maximum
			);
			""";
		command.Parameters.AddWithValue("$maximum", MaximumRecords);
		command.ExecuteNonQuery();
	}

	private static string ToStorage(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

	private static string ToStorage(AutomationAuditTaskKind value) => value switch
	{
		AutomationAuditTaskKind.Browser => "browser",
		AutomationAuditTaskKind.Desktop => "desktop",
		_ => "desktop",
	};

	private static string ToStorage(AutomationAuditEventCategory value) => value switch
	{
		AutomationAuditEventCategory.Task => "task",
		AutomationAuditEventCategory.Navigate => "navigate",
		AutomationAuditEventCategory.Click => "click",
		AutomationAuditEventCategory.Fill => "fill",
		AutomationAuditEventCategory.Scroll => "scroll",
		AutomationAuditEventCategory.Wait => "wait",
		AutomationAuditEventCategory.ReadVisibleText => "read_visible_text",
		AutomationAuditEventCategory.SafePage => "safe_page",
		AutomationAuditEventCategory.Approval => "approval",
		_ => "task",
	};

	private static string ToStorage(AutomationAuditOutcome value) => value switch
	{
		AutomationAuditOutcome.Queued => "queued",
		AutomationAuditOutcome.Running => "running",
		AutomationAuditOutcome.Succeeded => "succeeded",
		AutomationAuditOutcome.Failed => "failed",
		AutomationAuditOutcome.Cancelled => "cancelled",
		AutomationAuditOutcome.Rejected => "rejected",
		AutomationAuditOutcome.Requested => "requested",
		AutomationAuditOutcome.Approved => "approved",
		AutomationAuditOutcome.Denied => "denied",
		AutomationAuditOutcome.TimedOut => "timed_out",
		AutomationAuditOutcome.Paused => "paused",
		_ => "failed",
	};

	private static AutomationAuditTaskKind FromTaskKind(string value) => value == "browser"
		? AutomationAuditTaskKind.Browser
		: AutomationAuditTaskKind.Desktop;

	private static AutomationAuditEventCategory FromCategory(string value) => value switch
	{
		"navigate" => AutomationAuditEventCategory.Navigate,
		"click" => AutomationAuditEventCategory.Click,
		"fill" => AutomationAuditEventCategory.Fill,
		"scroll" => AutomationAuditEventCategory.Scroll,
		"wait" => AutomationAuditEventCategory.Wait,
		"read_visible_text" => AutomationAuditEventCategory.ReadVisibleText,
		"safe_page" => AutomationAuditEventCategory.SafePage,
		"approval" => AutomationAuditEventCategory.Approval,
		_ => AutomationAuditEventCategory.Task,
	};

	private static AutomationAuditOutcome FromOutcome(string value) => value switch
	{
		"queued" => AutomationAuditOutcome.Queued,
		"running" => AutomationAuditOutcome.Running,
		"succeeded" => AutomationAuditOutcome.Succeeded,
		"cancelled" => AutomationAuditOutcome.Cancelled,
		"rejected" => AutomationAuditOutcome.Rejected,
		"requested" => AutomationAuditOutcome.Requested,
		"approved" => AutomationAuditOutcome.Approved,
		"denied" => AutomationAuditOutcome.Denied,
		"timed_out" => AutomationAuditOutcome.TimedOut,
		"paused" => AutomationAuditOutcome.Paused,
		_ => AutomationAuditOutcome.Failed,
	};

	private static long? NormalizeDuration(TimeSpan? duration)
	{
		if (duration is not { } value || value < TimeSpan.Zero) return null;
		return Math.Min((long)value.TotalMilliseconds, (long)BrowserAutomationTaskLimits.MaximumDuration.TotalMilliseconds);
	}

	private static string? NormalizeFailureCode(string? value)
	{
		if (string.IsNullOrWhiteSpace(value)) return null;
		return value.Trim() switch
		{
			"execution_failed" => "execution_failed",
			"timeout" => "timeout",
			"safe_page" => "safe_page",
			"approval_denied" => "approval_denied",
			"approval_timeout" => "approval_timeout",
			"approval_cancelled" => "approval_cancelled",
			"approval_failed" => "approval_failed",
			"policy_rejected" => "policy_rejected",
			"start_failed" => "start_failed",
			"invalid_action" => "invalid_action",
			"browser_unavailable" => "browser_unavailable",
			"cancelled" => "cancelled",
			"target_not_foreground" => "target_not_foreground",
			"screenshot_failed" => "screenshot_failed",
			"planner_failed" => "planner_failed",
			"step_limit_exceeded" => "step_limit_exceeded",
			"invalid_input" => "invalid_input",
			_ => "execution_failed",
		};
	}
}
