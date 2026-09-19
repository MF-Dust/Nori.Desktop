using System.Collections.Concurrent;
using Nori.Core.Configuration;
using Nori.Core.Data;
using Nori.Core.Embedding;
using Nori.Core.Memory;

namespace Nori.Core.Tests;

/// <summary>后台向量队列的容量、重试和数据库补偿测试。</summary>
public sealed class MemoryEmbeddingQueueReliabilityTests
{
	[Fact]
	public async Task 队列饱和后由数据库补偿且正文不丢失()
	{
		string path = TempPath("saturation");
		try
		{
			using NoriDatabase database = NoriDatabase.Open(path);
			ConfigStore config = CreateEmbeddingConfig(database);
			const int total = 200;
			SaturatingEmbedding embedding = new(total);
			await using MemoryService service = new(new MemoryStore(database), embedding, config);

			List<MemoryItem> items = [await service.AddAsync("队列饱和 0")];
			await embedding.FirstBatchStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
			for (int index = 1; index < total; index++) items.Add(await service.AddAsync($"队列饱和 {index}"));

			Assert.True(service.EmbeddingQueueStatus.SaturatedCount > 0);
			embedding.ReleaseFirstBatch();
			await embedding.AllInputsProcessed.Task.WaitAsync(TimeSpan.FromSeconds(10));
			await WaitUntilAsync(() => service.EmbeddingQueueStatus.CompletedCount >= total);

			Assert.Equal(total, service.GetOverview().Total);
			Assert.All(items, item => Assert.NotNull(service.Get(item.Id)!.GetVector()));
			Assert.True(service.EmbeddingQueueStatus.RecoveryBatchCount > 0);
		}
		finally
		{
			DeleteDatabaseFiles(path);
		}
	}

	[Fact]
	public async Task 批处理失败后有限重试并恢复向量()
	{
		string path = TempPath("retry");
		try
		{
			using NoriDatabase database = NoriDatabase.Open(path);
			ConfigStore config = CreateEmbeddingConfig(database);
			RetryOnceEmbedding embedding = new();
			await using MemoryService service = new(new MemoryStore(database), embedding, config);

			MemoryItem item = await service.AddAsync("批处理失败后仍然补齐");
			await embedding.Recovered.Task.WaitAsync(TimeSpan.FromSeconds(5));
			await WaitUntilAsync(() => service.EmbeddingQueueStatus.CompletedCount == 1);

			Assert.NotNull(service.Get(item.Id)!.GetVector());
			Assert.True(service.EmbeddingQueueStatus.AttemptCount >= 2);
			Assert.Equal(0, service.EmbeddingQueueStatus.FailedBatchCount);
		}
		finally
		{
			DeleteDatabaseFiles(path);
		}
	}

	[Fact]
	public async Task 返回数量不匹配后重试而不推进失败任务()
	{
		string path = TempPath("count-mismatch");
		try
		{
			using NoriDatabase database = NoriDatabase.Open(path);
			ConfigStore config = CreateEmbeddingConfig(database);
			MismatchOnceEmbedding embedding = new();
			await using MemoryService service = new(new MemoryStore(database), embedding, config);

			MemoryItem item = await service.AddAsync("返回数量不匹配后仍然补齐");
			await embedding.Recovered.Task.WaitAsync(TimeSpan.FromSeconds(5));
			await WaitUntilAsync(() => service.EmbeddingQueueStatus.CompletedCount == 1);

			Assert.NotNull(service.Get(item.Id)!.GetVector());
			Assert.True(service.EmbeddingQueueStatus.CountMismatchCount >= 1);
			Assert.True(service.EmbeddingQueueStatus.AttemptCount >= 2);
		}
		finally
		{
			DeleteDatabaseFiles(path);
		}
	}

	[Fact]
	public void 恢复扫描包含过期指纹向量()
	{
		string path = TempPath("expired-fingerprint");
		try
		{
			using NoriDatabase database = NoriDatabase.Open(path);
			MemoryStore store = new(database);
			MemoryItem item = store.AddAggregate("fact", "过期指纹记忆", embedding: "[1, 0]", embeddingFingerprint: "old");

			Assert.Empty(store.GetUnembedded(10));
			Assert.Contains(store.GetUnembedded(10, fingerprint: "current"), pending => pending.Id == item.Id);
		}
		finally
		{
			DeleteDatabaseFiles(path);
		}
	}

	[Fact]
	public async Task 仅更新摘要时首次批处理失败仍由补偿写入新向量()
	{
		string path = TempPath("canonical-summary-recovery");
		try
		{
			using NoriDatabase database = NoriDatabase.Open(path);
			ConfigStore config = CreateEmbeddingConfig(database);
			MemoryStore store = new(database);
			MemoryItem item = store.AddAggregate("fact", "固定正文", embedding: "[1, 0]", canonicalSummary: "旧摘要", embeddingFingerprint: "old");
			CanonicalSummaryRecoveryEmbedding embedding = new();
			await using MemoryService service = new(store, embedding, config);

			Assert.True(await service.UpdateAsync(item.Id, "固定正文", canonicalSummary: "新摘要"));
			await embedding.InitialAttemptsFailed.Task.WaitAsync(TimeSpan.FromSeconds(5));

			MemoryItem pending = service.Get(item.Id)!;
			Assert.Null(pending.GetVector());
			Assert.Null(pending.EmbeddingFingerprint);
			Assert.Equal("新摘要", embedding.LastInput);
			Assert.True(service.EmbeddingQueueStatus.FailedBatchCount >= 1);

			embedding.ReleaseCompensation();
			await embedding.CompensationCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
			await WaitUntilAsync(() => service.EmbeddingQueueStatus.CompletedCount == 1);

			float[] vector = Assert.IsType<float[]>(service.Get(item.Id)!.GetVector());
			Assert.Equal(new float[] {0, 1}, vector);
			Assert.Null(service.EmbeddingQueueStatus.LastFailure);
		}
		finally
		{
			DeleteDatabaseFiles(path);
		}
	}

