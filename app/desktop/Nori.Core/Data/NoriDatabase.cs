using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Nori.Core.Data;

/// <summary>
/// 数据库
///
/// 对应 Rust 版的 db.rs: 打开或创建 nori.db, 建表, 补默认配置, 校验结构版本.
/// Rust 用 Mutex&lt;Connection&gt; 跨命令共享单连接, 这里用同一把锁保持等价语义.
/// </summary>
public sealed class NoriDatabase : IDisposable
{
	private enum StartupStatement
	{
		ForeignKeys,
		JournalMode,
		SynchronousMode,
		WalAutoCheckpoint,
		BusyTimeout,
		Schema,
	}

	/// <summary>当前数据库结构版本</summary>
	public const long DatabaseSchemaVersion = 7;

	/// <summary>单个迁移备份的最大大小，避免损坏的旧库拖垮磁盘。</summary>
	private const long MigrationBackupMaxBytes = 64L * 1024 * 1024;

	/// <summary>每个数据库最多保留的迁移前备份数。</summary>
	private const int MigrationBackupCount = 3;

	private const string MigrationBackupMarker = ".pre-migration-";

	/// <summary>建表语句, 与 Rust 版 SCHEMA 完全一致</summary>
	private const string Schema = """
		CREATE TABLE IF NOT EXISTS config (
		    key   TEXT PRIMARY KEY,
		    value TEXT NOT NULL
		);
		CREATE TABLE IF NOT EXISTS chat_messages (
		    id         INTEGER PRIMARY KEY AUTOINCREMENT,
		    role       TEXT NOT NULL,
		    content    TEXT NOT NULL,
		    created_at TEXT NOT NULL
		);
		CREATE TABLE IF NOT EXISTS memories (
		    id          INTEGER PRIMARY KEY AUTOINCREMENT,
		    type        TEXT NOT NULL,
		    content     TEXT NOT NULL,
		    importance  REAL NOT NULL DEFAULT 0.5,
		    source      TEXT NOT NULL DEFAULT 'chat',
		    tags        TEXT,
		    embedding   TEXT,
		    embedding_blob BLOB,
		    created_at  TEXT NOT NULL,
		    updated_at  TEXT NOT NULL
		);
		CREATE INDEX IF NOT EXISTS idx_memories_importance ON memories(importance DESC, id DESC);
		CREATE INDEX IF NOT EXISTS idx_chat_messages_created ON chat_messages(created_at, id);
		CREATE TABLE IF NOT EXISTS automation_audit (
		    id          INTEGER PRIMARY KEY AUTOINCREMENT,
		    timestamp   TEXT NOT NULL,
		    task_id     TEXT,
		    task_kind   TEXT NOT NULL,
		    event_category TEXT NOT NULL,
		    outcome     TEXT NOT NULL,
		    failure_code TEXT,
		    duration_ms INTEGER
		);
		CREATE INDEX IF NOT EXISTS idx_automation_audit_timestamp ON automation_audit(timestamp DESC, id DESC);
		""";
	private readonly SqliteConnection _connection;
	private readonly string _databasePath;
	private readonly Lock _gate = new();
	private bool _migrationBackupAttempted;

	private NoriDatabase(SqliteConnection connection, string databasePath)
	{
		_connection = connection;
		_databasePath = databasePath;
	}

