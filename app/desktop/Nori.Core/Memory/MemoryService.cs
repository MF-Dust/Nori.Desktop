using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using Nori.Core.Chat;
using Nori.Core.Configuration;
using Nori.Core.Data;
using Nori.Core.Embedding;

namespace Nori.Core.Memory;

/// <summary>
/// 长期记忆 Facade。
/// 写入、检索和生命周期逻辑均通过 MemoryStore 聚合层完成，保留旧版公开 API 兼容性。
/// </summary>
public sealed class MemoryService : IAsyncDisposable
{
	public const int MaxCacheSize = 250;
	private const int EmbeddingQueueCapacity = 128;
	private const int EmbeddingBatchSize = 32;
	private const int MaxEmbeddingAttempts = 3;
	private const int MaxRecoveryBatchesPerWake = 4;
	private const string ReembedFingerprintState = "embedding_reembed_fingerprint";
	private const string ReembedCursorState = "embedding_reembed_cursor";
	private static readonly string[] MemorySettingsKeys =
	[
		"memory_enabled",
		"memory_reflection_enabled",
		"memory_reflection_rounds",
		"memory_reflection_min_chars",
		"memory_recall_top_k",
		"memory_keyword_top_k",
		"memory_vector_top_k",
		"memory_rrf_k",
		"memory_min_similarity",
		"memory_decay_enabled",
		"memory_archive_enabled",
		"memory_source_retention_threshold",
		"memory_archive_threshold",
		"memory_knowledge_enabled",
		"memory_knowledge_watch",
		"memory_debug_retrieval",
	];
	private static readonly TimeSpan EmbeddingRecoveryInterval = TimeSpan.FromSeconds(5);
	private readonly MemoryStore _store;
	private readonly IEmbeddingAdapter _embedding;
	private readonly ConfigStore _config;
	private readonly AiSettingsStore _aiSettings;
	private readonly MemoryTransferService _transfer;
	private readonly SemaphoreSlim _reembedGate = new(1, 1);
	private readonly Channel<EmbeddingJob> _embeddingQueue = Channel.CreateBounded<EmbeddingJob>(new BoundedChannelOptions(EmbeddingQueueCapacity)
	{
		// TryWrite 在队列满时返回 false, 由数据库中的未嵌入记录承担补偿入口, 不丢写入.
		FullMode = BoundedChannelFullMode.Wait,
		SingleReader = true,
		SingleWriter = false,
	});
	private readonly SemaphoreSlim _embeddingWakeup = new(0, 1);
	private readonly CancellationTokenSource _embeddingCts = new();
	private readonly Task _embeddingWorker;
	private int _disposed;
	private int _embeddingResourcesDisposed;
	private int _queueDepth;
	private int _recoveryRequested;
	private int _embeddingState = (int)MemoryEmbeddingQueueState.Stopped;
	private long _enqueuedCount;
	private long _saturatedCount;
	private long _attemptCount;
	private long _completedCount;
	private long _failedBatchCount;
	private long _countMismatchCount;
	private long _recoveryBatchCount;
	private string? _lastFailure;

	public MemoryService(
		MemoryStore store,
		IEmbeddingAdapter embedding,
		ConfigStore config,
		bool startBackgroundWorker = true)
	{
		_store = store;
		_embedding = embedding;
		_config = config;
		_aiSettings = new AiSettingsStore(config);
		_transfer = new MemoryTransferService(_store, queueEmbedding: QueueTransferEmbedding);
		if (startBackgroundWorker)
		{
			SetEmbeddingState(MemoryEmbeddingQueueState.Waiting);
			_embeddingWorker = Task.Run(ProcessEmbeddingQueueAsync);
		}
		else
		{
			_embeddingWorker = Task.CompletedTask;
		}
	}

	public MemoryStore Store => _store;