	private static ConfigStore CreateEmbeddingConfig(NoriDatabase database)
	{
		ConfigStore config = new(database);
		config.InitDefaults("test");
		config.Set("embedding_api_base", new ConfigValue.Text("http://embedding.test/v1")); // NOSONAR
		return config;
	}

	private static async Task WaitUntilAsync(Func<bool> predicate)
	{
		for (int attempt = 0; attempt < 500 && !predicate(); attempt++) await Task.Delay(10);
		Assert.True(predicate());
	}

	private static string TempPath(string name) => Path.Combine(Path.GetTempPath(), $"nori-embedding-queue-{name}-{Guid.NewGuid():N}.db");

	private static void DeleteDatabaseFiles(string path)
	{
		try
		{
			File.Delete(path);
			File.Delete($"{path}-wal");
			File.Delete($"{path}-shm");
		}
		catch (IOException) { }
	}

	private sealed class SaturatingEmbedding(int expectedInputs) : IEmbeddingAdapter
	{
		private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
		private readonly ConcurrentDictionary<string, byte> _inputs = new(StringComparer.Ordinal);
		private readonly int _expectedInputs = expectedInputs;
		private int _calls;

		public TaskCompletionSource FirstBatchStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public TaskCompletionSource AllInputsProcessed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

		public Task<float[]> GetEmbeddingAsync(string baseUrl, string apiKey, string model, string input, int? dimensions = null, CancellationToken cancellationToken = default) => Task.FromResult<float[]>([1, 0]);

		public async Task<IReadOnlyList<float[]>> GetEmbeddingsAsync(string baseUrl, string apiKey, string model, IReadOnlyList<string> inputs, int? dimensions = null, CancellationToken cancellationToken = default)
		{
			if (Interlocked.Increment(ref _calls) == 1)
			{
				FirstBatchStarted.TrySetResult();
				await _release.Task.WaitAsync(cancellationToken);
			}
			foreach (string input in inputs) _inputs.TryAdd(input, 0);
			if (_inputs.Count >= _expectedInputs) AllInputsProcessed.TrySetResult();
			return inputs.Select(_ => new float[] {1, 0}).ToList();
		}

		public void ReleaseFirstBatch() => _release.TrySetResult();
	}

	private sealed class RetryOnceEmbedding : IEmbeddingAdapter
	{
		private int _calls;
		public TaskCompletionSource Recovered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

		public Task<float[]> GetEmbeddingAsync(string baseUrl, string apiKey, string model, string input, int? dimensions = null, CancellationToken cancellationToken = default) => Task.FromResult<float[]>([1, 0]);

		public Task<IReadOnlyList<float[]>> GetEmbeddingsAsync(string baseUrl, string apiKey, string model, IReadOnlyList<string> inputs, int? dimensions = null, CancellationToken cancellationToken = default)
		{
			if (Interlocked.Increment(ref _calls) == 1) throw new InvalidOperationException("测试 Provider 暂时失败");
			Recovered.TrySetResult();
			return Task.FromResult<IReadOnlyList<float[]>>(inputs.Select(_ => new float[] {1, 0}).ToList());
		}
	}

	private sealed class MismatchOnceEmbedding : IEmbeddingAdapter
	{
		private int _calls;
		public TaskCompletionSource Recovered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

		public Task<float[]> GetEmbeddingAsync(string baseUrl, string apiKey, string model, string input, int? dimensions = null, CancellationToken cancellationToken = default) => Task.FromResult<float[]>([1, 0]);

		public Task<IReadOnlyList<float[]>> GetEmbeddingsAsync(string baseUrl, string apiKey, string model, IReadOnlyList<string> inputs, int? dimensions = null, CancellationToken cancellationToken = default)
		{
			if (Interlocked.Increment(ref _calls) == 1) return Task.FromResult<IReadOnlyList<float[]>>([]);
			Recovered.TrySetResult();
			return Task.FromResult<IReadOnlyList<float[]>>(inputs.Select(_ => new float[] {1, 0}).ToList());
		}
	}

	private sealed class CanonicalSummaryRecoveryEmbedding : IEmbeddingAdapter
	{
		private readonly TaskCompletionSource _allowCompensation = new(TaskCreationOptions.RunContinuationsAsynchronously);
		private string? _lastInput;
		private int _calls;

		public TaskCompletionSource InitialAttemptsFailed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public TaskCompletionSource CompensationCompleted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public string? LastInput => Volatile.Read(ref _lastInput);

		public Task<float[]> GetEmbeddingAsync(string baseUrl, string apiKey, string model, string input, int? dimensions = null, CancellationToken cancellationToken = default) => Task.FromResult<float[]>([0, 1]);

		public async Task<IReadOnlyList<float[]>> GetEmbeddingsAsync(string baseUrl, string apiKey, string model, IReadOnlyList<string> inputs, int? dimensions = null, CancellationToken cancellationToken = default)
		{
			Volatile.Write(ref _lastInput, inputs[0]);
			int call = Interlocked.Increment(ref _calls);
			if (call <= 3)
			{
				if (call == 3) InitialAttemptsFailed.TrySetResult();
				throw new InvalidOperationException("测试 Provider 首批失败");
			}

			await _allowCompensation.Task.WaitAsync(cancellationToken);
			CompensationCompleted.TrySetResult();
			return inputs.Select(_ => new float[] {0, 1}).ToList();
		}

		public void ReleaseCompensation() => _allowCompensation.TrySetResult();
	}
}
