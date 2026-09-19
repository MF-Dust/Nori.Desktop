using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Data.Sqlite;
using Nori.Core.Configuration;
using Nori.Core.Data;
using Nori.Core.Embedding;
using Nori.Core.Memory;
using Nori.Core.Proactive;
using Nori.Core.Logging;

namespace Nori.Core.Tests;

[SuppressMessage("Security", "S5332", Justification = "HTTP 地址是内存中的模拟端点，不会发起网络请求。")]
public sealed class ReliabilitySliceTests
{
	[Fact]
	public void Float32向量往返并惰性迁移旧JSON()
	{
		string path = TempPath("vector");
		try
		{
			using NoriDatabase database = NoriDatabase.Open(path);
			MemoryStore store = new(database);
			MemoryItem item = store.AddAggregate("fact", "BLOB 向量", embedding: "[1.0,-2.5,0.25]");

			(string type, string? legacy) = database.Locked(connection =>
			{
				using SqliteCommand command = connection.CreateCommand();
				command.CommandText = "SELECT typeof(embedding_blob), embedding FROM memories WHERE id = $id";
				command.Parameters.AddWithValue("$id", item.Id);
				using SqliteDataReader reader = command.ExecuteReader();
				Assert.True(reader.Read());
				return (reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1));
			});
			Assert.Equal("blob", type);
			Assert.Null(legacy);
			float[] storedVector = Assert.IsType<float[]>(store.Get(item.Id)!.GetVector());
			Assert.Equal([1.0f, -2.5f, 0.25f], storedVector);

			database.Locked(connection =>
			{
				using SqliteCommand command = connection.CreateCommand();
				command.CommandText = "UPDATE memories SET embedding = '[0,1,0]', embedding_blob = NULL WHERE id = $id";
				command.Parameters.AddWithValue("$id", item.Id);
				command.ExecuteNonQuery();
			});
			Assert.Equal(item.Id, Assert.Single(store.SearchSemantic([0, 1, 0], 1, 0)).Item.Id);
			(string? migratedLegacy, string migratedType) = database.Locked(connection =>
			{
				using SqliteCommand command = connection.CreateCommand();
				command.CommandText = "SELECT embedding, typeof(embedding_blob) FROM memories WHERE id = $id";
				command.Parameters.AddWithValue("$id", item.Id);
				using SqliteDataReader reader = command.ExecuteReader();
				Assert.True(reader.Read());
				return (reader.IsDBNull(0) ? null : reader.GetString(0), reader.GetString(1));
			});
			Assert.Null(migratedLegacy);
			Assert.Equal("blob", migratedType);
		}
		finally
		{
			DeleteDatabaseFiles(path);
		}
	}

	[Fact]
	public void 大量候选只返回稳定的TopK结果()
	{
		string path = TempPath("ranking");
		try
		{
			using NoriDatabase database = NoriDatabase.Open(path);
			MemoryStore store = new(database, semanticCandidateLimit: 1200);
			database.Locked(connection =>
			{
				using SqliteTransaction transaction = connection.BeginTransaction();
				for (int index = 0; index < 1200; index++)
				{
					using SqliteCommand command = connection.CreateCommand();
					command.Transaction = transaction;
					command.CommandText = "INSERT INTO memories(type, content, importance, source, embedding_blob, created_at, updated_at, kind, canonical_summary, persona_summary, confidence, status) VALUES ('fact', $content, $importance, 'test', $embedding, $created, $created, 'factual', $content, $content, 0.8, 'active')";
					command.Parameters.AddWithValue("$content", $"候选 {index}");
					command.Parameters.AddWithValue("$importance", index == 1199 ? 1.0 : 0.5);
					command.Parameters.AddWithValue("$embedding", EmbeddingVectorCodec.Encode(index == 1199 ? [1, 0, 0] : [0, 1, 0]));
					command.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToString("o"));
					command.ExecuteNonQuery();
				}
				transaction.Commit();
			});

			IReadOnlyList<MemorySearchResult> results = store.SearchSemantic([1, 0, 0], 5, 0.1);
			MemorySearchResult result = Assert.Single(results);
			Assert.Equal("候选 1199", result.Item.Content);
			Assert.Equal(1, result.Similarity, precision: 4);
		}
		finally
		{
			DeleteDatabaseFiles(path);
		}
	}

	[Fact]
	public async Task 失败的Embedding不阻塞文本保存()
	{
		string path = TempPath("degrade");
		try
		{
			using NoriDatabase database = NoriDatabase.Open(path);
			ConfigStore config = new(database);
			config.InitDefaults("test");
			config.Set("embedding_api_base", new ConfigValue.Text("http://embedding.test/v1"));
			await using MemoryService service = new(new MemoryStore(database), new FailingEmbedding(), config);

			MemoryItem item = await service.AddAsync("即使向量服务离线也要保存这段文本");
			Assert.Equal(item.Content, service.Get(item.Id)!.Content);
			Assert.Null(service.Get(item.Id)!.GetVector());
		}
		finally
		{
			DeleteDatabaseFiles(path);
		}
	}

	[Fact]
	public async Task 重嵌入按批次并可取消后按fingerprint恢复()
	{
		string path = TempPath("reembed");
		try
		{
			using NoriDatabase database = NoriDatabase.Open(path);
			ConfigStore config = new(database);
			config.InitDefaults("test");
			config.Set("embedding_api_base", new ConfigValue.Text("http://embedding.test/v1"));
			MemoryStore store = new(database);
			store.AddAggregate("fact", "批次一");
			store.AddAggregate("fact", "批次二");
			store.AddAggregate("fact", "批次三");
			ControlledBatchEmbedding embedding = new();
			await using MemoryService service = new(store, embedding, config);
			using CancellationTokenSource cancellation = new();
			Task<int> first = service.ReembedAllAsync(cancellation.Token, true);
			await embedding.FirstBatchStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
			cancellation.Cancel();
			await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
			embedding.ReleaseFirstBatch();

			int count = await service.ReembedAllAsync(force: false);
			Assert.Equal(3, count);
			Assert.All(store.GetAll(10), item => Assert.NotNull(item.GetVector()));
			Assert.True(embedding.BatchCalls >= 2);
		}
		finally
		{
			DeleteDatabaseFiles(path);
		}
	}

	private static string TempPath(string name) => Path.Combine(Path.GetTempPath(), $"nori-reliability-{name}-{Guid.NewGuid():N}.db");

	private static void DeleteDatabaseFiles(string path)
	{
		try
		{
			File.Delete(path);
			File.Delete($"{path}-wal");
			File.Delete($"{path}-shm");
			foreach (string backup in Directory.GetFiles(Path.GetDirectoryName(path)!, $"{Path.GetFileName(path)}.pre-migration-*.bak")) File.Delete(backup);
		}
		catch (IOException) { }
	}

	private sealed class FailingEmbedding : IEmbeddingAdapter
	{
		public Task<float[]> GetEmbeddingAsync(string baseUrl, string apiKey, string model, string input, int? dimensions = null, CancellationToken cancellationToken = default) => throw new InvalidOperationException("offline");
		public Task<IReadOnlyList<float[]>> GetEmbeddingsAsync(string baseUrl, string apiKey, string model, IReadOnlyList<string> inputs, int? dimensions = null, CancellationToken cancellationToken = default) => throw new InvalidOperationException("offline");
	}

	private sealed class ControlledBatchEmbedding : IEmbeddingAdapter
	{
		private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public TaskCompletionSource FirstBatchStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public int BatchCalls => Volatile.Read(ref _batchCalls);
		private int _batchCalls;

		public Task<float[]> GetEmbeddingAsync(string baseUrl, string apiKey, string model, string input, int? dimensions = null, CancellationToken cancellationToken = default) => Task.FromResult<float[]>([1, 0]);

		public async Task<IReadOnlyList<float[]>> GetEmbeddingsAsync(string baseUrl, string apiKey, string model, IReadOnlyList<string> inputs, int? dimensions = null, CancellationToken cancellationToken = default)
		{
			int call = Interlocked.Increment(ref _batchCalls);
			if (call == 1)
			{
				FirstBatchStarted.SetResult();
				await _release.Task.WaitAsync(cancellationToken);
			}
			return inputs.Select(_ => new float[] {1, 0}).ToList();
		}

		public void ReleaseFirstBatch() => _release.TrySetResult();
	}
}

