using System.Text.Json;
using Nori.Core.Data;
using Nori.Core.Memory;
using Nori.Core.Tests.TestSupport;

namespace Nori.Core.Tests;

/// <summary>nori-memory-v1 的白名单、事务写入、去重与令牌回归测试。</summary>
public sealed class MemoryTransferTests : IDisposable
{
	private readonly TempDatabase _tempDatabase = new("nori-memory-transfer");
	private readonly NoriDatabase _database;
	private readonly MemoryStore _store;
	private readonly MutableTimeProvider _clock = new(new DateTimeOffset(2026, 8, 26, 0, 0, 0, TimeSpan.Zero));
	private readonly MemoryTransferService _service;

	public MemoryTransferTests()
	{
		_database = NoriDatabase.Open(_tempDatabase.Path);
		_store = new MemoryStore(_database);
		_service = new MemoryTransferService(_store, timeProvider: _clock);
	}

	[Fact]
	public void 导出仅包含白名单并排除已替代和过期记忆()
	{
		MemoryItem active = _store.AddAggregate("factual", "活跃记忆", source: "chat", kind: MemoryKind.Factual,
			canonicalSummary: "活跃摘要", personaSummary: "人格摘要", tags: "safe", importance: 0.9, confidence: 0.8);
		MemoryItem dormant = _store.AddAggregate("preference", "休眠记忆", kind: MemoryKind.Preference);
		_store.SetStatus(dormant.Id, MemoryStatus.Dormant);
		MemoryItem archived = _store.AddAggregate("general", "归档记忆");
		_store.Archive(archived.Id);
		MemoryItem superseded = _store.AddAggregate("general", "已替代记忆");
		_store.SetStatus(superseded.Id, MemoryStatus.Superseded);
		MemoryItem expired = _store.AddAggregate("general", "过期状态记忆");
		_store.SetStatus(expired.Id, MemoryStatus.Expired);
		_store.AddAggregate("general", "超期时间记忆", expiresAt: _clock.GetUtcNow().AddSeconds(-1).ToString("o"));

		MemoryTransferExport export = _service.ExportResult();
		using JsonDocument document = JsonDocument.Parse(export.Content);
		JsonElement root = document.RootElement;
		JsonElement[] items = root.GetProperty("memories").EnumerateArray().ToArray();

		Assert.Equal(MemoryTransferService.Format, root.GetProperty("version").GetString());
		Assert.Equal(MemoryTransferService.Format, root.GetProperty("format").GetString());
		Assert.Equal(3, export.TotalCount);
		Assert.Equal(2, export.ActiveCount);
		Assert.Equal(1, export.ArchivedCount);
		Assert.Equal(3, items.Length);
		JsonElement exportedActive = Assert.Single(items, item => item.GetProperty("content").GetString() == active.Content);
		Assert.Equal("factual", exportedActive.GetProperty("kind").GetString());
		Assert.Equal(MemoryTransferService.ComputeDedupeHash("factual", "活跃摘要"), exportedActive.GetProperty("dedupe_hash").GetString());
		Assert.False(exportedActive.TryGetProperty("id", out _));
		Assert.False(exportedActive.TryGetProperty("status", out _));
		Assert.False(exportedActive.TryGetProperty("embedding", out _));
		Assert.False(exportedActive.TryGetProperty("embedding_blob", out _));
		Assert.False(exportedActive.TryGetProperty("sources", out _));
		Assert.DoesNotContain("已替代记忆", export.Content);
		Assert.DoesNotContain("过期状态记忆", export.Content);
		Assert.DoesNotContain("超期时间记忆", export.Content);
	}

	[Fact]
	public void 预览严格校验UTF8边界分数和未知字段且不写库()
	{
		MemoryTransferService limited = new(_store, new MemoryTransferLimits {MaxFieldBytes = 4, MaxBytes = 512}, _clock);
		MemoryTransferPreview fieldTooLarge = limited.Preview(CreateDocument(new MemoryTransferItem {Content = "两个汉字", Kind = "general"}));
		Assert.False(fieldTooLarge.IsValid);
		Assert.Equal(MemoryTransferErrorCategory.InvalidItem, Assert.Single(fieldTooLarge.Errors).Category);

		MemoryTransferPreview invalidScore = _service.Preview(CreateDocument(new MemoryTransferItem
		{
			Content = "分数错误",
			Kind = "general",
			Importance = 1.1,
		}));
		Assert.False(invalidScore.IsValid);
		Assert.Equal(MemoryTransferErrorCategory.InvalidItem, Assert.Single(invalidScore.Errors).Category);

		// 脱敏回归夹具，验证未知字段不会回显到结果。
		const string secret = "secret-do-not-leak"; // nosemgrep
		MemoryTransferPreview unknownField = _service.Preview(
			"{\"version\":\"nori-memory-v1\",\"memories\":[{\"content\":\"安全内容\",\"kind\":\"general\",\"embedding\":\""
			+ secret + "\"}]}");
		Assert.False(unknownField.IsValid);
		Assert.Equal(MemoryTransferErrorCategory.InvalidItem, Assert.Single(unknownField.Errors).Category);
		Assert.DoesNotContain(secret, JsonSerializer.Serialize(unknownField));
		Assert.Equal(0, _store.GetOverview().Total);
		limited.Dispose();
	}

