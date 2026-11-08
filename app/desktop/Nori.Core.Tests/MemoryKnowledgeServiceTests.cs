using System.Reflection;
using Nori.Core.Configuration;
using Nori.Core.Data;
using Nori.Core.Embedding;
using Nori.Core.Memory;

namespace Nori.Core.Tests;

public sealed class MemoryKnowledgeServiceTests : IAsyncLifetime
{
	private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"nori-knowledge-{Guid.NewGuid():N}.db");
	private readonly string _knowledgePath = Path.Combine(Path.GetTempPath(), $"nori-knowledge-{Guid.NewGuid():N}.md");
	private readonly NoriDatabase _database;
	private readonly ConfigStore _config;
	private readonly MemoryService _memory;
	private readonly KnowledgeService _knowledge;

	public MemoryKnowledgeServiceTests()
	{
		_database = NoriDatabase.Open(_databasePath);
		_config = new ConfigStore(_database);
		_config.InitDefaults("test");
		_config.Set("memory_knowledge_path", new ConfigValue.Text(_knowledgePath));
		_memory = new MemoryService(new MemoryStore(_database), new StubEmbedding(), _config);
		_knowledge = new KnowledgeService(_database, _memory, _config);
	}

	[Fact]
	public async Task MemoryMd按认知Gate检索并支持增量重建()
	{
		await File.WriteAllTextAsync(_knowledgePath, """
			# 当前

			## 自我
			[NORI_KNOWS]
			Nori 知道自己的名字。

			## 残响
			[NORI_ECHO]
			研究员这个词带来熟悉感。

			## 未知
			[NORI_UNKNOWN]
			某段尚未恢复的背景。
			""");
		MemoryIndexStatus status = await _knowledge.ReindexAsync();
		Assert.Equal(3, status.Total);
		Assert.NotEmpty(_knowledge.Search("名字"));
		Assert.Empty(_knowledge.Search("背景"));
		Assert.NotEmpty(_knowledge.Search("分析未知背景"));
		Assert.NotEmpty(_knowledge.SearchEchoes("研究员"));

		MemoryIndexStatus unchanged = await _knowledge.ReindexAsync();
		Assert.Equal(3, unchanged.Processed);
	}

	[Fact]
	public async Task 文件变更完成后再次变更可重建并正常关闭()
	{
		await File.WriteAllTextAsync(_knowledgePath, "# 自我\n[NORI_KNOWS]\n第一轮内容。");
		await NotifyFileChanged().WaitAsync(TimeSpan.FromSeconds(10));
		Assert.Null(GetWatcherField<CancellationTokenSource>("_watchDebounce"));

		await File.WriteAllTextAsync(_knowledgePath, "# 自我\n[NORI_KNOWS]\n第二轮内容。");
		await NotifyFileChanged().WaitAsync(TimeSpan.FromSeconds(10));
		RetrievedKnowledge result = Assert.Single(_knowledge.Search("第二轮内容"));
		Assert.Contains("第二轮内容", result.Content);
		Assert.DoesNotContain("第一轮内容", result.Content);
		await _knowledge.DisposeAsync();
	}

	[Fact]
	public async Task 快速连续变更取消旧任务且最终索引最新内容()
	{
		await File.WriteAllTextAsync(_knowledgePath, "# 自我\n[NORI_KNOWS]\n旧版内容。");
		Task[] tasks = Enumerable.Range(0, 100).Select(_ => NotifyFileChanged()).ToArray();
		await File.WriteAllTextAsync(_knowledgePath, "# 自我\n[NORI_KNOWS]\n最终版本。");
		Task latest = NotifyFileChanged();
		await latest.WaitAsync(TimeSpan.FromSeconds(10));
		Assert.All(tasks, task => Assert.True(task.IsCompletedSuccessfully));
		Assert.NotEmpty(_knowledge.Search("最终版本"));
		Assert.Empty(_knowledge.Search("旧版内容"));
		Assert.Null(GetWatcherField<CancellationTokenSource>("_watchDebounce"));
	}

	[Fact]
	public async Task 完成释放与并发变更交错不抛异常()
	{
		await File.WriteAllTextAsync(_knowledgePath, "# 自我\n[NORI_KNOWS]\n并发内容。");
		Task first = NotifyFileChanged();
		Task[] changes = Enumerable.Range(0, 32).Select(_ => Task.Run(async () =>
		{
			await first;
			await NotifyFileChanged();
		})).ToArray();
		await Task.WhenAll(changes).WaitAsync(TimeSpan.FromSeconds(10));
		Assert.NotEmpty(_knowledge.Search("并发内容"));
		Assert.Null(GetWatcherField<CancellationTokenSource>("_watchDebounce"));
	}

	[Fact]
	public async Task 关闭等待旧索引退出并拒绝排队回调和重启监听()
	{
		await File.WriteAllTextAsync(_knowledgePath, "# 自我\n[NORI_KNOWS]\n关闭时内容。");
		TaskCompletionSource rebuilding = new(TaskCreationOptions.RunContinuationsAsynchronously);
		using ManualResetEventSlim release = new();
		int rebuilds = 0;
		_knowledge.StatusChanged = () =>
		{
			if (_knowledge.Status.State != MemoryIndexState.Rebuilding) return;
			Interlocked.Increment(ref rebuilds);
			rebuilding.TrySetResult();
			Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
		};
		Task first = NotifyFileChanged();
		try
		{
			await rebuilding.Task.WaitAsync(TimeSpan.FromSeconds(10));
			Task latest = NotifyFileChanged();
			Task closing = _knowledge.DisposeAsync().AsTask();
			Assert.False(closing.IsCompleted);
			Assert.Null(NotifyFileChanged());
			_knowledge.StartWatcher();
			Assert.Null(GetWatcherField<FileSystemWatcher>("_watcher"));
			release.Set();
			await closing.WaitAsync(TimeSpan.FromSeconds(10));
			Assert.True(first.IsCompletedSuccessfully);
			Assert.True(latest.IsCompletedSuccessfully);
			Assert.Equal(1, rebuilds);
			Assert.Null(GetWatcherField<CancellationTokenSource>("_watchDebounce"));
		}
		finally
		{
			release.Set();
			await first.WaitAsync(TimeSpan.FromSeconds(10));
			_knowledge.StatusChanged = null;
		}
	}

	[Fact]
	public async Task 旧任务结束不会清掉新任务字段且最终索引最新内容()
	{
		await File.WriteAllTextAsync(_knowledgePath, "# 自我\n[NORI_KNOWS]\n第一版。");
		// 闸门结果回到测试线程再断言。StatusChanged 里的异常会被 SetStatus 吞掉。
		TaskCompletionSource firstEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource<bool> firstGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource secondEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource<bool> secondGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
		using ManualResetEventSlim releaseFirst = new();
		using ManualResetEventSlim releaseSecond = new();
		int rebuilds = 0;
		_knowledge.StatusChanged = () =>
		{
			if (_knowledge.Status.State != MemoryIndexState.Rebuilding) return;
			int visit = Interlocked.Increment(ref rebuilds);
			if (visit == 1)
			{
				firstEntered.TrySetResult();
				firstGate.TrySetResult(releaseFirst.Wait(TimeSpan.FromSeconds(10)));
				return;
			}
			secondEntered.TrySetResult();
			secondGate.TrySetResult(releaseSecond.Wait(TimeSpan.FromSeconds(10)));
		};
		Task first = NotifyFileChanged();
		try
		{
			await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
			await File.WriteAllTextAsync(_knowledgePath, "# 自我\n[NORI_KNOWS]\n第二版。");
			Task second = NotifyFileChanged();
			CancellationTokenSource? duringSecond = GetWatcherField<CancellationTokenSource>("_watchDebounce");
			Assert.NotNull(duringSecond);
			releaseFirst.Set();
			Assert.True(await firstGate.Task.WaitAsync(TimeSpan.FromSeconds(10)));
			await first.WaitAsync(TimeSpan.FromSeconds(10));
			// 第二个任务停在自己的重建通知里，不靠 750ms 延迟抢在它结束前读字段。
			await secondEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
			Assert.False(second.IsCompleted);
			Assert.Same(duringSecond, GetWatcherField<CancellationTokenSource>("_watchDebounce"));
			Assert.Null(Record.Exception(() => _ = duringSecond.Token));
			releaseSecond.Set();
			Assert.True(await secondGate.Task.WaitAsync(TimeSpan.FromSeconds(10)));
			await second.WaitAsync(TimeSpan.FromSeconds(10));
			Assert.Contains("第二版", Assert.Single(_knowledge.Search("第二版")).Content);
			Assert.Empty(_knowledge.Search("第一版"));
			Assert.Null(GetWatcherField<CancellationTokenSource>("_watchDebounce"));
		}
		finally
		{
			releaseFirst.Set();
			releaseSecond.Set();
			_knowledge.StatusChanged = null;
		}
	}

	[Fact]
	public async Task 连续Changed_Created_Renamed后索引最新内容()
	{
		await File.WriteAllTextAsync(_knowledgePath, "# 自我\n[NORI_KNOWS]\n甲。");
		Task changed = NotifyFileChanged(WatcherChangeTypes.Changed);
		await File.WriteAllTextAsync(_knowledgePath, "# 自我\n[NORI_KNOWS]\n乙。");
		Task created = NotifyFileChanged(WatcherChangeTypes.Created);
		await File.WriteAllTextAsync(_knowledgePath, "# 自我\n[NORI_KNOWS]\n丙。");
		Task renamed = NotifyFileChanged(WatcherChangeTypes.Renamed);
		await Task.WhenAll(changed, created, renamed).WaitAsync(TimeSpan.FromSeconds(10));
		Assert.Contains("丙", Assert.Single(_knowledge.Search("丙")).Content);
		Assert.Empty(_knowledge.Search("甲"));
		Assert.Empty(_knowledge.Search("乙"));
		Assert.Null(GetWatcherField<CancellationTokenSource>("_watchDebounce"));
	}

	[Fact]
	public async Task 重复关闭立即返回且锁内取消不死锁()
	{
		await File.WriteAllTextAsync(_knowledgePath, "# 自我\n[NORI_KNOWS]\n关闭内容。");
		Task pending = NotifyFileChanged();
		await _knowledge.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
		Assert.True(pending.IsCompletedSuccessfully);
		await _knowledge.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
		Assert.Null(NotifyFileChanged());
		_knowledge.StartWatcher();
		Assert.Null(GetWatcherField<FileSystemWatcher>("_watcher"));
	}

	// 直接投递监听回调，避免依赖操作系统通知次数和时序。
	private Task NotifyFileChanged(WatcherChangeTypes change = WatcherChangeTypes.Changed)
	{
		string directory = Path.GetDirectoryName(_knowledgePath)!;
		string name = Path.GetFileName(_knowledgePath);
		FileSystemEventArgs args = change == WatcherChangeTypes.Renamed
			? new RenamedEventArgs(change, directory, name, name)
			: new FileSystemEventArgs(change, directory, name);
		typeof(KnowledgeService).GetMethod("OnFileChanged", BindingFlags.Instance | BindingFlags.NonPublic)!
			.Invoke(_knowledge, [this, args]);
		return GetWatcherField<Task>("_watchTask")!;
	}

	private T? GetWatcherField<T>(string name) where T : class =>
		(T?)typeof(KnowledgeService).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_knowledge);

	public Task InitializeAsync() => Task.CompletedTask;

	public async Task DisposeAsync()
	{
		await _knowledge.DisposeAsync();
		await _memory.DisposeAsync();
		_database.Dispose();
		File.Delete(_databasePath);
		File.Delete($"{_databasePath}-wal");
		File.Delete($"{_databasePath}-shm");
		File.Delete(_knowledgePath);
	}

	private sealed class StubEmbedding : IEmbeddingAdapter
	{
		public Task<float[]> GetEmbeddingAsync(string baseUrl, string apiKey, string model, string input, int? dimensions = null, CancellationToken cancellationToken = default) => Task.FromResult<float[]>([1, 0]);
		public Task<IReadOnlyList<float[]>> GetEmbeddingsAsync(string baseUrl, string apiKey, string model, IReadOnlyList<string> inputs, int? dimensions = null, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<float[]>>(inputs.Select(_ => new float[] {1, 0}).ToList());
	}
}
