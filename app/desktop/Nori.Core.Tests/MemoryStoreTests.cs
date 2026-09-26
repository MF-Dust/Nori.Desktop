using Nori.Core.Data;
using Nori.Core.Memory;
using Nori.Core.Tests.TestSupport;

namespace Nori.Core.Tests;

/// <summary>
/// 记忆存储库单元测试
/// </summary>
public class MemoryStoreTests : IDisposable
{
	private readonly TempDatabase _tempDatabase = new("nori-memory-test");
	private readonly NoriDatabase _database;
	private readonly MemoryStore _memory;

	public MemoryStoreTests()
	{
		_database = NoriDatabase.Open(_tempDatabase.Path);
		_memory = new MemoryStore(_database);
	}

	public void Dispose()
	{
		_database.Dispose();
		_tempDatabase.Dispose();
		GC.SuppressFinalize(this);
	}

	[Fact]
	public void 添加与查询全部记忆()
	{
		MemoryItem item = _memory.AddAggregate("fact", "主人喜欢吃草莓蛋糕", 0.9, "chat", "food");

		Assert.True(item.Id > 0);
		Assert.Equal("fact", item.Type);
		Assert.Equal("主人喜欢吃草莓蛋糕", item.Content);
		Assert.Equal(0.9, item.Importance);
		Assert.Equal("food", item.Tags);

		IReadOnlyList<MemoryItem> all = _memory.GetAll();
		Assert.Single(all);
		Assert.Equal("主人喜欢吃草莓蛋糕", all[0].Content);
	}

	[Fact]
	public void 搜索记忆()
	{
		_memory.AddAggregate("fact", "主人养了一只猫叫咪咪", 0.8, "chat", "pet");
		_memory.AddAggregate("fact", "明天下午三点有会议", 0.7, "chat", "schedule");

		IReadOnlyList<RetrievalHit> catHits = _memory.SearchKeyword("猫");
		Assert.Single(catHits);
		Assert.Equal("主人养了一只猫叫咪咪", _memory.Get(catHits[0].MemoryId)!.Content);

		IReadOnlyList<RetrievalHit> scheduleHits = _memory.SearchKeyword("会议");
		Assert.Single(scheduleHits);
		Assert.Equal("明天下午三点有会议", _memory.Get(scheduleHits[0].MemoryId)!.Content);

		Assert.Empty(_memory.SearchKeyword("不存在的内容"));
	}

	[Fact]
	public void 更新记忆()
	{
		MemoryItem item = _memory.AddAggregate("fact", "主人住在北京", 0.5, embedding: "[1, 0]");

		Assert.NotEmpty(_memory.SearchSemantic([1, 0], 5, 0));
		bool updated = _memory.Update(item.Id, "主人住在上海", 0.8, "city");
		Assert.True(updated);

		IReadOnlyList<MemoryItem> all = _memory.GetAll();
		Assert.Single(all);
		Assert.Equal("主人住在上海", all[0].Content);
		Assert.Equal(0.8, all[0].Importance);
		Assert.Equal("city", all[0].Tags);
		Assert.Null(all[0].Embedding);
		Assert.Empty(_memory.SearchSemantic([1, 0], 5, 0));
	}

	[Fact]
	public void 更新内容时可在同一次操作提交新向量()
	{
		MemoryItem item = _memory.AddAggregate("fact", "旧内容", embedding: "[1, 0]");

		Assert.True(_memory.Update(item.Id, "新内容", embedding: "[0, 1]"));
		MemoryItem updated = Assert.Single(_memory.GetAll());
		float[] vector = Assert.IsType<float[]>(updated.GetVector());
		Assert.Equal(new float[] {0, 1}, vector);
		Assert.Equal("新内容", Assert.Single(_memory.SearchSemantic([0, 1], 5, 0)).Item.Content);
	}

	[Fact]
	public void 删除与清空记忆()
	{
		MemoryItem item1 = _memory.AddAggregate("fact", "记忆一", 0.5);
		MemoryItem item2 = _memory.AddAggregate("fact", "记忆二", 0.6);

		Assert.Equal(2, _memory.GetAll().Count);

		bool deleted = _memory.Delete(item1.Id);
		Assert.True(deleted);
		Assert.Single(_memory.GetAll());
		Assert.Equal(item2.Id, _memory.GetAll()[0].Id);

		_memory.Clear();
		Assert.Empty(_memory.GetAll());
	}

	[Fact]
	public void 向量余弦相似度计算()
	{
		float[] v1 = [1.0f, 0.0f, 0.0f];
		float[] v2 = [1.0f, 0.0f, 0.0f];
		float[] v3 = [0.0f, 1.0f, 0.0f];

		double simSame = MemoryStore.CosineSimilarity(v1, v2);
		double simOrthogonal = MemoryStore.CosineSimilarity(v1, v3);

		Assert.True(Math.Abs(simSame - 1.0) < 0.0001);
		Assert.True(Math.Abs(simOrthogonal - 0.0) < 0.0001);
	}

	[Fact]
	public void 向量语义检索()
	{
		// 模拟 3 维向量
		_memory.AddAggregate("fact", "主人喜欢喝拿铁", 0.9, "chat", "drink", "[0.9, 0.1, 0.0]");
		_memory.AddAggregate("fact", "主人养了一只柯基", 0.8, "chat", "pet", "[0.0, 0.1, 0.9]");

		float[] queryDrink = [0.85f, 0.15f, 0.0f];

		IReadOnlyList<MemorySearchResult> results = _memory.SearchSemantic(queryDrink, 5);
		Assert.NotEmpty(results);
		Assert.Equal("主人喜欢喝拿铁", results[0].Item.Content);
		Assert.True(results[0].Similarity > 0.8);
	}