	/// <summary>读取后台向量队列的脱敏状态, 不包含记忆正文。</summary>
	public MemoryEmbeddingQueueStatus EmbeddingQueueStatus => new()
	{
		State = (MemoryEmbeddingQueueState)Volatile.Read(ref _embeddingState),
		QueueDepth = Math.Max(0, Volatile.Read(ref _queueDepth)),
		EnqueuedCount = Volatile.Read(ref _enqueuedCount),
		SaturatedCount = Volatile.Read(ref _saturatedCount),
		AttemptCount = Volatile.Read(ref _attemptCount),
		CompletedCount = Volatile.Read(ref _completedCount),
		FailedBatchCount = Volatile.Read(ref _failedBatchCount),
		CountMismatchCount = Volatile.Read(ref _countMismatchCount),
		RecoveryBatchCount = Volatile.Read(ref _recoveryBatchCount),
		LastFailure = Volatile.Read(ref _lastFailure),
	};

	/// <summary>记忆迁移内核；提交成功后才把新文本排入后台 Embedding 队列。</summary>
	public MemoryTransferService Transfer => _transfer;

	/// <summary>导出 nori-memory-v1 安全文档。</summary>
	public MemoryTransferExport ExportTransfer() => _transfer.ExportResult();

	/// <summary>解析 nori-memory-v1 文件而不写入数据库。</summary>
	public MemoryTransferPreview PreviewTransfer(string? content) => _transfer.Preview(content);

	/// <summary>使用一次性预览令牌提交 nori-memory-v1 导入。</summary>
	public MemoryTransferCommitResult CommitTransfer(string? previewToken, MemoryTransferConflictStrategy strategy = MemoryTransferConflictStrategy.Skip) =>
		_transfer.Commit(previewToken, strategy);

	/// <summary>独立 ARG 知识服务，由宿主装配后回填。</summary>
	public KnowledgeService? Knowledge { get; set; }

	/// <summary>读取记忆运行设置。</summary>
	public MemorySettings Settings
	{
		get
		{
			IReadOnlyDictionary<string, ConfigValue> values = _config.GetMany(MemorySettingsKeys);
			ConfigValue? Find(string key) => values.TryGetValue(key, out ConfigValue? value) ? value : null;
			bool Bool(string key, bool fallback) => ConfigStore.GetBoolOr(Find(key), fallback);
			int Int(string key, int fallback, int min, int max) => ConfigStore.GetClampedInt(Find(key), fallback, min, max);
			double Double(string key, double fallback, double min, double max) => ConfigStore.GetClampedDouble(Find(key), fallback, min, max);

			return new MemorySettings
			{
				Enabled = Bool("memory_enabled", true),
				ReflectionEnabled = Bool("memory_reflection_enabled", true),
				ReflectionRounds = Int("memory_reflection_rounds", 8, 1, 32),
				ReflectionMinChars = Int("memory_reflection_min_chars", 2500, 100, 20000),
				RecallTopK = Int("memory_recall_top_k", 6, 1, 20),
				KeywordTopK = Int("memory_keyword_top_k", 20, 1, 100),
				VectorTopK = Int("memory_vector_top_k", 20, 1, 100),
				RrfK = Int("memory_rrf_k", 60, 1, 500),
				MinSimilarity = Double("memory_min_similarity", 0.25, 0, 1),
				DecayEnabled = Bool("memory_decay_enabled", true),
				ArchiveEnabled = Bool("memory_archive_enabled", true),
				SourceRetentionThreshold = Double("memory_source_retention_threshold", 0.75, 0, 1),
				ArchiveThreshold = Double("memory_archive_threshold", 0.15, 0, 1),
				KnowledgeEnabled = Bool("memory_knowledge_enabled", true),
				KnowledgeWatch = Bool("memory_knowledge_watch", true),
				DebugRetrieval = Bool("memory_debug_retrieval", false),
			};
		}
	}

	/// <summary>解析独立 Embedding 接入配置, 不从聊天配置回退。</summary>
	public (string BaseUrl, string ApiKey, string Model, int? Dimensions) ResolveConfig()
	{
		AiEmbeddingSettings embedding = _aiSettings.Read().Embedding;
		return (embedding.BaseUrl, embedding.ApiKey, embedding.Model, embedding.Dimensions);
	}

	/// <summary>当前 Embedding 配置指纹，不包含 API Key。</summary>
	public bool EmbeddingConfigured => _aiSettings.Read().Embedding.IsConfigured;