	/// <summary>
	/// 打开数据库文件。宿主必须注入包内路径；测试可传临时路径。
	/// </summary>
	public static NoriDatabase Open(string? databasePath = null, AppStoragePaths? paths = null)
	{
		if (databasePath is null && paths is null)
			throw new InvalidOperationException("数据库路径必须由宿主显式注入");
		string path = databasePath ?? paths!.DatabasePath;
		bool databaseExisted = File.Exists(path);
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		SqliteConnection connection = new(new SqliteConnectionStringBuilder
		{
			DataSource = path,
			Mode = SqliteOpenMode.ReadWriteCreate,
		}.ToString());
		connection.Open();
		NoriDatabase database = new(connection, path);
		try
		{
			// 外键必须在事务开始前启用, 记忆删除时由 SQLite 清理 Atom/Source/Knowledge 子记录.
			database.Execute(StartupStatement.ForeignKeys);
			// WAL: 写不阻塞读, 拖拽落盘这类高频小写不会让界面卡在 fsync 上.
			database.Execute(StartupStatement.JournalMode);
			// NORMAL 在 WAL 下仍保持崩溃一致性, 避免每次小写入都强制 fsync.
			database.Execute(StartupStatement.SynchronousMode);
			// 由应用在可控的维护点主动 checkpoint, 不让 WAL 无限增长.
			database.Execute(StartupStatement.WalAutoCheckpoint);
			// 多线程争用单连接时等锁而不是立刻抛 "database is locked".
			database.Execute(StartupStatement.BusyTimeout);

			long current = ReadUserVersion(connection);
			if (databaseExisted && current < DatabaseSchemaVersion) database.EnsureMigrationBackup();
			database.Execute(StartupStatement.Schema);
			database.MigrateSchema();
			database.OptimizeAndCheckpoint();
			return database;
		}
		catch
		{
			database.Dispose();
			throw;
		}
	}

	/// <summary>
	/// 在锁内执行一段数据库操作
	/// </summary>
	public T Locked<T>(Func<SqliteConnection, T> action)
	{
		lock (_gate) return action(_connection);
	}

	/// <summary>
	/// 在锁内执行一段无返回值的数据库操作
	/// </summary>
	public void Locked(Action<SqliteConnection> action)
	{
		lock (_gate) action(_connection);
	}

	/// <summary>
	/// 执行不返回结果的 SQL (可含多条语句)
	/// </summary>
	private void Execute(StartupStatement statement) => Locked(connection =>
	{
		using SqliteCommand command = connection.CreateCommand();
		// 启动语句仅来自封闭枚举，不接受外部 SQL。
		command.CommandText = statement switch // nosemgrep
		{
			StartupStatement.ForeignKeys => "PRAGMA foreign_keys=ON;",
			StartupStatement.JournalMode => "PRAGMA journal_mode=WAL;",
			StartupStatement.SynchronousMode => "PRAGMA synchronous=NORMAL;",
			StartupStatement.WalAutoCheckpoint => "PRAGMA wal_autocheckpoint=1000;",
			StartupStatement.BusyTimeout => "PRAGMA busy_timeout=5000;",
			StartupStatement.Schema => Schema,
			_ => throw new ArgumentOutOfRangeException(nameof(statement)),
		};
		command.ExecuteNonQuery();
	});

	/// <summary>
	/// 按 user_version 逐级迁移 memories 数据库结构.
	/// 每一级都在同一个事务中提交; 任何磁盘、锁、语法或损坏错误都会向上传播.
	/// </summary>
	private void MigrateSchema() => Locked(connection =>
	{
		long current = ReadUserVersion(connection);
		if (current > DatabaseSchemaVersion)
		{
			throw new InvalidOperationException($"记忆数据库版本 {current} 高于当前应用支持版本 {DatabaseSchemaVersion}, 请升级应用");
		}
		if (current == DatabaseSchemaVersion)
		{
			MigrateEmbeddingStorageV5(connection, null);
			MigrateRemindersV6(connection, null);
			MigrateAutomationAuditV7(connection, null);
			EnsureOperationalIndexes(connection, null);
			return;
		}

		using SqliteTransaction transaction = connection.BeginTransaction();
		try
		{
			long version = current;
			while (version < DatabaseSchemaVersion)
			{
				switch (version)
				{
					case 0:
						EnsureEmbeddingColumn(connection, transaction);
						break;
					case 1:
						// 保留旧 JSON 向量, 由读取路径按需转换到 BLOB.
						break;
					case 2:
						CreateRemindersTable(connection, transaction);
						break;
					case 3:
						MigrateMemoryEngineV4(connection, transaction);
						break;
					case 4:
						MigrateEmbeddingStorageV5(connection, transaction);
						break;
					case 5:
						MigrateRemindersV6(connection, transaction);
						break;
					case 6:
						MigrateAutomationAuditV7(connection, transaction);
						break;
					default:
						throw new InvalidOperationException($"不支持的记忆数据库版本: {version}");
				}

				version++;
				SetUserVersion(connection, transaction, version);
			}
			EnsureOperationalIndexes(connection, transaction);
			transaction.Commit();
		}
		catch
		{
			try
			{
				transaction.Rollback();
			}
			catch
			{
				// 保留原始迁移异常, 回滚失败不会掩盖真正原因.
			}
			throw;
		}
	});