	[Fact]
	public void 预览识别规范化载荷重复与本地冲突()
	{
		_store.AddAggregate("factual", "本地内容", kind: MemoryKind.Factual, canonicalSummary: "主人喜欢 海边散步");
		MemoryTransferPreview preview = _service.Preview(CreateDocument(
			new MemoryTransferItem {Content = "主人喜欢 海边散步", Kind = "fact", Importance = 0.8, Confidence = 0.9},
			new MemoryTransferItem {Content = "  主人喜欢   海边散步 ", Kind = "factual", Importance = 0.8, Confidence = 0.9}));

		Assert.True(preview.IsValid);
		Assert.Equal(2, preview.TotalCount);
		Assert.Equal(0, preview.AcceptedCount);
		Assert.Equal(1, preview.ConflictCount);
		Assert.Equal(1, preview.DuplicateCount);
		Assert.Contains(preview.Conflicts, conflict => conflict.Reason == MemoryTransferConflictReason.Existing);
		Assert.Contains(preview.Conflicts, conflict => conflict.Reason == MemoryTransferConflictReason.DuplicateInPayload);
		Assert.All(preview.Items, item => Assert.True(item.ContentSummary.Length <= 241));
	}

	[Fact]
	public void 提交会原子写入活跃记忆默认Atom索引并在提交后排队向量()
	{
		List<MemoryEmbeddingWorkItem> queued = [];
		MemoryTransferService service = new(_store, timeProvider: _clock, queueEmbedding: work =>
		{
			Assert.NotNull(_store.Get(work.Id));
			queued.Add(work);
		});
		MemoryTransferPreview preview = service.Preview(CreateDocument(new MemoryTransferItem
		{
			Content = "主人喜欢夜间散步",
			CanonicalSummary = "喜欢夜间散步",
			PersonaSummary = "主人更享受安静的夜晚",
			Kind = "preference",
			Importance = 0.85,
			Confidence = 0.9,
			Tags = "walk",
			CreatedAt = "2001-01-01T00:00:00Z",
			SourceType = "chat",
		}));

		MemoryTransferCommitResult result = service.Commit(preview.PreviewToken);

		Assert.True(result.Succeeded);
		Assert.Equal(1, result.AddedCount);
		Assert.Equal(0, result.UpdatedCount);
		MemoryItem imported = Assert.Single(_store.GetAll());
		Assert.Equal(MemoryTransferService.ImportSource, imported.Source);
		Assert.Equal("active", imported.Status);
		Assert.Null(imported.ExpiresAt);
		Assert.Null(imported.Embedding);
		Assert.Null(imported.EmbeddingBlob);
		Assert.NotEqual("2001-01-01T00:00:00.0000000+00:00", imported.CreatedAt);
		MemoryAtom atom = Assert.Single(_store.GetAtoms(imported.Id));
		Assert.Equal("喜欢夜间散步", atom.Content);
		Assert.Equal(MemoryStatus.Active, atom.Status);
		Assert.Contains(_store.SearchKeyword("夜间散步"), hit => hit.MemoryId == imported.Id);
		Assert.Contains(_store.SearchAtomKeyword("夜间散步"), hit => hit.MemoryId == atom.Id);
		Assert.Single(queued);
		Assert.Equal(imported.Id, queued[0].Id);
		service.Dispose();
	}

