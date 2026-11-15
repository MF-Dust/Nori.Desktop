using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Nori.Core.Data;
using Nori.Core.Embedding;
using Nori.Core.Memory;
using Nori.Core.Tests.TestSupport;

namespace Nori.Core.Tests;

/// <summary>记忆时间线的日期、筛选、分页和详情。不覆盖召回检索。</summary>
public class MemoryTimelineTests : IDisposable
{
	private static readonly TimeZoneInfo Zone = TimeZoneInfo.CreateCustomTimeZone("Timeline+8", TimeSpan.FromHours(8), "Timeline+8", "Timeline+8");
	private readonly TempDatabase _temp = new("nori-timeline");
	private readonly NoriDatabase _database;
	private readonly MemoryStore _store;

	public MemoryTimelineTests()
	{
		_database = NoriDatabase.Open(_temp.Path);
		_store = new MemoryStore(_database);
	}

	public void Dispose()
	{
		_database.Dispose();
		_temp.Dispose();
		GC.SuppressFinalize(this);
	}

	[Fact]
	public void 缺失时间和日期边界不推测()
	{
		Assert.False(MemoryTimelineTime.Classify(null, Zone).Known);
		Assert.False(MemoryTimelineTime.Classify("   ", Zone).Known);
		Assert.False(MemoryTimelineTime.Classify("not-a-date", Zone).Known);
		Assert.False(MemoryTimelineTime.Classify("2024-03-10T16:00:00", Zone).Known);
		Assert.False(MemoryTimelineTime.Classify("2024-03-10 16:00:00", Zone).Known);

		MemoryCreatedStamp dateOnly = MemoryTimelineTime.Classify("2024-03-10", Zone);
		Assert.True(dateOnly.Known);
		Assert.False(dateOnly.HasTime);
		Assert.Equal(new DateOnly(2024, 3, 10), dateOnly.Date);

		MemoryCreatedStamp beforeMidnight = MemoryTimelineTime.Classify("2024-03-10T15:59:59Z", Zone);
		MemoryCreatedStamp atMidnight = MemoryTimelineTime.Classify("2024-03-10T16:00:00+00:00", Zone);
		Assert.Equal(new DateOnly(2024, 3, 10), beforeMidnight.Date);
		Assert.Equal(new DateOnly(2024, 3, 11), atMidnight.Date);
		Assert.True(beforeMidnight.HasTime);
		Assert.True(MemoryTimelineTime.InRange(atMidnight, new DateOnly(2024, 3, 11), new DateOnly(2024, 3, 11)));
		Assert.False(MemoryTimelineTime.InRange(beforeMidnight, new DateOnly(2024, 3, 11), new DateOnly(2024, 3, 11)));
		Assert.True(MemoryTimelineTime.InRange(dateOnly, new DateOnly(2024, 3, 10), new DateOnly(2024, 3, 10)));
		Assert.False(MemoryTimelineTime.InRange(MemoryTimelineTime.Classify("nope", Zone), new DateOnly(2024, 3, 10), new DateOnly(2024, 3, 11)));
		Assert.True(MemoryTimelineTime.InRange(MemoryTimelineTime.Classify("nope", Zone), null, null));
	}