	/// <summary>读取 SQLite 的独立结构版本</summary>
	private static long ReadUserVersion(SqliteConnection connection)
	{
		using SqliteCommand command = connection.CreateCommand();
		command.CommandText = "PRAGMA user_version;";
		return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
	}

	/// <summary>写入 SQLite 的独立结构版本</summary>
	private static void SetUserVersion(SqliteConnection connection, SqliteTransaction transaction, long version)
	{
		using SqliteCommand command = connection.CreateCommand();
		command.Transaction = transaction;
		// schema 版本仅允许当前代码声明的固定值。
		command.CommandText = version switch // nosemgrep
		{
			1 => "PRAGMA user_version = 1;",
			2 => "PRAGMA user_version = 2;",
			3 => "PRAGMA user_version = 3;",
			4 => "PRAGMA user_version = 4;",
			5 => "PRAGMA user_version = 5;",
			6 => "PRAGMA user_version = 6;",
			7 => "PRAGMA user_version = 7;",
			_ => throw new ArgumentOutOfRangeException(nameof(version)),
		};
		command.ExecuteNonQuery();
	}

	/// <summary>v5: 增加 Float32 BLOB 向量列, 不触碰旧 JSON, 由读取路径惰性迁移。</summary>
	private static void MigrateEmbeddingStorageV5(SqliteConnection connection, SqliteTransaction? transaction)
	{
		if (!HasColumn(connection, transaction, "memories", "embedding_blob"))
		{
			using SqliteCommand alter = connection.CreateCommand();
			alter.Transaction = transaction;
			alter.CommandText = "ALTER TABLE memories ADD COLUMN embedding_blob BLOB;";
			alter.ExecuteNonQuery();
		}
		if (!HasColumn(connection, transaction, "knowledge_chunks", "embedding_blob"))
		{
			using SqliteCommand alter = connection.CreateCommand();
			alter.Transaction = transaction;
			alter.CommandText = "ALTER TABLE knowledge_chunks ADD COLUMN embedding_blob BLOB;";
			alter.ExecuteNonQuery();
		}
		using SqliteCommand state = connection.CreateCommand();
		state.Transaction = transaction;
		state.CommandText = """
			CREATE TABLE IF NOT EXISTS proactive_occurrences (
			    key TEXT PRIMARY KEY,
			    occurrence TEXT NOT NULL,
			    updated_at TEXT NOT NULL
			);
			""";
		state.ExecuteNonQuery();
	}

	/// <summary>幂等补齐聊天、记忆和后台索引使用的索引。</summary>
	private static void EnsureOperationalIndexes(SqliteConnection connection, SqliteTransaction? transaction)
	{
		using SqliteCommand command = connection.CreateCommand();
		command.Transaction = transaction;
		command.CommandText = """
			CREATE TABLE IF NOT EXISTS proactive_occurrences (
			    key TEXT PRIMARY KEY,
			    occurrence TEXT NOT NULL,
			    updated_at TEXT NOT NULL
			);
			CREATE INDEX IF NOT EXISTS idx_chat_messages_created ON chat_messages(created_at, id);
			CREATE INDEX IF NOT EXISTS idx_chat_messages_role_id ON chat_messages(role, id DESC);
			CREATE INDEX IF NOT EXISTS idx_memories_status_importance_id ON memories(status, importance DESC, id DESC);
			CREATE INDEX IF NOT EXISTS idx_memories_embedding_work ON memories(status, embedding_fingerprint, id);
			CREATE INDEX IF NOT EXISTS idx_memories_expiry ON memories(status, expires_at);
			CREATE INDEX IF NOT EXISTS idx_memory_sources_memory_sequence ON memory_sources(memory_id, sequence);
			CREATE INDEX IF NOT EXISTS idx_knowledge_chunks_embedding_work ON knowledge_chunks(document_id, embedding_fingerprint, id);
			CREATE INDEX IF NOT EXISTS idx_reminders_due ON reminders(status, trigger_at ASC, snoozed_until ASC);
			CREATE INDEX IF NOT EXISTS idx_reminders_claimed ON reminders(status, claimed_at ASC);
			""";
		command.ExecuteNonQuery();
	}