	[Fact]
	public void 提交支持跳过覆盖和创建副本且载荷重复始终跳过()
	{
		MemoryItem existing = _store.AddAggregate("factual", "旧内容", source: "manual", kind: MemoryKind.Factual,
			canonicalSummary: "同一语义", tags: "old");
		MemoryTransferItem incoming = new()
		{
			Content = "新内容",
			CanonicalSummary = "同一语义",
			Kind = "factual",
			Importance = 0.9,
			Confidence = 0.7,
			Tags = "new",
		};

		MemoryTransferPreview skipPreview = _service.Preview(CreateDocument(incoming));
		MemoryTransferCommitResult skipped = _service.Commit(skipPreview.PreviewToken, MemoryTransferConflictStrategy.Skip);
		Assert.True(skipped.Succeeded);
		Assert.Equal(1, skipped.SkippedCount);
		Assert.Equal("旧内容", _store.Get(existing.Id)!.Content);

		MemoryTransferPreview overwritePreview = _service.Preview(CreateDocument(incoming));
		MemoryTransferCommitResult overwritten = _service.Commit(overwritePreview.PreviewToken, MemoryTransferConflictStrategy.Overwrite);
		Assert.True(overwritten.Succeeded);
		Assert.Equal(1, overwritten.UpdatedCount);
		MemoryItem updated = _store.Get(existing.Id)!;
		Assert.Equal("新内容", updated.Content);
		Assert.Equal(MemoryTransferService.ImportSource, updated.Source);
		Assert.Equal("active", updated.Status);
		Assert.Equal("同一语义", Assert.Single(_store.GetAtoms(existing.Id)).Content);
		Assert.Empty(_store.SearchKeyword("旧内容"));
		Assert.Contains(_store.SearchKeyword("新内容"), hit => hit.MemoryId == existing.Id);

		MemoryTransferPreview copyPreview = _service.Preview(CreateDocument(incoming));
		MemoryTransferCommitResult copy = _service.Commit(copyPreview.PreviewToken, MemoryTransferConflictStrategy.CreateCopy);
		Assert.True(copy.Succeeded);
		Assert.Equal(1, copy.AddedCount);
		Assert.Equal(2, _store.GetAll().Count);

		MemoryTransferPreview duplicatePreview = _service.Preview(CreateDocument(incoming, incoming));
		MemoryTransferCommitResult duplicate = _service.Commit(duplicatePreview.PreviewToken, MemoryTransferConflictStrategy.CreateCopy);
		Assert.True(duplicate.Succeeded);
		Assert.Equal(1, duplicate.AddedCount);
		Assert.Equal(1, duplicate.SkippedCount);
		Assert.Equal(3, _store.GetAll().Count);
	}

	[Fact]
	public async Task 预览令牌有五分钟有效期且并发只能消费一次()
	{
		MemoryTransferPreview preview = _service.Preview(CreateDocument(new MemoryTransferItem {Content = "只提交一次", Kind = "general"}));
		Assert.True(preview.IsValid, JsonSerializer.Serialize(preview));
		Task<MemoryTransferCommitResult>[] commits = Enumerable.Range(0, 8)
			.Select(_ => Task.Run(() => _service.Commit(preview.PreviewToken)))
			.ToArray();
		MemoryTransferCommitResult[] results = await Task.WhenAll(commits);

		Assert.Single(results, result => result.Succeeded);
		Assert.Equal(1, _store.GetOverview().Total);
		Assert.Contains(results, result => !result.Succeeded
			&& Assert.Single(result.Errors).Category == MemoryTransferErrorCategory.PreviewAlreadyUsed);

		MemoryTransferPreview expires = _service.Preview(CreateDocument(new MemoryTransferItem {Content = "过期内容", Kind = "general"}));
		_clock.Advance(TimeSpan.FromMinutes(5).Add(TimeSpan.FromTicks(1)));
		MemoryTransferCommitResult expired = _service.Commit(expires.PreviewToken);
		Assert.False(expired.Succeeded);
		Assert.Equal(MemoryTransferErrorCategory.PreviewExpired, Assert.Single(expired.Errors).Category);
	}

	[Fact]
	public void 数量和总UTF8大小上限会稳定拒绝()
	{
		MemoryTransferService oneItem = new(_store, new MemoryTransferLimits {MaxItems = 1}, _clock);
		MemoryTransferPreview tooMany = oneItem.Preview(CreateDocument(
			new MemoryTransferItem {Content = "一", Kind = "general"},
			new MemoryTransferItem {Content = "二", Kind = "general"}));
		Assert.False(tooMany.IsValid);
		Assert.Equal(MemoryTransferErrorCategory.TooManyItems, Assert.Single(tooMany.Errors).Category);

		MemoryTransferService byteLimited = new(_store, new MemoryTransferLimits {MaxBytes = 64}, _clock);
		MemoryTransferPreview tooLarge = byteLimited.Preview(CreateDocument(new MemoryTransferItem {Content = "总大小限制", Kind = "general"}));
		Assert.False(tooLarge.IsValid);
		Assert.Equal(MemoryTransferErrorCategory.PayloadTooLarge, Assert.Single(tooLarge.Errors).Category);
		oneItem.Dispose();
		byteLimited.Dispose();
	}

	private static string CreateDocument(params MemoryTransferItem[] items) => JsonSerializer.Serialize(new MemoryTransferDocument
	{
		Version = MemoryTransferService.Format,
		Format = MemoryTransferService.Format,
		Memories = items,
	});

	public void Dispose()
	{
		_service.Dispose();
		_database.Dispose();
		_clock.Dispose();
		_tempDatabase.Dispose();
		GC.SuppressFinalize(this);
	}
}