	[Fact]
	public void 筛选组合分页与排序跟真实创建时间一致()
	{
		List<Seed> seeds =
		[
			AddSeed("偏好蛋糕", "preference", "active", "2024-06-02T02:00:00Z", "cake-summary", "food-tag", "persona-cake"),
			AddSeed("同一时刻较新", "preference", "active", "2024-06-02T02:00:00Z", null, null, null),
			AddSeed("午夜之后", "factual", "active", "2024-06-01T16:00:00Z", null, null, null),
			AddSeed("午夜之前", "factual", "dormant", "2024-06-01T15:59:59Z", null, null, null),
			AddSeed("只有日期", "general", "expired", "2024-06-01", null, null, null),
			AddSeed("已归档蛋糕", "preference", "archived", "2024-05-01T01:00:00Z", null, "archive-tag", null),
			AddSeed("文件_名", "planned", "active", "2024-04-01T01:00:00+08:00", null, null, null),
			AddSeed("百分号100%完成", "identity", "active", "2024-04-02T01:00:00+08:00", null, null, null),
			AddSeed("未知时间但更新很新", "general", "active", "not-a-date", null, null, null),
			AddSeed("空白时间", "relational", "active", "", null, null, null),
		];
		Stamp(seeds[8], updatedAt: "2099-01-01T00:00:00Z");
		Stamp(seeds[9], updatedAt: "2099-01-01T00:00:00Z");

		string?[] kinds = [null, "preference", "fact"];
		string?[] keywords = [null, "cake", "TAG", "_", "%", "百分号"];
		(DateOnly? From, DateOnly? To)[] ranges =
		[
			(null, null),
			(new DateOnly(2024, 6, 2), new DateOnly(2024, 6, 2)),
			(new DateOnly(2024, 6, 1), new DateOnly(2024, 6, 2)),
			(new DateOnly(2024, 4, 1), null),
		];
		MemoryTimelineActivity[] activities = [MemoryTimelineActivity.All, MemoryTimelineActivity.Active, MemoryTimelineActivity.Archived];
		foreach (MemoryTimelineActivity activity in activities)
		foreach (string? kind in kinds)
		foreach (string? keyword in keywords)
		foreach ((DateOnly? from, DateOnly? to) in ranges)
		{
			var query = new MemoryTimelineQuery { Activity = activity, Kind = kind, Keyword = keyword, From = from, To = to, Limit = 3 };
			List<Seed> expected = Expected(seeds, query);
			List<long> actual = [];
			int offset = 0;
			MemoryTimelinePage? first = null;
			do
			{
				MemoryTimelinePage page = _store.QueryTimeline(query with { Offset = offset }, Zone);
				first ??= page;
				Assert.Equal(expected.Count, page.Total);
				Assert.Equal(3, page.Limit);
				actual.AddRange(page.Items.Select(item => item.Item.Id));
				Assert.True(page.Items.Count <= 3);
				offset += page.Items.Count;
				if (page.Items.Count == 0) break;
			}
			while (actual.Count < expected.Count);
			Assert.Equal(expected.Select(item => item.Id), actual);
			if (first is not null)
			{
				foreach (MemoryTimelineEntry entry in first.Items)
				{
					Seed seed = seeds.Single(item => item.Id == entry.Item.Id);
					MemoryCreatedStamp stamp = MemoryTimelineTime.Classify(seed.CreatedAt, Zone);
					Assert.Equal(stamp.Known, entry.Created.Known);
					Assert.Equal(stamp.Date, entry.Created.Date);
					if (!stamp.Known) Assert.NotEqual("2099-01-01", entry.Item.CreatedAt);
				}
			}
		}

		MemoryTimelinePage ordered = _store.QueryTimeline(new MemoryTimelineQuery { Limit = 50 }, Zone);
		Assert.Equal(seeds.Count, ordered.Total);
		long[] ids = ordered.Items.Select(item => item.Item.Id).ToArray();
		int sameInstant = Array.IndexOf(ids, seeds[0].Id);
		int newerSameInstant = Array.IndexOf(ids, seeds[1].Id);
		Assert.True(newerSameInstant < sameInstant);
		Assert.Equal(seeds[9].Id, ids[^2]);
		Assert.Equal(seeds[8].Id, ids[^1]);
		Assert.All(ordered.Items.TakeLast(2), item => Assert.False(item.Created.Known));
	}

	[Fact]
	public void 非法筛选被拒绝且不改检索和库结构()
	{
		MemoryItem recalled = _store.AddAggregate("fact", "主人喜欢草莓蛋糕", 0.8, tags: "food");
		long[] beforeSearch = _store.SearchKeyword("草莓").Select(hit => hit.MemoryId).ToArray();
		long[] beforeAll = _store.GetAll(20).Select(item => item.Id).ToArray();
		_ = _store.QueryTimeline(new MemoryTimelineQuery { Keyword = "草莓" }, Zone);
		Assert.Equal(beforeSearch, _store.SearchKeyword("草莓").Select(hit => hit.MemoryId).ToArray());
		Assert.Equal(beforeAll, _store.GetAll(20).Select(item => item.Id).ToArray());
		Assert.Equal(recalled.Id, Assert.Single(beforeSearch));
		Assert.Equal(7, NoriDatabase.DatabaseSchemaVersion);
		_database.Locked(connection =>
		{
			using SqliteCommand command = connection.CreateCommand();
			command.CommandText = "SELECT name FROM sqlite_master WHERE name LIKE '%timeline%'";
			using SqliteDataReader reader = command.ExecuteReader();
			Assert.False(reader.Read());
		});
		Assert.Throws<InvalidOperationException>(() => new MemoryTimelineQuery { Kind = "nope" }.Normalize());
		Assert.Throws<InvalidOperationException>(() => new MemoryTimelineQuery { From = new DateOnly(2024, 2, 2), To = new DateOnly(2024, 1, 1) }.Normalize());
		Assert.Throws<InvalidOperationException>(() => new MemoryTimelineQuery { Keyword = new string('a', 201) }.Normalize());
		Assert.Throws<InvalidOperationException>(() => new MemoryTimelineQuery { Activity = (MemoryTimelineActivity)99 }.Normalize());
	}