	/// <summary>
	/// 生成本次进程内唯一的迁移前一致性备份。
	/// VACUUM INTO 会包含 WAL 中已提交的数据；备份失败时拒绝继续迁移。
	/// </summary>
	public void EnsureMigrationBackup()
	{
		lock (_gate)
		{
			if (_migrationBackupAttempted) return;
			CreateMigrationBackup(_databasePath);
			_migrationBackupAttempted = true;
		}
	}

	private void CreateMigrationBackup(string databasePath)
	{
		FileInfo source = new(databasePath);
		if (!source.Exists || source.Length == 0)
			throw new InvalidOperationException("迁移前备份失败: 数据库文件不存在或为空");
		if (source.Length > MigrationBackupMaxBytes)
			throw new InvalidOperationException($"迁移前备份失败: 数据库超过 {MigrationBackupMaxBytes / 1024 / 1024} MiB 限制");

		string directory = source.DirectoryName ?? ".";
		string temporary = Path.Combine(directory, $"{source.Name}{MigrationBackupMarker}{Guid.NewGuid():N}.tmp");
		string backup = Path.Combine(directory, $"{source.Name}{MigrationBackupMarker}{DateTime.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}.bak");
		try
		{
			using (SqliteCommand command = _connection.CreateCommand())
			{
				command.CommandText = "VACUUM INTO $path";
				command.Parameters.AddWithValue("$path", temporary);
				command.ExecuteNonQuery();
			}

			FileInfo created = new(temporary);
			if (!created.Exists || created.Length == 0 || created.Length > MigrationBackupMaxBytes)
				throw new InvalidOperationException("迁移前备份失败: 备份文件大小无效");

			// 临时文件和最终文件位于同一目录，重命名不会暴露半成品备份。
			File.Move(temporary, backup);
			VerifyMigrationBackup(backup);
			PruneMigrationBackups(directory, source.Name);
		}
		catch (Exception exception)
		{
			TryDelete(temporary);
			TryDelete(backup);
			throw new InvalidOperationException("迁移前备份失败，已中止迁移", exception);
		}
		finally
		{
			TryDelete(temporary);
		}
	}

	private static void VerifyMigrationBackup(string backupPath)
	{
		using SqliteConnection connection = new(new SqliteConnectionStringBuilder
		{
			DataSource = backupPath,
			Mode = SqliteOpenMode.ReadOnly,
		}.ToString());
		connection.Open();
		using SqliteCommand command = connection.CreateCommand();
		command.CommandText = "PRAGMA integrity_check;";
		string result = command.ExecuteScalar()?.ToString() ?? "";
		if (!result.Equals("ok", StringComparison.OrdinalIgnoreCase))
			throw new InvalidOperationException("备份完整性校验失败");
	}

	private static void PruneMigrationBackups(string directory, string databaseName)
	{
		string pattern = $"{databaseName}{MigrationBackupMarker}*.bak";
		string[] paths = Directory.EnumerateFiles(directory, pattern)
			.OrderByDescending(path => File.GetLastWriteTimeUtc(path))
			.ThenByDescending(path => Path.GetFileName(path), StringComparer.Ordinal)
			.ToArray();
		foreach (string path in paths.Skip(MigrationBackupCount)) TryDelete(path);
		if (Directory.EnumerateFiles(directory, pattern).Skip(MigrationBackupCount).Any())
			throw new IOException($"无法将迁移前备份限制在最新 {MigrationBackupCount} 份以内");
	}