public sealed class DatabaseMigrationReliabilityTests
{
	[Fact]
	public void 迁移原子幂等并生成有界备份()
	{
		string path = Path.Combine(Path.GetTempPath(), $"nori-migration-{Guid.NewGuid():N}.db");
		try
		{
			using (SqliteConnection connection = new($"Data Source={path}"))
			{
				connection.Open();
				using SqliteCommand command = connection.CreateCommand();
				command.CommandText = "CREATE TABLE memories (id INTEGER PRIMARY KEY AUTOINCREMENT, type TEXT NOT NULL, content TEXT NOT NULL, importance REAL NOT NULL, source TEXT NOT NULL, tags TEXT, embedding TEXT, created_at TEXT NOT NULL, updated_at TEXT NOT NULL); PRAGMA user_version = 3; INSERT INTO memories(type, content, importance, source, embedding, created_at, updated_at) VALUES ('fact', '保留旧向量', 0.5, 'test', '[1,0]', 'a', 'a');";
				command.ExecuteNonQuery();
			}

			using (NoriDatabase database = NoriDatabase.Open(path))
			{
				long userVersion = database.Locked(connection =>
				{
					using SqliteCommand command = connection.CreateCommand();
					command.CommandText = "PRAGMA user_version";
					return Convert.ToInt64(command.ExecuteScalar());
				});
				Assert.Equal(NoriDatabase.DatabaseSchemaVersion, userVersion);
				// v5 结构迁移只补列, 旧 JSON 在首次语义检索时惰性转成 BLOB。
				string embeddingType = database.Locked(connection =>
				{
					using SqliteCommand command = connection.CreateCommand();
					command.CommandText = "SELECT typeof(embedding_blob) FROM memories LIMIT 1";
					return command.ExecuteScalar()?.ToString() ?? "";
				});
				Assert.Equal("null", embeddingType);

				MemoryStore store = new(database);
				MemorySearchResult match = Assert.Single(store.SearchSemantic([1, 0], 1, 0));
				Assert.Equal("保留旧向量", match.Item.Content);
				Assert.Equal("blob", database.Locked(connection =>
				{
					using SqliteCommand command = connection.CreateCommand();
					command.CommandText = "SELECT typeof(embedding_blob) FROM memories LIMIT 1";
					return Assert.IsType<string>(command.ExecuteScalar());
				}));
			}
			string[] backups = Directory.GetFiles(Path.GetDirectoryName(path)!, $"{Path.GetFileName(path)}.pre-migration-*.bak");
			Assert.Single(backups);
			Assert.InRange(new FileInfo(backups[0]).Length, 1, 64L * 1024 * 1024);

			using (NoriDatabase database = NoriDatabase.Open(path))
			{
				database.OptimizeAndCheckpoint();
			}
			Assert.Single(Directory.GetFiles(Path.GetDirectoryName(path)!, $"{Path.GetFileName(path)}.pre-migration-*.bak"));
		}
		finally
		{
			try
			{
				File.Delete(path);
				File.Delete($"{path}-wal");
				File.Delete($"{path}-shm");
				foreach (string backup in Directory.GetFiles(Path.GetDirectoryName(path)!, $"{Path.GetFileName(path)}.pre-migration-*.bak")) File.Delete(backup);
			}
			catch (IOException) { }
		}
	}
}