	[Fact]
	public async Task 详情只返回库里确认过的内容和关系()
	{
		MemoryItem older = _store.AddAggregate("factual", "旧约定", kind: MemoryKind.Factual, canonicalSummary: "旧摘要");
		MemoryItem newer = _store.AddAggregate("factual", "新约定", kind: MemoryKind.Factual);
		MemoryItem missingLink = _store.AddAggregate("general", "断掉的关系");
		_store.SetStatus(older.Id, MemoryStatus.Superseded, newer.Id);
		_store.SetStatus(missingLink.Id, MemoryStatus.Superseded, 987654321);
		_store.AddSources(newer.Id,
		[
			new MemorySource { Id = 0, MemoryId = newer.Id, Role = "user", Content = "当时说的话", MessageTime = null, Sequence = 2 },
			new MemorySource { Id = 0, MemoryId = newer.Id, Role = "assistant", Content = "回应", MessageTime = "2024-06-02T03:04:05Z", Sequence = 1 },
		]);
		StampCreated(older.Id, "later-but-invalid", "2099-12-31T00:00:00Z");

		MemoryTimelineDetail olderDetail = _store.GetTimelineDetail(older.Id, Zone)!;
		Assert.False(olderDetail.Created.Known);
		Assert.Equal("later-but-invalid", olderDetail.Item.CreatedAt);
		Assert.True(olderDetail.Updated.Known);
		MemoryTimelineRelation successor = Assert.Single(olderDetail.Related);
		Assert.Equal("superseded-by", successor.Link);
		Assert.True(successor.Resolved);
		Assert.Equal(newer.Id, successor.Id);
		Assert.Equal("新约定", successor.Content);
		Assert.Empty(olderDetail.Sources);

		MemoryTimelineDetail newerDetail = _store.GetTimelineDetail(newer.Id, Zone)!;
		MemoryTimelineRelation previous = Assert.Single(newerDetail.Related);
		Assert.Equal("supersedes", previous.Link);
		Assert.Equal(older.Id, previous.Id);
		Assert.Equal("旧摘要", previous.CanonicalSummary);
		Assert.Equal(["assistant", "user"], newerDetail.Sources.Select(source => source.Role).ToArray());
		Assert.Equal("2024-06-02T03:04:05Z", newerDetail.Sources[0].MessageTime);
		Assert.Null(newerDetail.Sources[1].MessageTime);
		Assert.Equal("当时说的话", newerDetail.Sources[1].Content);

		MemoryTimelineDetail broken = _store.GetTimelineDetail(missingLink.Id, Zone)!;
		MemoryTimelineRelation unresolved = Assert.Single(broken.Related);
		Assert.False(unresolved.Resolved);
		Assert.Equal(987654321, unresolved.Id);
		Assert.Null(unresolved.Content);
		Assert.Null(unresolved.Kind);
		Assert.Null(_store.GetTimelineDetail(424242, Zone));
		await using MemoryService service = new(_store, new TimelineEmbedding(), new Nori.Core.Configuration.ConfigStore(_database), startBackgroundWorker: false);
		Assert.Throws<InvalidOperationException>(() => service.GetTimelineDetail(424242, Zone));
	}

	[Fact]
	public async Task 取消时间线读取且大数据只装载当前页()
	{
		TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
		_store.BeforeTimelineRead = cancellation =>
		{
			started.TrySetResult();
			cancellation.WaitHandle.WaitOne(TimeSpan.FromSeconds(5));
			cancellation.ThrowIfCancellationRequested();
		};
		using CancellationTokenSource cancellation = new();
		Task<MemoryTimelinePage> reading = Task.Run(() => _store.QueryTimeline(new MemoryTimelineQuery(), Zone, cancellation.Token));
		await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
		cancellation.Cancel();
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reading);
		_store.BeforeTimelineRead = null;

