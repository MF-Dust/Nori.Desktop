using Microsoft.Data.Sqlite;
using Nori.Core.Configuration;
using Nori.Core.Data;
using Nori.Core.Embedding;
using Nori.Core.Memory;

namespace Nori.Core.Tests;

public sealed class MemoryBatchTests : IDisposable
{
	private readonly string _path = Path.Combine(Path.GetTempPath(), $"nori-memory-batch-{Guid.NewGuid():N}.db");
	private readonly NoriDatabase _database;
	private readonly ConfigStore _config;
	private readonly MemoryStore _store;

	public MemoryBatchTests()
	{
		_database = NoriDatabase.Open(_path);
		_config = new ConfigStore(_database);
		_config.InitDefaults("test");
		_config.Set("memory_enabled", new ConfigValue.Boolean(true));
		_config.Set("memory_recall_top_k", new ConfigValue.Integer(20));
		_store = new MemoryStore(_database);
	}

	[Fact]
	public async Task BuildContextUsesBatchedReadsAndKeepsRecallOrderAndAccessUpdates()
	{
		List<MemoryItem> parents = [];
		for (int index = 0; index < 12; index++)
		{
			MemoryItem parent = _store.AddAggregate("fact", $"sharedterm parent {index}", 0.8);
			parents.Add(parent);
			_store.AddAtom(parent.Id, MemoryKind.Factual, $"sharedterm extra {index} a", 0.93);
			_store.AddAtom(parent.Id, MemoryKind.Factual, $"sharedterm extra {index} b", 0.92);
			_store.AddAtom(parent.Id, MemoryKind.Factual, $"sharedterm extra {index} c", 0.91);
		}

		int batchReadCount = 0;
		_store.ReadQueryExecuted = () => batchReadCount++;
		await using MemoryService service = new(_store, new DisabledEmbedding(), _config, startBackgroundWorker: false);
		int settingsReadCount = 0;
		_config.ReadQueryExecuted = () => settingsReadCount++;
		Assert.True(service.Settings.Enabled);
		Assert.Equal(1, settingsReadCount);
		_config.ReadQueryExecuted = null;

		MemoryContext context = await service.BuildContextAsync("sharedterm", [], includeDebug: true);

		Assert.Equal(12, context.Personal.Count);
		Assert.Equal(12, context.Debug!.InjectedIds.Count);
		Assert.Equal(context.Debug.InjectedIds, context.Personal.Select(item => item.Id));
		Assert.Equal(36, context.Atoms.Count);
		Assert.All(context.Atoms.GroupBy(atom => atom.ParentMemoryId), group => Assert.Equal(3, group.Count()));
		Assert.Equal(3, batchReadCount);
		long[] baselineAtomIds = context.Personal
			.SelectMany(item => _store.GetAtoms(item.Id, MemoryStatus.Active, 3))
			.Select(atom => atom.Id)
			.ToArray();
		Assert.Equal(baselineAtomIds, context.Atoms.Select(atom => atom.Id));
		Assert.All(context.Personal, item => Assert.Equal(0, item.AccessCount));
		Assert.All(parents, item => Assert.Equal(1, _store.Get(item.Id)!.AccessCount));
	}

	[Fact]
	public void MemoryBatchQueriesBindAtMostFiveHundredIdsPerStatement()
	{
		int queryCount = 0;
		_store.ReadQueryExecuted = () => queryCount++;
		long[] ids = Enumerable.Range(1, 1001).Select(value => (long)value).ToArray();

		Assert.Empty(_store.GetMany(ids));
		Assert.Empty(_store.GetAtomsByIds(ids));
		Assert.Empty(_store.GetActiveAtomsByParents(ids, 3));

		Assert.Equal(9, queryCount);
	}

	[Fact]
	public void SetStatusRefreshesOnlyAffectedAtomsAndPreservesSupersededChildren()
	{
		MemoryItem parent = _store.AddAggregate("fact", "batchstatusmark parent");
		MemoryAtom activeAtom = Assert.Single(_store.GetAtoms(parent.Id));
		MemoryAtom supersededAtom = _store.AddAtom(parent.Id, MemoryKind.Factual, "batchstatusmark superseded");
		Assert.True(_store.SetAtomStatus(supersededAtom.Id, MemoryStatus.Superseded, 42));

		Assert.True(_store.SetStatus(parent.Id, MemoryStatus.Archived, 99));

		Assert.Equal("archived", _store.Get(parent.Id)!.Status);
		Assert.Equal(MemoryStatus.Archived, _store.GetAtom(activeAtom.Id)!.Status);
		Assert.Equal(MemoryStatus.Superseded, _store.GetAtom(supersededAtom.Id)!.Status);
		Assert.Equal(42, _store.GetAtom(supersededAtom.Id)!.SupersededBy);
		Assert.Empty(_store.SearchAtomKeyword("batchstatusmark"));

		Assert.True(_store.Restore(parent.Id));
		Assert.Equal(MemoryStatus.Active, _store.GetAtom(activeAtom.Id)!.Status);
		Assert.Equal(MemoryStatus.Superseded, _store.GetAtom(supersededAtom.Id)!.Status);
		Assert.Equal(activeAtom.Id, Assert.Single(_store.SearchAtomKeyword("batchstatusmark")).MemoryId);
	}

	[Fact]
	public void SetStatusRollsBackParentAndChildUpdatesWhenFtsRefreshFails()
	{
		Assert.True(_store.IsFtsAvailable);
		MemoryItem parent = _store.AddAggregate("fact", "rollbackmark parent");
		MemoryAtom atom = Assert.Single(_store.GetAtoms(parent.Id));
		_database.Locked(connection =>
		{
			using SqliteCommand command = connection.CreateCommand();
			command.CommandText = "DROP TABLE memory_atoms_fts";
			command.ExecuteNonQuery();
		});

		Assert.Throws<SqliteException>(() => _store.SetStatus(parent.Id, MemoryStatus.Archived));

		Assert.Equal("active", _store.Get(parent.Id)!.Status);
		Assert.Equal(MemoryStatus.Active, _store.GetAtom(atom.Id)!.Status);
		Assert.Single(_store.SearchKeyword("rollbackmark"));
	}

	public void Dispose()
	{
		_database.Dispose();
		try
		{
			File.Delete(_path);
			File.Delete($"{_path}-wal");
			File.Delete($"{_path}-shm");
		}
		catch (IOException)
		{
		}
		GC.SuppressFinalize(this);
	}

	private sealed class DisabledEmbedding : IEmbeddingAdapter
	{
		public Task<float[]> GetEmbeddingAsync(string baseUrl, string apiKey, string model, string input, int? dimensions = null, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Embedding should be disabled in this test.");

		public Task<IReadOnlyList<float[]>> GetEmbeddingsAsync(string baseUrl, string apiKey, string model, IReadOnlyList<string> inputs, int? dimensions = null, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Embedding should be disabled in this test.");
	}
}