	private static void TryDelete(string path)
	{
		try { if (File.Exists(path)) File.Delete(path); }
		catch (IOException) { }
		catch (UnauthorizedAccessException) { }
	}

	/// <summary>在受控维护点运行优化并尝试被动 checkpoint。</summary>
	public void OptimizeAndCheckpoint()
	{
		Locked(connection =>
		{
			using (SqliteCommand optimize = connection.CreateCommand())
			{
				optimize.CommandText = "PRAGMA optimize;";
				optimize.ExecuteNonQuery();
			}
			using (SqliteCommand checkpoint = connection.CreateCommand())
			{
				checkpoint.CommandText = "PRAGMA wal_checkpoint(PASSIVE);";
				checkpoint.ExecuteReader().Dispose();
			}
		});
	}

	/// <summary>
	/// 仅在元数据确认缺列时补 embedding.
	/// 不通过捕获 ALTER 异常判断“列已存在”, 避免吞掉损坏/权限/磁盘错误.
	/// </summary>
	private static void EnsureEmbeddingColumn(SqliteConnection connection, SqliteTransaction transaction)
	{
		bool hasEmbedding = false;
		using (SqliteCommand command = connection.CreateCommand())
		{
			command.Transaction = transaction;
			command.CommandText = "PRAGMA table_info(memories);";
			using SqliteDataReader reader = command.ExecuteReader();
			while (reader.Read())
			{
				if (reader.GetString(1).Equals("embedding", StringComparison.OrdinalIgnoreCase))
				{
					hasEmbedding = true;
					break;
				}
			}
		}

		if (hasEmbedding) return;
		using SqliteCommand alter = connection.CreateCommand();
		alter.Transaction = transaction;
		alter.CommandText = "ALTER TABLE memories ADD COLUMN embedding TEXT;";
		alter.ExecuteNonQuery();
	}

	/// <summary>
	/// v3: 新增可恢复的定时提醒表.
	/// 幂等: 建表语句带 IF NOT EXISTS, 重复执行安全。
	/// </summary>
	private static void CreateRemindersTable(SqliteConnection connection, SqliteTransaction? transaction)
	{
		using SqliteCommand command = connection.CreateCommand();
		command.Transaction = transaction;
		command.CommandText = """
			CREATE TABLE IF NOT EXISTS reminders (
			    id          TEXT PRIMARY KEY,
			    content     TEXT NOT NULL,
			    trigger_at  INTEGER NOT NULL,
			    repeat_daily INTEGER NOT NULL DEFAULT 0,
			    created_at  TEXT NOT NULL
			);
			CREATE INDEX IF NOT EXISTS idx_reminders_trigger ON reminders(trigger_at ASC);
			""";
		command.ExecuteNonQuery();
	}