		_database.Locked(connection =>
		{
			using SqliteTransaction transaction = connection.BeginTransaction();
			using SqliteCommand command = connection.CreateCommand();
			command.Transaction = transaction;
			command.CommandText = """
				INSERT INTO memories(type, content, importance, source, created_at, updated_at, kind, canonical_summary, status)
				VALUES ('general', $content, 0.5, 'chat', $created, $created, 'general', $content, 'active')
				""";
			SqliteParameter content = command.Parameters.Add("$content", SqliteType.Text);
			SqliteParameter created = command.Parameters.Add("$created", SqliteType.Text);
			for (int index = 0; index < 3000; index++)
			{
				content.Value = "bulk-" + index.ToString(System.Globalization.CultureInfo.InvariantCulture);
				created.Value = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero).AddMinutes(index).ToString("o", System.Globalization.CultureInfo.InvariantCulture);
				command.ExecuteNonQuery();
			}
			transaction.Commit();
		});
		var watch = Stopwatch.StartNew();
		MemoryTimelinePage first = _store.QueryTimeline(new MemoryTimelineQuery { Limit = 24 }, Zone);
		MemoryTimelinePage second = _store.QueryTimeline(new MemoryTimelineQuery { Limit = 24, Offset = 24 }, Zone);
		MemoryTimelinePage again = _store.QueryTimeline(new MemoryTimelineQuery { Limit = 24 }, Zone);
		watch.Stop();
		Assert.Equal(3000, first.Total);
		Assert.Equal(24, first.Items.Count);
		Assert.Equal(24, _store.TimelineItemMaterializations);
		Assert.Equal(3000, second.Total);
		Assert.Empty(first.Items.Select(item => item.Item.Id).Intersect(second.Items.Select(item => item.Item.Id)));
		Assert.Equal(first.Items.Select(item => item.Item.Id), again.Items.Select(item => item.Item.Id));
		Assert.True(first.Items[0].Created.Instant > first.Items[^1].Created.Instant);
		Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3), watch.Elapsed.ToString());
		MemoryTimelinePage matched = _store.QueryTimeline(new MemoryTimelineQuery { Keyword = "bulk-2999", Limit = 24 }, Zone);
		Assert.Equal(1, matched.Total);
		Assert.Equal(1, _store.TimelineItemMaterializations);
		Assert.Equal("bulk-2999", Assert.Single(matched.Items).Item.Content);
	}

	private Seed AddSeed(string content, string kind, string status, string createdAt, string? summary, string? tags, string? persona)
	{
		MemoryItem item = _store.AddAggregate(kind, content, 0.5, "chat", tags, kind: MemoryKindExtensions.Parse(kind), canonicalSummary: summary, personaSummary: persona);
		StampCreated(item.Id, createdAt, createdAt.Length == 0 ? "2020-01-01T00:00:00Z" : createdAt);
		if (status != "active") _store.SetStatus(item.Id, MemoryStatusExtensions.Parse(status));
		return new Seed(item.Id, content, summary, persona, tags, MemoryKindExtensions.Parse(kind).ToStorage(), status, createdAt);
	}

	private void Stamp(Seed seed, string updatedAt) => StampCreated(seed.Id, seed.CreatedAt, updatedAt);

	private void StampCreated(long id, string createdAt, string updatedAt) => _database.Locked(connection =>
	{
		using SqliteCommand command = connection.CreateCommand();
		command.CommandText = "UPDATE memories SET created_at = $created, updated_at = $updated WHERE id = $id";
		command.Parameters.AddWithValue("$created", createdAt);
		command.Parameters.AddWithValue("$updated", updatedAt);
		command.Parameters.AddWithValue("$id", id);
		Assert.Equal(1, command.ExecuteNonQuery());
	});

	private static List<Seed> Expected(IReadOnlyList<Seed> seeds, MemoryTimelineQuery query)
	{
		MemoryTimelineQuery normalized = query.Normalize();
		List<Seed> matched = [];
		foreach (Seed seed in seeds)
		{
			if (normalized.Kind is not null && !string.Equals(seed.Kind, normalized.Kind, StringComparison.OrdinalIgnoreCase)) continue;
			if (normalized.Activity == MemoryTimelineActivity.Active && seed.Status == "archived") continue;
			if (normalized.Activity == MemoryTimelineActivity.Archived && seed.Status != "archived") continue;
			if (!Contains(seed, normalized.Keyword)) continue;
			if (!MemoryTimelineTime.InRange(MemoryTimelineTime.Classify(seed.CreatedAt, Zone), normalized.From, normalized.To)) continue;
			matched.Add(seed);
		}
		matched.Sort((left, right) => MemoryTimelineTime.Compare(
			MemoryTimelineTime.Classify(left.CreatedAt, Zone), left.Id,
			MemoryTimelineTime.Classify(right.CreatedAt, Zone), right.Id));
		return matched;
	}

	private static bool Contains(Seed seed, string? keyword)
	{
		if (string.IsNullOrEmpty(keyword)) return true;
		return Has(seed.Content) || Has(seed.Summary) || Has(seed.Persona) || Has(seed.Tags);
		bool Has(string? value) => value?.Contains(keyword, StringComparison.OrdinalIgnoreCase) == true;
	}

	private sealed record Seed(long Id, string Content, string? Summary, string? Persona, string? Tags, string Kind, string Status, string CreatedAt);

	private sealed class TimelineEmbedding : IEmbeddingAdapter
	{
		public Task<float[]> GetEmbeddingAsync(string baseUrl, string apiKey, string model, string input, int? dimensions = null, CancellationToken cancellationToken = default) =>
			Task.FromResult<float[]>([1f, 0f]);

		public Task<IReadOnlyList<float[]>> GetEmbeddingsAsync(string baseUrl, string apiKey, string model, IReadOnlyList<string> inputs, int? dimensions = null, CancellationToken cancellationToken = default) =>
			Task.FromResult<IReadOnlyList<float[]>>(inputs.Select(_ => new float[] { 1f, 0f }).ToArray());
	}
}