public sealed class ProactiveReliabilityTests
{
	[Fact]
	public void 挂机session只触发一次并在活动后重置()
	{
		string path = Path.Combine(Path.GetTempPath(), $"nori-proactive-{Guid.NewGuid():N}.db");
		string logPath = Path.Combine(Path.GetTempPath(), $"nori-proactive-log-{Guid.NewGuid():N}");
		try
		{
			using NoriDatabase database = NoriDatabase.Open(path);
			ConfigStore config = new(database);
			config.InitDefaults("test");
			config.Set("proactive_daily_greeting", new ConfigValue.Boolean(false));
			config.Set("proactive_idle_minutes", new ConfigValue.Integer(1));
			List<ProactiveMessage> messages = [];
			double? idle = 61;
			using ProactiveScheduler scheduler = new(new ReminderStore(database), config, new FileLogger(logPath), () => idle);
			scheduler.Message += messages.Add;

			scheduler.TickForTests(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
			scheduler.TickForTests(new DateTime(2026, 1, 1, 0, 0, 1, DateTimeKind.Utc));
			Assert.Single(messages);
			idle = 0;
			scheduler.TickForTests(new DateTime(2026, 1, 1, 0, 1, 0, DateTimeKind.Utc));
			idle = 61;
			scheduler.TickForTests(new DateTime(2026, 1, 1, 0, 2, 0, DateTimeKind.Utc));
			Assert.Equal(2, messages.Count);
		}
		finally
		{
			DeleteDirectory(logPath);
			DeleteDatabase(path);
		}
	}

	[Fact]
	public void 问候跨重启去重并按语言显示且日志不含提醒内容()
	{
		string path = Path.Combine(Path.GetTempPath(), $"nori-proactive-greeting-{Guid.NewGuid():N}.db");
		string logPath = Path.Combine(Path.GetTempPath(), $"nori-proactive-greeting-log-{Guid.NewGuid():N}");
		const string reminderContent = "只应出现在消息里的私密提醒";
		try
		{
			using NoriDatabase database = NoriDatabase.Open(path);
			ConfigStore config = new(database);
			config.InitDefaults("test");
			config.Set("language", new ConfigValue.Text("en-US"));
			config.Set("proactive_idle_enabled", new ConfigValue.Boolean(false));
			config.Set("proactive_daily_greeting", new ConfigValue.Boolean(true));
			FileLogger logger = new(logPath);
			DateTime localDate = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TimeZoneInfo.Local).Date;
			DateTime localMorning = localDate.AddHours(8).AddMinutes(30);
			DateTime utcMorning = TimeZoneInfo.ConvertTimeToUtc(localMorning, TimeZoneInfo.Local);
			DateTimeOffset now = new(utcMorning, TimeSpan.Zero);
			List<ProactiveMessage> firstMessages = [];
			using (ProactiveScheduler first = new(new ReminderStore(database), config, logger, () => null))
			{
				first.Message += firstMessages.Add;
				first.TickForTests(now.UtcDateTime);
			}
			List<ProactiveMessage> secondMessages = [];
			using (ProactiveScheduler second = new(new ReminderStore(database), config, logger, () => null))
			{
				second.Message += secondMessages.Add;
				second.TickForTests(now.UtcDateTime);
			}
			Assert.Single(firstMessages);
			Assert.Contains("Good morning", firstMessages[0].Text, StringComparison.Ordinal);
			Assert.Empty(secondMessages);

			ReminderStore reminders = new(database);
			reminders.Add(reminderContent, now.ToUnixTimeMilliseconds() - 1);
			List<ProactiveMessage> reminderMessages = [];
			using (ProactiveScheduler scheduler = new(new ReminderStore(database), config, logger, () => null))
			{
				scheduler.Message += reminderMessages.Add;
				scheduler.TickForTests(now.UtcDateTime);
			}
			Assert.Single(reminderMessages);
			Assert.Contains("Reminder time", reminderMessages[0].Text, StringComparison.Ordinal);
			Assert.DoesNotContain(logger.RecentLogs(), entry => entry.Message.Contains(reminderContent, StringComparison.Ordinal));
		}
		finally
		{
			DeleteDirectory(logPath);
			DeleteDatabase(path);
		}
	}

	private static void DeleteDatabase(string path)
	{
		try { File.Delete(path); File.Delete($"{path}-wal"); File.Delete($"{path}-shm"); }
		catch (IOException) { }
	}

	private static void DeleteDirectory(string path)
	{
		try { if (Directory.Exists(path)) Directory.Delete(path, true); }
		catch (IOException) { }
	}
}