	/// <summary>
	/// v6: 为提醒增加可恢复领取状态.
	/// 旧列和 repeat_daily 原值保留; 每日重复同时回填为明确的 recurrence JSON.
	/// </summary>
	private static void MigrateRemindersV6(SqliteConnection connection, SqliteTransaction? transaction)
	{
		CreateRemindersTable(connection, transaction);
		string[] columns =
		[
			"status",
			"timezone",
			"recurrence_json",
			"snoozed_until",
			"claimed_at",
			"fired_at",
			"updated_at",
		];
		foreach (string name in columns)
		{
			if (HasColumn(connection, transaction, "reminders", name)) continue;
			using SqliteCommand alter = connection.CreateCommand();
			alter.Transaction = transaction;
			// 列名来自上方固定迁移清单，不接受数据库或用户输入。
			alter.CommandText = name switch // nosemgrep
			{
				"status" => "ALTER TABLE reminders ADD COLUMN status TEXT NOT NULL DEFAULT 'pending';",
				"timezone" => "ALTER TABLE reminders ADD COLUMN timezone TEXT NOT NULL DEFAULT 'UTC';",
				"recurrence_json" => "ALTER TABLE reminders ADD COLUMN recurrence_json TEXT;",
				"snoozed_until" => "ALTER TABLE reminders ADD COLUMN snoozed_until INTEGER;",
				"claimed_at" => "ALTER TABLE reminders ADD COLUMN claimed_at TEXT;",
				"fired_at" => "ALTER TABLE reminders ADD COLUMN fired_at TEXT;",
				"updated_at" => "ALTER TABLE reminders ADD COLUMN updated_at TEXT NOT NULL DEFAULT '';",
				_ => throw new InvalidOperationException("未知提醒迁移列"),
			};
			alter.ExecuteNonQuery();
		}

		using SqliteCommand backfill = connection.CreateCommand();
		backfill.Transaction = transaction;
		backfill.CommandText = """
			UPDATE reminders
			SET status = CASE
					WHEN status IS NULL OR trim(status) = '' THEN 'pending'
					ELSE status
				END,
				timezone = CASE
					WHEN timezone IS NULL OR trim(timezone) = '' THEN 'UTC'
					ELSE timezone
				END,
				recurrence_json = CASE
					WHEN recurrence_json IS NULL AND repeat_daily <> 0 THEN '{"type":"daily"}'
					ELSE recurrence_json
				END,
				updated_at = CASE
					WHEN updated_at IS NULL OR trim(updated_at) = '' THEN created_at
					ELSE updated_at
				END;
			""";
		backfill.ExecuteNonQuery();
	}

	/// <summary>v7: 创建有界脱敏的自动化审计表；绝不保存页面、动作正文或凭据。</summary>
	private static void MigrateAutomationAuditV7(SqliteConnection connection, SqliteTransaction? transaction)
	{
		using SqliteCommand command = connection.CreateCommand();
		command.Transaction = transaction;
		command.CommandText = """
			CREATE TABLE IF NOT EXISTS automation_audit (
			    id          INTEGER PRIMARY KEY AUTOINCREMENT,
			    timestamp   TEXT NOT NULL,
			    task_id     TEXT,
			    task_kind   TEXT NOT NULL,
			    event_category TEXT NOT NULL,
			    outcome     TEXT NOT NULL,
			    failure_code TEXT,
			    duration_ms INTEGER
			);
			CREATE INDEX IF NOT EXISTS idx_automation_audit_timestamp ON automation_audit(timestamp DESC, id DESC);
			""";
		command.ExecuteNonQuery();
	}

