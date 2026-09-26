using System.Diagnostics.CodeAnalysis;
using Nori.Core.Configuration;
using Nori.Core.Data;
using Nori.Core.Embedding;
using Nori.Core.Memory;
using Nori.Core.Tests.TestSupport;

namespace Nori.Core.Tests;

/// <summary>M0: 锁定旧 MemoryStore/MemoryService 行为，避免 v4 重构回归。</summary>
[SuppressMessage("Security", "S5332", Justification = "HTTP 地址是内存中的模拟端点，不会发起网络请求。")]
public sealed class MemoryEngineBaselineTests : IDisposable
{
	private readonly TempDatabase _tempDatabase = new("nori-memory-baseline");
	private readonly NoriDatabase _database;
	private readonly ConfigStore _config;

	public MemoryEngineBaselineTests()
	{
		_database = NoriDatabase.Open(_tempDatabase.Path);
		_config = new ConfigStore(_database);
		_config.InitDefaults("test");
		_config.Set("embedding_api_base", new ConfigValue.Text("http://embedding.test/v1"));
	}

	[Fact]
	public void v3数据迁移到v4_保留向量并回填Atom()
	{
		string legacy = Path.Combine(Path.GetTempPath(), $"nori-memory-v3-{Guid.NewGuid():N}.db");
		try
		{
			using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={legacy}"))
			{
				connection.Open();
				using var command = connection.CreateCommand();
				command.CommandText = """
					CREATE TABLE memories (id INTEGER PRIMARY KEY AUTOINCREMENT, type TEXT NOT NULL, content TEXT NOT NULL, importance REAL NOT NULL, source TEXT NOT NULL, tags TEXT, embedding TEXT, created_at TEXT NOT NULL, updated_at TEXT NOT NULL);
					PRAGMA user_version = 3;
					INSERT INTO memories(type, content, importance, source, embedding, created_at, updated_at) VALUES ('fact', '旧向量记忆', 0.8, 'chat', '[1,0]', '2026-01-01', '2026-01-01');
					""";
				command.ExecuteNonQuery();
			}

			using NoriDatabase migrated = NoriDatabase.Open(legacy);
			MemoryStore store = new(migrated);
			MemoryItem item = Assert.Single(store.GetAll());
			Assert.Equal("factual", item.Kind);
			Assert.Equal("legacy-unknown", item.EmbeddingFingerprint);
			Assert.Single(store.GetAtoms(item.Id));
		}
		finally
		{
			try { File.Delete(legacy); File.Delete($"{legacy}-wal"); File.Delete($"{legacy}-shm"); }
			catch (IOException) { }
		}
	}

	[Fact]
	public async Task MemoryService_AddAndSearch_兼容旧接口并创建Atom()
	{
		MemoryService service = new(new MemoryStore(_database), new StubEmbedding(), _config);
		MemoryItem item = await service.AddAsync("主人喜欢 RPG", "fact", 0.9, "游戏", "agent");

		Assert.Equal("fact", item.Type);
		Assert.Equal("factual", item.Kind);
		Assert.Single(service.GetAtoms(item.Id));
		Assert.Contains(await service.SearchHybridAsync("RPG"), result => result.Id == item.Id);
	}

	[Fact]
	public async Task Embedding失败时关键词仍可召回()
	{
		MemoryStore store = new(_database);
		store.AddAggregate("fact", "主人喜欢草莓蛋糕", 0.8, tags: "food");
		MemoryService service = new(store, new FailingEmbedding(), _config);

		IReadOnlyList<MemoryItem> results = await service.SearchHybridAsync("草莓");
		Assert.Single(results);
		Assert.Equal("主人喜欢草莓蛋糕", results[0].Content);
	}

	[Fact]
	public async Task ReembedAll_按id游标补齐向量()
	{
		MemoryStore store = new(_database);
		store.AddAggregate("fact", "第一条");
		store.AddAggregate("fact", "第二条");
		MemoryService service = new(store, new StubEmbedding(), _config);

		Assert.Equal(2, await service.ReembedAllAsync());
		Assert.All(store.GetAll(), item => Assert.NotNull(item.Embedding));
	}

	[Fact]
	public async Task Embed取消会向上传播()
	{
		MemoryService service = new(new MemoryStore(_database), new BlockingEmbedding(), _config);
		using CancellationTokenSource cancellation = new();
		cancellation.Cancel();
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.EmbedAsync("测试", cancellation.Token));
	}

	public void Dispose()
	{
		_database.Dispose();
		_tempDatabase.Dispose();
		GC.SuppressFinalize(this);
	}

	private sealed class StubEmbedding : IEmbeddingAdapter
	{
		public Task<float[]> GetEmbeddingAsync(string baseUrl, string apiKey, string model, string input, int? dimensions = null, CancellationToken cancellationToken = default) => Task.FromResult<float[]>([1, 0]);
		public Task<IReadOnlyList<float[]>> GetEmbeddingsAsync(string baseUrl, string apiKey, string model, IReadOnlyList<string> inputs, int? dimensions = null, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<float[]>>(inputs.Select(_ => new float[] {1, 0}).ToList());
	}

	private sealed class FailingEmbedding : IEmbeddingAdapter
	{
		public Task<float[]> GetEmbeddingAsync(string baseUrl, string apiKey, string model, string input, int? dimensions = null, CancellationToken cancellationToken = default) => throw new InvalidOperationException("offline");
		public Task<IReadOnlyList<float[]>> GetEmbeddingsAsync(string baseUrl, string apiKey, string model, IReadOnlyList<string> inputs, int? dimensions = null, CancellationToken cancellationToken = default) => throw new InvalidOperationException("offline");
	}

	private sealed class BlockingEmbedding : IEmbeddingAdapter
	{
		public Task<float[]> GetEmbeddingAsync(string baseUrl, string apiKey, string model, string input, int? dimensions = null, CancellationToken cancellationToken = default) => Task.FromCanceled<float[]>(cancellationToken);
		public Task<IReadOnlyList<float[]>> GetEmbeddingsAsync(string baseUrl, string apiKey, string model, IReadOnlyList<string> inputs, int? dimensions = null, CancellationToken cancellationToken = default) => Task.FromCanceled<IReadOnlyList<float[]>>(cancellationToken);
	}
}