	[Fact]
	public void 语义候选集有上限()
	{
		MemoryStore bounded = new(_database, semanticCandidateLimit: 2);
		bounded.AddAggregate("fact", "高重要度一", 1.0, embedding: "[1, 0, 0]");
		bounded.AddAggregate("fact", "高重要度二", 0.9, embedding: "[0, 1, 0]");
		bounded.AddAggregate("fact", "低重要度目标", 0.1, embedding: "[0, 0, 1]");

		Assert.Empty(bounded.SearchSemantic([0, 0, 1], 5, 0.9));
	}

	[Fact]
	public void 待嵌入记忆支持id游标分页()
	{
		_memory.AddAggregate("fact", "已有向量", embedding: "[1]");
		MemoryItem pending1 = _memory.AddAggregate("fact", "待处理一");
		MemoryItem pending2 = _memory.AddAggregate("fact", "待处理二");

		IReadOnlyList<MemoryItem> first = _memory.GetUnembedded(1);
		Assert.Single(first);
		Assert.Equal(pending1.Id, first[0].Id);
		IReadOnlyList<MemoryItem> second = _memory.GetUnembedded(1, first[0].Id);
		Assert.Equal(pending2.Id, Assert.Single(second).Id);
	}

	[Fact]
	public void 旧数据库打开时会补列并清空历史向量()
	{
		string oldPath = Path.Combine(Path.GetTempPath(), $"nori-memory-old-{Guid.NewGuid():N}.db");
		try
		{
			using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={oldPath}"))
			{
				connection.Open();
				using var command = connection.CreateCommand();
				command.CommandText = """
					CREATE TABLE memories (id INTEGER PRIMARY KEY AUTOINCREMENT, type TEXT NOT NULL, content TEXT NOT NULL, importance REAL NOT NULL, source TEXT NOT NULL, tags TEXT, created_at TEXT NOT NULL, updated_at TEXT NOT NULL);
					INSERT INTO memories (type, content, importance, source, tags, created_at, updated_at) VALUES ('fact', '旧记录', 0.5, 'chat', NULL, 'a', 'a');
					""";
				command.ExecuteNonQuery();
			}

			using Nori.Core.Data.NoriDatabase migrated = Nori.Core.Data.NoriDatabase.Open(oldPath);
			MemoryStore store = new(migrated);
			MemoryItem item = Assert.Single(store.GetAll());
			Assert.Null(item.Embedding);
			Assert.Equal(Nori.Core.Data.NoriDatabase.DatabaseSchemaVersion, migrated.Locked(connection =>
			{
				using var command = connection.CreateCommand();
				command.CommandText = "PRAGMA user_version";
				return Convert.ToInt64(command.ExecuteScalar());
			}));
		}
		finally
		{
			try
			{
				File.Delete(oldPath);
				File.Delete($"{oldPath}-wal");
				File.Delete($"{oldPath}-shm");
			}
			catch (IOException)
			{
			}
		}
	}
}

public class EmbeddingAdapterTests
{
	[Fact]
	public async Task OpenAiEmbeddingAdapter_解析BgeM3向量响应()
	{
		using HttpTestHandler handler = new(req =>
		{
			Assert.Equal(HttpMethod.Post, req.Method);
			Assert.Equal("https://api.siliconflow.cn/v1/embeddings", req.RequestUri?.ToString());

			string json = """
				{
				  "data": [
				    {
				      "index": 0,
				      "embedding": [0.123, -0.456, 0.789]
				    }
				  ]
				}
				""";

			return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
			{
				Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
			};
		});

		using HttpClient client = new(handler);
		Nori.Core.Embedding.OpenAiEmbeddingAdapter adapter = new(client);

		float[] vec = await adapter.GetEmbeddingAsync(
			"https://api.siliconflow.cn/v1",
			"sk-test",
			"BAAI/bge-m3",
			"测试输入文本");

		Assert.Equal(3, vec.Length);
		Assert.Equal(0.123f, vec[0]);
		Assert.Equal(-0.456f, vec[1]);
		Assert.Equal(0.789f, vec[2]);
	}

	[Fact]
	public async Task OpenAiEmbeddingAdapter_指定维数时请求体携带dimensions()
	{
		using HttpTestHandler handler = new(req =>
		{
			string json = """
				{
				  "data": [
				    {"index": 0, "embedding": [0.1, 0.2]}
				  ]
				}
				""";

			return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
			{
				Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
			};
		});

		using HttpClient client = new(handler);
		Nori.Core.Embedding.OpenAiEmbeddingAdapter adapter = new(client);

		await adapter.GetEmbeddingAsync(
			"https://api.openai.com/v1",
			"sk-test",
			"text-embedding-3-small",
			"测试输入文本",
			dimensions: 512);

		Assert.NotNull(handler.LastBody);
		Assert.Contains("\"dimensions\":512", handler.LastBody);

		// 不指定维数时请求体不应携带该字段, 避免不支持的端点报错
		await adapter.GetEmbeddingAsync(
			"https://api.openai.com/v1",
			"sk-test",
			"text-embedding-3-small",
			"测试输入文本");

		Assert.NotNull(handler.LastBody);
		Assert.DoesNotContain("dimensions", handler.LastBody);
	}
}