	/// <summary>
	/// v4: 建立 Living Memory 聚合、事实原子、来源和知识索引元数据.
	/// 旧记忆不删除: 摘要、状态、来源和 Atom 都在同一事务中回填.
	/// </summary>
	private static void MigrateMemoryEngineV4(SqliteConnection connection, SqliteTransaction transaction)
	{
		string[] columns =
		[
			"kind",
			"canonical_summary",
			"persona_summary",
			"confidence",
			"status",
			"access_count",
			"reinforcement_count",
			"last_accessed_at",
			"last_reinforced_at",
			"ttl_days",
			"expires_at",
			"superseded_by",
			"embedding_fingerprint",
		];
		foreach (string name in columns)
		{
			if (HasColumn(connection, transaction, "memories", name)) continue;
			using SqliteCommand alter = connection.CreateCommand();
			alter.Transaction = transaction;
			// 列名来自上方固定迁移清单，不接受数据库或用户输入。
			alter.CommandText = name switch // nosemgrep
			{
				"kind" => "ALTER TABLE memories ADD COLUMN kind TEXT NOT NULL DEFAULT 'general';",
				"canonical_summary" => "ALTER TABLE memories ADD COLUMN canonical_summary TEXT;",
				"persona_summary" => "ALTER TABLE memories ADD COLUMN persona_summary TEXT;",
				"confidence" => "ALTER TABLE memories ADD COLUMN confidence REAL NOT NULL DEFAULT 0.8;",
				"status" => "ALTER TABLE memories ADD COLUMN status TEXT NOT NULL DEFAULT 'active';",
				"access_count" => "ALTER TABLE memories ADD COLUMN access_count INTEGER NOT NULL DEFAULT 0;",
				"reinforcement_count" => "ALTER TABLE memories ADD COLUMN reinforcement_count INTEGER NOT NULL DEFAULT 0;",
				"last_accessed_at" => "ALTER TABLE memories ADD COLUMN last_accessed_at TEXT;",
				"last_reinforced_at" => "ALTER TABLE memories ADD COLUMN last_reinforced_at TEXT;",
				"ttl_days" => "ALTER TABLE memories ADD COLUMN ttl_days REAL;",
				"expires_at" => "ALTER TABLE memories ADD COLUMN expires_at TEXT;",
				"superseded_by" => "ALTER TABLE memories ADD COLUMN superseded_by INTEGER;",
				"embedding_fingerprint" => "ALTER TABLE memories ADD COLUMN embedding_fingerprint TEXT;",
				_ => throw new InvalidOperationException("未知记忆迁移列"),
			};
			alter.ExecuteNonQuery();
		}

		using (SqliteCommand backfill = connection.CreateCommand())
		{
			backfill.Transaction = transaction;
			backfill.CommandText = """
				UPDATE memories
				SET kind = CASE lower(type)
					WHEN 'fact' THEN 'factual'
					WHEN 'preference' THEN 'preference'
					WHEN 'identity' THEN 'identity'
					WHEN 'relational' THEN 'relational'
					WHEN 'episodic' THEN 'episodic'
					WHEN 'planned' THEN 'planned'
					ELSE 'general'
				END,
				canonical_summary = COALESCE(canonical_summary, content),
				persona_summary = COALESCE(persona_summary, content),
				confidence = COALESCE(confidence, 0.8),
				status = COALESCE(status, 'active'),
				embedding_fingerprint = CASE
					WHEN embedding IS NOT NULL AND embedding <> '' AND (embedding_fingerprint IS NULL OR embedding_fingerprint = '')
					THEN 'legacy-unknown'
					ELSE embedding_fingerprint
				END;
				""";
			backfill.ExecuteNonQuery();
		}

		using (SqliteCommand create = connection.CreateCommand())
		{
			create.Transaction = transaction;
			create.CommandText = """
				CREATE TABLE IF NOT EXISTS memory_atoms (
				    id INTEGER PRIMARY KEY AUTOINCREMENT,
				    parent_memory_id INTEGER NOT NULL,
				    atom_type TEXT NOT NULL,
				    content TEXT NOT NULL,
				    importance REAL NOT NULL DEFAULT 0.5,
				    confidence REAL NOT NULL DEFAULT 0.8,
				    status TEXT NOT NULL DEFAULT 'active',
				    created_at TEXT NOT NULL,
				    last_accessed_at TEXT,
				    last_reinforced_at TEXT,
				    ttl_days REAL,
				    expires_at TEXT,
				    reinforcement_count INTEGER NOT NULL DEFAULT 0,
				    decay_type TEXT NOT NULL DEFAULT 'exponential',
				    entities TEXT,
				    superseded_by INTEGER,
				    FOREIGN KEY(parent_memory_id) REFERENCES memories(id) ON DELETE CASCADE
				);
				CREATE TABLE IF NOT EXISTS memory_sources (
				    id INTEGER PRIMARY KEY AUTOINCREMENT,
				    memory_id INTEGER NOT NULL,
				    role TEXT NOT NULL,
				    content TEXT NOT NULL,
				    message_time TEXT,
				    sequence INTEGER NOT NULL,
				    FOREIGN KEY(memory_id) REFERENCES memories(id) ON DELETE CASCADE
				);
				CREATE TABLE IF NOT EXISTS knowledge_documents (
				    id INTEGER PRIMARY KEY AUTOINCREMENT,
				    path TEXT NOT NULL UNIQUE,
				    content_hash TEXT NOT NULL,
				    updated_at TEXT NOT NULL
				);
				CREATE TABLE IF NOT EXISTS knowledge_chunks (
				    id INTEGER PRIMARY KEY AUTOINCREMENT,
				    document_id INTEGER NOT NULL,
				    chunk_key TEXT NOT NULL,
				    sequence INTEGER NOT NULL DEFAULT 0,
				    heading TEXT,
				    subheading TEXT,
				    content TEXT NOT NULL,
				    knowledge_type TEXT,
				    awareness TEXT,
				    content_hash TEXT NOT NULL,
				    embedding TEXT,
				    embedding_fingerprint TEXT,
				    updated_at TEXT NOT NULL,
				    FOREIGN KEY(document_id) REFERENCES knowledge_documents(id) ON DELETE CASCADE,
				    UNIQUE(document_id, chunk_key)
				);
				CREATE TABLE IF NOT EXISTS memory_engine_state (
				    key TEXT PRIMARY KEY,
				    value TEXT NOT NULL
				);
				CREATE INDEX IF NOT EXISTS idx_memories_status_kind ON memories(status, kind, importance DESC);
				CREATE INDEX IF NOT EXISTS idx_memories_accessed ON memories(last_accessed_at);
				CREATE INDEX IF NOT EXISTS idx_memory_atoms_parent_status ON memory_atoms(parent_memory_id, status);
				CREATE INDEX IF NOT EXISTS idx_memory_atoms_type_status ON memory_atoms(atom_type, status, importance DESC);
				CREATE INDEX IF NOT EXISTS idx_memory_sources_memory ON memory_sources(memory_id, sequence);
				CREATE INDEX IF NOT EXISTS idx_knowledge_chunks_document ON knowledge_chunks(document_id, sequence);
				""";
			create.ExecuteNonQuery();
		}

		using (SqliteCommand atoms = connection.CreateCommand())
		{
			atoms.Transaction = transaction;
			atoms.CommandText = """
				INSERT INTO memory_atoms
					(parent_memory_id, atom_type, content, importance, confidence, status, created_at, ttl_days)
				SELECT m.id, m.kind, COALESCE(m.canonical_summary, m.content), m.importance,
				       m.confidence, m.status, m.created_at, m.ttl_days
				FROM memories AS m
				WHERE NOT EXISTS (
					SELECT 1 FROM memory_atoms AS a WHERE a.parent_memory_id = m.id
				);
				""";
			atoms.ExecuteNonQuery();
		}
	}