	public string ResolveEmbeddingFingerprint()
	{
		(string baseUrl, _, string model, int? dimensions) = ResolveConfig();
		string authority = baseUrl.TrimEnd('/');
		if (Uri.TryCreate(authority, UriKind.Absolute, out Uri? uri))
		{
			authority = $"{uri.Scheme.ToLowerInvariant()}://{uri.Authority.ToLowerInvariant()}{uri.AbsolutePath.TrimEnd('/')}";
		}
		string input = $"openai-compatible\n{authority}\n{model.Trim()}\n{dimensions?.ToString() ?? ""}";
		return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input)));
	}

	/// <summary>获取文本向量；任何远程失败均返回 null。</summary>
	public Task<float[]?> EmbedAsync(string text) => EmbedAsync(text, CancellationToken.None);

	public async Task<float[]?> EmbedAsync(string text, CancellationToken cancellationToken)
	{
		string trimmed = text.Trim();
		if (trimmed.Length == 0 || !EmbeddingConfigured) return null;
		try
		{
			(string baseUrl, string apiKey, string model, int? dimensions) = ResolveConfig();
			float[] vector = await _embedding.GetEmbeddingAsync(baseUrl, apiKey, model, trimmed, dimensions, cancellationToken).ConfigureAwait(false);
			return vector.Length == 0 ? null : vector;
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			throw;
		}
		catch
		{
			return null;
		}
	}

	/// <summary>3 秒查询向量超时，确保聊天不会被 Embedding 服务拖住。</summary>
	private async Task<float[]?> EmbedForRecallAsync(string text, CancellationToken cancellationToken)
	{
		using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		timeout.CancelAfter(TimeSpan.FromSeconds(3));
		try { return await EmbedAsync(text, timeout.Token).ConfigureAwait(false); }
		catch (OperationCanceledException) { return null; }
	}

	public void ClearCache() => _embedding.ClearCache();

	/// <summary>添加长期记忆，先保证文本与 Atom 落库，再排队生成向量。</summary>
	public Task<MemoryItem> AddAsync(
		string content,
		string type = "general",
		double importance = 0.5,
		string? tags = null,
		string source = "chat",
		MemoryKind? kind = null,
		string? canonicalSummary = null,
		string? personaSummary = null,
		double confidence = 0.8,
		IReadOnlyList<MemorySource>? sources = null,
		string? embeddingText = null)
	{
		if (string.IsNullOrWhiteSpace(content)) throw new ArgumentException("记忆内容不能为空", nameof(content));
		MemoryKind resolvedKind = kind ?? MemoryKindExtensions.Parse(type);
		double? ttl = DefaultTtl(resolvedKind);
		MemoryItem item = _store.AddAggregate(type, content, importance, source, tags, null, resolvedKind,
			canonicalSummary, personaSummary, confidence, ttl, null, null, sources);
		QueueEmbedding(item.Id, embeddingText ?? canonicalSummary ?? content, item.UpdatedAt);
		return Task.FromResult(item);
	}

	/// <summary>用户或 Agent 明确要求记住时使用的入口，先做同类型精确去重和强化。</summary>
	public async Task<MemoryItem> RememberAsync(string content, MemoryKind kind = MemoryKind.Factual, double importance = 0.8, string? tags = null, string source = "agent")
	{
		string normalized = Normalize(content);
		foreach (RetrievalHit hit in _store.SearchKeyword(content, 5))
		{
			MemoryItem? existing = _store.Get(hit.MemoryId);
			if (existing is null || MemoryKindExtensions.Parse(existing.Kind) != kind) continue;
			if (Normalize(existing.CanonicalSummary ?? existing.Content) == normalized)
			{
				_store.Reinforce(existing.Id);
				return existing;
			}
		}
		return await AddAsync(content, kind.ToStorage(), importance, tags, source, kind).ConfigureAwait(false);
	}

	/// <summary>更新文本时先清除旧向量，再把新向量放入后台队列。</summary>
	public Task<bool> UpdateAsync(long id, string content, double? importance = null, string? tags = null,
		MemoryKind? kind = null, string? canonicalSummary = null, string? personaSummary = null, double? confidence = null)
	{
		if (string.IsNullOrWhiteSpace(content)) throw new ArgumentException("记忆内容不能为空", nameof(content));
		MemoryItem? before = _store.Get(id);
		if (before is null) return Task.FromResult(false);
		MemoryKind resolvedKind = kind ?? MemoryKindExtensions.Parse(before.Kind);
		bool updated = _store.Update(id, content, importance, tags, kind: kind,
			canonicalSummary: canonicalSummary, personaSummary: personaSummary, confidence: confidence);
		if (updated)
		{
			string oldSummary = before.CanonicalSummary ?? before.Content;
			MemoryAtom? defaultAtom = _store.GetAtoms(id, null, 100, 0).FirstOrDefault(atom => atom.Content == oldSummary);
			if (defaultAtom is not null)
			{
				_store.UpdateAtom(defaultAtom.Id, canonicalSummary ?? content, resolvedKind, importance, confidence);
			}
			MemoryItem? current = _store.Get(id);
			if (current is not null) QueueEmbedding(id, current.CanonicalSummary ?? current.Content, current.UpdatedAt);
		}
		return Task.FromResult(updated);
	}

	/// <summary>真正的关键词 + 向量混合检索。</summary>
	public async Task<IReadOnlyList<MemoryItem>> SearchHybridAsync(string keyword, int limit = 10, CancellationToken cancellationToken = default)
	{
		if (!Settings.Enabled) return [];
		MemoryContext context = await BuildContextAsync(keyword, [], cancellationToken, false, false, Math.Clamp(limit, 1, 100)).ConfigureAwait(false);
		return context.Personal;
	}

	/// <summary>构建分层 MemoryContext，并只强化最终实际注入的记忆。</summary>
	public async Task<MemoryContext> BuildContextAsync(
		string userText,
		IReadOnlyList<(string Role, string Content)> recentMessages,
		CancellationToken cancellationToken = default,
		bool includeDebug = false,
		bool markAccess = true,
		int? personalLimitOverride = null)
	{
		MemorySettings settings = Settings;
		if (!settings.Enabled) return new MemoryContext();
		string expanded = MemoryQueryBuilder.Build(userText, recentMessages);
		float[]? vector = await EmbedForRecallAsync(expanded, cancellationToken).ConfigureAwait(false);
		IReadOnlyList<RetrievalHit> keywordHits = _store.SearchKeyword(expanded, settings.KeywordTopK);
		IReadOnlyList<RetrievalHit> vectorHits = vector is null
			? []
			: _store.SearchSemantic(vector, settings.VectorTopK, settings.MinSimilarity)
			.Select((hit, index) => new RetrievalHit(hit.Item.Id, hit.Similarity, index + 1)).ToList();
		IReadOnlyList<RetrievalHit> atomHits = _store.SearchAtomKeyword(expanded, 10);
		Dictionary<long, MemoryAtom> atomsById = _store.GetAtomsByIds(atomHits.Select(hit => hit.MemoryId).ToArray());
		IReadOnlyList<RetrievalHit> atomParentHits = atomHits
			.Select((hit, index) => (Hit: hit, Rank: index + 1))
			.Where(pair => atomsById.ContainsKey(pair.Hit.MemoryId))
			.Select(pair => new RetrievalHit(atomsById[pair.Hit.MemoryId].ParentMemoryId, 1.0 / pair.Rank, pair.Rank))
			.ToList();
		IReadOnlyList<RetrievalHit> fused = RrfFusion.Fuse([keywordHits, vectorHits, atomParentHits], settings.RrfK);
		Dictionary<long, double> scores = fused.ToDictionary(hit => hit.MemoryId, hit => hit.Score);
		Dictionary<long, MemoryItem> candidates = _store.GetMany(scores.Keys.ToArray());

		DateTimeOffset now = DateTimeOffset.UtcNow;
		List<(MemoryItem Item, double Score)> ranked = scores
			.Where(pair => candidates.ContainsKey(pair.Key))
			.Select(pair => (Item: candidates[pair.Key], Rrf: pair.Value))
			.Select(pair => (Item: pair.Item, Score: DecayCalculator.FinalScore(pair.Rrf, pair.Item, now, settings.DecayEnabled)))
			.Where(pair => (pair.Item.Status is "active" or "dormant") && pair.Score > 0)
			.OrderByDescending(pair => pair.Score)
			.ToList();

		List<MemoryItem> personal = TakeWithinBudget(ranked, personalLimitOverride ?? settings.RecallTopK, 900);
		long[] injectedIds = personal.Select(item => item.Id).ToArray();
		if (markAccess) _store.MarkAccessed(injectedIds);
		List<MemoryAtom> atoms = [.. _store.GetActiveAtomsByParents(injectedIds, 3)];
		IReadOnlyList<RetrievedKnowledge> knowledge = TakeKnowledgeBudget(Knowledge?.Search(userText, 4) ?? [], 2200);
		IReadOnlyList<MemoryEcho> echoes = (Knowledge?.SearchEchoes(userText, 2) ?? [])
			.Select(echo => echo with {Content = echo.Content.Length > 320 ? echo.Content[..320] : echo.Content})
			.Take(2)
			.ToList();

		RecallDebugTrace? debug = settings.DebugRetrieval || includeDebug
			? new RecallDebugTrace
			{
				Query = userText,
				ExpandedQuery = expanded,
				KeywordHits = keywordHits,
				VectorHits = vectorHits,
				AtomHits = atomHits,
				RrfHits = fused,
				FilteredIds = ranked.Select(pair => pair.Item.Id).Except(injectedIds).ToArray(),
				InjectedIds = injectedIds,
			}
			: null;
		return new MemoryContext {Personal = personal, Atoms = atoms, Knowledge = knowledge, Echoes = echoes, Debug = debug};
	}

	/// <summary>重嵌入连续失败多少轮后才升级为 Error 遥测; 单轮失败只降级为诊断日志。</summary>
	public const int ReembedEscalationThreshold = 5;

	/// <summary>嵌入批处理恢复期的低噪声诊断回调 (severity, message); 宿主接到日志器, 不进 Error 遥测。</summary>
	public Action<string, string>? EmbeddingDiagnostic { get; set; }

	private int _consecutiveReembedFailures;

	/// <summary>按 fingerprint 批量重建向量，进度持久化后可取消并从游标恢复。</summary>
	public async Task<int> ReembedAllAsync(CancellationToken cancellationToken = default, bool force = true)
	{
		if (!EmbeddingConfigured)
		{
			SetEmbeddingState(MemoryEmbeddingQueueState.Disabled);
			return 0;
		}
		await _reembedGate.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			string fingerprint = ResolveEmbeddingFingerprint();
			string? savedFingerprint = _store.GetEngineState(ReembedFingerprintState);
			long afterId = savedFingerprint == fingerprint && long.TryParse(_store.GetEngineState(ReembedCursorState), out long savedCursor)
				? Math.Max(0, savedCursor)
				: 0;
			_store.SetEngineState(ReembedFingerprintState, fingerprint);
			if (!string.Equals(savedFingerprint, fingerprint, StringComparison.Ordinal)) SaveReembedCursor(0);

			int count = 0;
			while (true)
			{
				cancellationToken.ThrowIfCancellationRequested();
				IReadOnlyList<MemoryEmbeddingWorkItem> page = _store.GetReembedWork(fingerprint, EmbeddingBatchSize, afterId, force);
				if (page.Count == 0) break;
				EmbeddingJob[] batch = page.Select(item => new EmbeddingJob(item.Id, item.Text, item.UpdatedAt)).ToArray();
				int? completed = await TryEmbedBatchWithRetryAsync(batch, cancellationToken, fingerprint).ConfigureAwait(false);
				if (completed is null)
				{
					// 游标停在上一页, 下次运行会重新补偿这一批, 不把失败项推进到永久盲区.
					int failures = Interlocked.Increment(ref _consecutiveReembedFailures);
					if (failures >= ReembedEscalationThreshold)
					{
						throw new InvalidOperationException($"向量批处理连续 {failures} 轮失败, 待处理记忆将在下次重试");
					}
					// 单轮失败降级: 不抛错 (避免每次都成为 Error 遥测), 状态经 EmbeddingQueueStatus
					// (Degraded) 暴露给前端, 连续多轮无进度才升级为 Error。
					EmbeddingDiagnostic?.Invoke("warn",
						$"向量批处理失败, 待处理记忆将在下次重试 (pending_batch_count={Volatile.Read(ref _queueDepth)}, 连续失败={failures})");
					return count;
				}
				count += completed.Value;
				afterId = page[^1].Id;
				SaveReembedCursor(afterId);
			}
			Interlocked.Exchange(ref _consecutiveReembedFailures, 0);
			SaveReembedCursor(0);
			return count;
		}
		finally
		{
			_reembedGate.Release();
		}
	}

	public bool Archive(long id) => _store.Archive(id);
	public bool Restore(long id) => _store.Restore(id);
	public bool Delete(long id) => _store.Delete(id);
	public void Clear() => _store.Clear();
	public MemoryItem? Get(long id) => _store.Get(id);
	public IReadOnlyList<MemoryAtom> GetAtoms(long? parentId = null, MemoryStatus? status = null, int limit = 100, int offset = 0) => _store.GetAtoms(parentId, status, limit, offset);
	public IReadOnlyList<MemorySource> GetSources(long memoryId) => _store.GetSources(memoryId);
	public (int Active, int Atoms, int Archived, int Total) GetOverview() => _store.GetOverview();

	private void QueueEmbedding(long id, string text, string updatedAt)
	{
		if (Volatile.Read(ref _disposed) != 0 || string.IsNullOrWhiteSpace(text)) return;
		EmbeddingJob job = new(id, text, updatedAt);
		Interlocked.Increment(ref _queueDepth);
		bool accepted;
		try { accepted = _embeddingQueue.Writer.TryWrite(job); }
		catch { accepted = false; }
		if (accepted)
		{
			Interlocked.Increment(ref _enqueuedCount);
			SignalEmbeddingWorker();
			return;
		}

		Interlocked.Decrement(ref _queueDepth);
		if (Volatile.Read(ref _disposed) != 0) return;
		Interlocked.Increment(ref _saturatedCount);
		RequestEmbeddingRecovery();
	}

	private void QueueTransferEmbedding(MemoryEmbeddingWorkItem work) => QueueEmbedding(work.Id, work.Text, work.UpdatedAt);

	private void RequestEmbeddingRecovery()
	{
		if (Volatile.Read(ref _disposed) != 0) return;
		Interlocked.Exchange(ref _recoveryRequested, 1);
		SignalEmbeddingWorker();
	}

	private void SignalEmbeddingWorker()
	{
		try { _embeddingWakeup.Release(); }
		catch (SemaphoreFullException) { }
		catch (ObjectDisposedException) { }
	}

	private async Task ProcessEmbeddingQueueAsync()
	{
		try
		{
			while (true)
			{
				bool signaled = await _embeddingWakeup.WaitAsync(EmbeddingRecoveryInterval, _embeddingCts.Token).ConfigureAwait(false);
				if (!signaled) Interlocked.Exchange(ref _recoveryRequested, 1);

				do
				{
					while (TryTakeEmbeddingJob(out EmbeddingJob first))
					{
						List<EmbeddingJob> batch = [first];
						while (batch.Count < EmbeddingBatchSize && TryTakeEmbeddingJob(out EmbeddingJob next)) batch.Add(next);
						SetEmbeddingState(MemoryEmbeddingQueueState.Processing);
						int? completed = await TryEmbedBatchWithRetryAsync(batch, _embeddingCts.Token).ConfigureAwait(false);
						if (completed is null) RequestEmbeddingRecovery();
					}

					if (Volatile.Read(ref _queueDepth) == 0
						&& Interlocked.Exchange(ref _recoveryRequested, 0) != 0)
					{
						await RecoverUnembeddedAsync(_embeddingCts.Token).ConfigureAwait(false);
					}
				}
				while (Volatile.Read(ref _queueDepth) > 0 || Volatile.Read(ref _recoveryRequested) != 0);

				MemoryEmbeddingQueueState state = (MemoryEmbeddingQueueState)Volatile.Read(ref _embeddingState);
				if (state != MemoryEmbeddingQueueState.Degraded)
				{
					SetEmbeddingState(EmbeddingConfigured ? MemoryEmbeddingQueueState.Waiting : MemoryEmbeddingQueueState.Disabled);
				}
			}
		}
		catch (OperationCanceledException) when (_embeddingCts.IsCancellationRequested) { }
		catch { RecordEmbeddingFailure("worker_failure"); }
		finally { SetEmbeddingState(MemoryEmbeddingQueueState.Stopped); }
	}

	private bool TryTakeEmbeddingJob(out EmbeddingJob job)
	{
		if (_embeddingQueue.Reader.TryRead(out EmbeddingJob? next) && next is not null)
		{
			Interlocked.Decrement(ref _queueDepth);
			job = next;
			return true;
		}
		job = null!;
		return false;
	}

	private async Task RecoverUnembeddedAsync(CancellationToken cancellationToken)
	{
		if (!EmbeddingConfigured)
		{
			SetEmbeddingState(MemoryEmbeddingQueueState.Disabled);
			return;
		}

		string fingerprint = ResolveEmbeddingFingerprint();
		for (int index = 0; index < MaxRecoveryBatchesPerWake; index++)
		{
			cancellationToken.ThrowIfCancellationRequested();
			IReadOnlyList<MemoryItem> pending;
			try { pending = _store.GetUnembedded(EmbeddingBatchSize, fingerprint: fingerprint); }
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
			catch
			{
				RecordEmbeddingFailure("database_failure");
				return;
			}
			if (pending.Count == 0) return;

			Interlocked.Increment(ref _recoveryBatchCount);
			EmbeddingJob[] batch = pending
				.Select(item => new EmbeddingJob(item.Id, item.CanonicalSummary ?? item.Content, item.UpdatedAt))
			.ToArray();
			int? completed = await TryEmbedBatchWithRetryAsync(batch, cancellationToken).ConfigureAwait(false);
			if (completed is null || completed == 0) return;
		}
	}

	private async Task<int?> TryEmbedBatchWithRetryAsync(
		IReadOnlyList<EmbeddingJob> batch,
		CancellationToken cancellationToken,
		string? fingerprint = null)
	{
		if (batch.Count == 0) return 0;
		if (!EmbeddingConfigured)
		{
			SetEmbeddingState(MemoryEmbeddingQueueState.Disabled);
			return null;
		}

		for (int attempt = 0; attempt < MaxEmbeddingAttempts; attempt++)
		{
			if (!EmbeddingConfigured)
			{
				SetEmbeddingState(MemoryEmbeddingQueueState.Disabled);
				return null;
			}
			Interlocked.Increment(ref _attemptCount);
			try
			{
				SetEmbeddingState(MemoryEmbeddingQueueState.Processing);
				int completed = await EmbedAndStoreBatchAsync(batch, cancellationToken, fingerprint).ConfigureAwait(false);
				Interlocked.Add(ref _completedCount, completed);
				Volatile.Write(ref _lastFailure, null);
				SetEmbeddingState(MemoryEmbeddingQueueState.Waiting);
				return completed;
			}
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
			{
				throw;
			}
			catch (EmbeddingBatchException exception)
			{
				RecordEmbeddingFailure(exception.Reason);
			}
			catch
			{
				RecordEmbeddingFailure("provider_failure");
			}

			if (attempt + 1 < MaxEmbeddingAttempts)
			{
				await Task.Delay(TimeSpan.FromMilliseconds(100 * (attempt + 1)), cancellationToken).ConfigureAwait(false);
			}
		}

		Interlocked.Increment(ref _failedBatchCount);
		SetEmbeddingState(MemoryEmbeddingQueueState.Degraded);
		return null;
	}

	private async Task<int> EmbedAndStoreBatchAsync(
		IReadOnlyList<EmbeddingJob> batch,
		CancellationToken cancellationToken,
		string? fingerprint = null)
	{
		if (batch.Count == 0) return 0;
		if (!EmbeddingConfigured) throw new EmbeddingBatchException("disabled");
		cancellationToken.ThrowIfCancellationRequested();
		(string baseUrl, string apiKey, string model, int? dimensions) = ResolveConfig();
		IReadOnlyList<float[]> vectors = await _embedding.GetEmbeddingsAsync(baseUrl, apiKey, model,
			batch.Select(job => job.Text).ToArray(), dimensions, cancellationToken).ConfigureAwait(false);
		cancellationToken.ThrowIfCancellationRequested();
		if (vectors is null || vectors.Count != batch.Count) throw new EmbeddingBatchException("count_mismatch");

		List<MemoryEmbeddingUpdate> updates = [];
		for (int index = 0; index < batch.Count; index++)
		{
			float[] vector = vectors[index];
			if (vector.Length == 0 || vector.Any(value => !float.IsFinite(value)))
				throw new EmbeddingBatchException("invalid_vector");
			updates.Add(new MemoryEmbeddingUpdate {Id = batch[index].Id, UpdatedAt = batch[index].UpdatedAt, Vector = vector});
		}
		cancellationToken.ThrowIfCancellationRequested();
		return _store.UpdateEmbeddings(updates, fingerprint ?? ResolveEmbeddingFingerprint());
	}

	private void RecordEmbeddingFailure(string reason)
	{
		if (reason == "count_mismatch") Interlocked.Increment(ref _countMismatchCount);
		Volatile.Write(ref _lastFailure, reason);
		SetEmbeddingState(MemoryEmbeddingQueueState.Degraded);
	}

	private void SetEmbeddingState(MemoryEmbeddingQueueState state) => Volatile.Write(ref _embeddingState, (int)state);

	private void SaveReembedCursor(long cursor) => _store.SetEngineState(ReembedCursorState, cursor.ToString(System.Globalization.CultureInfo.InvariantCulture));

	public async ValueTask DisposeAsync()
	{
		if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
		_embeddingQueue.Writer.TryComplete();
		_embeddingCts.Cancel();
		try { await _embeddingWorker.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
		catch (OperationCanceledException) { }
		catch (TimeoutException) { }
		_transfer.Dispose();
		if (_embeddingWorker.IsCompleted) DisposeEmbeddingResources();
		else _ = DisposeEmbeddingResourcesWhenWorkerStopsAsync(_embeddingWorker);
		_reembedGate.Dispose();
	}

	private void DisposeEmbeddingResources()
	{
		if (Interlocked.Exchange(ref _embeddingResourcesDisposed, 1) != 0) return;
		_embeddingWakeup.Dispose();
		_embeddingCts.Dispose();
	}

	[System.Diagnostics.CodeAnalysis.SuppressMessage("CodeQuality", "S2486", Justification = "后台资源清理失败不能覆盖工作线程异常。")]
	private async Task DisposeEmbeddingResourcesWhenWorkerStopsAsync(Task worker)
	{
		try { await worker.ConfigureAwait(false); }
		catch { }
		finally { DisposeEmbeddingResources(); }
	}

	private sealed record EmbeddingJob(long Id, string Text, string UpdatedAt);

	private sealed class EmbeddingBatchException : InvalidOperationException
	{
		public EmbeddingBatchException(string reason) : base(reason) => Reason = reason;
		public string Reason { get; }
	}

	private static string Normalize(string value) => string.Join(' ', value.Trim().ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

	private static double? DefaultTtl(MemoryKind kind)
	{
		double value = DecayCalculator.HalfLifeDays(kind);
		return double.IsPositiveInfinity(value) ? null : value;
	}

	private static IReadOnlyList<RetrievedKnowledge> TakeKnowledgeBudget(IReadOnlyList<RetrievedKnowledge> source, int budget)
	{
		List<RetrievedKnowledge> result = [];
		int used = 0;
		foreach (RetrievedKnowledge item in source)
		{
			int size = Math.Max(1, item.Content.Length / 2);
			if (result.Count > 0 && used + size > budget) continue;
			result.Add(item);
			used += size;
		}
		return result;
	}

	private static List<MemoryItem> TakeWithinBudget(IReadOnlyList<(MemoryItem Item, double Score)> ranked, int limit, int budget)
	{
		List<MemoryItem> result = [];
		int used = 0;
		foreach ((MemoryItem item, _) in ranked)
		{
			if (result.Count >= Math.Max(0, limit)) break;
			string text = item.PersonaSummary ?? item.CanonicalSummary ?? item.Content;
			int size = Math.Max(1, text.Length / 2);
			if (result.Count > 0 && used + size > budget) continue;
			result.Add(item);
			used += size;
		}
		return result;
	}
}
