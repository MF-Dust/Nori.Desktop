using Nori.Core.Data;
using Nori.Core.Memory;
using Nori.Core.Tests.TestSupport;

namespace Nori.Core.Tests;

public sealed class MemoryIndexConsistencyTests : IDisposable
{
	private readonly TempDatabase _tempDatabase = new("nori-memory-index");
	private readonly NoriDatabase _database;
	private readonly MemoryStore _store;

	public MemoryIndexConsistencyTests()
	{
		_database = NoriDatabase.Open(_tempDatabase.Path);
		_store = new MemoryStore(_database);
	}

	[Fact]
	public void 归档恢复和删除同步Memory与Atom索引()
	{
		MemoryItem item = _store.AddAggregate("fact", "索引一致性测试", embedding: "[1,0]");
		_store.AddAtom(item.Id, MemoryKind.Factual, item.Content);
		Assert.NotEmpty(_store.SearchKeyword("一致性"));
		Assert.NotEmpty(_store.SearchAtomKeyword("一致性"));

		Assert.True(_store.Archive(item.Id));
		Assert.Empty(_store.SearchKeyword("一致性"));
		Assert.Empty(_store.SearchAtomKeyword("一致性"));

		Assert.True(_store.Restore(item.Id));
		Assert.NotEmpty(_store.SearchKeyword("一致性"));
		Assert.NotEmpty(_store.SearchAtomKeyword("一致性"));

		Assert.True(_store.Delete(item.Id));
		Assert.Empty(_store.SearchKeyword("一致性"));
		Assert.Empty(_store.SearchAtomKeyword("一致性"));
	}

	public void Dispose()
	{
		_database.Dispose();
		_tempDatabase.Dispose();
	}
}