	private static bool HasColumn(SqliteConnection connection, SqliteTransaction? transaction, string table, string column)
	{
		using SqliteCommand command = connection.CreateCommand();
		command.Transaction = transaction;
		// 表名来自迁移代码的固定集合。
		command.CommandText = table switch // nosemgrep
		{
			"memories" => "PRAGMA table_info(memories);",
			"reminders" => "PRAGMA table_info(reminders);",
			"knowledge_chunks" => "PRAGMA table_info(knowledge_chunks);",
			_ => throw new ArgumentOutOfRangeException(nameof(table)),
		};
		using SqliteDataReader reader = command.ExecuteReader();
		while (reader.Read())
		{
			if (reader.GetString(1).Equals(column, StringComparison.OrdinalIgnoreCase)) return true;
		}
		return false;
	}

	public void Dispose()
	{
		lock (_gate)
		{
			try
			{
				using SqliteCommand optimize = _connection.CreateCommand();
				optimize.CommandText = "PRAGMA optimize;";
				optimize.ExecuteNonQuery();
				using SqliteCommand checkpoint = _connection.CreateCommand();
				checkpoint.CommandText = "PRAGMA wal_checkpoint(PASSIVE);";
				checkpoint.ExecuteReader().Dispose();
			}
			catch (SqliteException)
			{
				// 关闭阶段的维护失败不应阻止数据库释放。
			}
			_connection.Dispose();
		}
	}
}
