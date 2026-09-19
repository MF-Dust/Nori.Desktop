using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Reflection;
using Microsoft.Data.Sqlite;
using Nori.Core.Configuration;
using Nori.Core.Data;

namespace Nori.Core.Memory;

/// <summary>独立于个人记忆的 Memory.md 知识服务。</summary>
public sealed class KnowledgeService : IAsyncDisposable
{
	private const string DocumentIdentifier = "nori://knowledge/Memory.md";
	private readonly NoriDatabase _database;
	private readonly MemoryService _memory;
	private readonly ConfigStore _config;
	private readonly string? _defaultPath;
	private readonly Lock _gate = new();
	private FileSystemWatcher? _watcher;
	private CancellationTokenSource? _watchDebounce;
	private Task? _watchTask;
	private MemoryIndexStatus _status = new();
	private int _disposed;

	public KnowledgeService(NoriDatabase database, MemoryService memory, ConfigStore config, string? defaultPath = null)
	{
		_database = database;
		_memory = memory;
		_config = config;
		_defaultPath = defaultPath;
		EnsureKnowledgeFts();
	}

	public string Path => ResolvePath();
	public Action? StatusChanged { get; set; }
	public MemoryIndexStatus Status { get { lock (_gate) return _status; } }

	/// <summary>首次启动复制程序集内置文档，绝不覆盖用户版本。</summary>
	public string EnsureDefaultFile()
	{
		string path = ResolvePath();
		Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
		if (File.Exists(path)) return path;
		using Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Nori.Core.Memory.md")
			?? throw new InvalidOperationException("找不到内置 Memory.md");
		using StreamReader reader = new(stream);
		File.WriteAllText(path, reader.ReadToEnd(), new UTF8Encoding(false));
		return path;
	}

	/// <summary>按整文件 hash 和稳定 chunk key 增量重建知识索引。</summary>
	public async Task<MemoryIndexStatus> ReindexAsync(CancellationToken cancellationToken = default)
	{
		if (!_memory.Settings.KnowledgeEnabled) return Status;
		SetStatus(new MemoryIndexStatus {State = MemoryIndexState.Checking});
		string path = EnsureDefaultFile();
		string content;
		try { content = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false); }
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			SetStatus(new MemoryIndexStatus {State = MemoryIndexState.Failed, LastError = exception.Message});
			throw;
		}
		IReadOnlyList<MarkdownChunker.Chunk> chunks;
		try { chunks = MarkdownChunker.Parse(content); }
		catch (Exception exception)
		{
			SetStatus(new MemoryIndexStatus {State = MemoryIndexState.Failed, LastError = exception.Message});
			throw;
		}
		string documentHash = Hash(content);
		KnowledgeDocument? existing = GetDocument(DocumentIdentifier);
		Dictionary<string, KnowledgeChunkRow> old = GetChunks(existing?.Id).ToDictionary(row => row.ChunkKey, StringComparer.Ordinal);
		bool keysCurrent = old.Count == chunks.Count && chunks.All(chunk => old.ContainsKey(chunk.ChunkKey));
		if (existing?.ContentHash == documentHash && keysCurrent)
		{
			SetStatus(new MemoryIndexStatus {State = MemoryIndexState.Ready, Processed = chunks.Count, Total = chunks.Count});
			return Status;
		}

		SetStatus(new MemoryIndexStatus {State = MemoryIndexState.Rebuilding, Total = chunks.Count});

		try
		{
			_database.Locked(connection =>
			{
				using SqliteTransaction transaction = connection.BeginTransaction();
				long documentId = UpsertDocument(connection, transaction, DocumentIdentifier, documentHash);
				HashSet<string> keys = chunks.Select(chunk => chunk.ChunkKey).ToHashSet(StringComparer.Ordinal);
				foreach (KnowledgeChunkRow row in old.Values.Where(row => !keys.Contains(row.ChunkKey)))
				{
					using SqliteCommand delete = connection.CreateCommand();
					delete.Transaction = transaction;
					delete.CommandText = "DELETE FROM knowledge_chunks WHERE id = $id";
					AddParameter(delete, "$id", row.Id);
					delete.ExecuteNonQuery();
				}
				foreach (MarkdownChunker.Chunk chunk in chunks)
				{
					using SqliteCommand upsert = connection.CreateCommand();
					upsert.Transaction = transaction;
					upsert.CommandText = """
						INSERT INTO knowledge_chunks(document_id, chunk_key, sequence, heading, subheading, content, knowledge_type, awareness, content_hash, updated_at)
						VALUES ($document, $key, $sequence, $heading, $subheading, $content, $type, $awareness, $hash, $updated)
						ON CONFLICT(document_id, chunk_key) DO UPDATE SET
						sequence = excluded.sequence, heading = excluded.heading, subheading = excluded.subheading,
						content = excluded.content, knowledge_type = excluded.knowledge_type, awareness = excluded.awareness,
						content_hash = excluded.content_hash, updated_at = excluded.updated_at
						""";
					AddParameter(upsert, "$document", documentId);
					AddParameter(upsert, "$key", chunk.ChunkKey);
					AddParameter(upsert, "$sequence", chunk.Sequence);
					AddParameter(upsert, "$heading", chunk.Heading);
					AddParameter(upsert, "$subheading", chunk.Subheading);
					AddParameter(upsert, "$content", chunk.Content);
					AddParameter(upsert, "$type", chunk.KnowledgeType);
					AddParameter(upsert, "$awareness", chunk.Awareness.ToStorage());
					AddParameter(upsert, "$hash", chunk.ContentHash);
					AddParameter(upsert, "$updated", DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture));
					upsert.ExecuteNonQuery();
				}
				RebuildKnowledgeFts(connection, transaction);
				transaction.Commit();
			});
		}
		catch
		{
			SetStatus(new MemoryIndexStatus {State = MemoryIndexState.Partial, LastError = "知识索引写入失败, 已保留上一代索引"});
			throw;
		}
		SetStatus(new MemoryIndexStatus
		{
			State = MemoryIndexState.Ready,
			Processed = chunks.Count,
			Total = chunks.Count,
			LastMaintenanceAt = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture),
		});
		return Status;
	}

	/// <summary>按当前问题检索知识，并应用 Lore awareness gate。</summary>
	public IReadOnlyList<RetrievedKnowledge> Search(string query, int limit = 4)
	{
		bool lore = IsLoreQuery(query);
		HashSet<KnowledgeAwareness> allowed = lore
			? Enum.GetValues<KnowledgeAwareness>().ToHashSet()
			: [KnowledgeAwareness.NoriKnows, KnowledgeAwareness.Recovered];
		IReadOnlyList<KnowledgeRow> rows = SearchRows(query, Math.Max(limit * 4, 12));
		return rows.Where(row => row.Awareness != KnowledgeAwareness.UserSharedMemory && allowed.Contains(row.Awareness))
			.Select((row, index) => new RetrievedKnowledge
			{
				Id = row.Id,
				Heading = row.Heading ?? "Memory",
				Subheading = row.Subheading,
				Content = row.Content,
				Awareness = row.Awareness,
				KnowledgeType = row.KnowledgeType,
				Score = 1.0 / (index + 1),
			})
			.Take(Math.Max(0, limit)).ToList();
	}

	/// <summary>将 Echo 转换为不声称亲历的短提示。</summary>
	public IReadOnlyList<MemoryEcho> SearchEchoes(string query, int limit = 2) => SearchRows(query, Math.Max(limit * 4, 8))
		.Where(row => row.Awareness == KnowledgeAwareness.NoriEcho)
		.Take(Math.Max(0, limit))
		.Select((row, index) => new MemoryEcho
		{
			Content = $"与“{row.Heading ?? "Memory"}”相关的资料可能让 Nori 产生熟悉或复杂情绪，但不能作为明确的亲历记忆：{row.Content}",
			Score = 1.0 / (index + 1),
		})
		.ToList();

	public void StartWatcher()
	{
		if (!_memory.Settings.KnowledgeWatch || _watcher is not null) return;
		string path = EnsureDefaultFile();
		_watcher = new FileSystemWatcher(System.IO.Path.GetDirectoryName(path)!, System.IO.Path.GetFileName(path))
		{
			NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
			EnableRaisingEvents = true,
		};
		_watcher.Changed += OnFileChanged;
		_watcher.Created += OnFileChanged;
		_watcher.Renamed += OnFileChanged;
	}

	private void OnFileChanged(object sender, FileSystemEventArgs args)
	{
		CancellationTokenSource next = new();
		CancellationTokenSource? previous;
		lock (_gate)
		{
			previous = _watchDebounce;
			_watchDebounce = next;
		}
		previous?.Cancel();
		_watchTask = Task.Run(async () =>
		{
			try
			{
				await Task.Delay(750, next.Token).ConfigureAwait(false);
				await ReindexAsync(next.Token).ConfigureAwait(false);
			}
			catch (OperationCanceledException) { }
			catch (Exception exception) { SetStatus(new MemoryIndexStatus {State = MemoryIndexState.Failed, LastError = exception.Message}); }
			finally { next.Dispose(); }
		});
	}

	private string ResolvePath()
	{
		string configured = _config.GetStringOr("memory_knowledge_path", "").Trim();
		return configured.Length == 0 || configured.Equals("nori://knowledge/Memory.md", StringComparison.Ordinal)
			? (_defaultPath ?? new AppStoragePaths(Environment.CurrentDirectory).KnowledgePath)
			: System.IO.Path.GetFullPath(configured);
	}

	private void EnsureKnowledgeFts()
	{
		_database.Locked(connection =>
		{
			try
			{
				using SqliteCommand command = connection.CreateCommand();
				command.CommandText = "CREATE VIRTUAL TABLE IF NOT EXISTS knowledge_fts USING fts5(chunk_id UNINDEXED, content, heading, tokenize = 'trigram');";
				command.ExecuteNonQuery();
			}
			catch (SqliteException)
			{
				// SearchRows 会按 LIKE 降级, 知识索引仍然可用.
			}
		});
	}

	private void RebuildKnowledgeFts(SqliteConnection connection, SqliteTransaction transaction)
	{
		try
		{
			using SqliteCommand command = connection.CreateCommand();
			command.Transaction = transaction;
			command.CommandText = "DELETE FROM knowledge_fts; INSERT INTO knowledge_fts(chunk_id, content, heading) SELECT id, content, COALESCE(heading, '') FROM knowledge_chunks;";
			command.ExecuteNonQuery();
		}
		catch (SqliteException) { }
	}

	private IReadOnlyList<KnowledgeRow> SearchRows(string query, int limit) => _database.Locked(connection =>
	{
		try
		{
			using SqliteCommand command = connection.CreateCommand();
			command.CommandText = """
				SELECT c.id, c.heading, c.subheading, c.content, c.knowledge_type, c.awareness
				FROM knowledge_chunks c
				WHERE c.id IN (SELECT CAST(chunk_id AS INTEGER) FROM knowledge_fts WHERE knowledge_fts MATCH $query)
				ORDER BY c.sequence ASC LIMIT $limit
				""";
			AddParameter(command, "$query", $"\"{query.Replace("\"", "\"\"", StringComparison.Ordinal)}\"");
			AddParameter(command, "$limit", Math.Max(0, limit));
			using SqliteDataReader reader = command.ExecuteReader();
			List<KnowledgeRow> rows = ReadKnowledgeRows(reader);
			if (rows.Count > 0) return rows;
		}
		catch (SqliteException) { }
		return SearchLikeKnowledgeRows(connection, query, limit);
	});

	private static IReadOnlyList<KnowledgeRow> SearchLikeKnowledgeRows(SqliteConnection connection, string query, int limit)
	{
		List<string> terms = [query];
		if (query.Length >= 2)
		{
			terms.AddRange(Enumerable.Range(0, query.Length - 1).Select(index => query.Substring(index, 2)));
		}
		terms = terms.Where(term => term.Length > 0).Distinct(StringComparer.Ordinal).Take(24).ToList();
		using SqliteCommand command = connection.CreateCommand();
		command.CommandText = """
			SELECT id, heading, subheading, content, knowledge_type, awareness
			FROM knowledge_chunks
			WHERE EXISTS (
				SELECT 1
				FROM json_each($patterns) AS pattern
				WHERE content LIKE CAST(pattern.value AS TEXT)
				   OR heading LIKE CAST(pattern.value AS TEXT)
			)
			ORDER BY sequence ASC LIMIT $limit
			""";
		AddParameter(command, "$patterns", JsonSerializer.Serialize(terms.Select(term => $"%{term}%")));
		AddParameter(command, "$limit", Math.Max(0, limit));
		using SqliteDataReader reader = command.ExecuteReader();
		return ReadKnowledgeRows(reader);
	}

	private KnowledgeDocument? GetDocument(string path) => _database.Locked(connection =>
	{
		using SqliteCommand command = connection.CreateCommand();
		command.CommandText = "SELECT id, path, content_hash, updated_at FROM knowledge_documents WHERE path = $path";
		AddParameter(command, "$path", path);
		using SqliteDataReader reader = command.ExecuteReader();
		return reader.Read() ? new KnowledgeDocument(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)) : null;
	});

	private IReadOnlyList<KnowledgeChunkRow> GetChunks(long? documentId) => _database.Locked(connection =>
	{
		if (documentId is null) return (IReadOnlyList<KnowledgeChunkRow>)[];
		using SqliteCommand command = connection.CreateCommand();
		command.CommandText = "SELECT id, chunk_key, content_hash FROM knowledge_chunks WHERE document_id = $document";
		AddParameter(command, "$document", documentId.Value);
		using SqliteDataReader reader = command.ExecuteReader();
		List<KnowledgeChunkRow> result = [];
		while (reader.Read()) result.Add(new KnowledgeChunkRow(reader.GetInt64(0), reader.GetString(1), reader.GetString(2)));
		return (IReadOnlyList<KnowledgeChunkRow>)result;
	});

	private static long UpsertDocument(SqliteConnection connection, SqliteTransaction transaction, string path, string hash)
	{
		using SqliteCommand command = connection.CreateCommand();
		command.Transaction = transaction;
		command.CommandText = """
			INSERT INTO knowledge_documents(path, content_hash, updated_at) VALUES ($path, $hash, $updated)
			ON CONFLICT(path) DO UPDATE SET content_hash = excluded.content_hash, updated_at = excluded.updated_at;
			SELECT id FROM knowledge_documents WHERE path = $path;
			""";
		AddParameter(command, "$path", path);
		AddParameter(command, "$hash", hash);
		AddParameter(command, "$updated", DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture));
		return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
	}

	private static List<KnowledgeRow> ReadKnowledgeRows(SqliteDataReader reader)
	{
		List<KnowledgeRow> result = [];
		while (reader.Read())
		{
			result.Add(new KnowledgeRow(reader.GetInt64(0), reader.IsDBNull(1) ? null : reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4), KnowledgeAwarenessExtensions.Parse(reader.IsDBNull(5) ? null : reader.GetString(5))));
		}
		return result;
	}

	private void SetStatus(MemoryIndexStatus status)
	{
		lock (_gate) _status = status;
		try { StatusChanged?.Invoke(); }
		catch { }
	}

	private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
	private static void AddParameter(SqliteCommand command, string name, object? value) => command.Parameters.AddWithValue(name, value ?? DBNull.Value);

	private static bool IsLoreQuery(string query)
	{
		string value = query.ToLowerInvariant();
		return value.Contains("arg", StringComparison.Ordinal) || value.Contains("设定", StringComparison.Ordinal)
			|| value.Contains("世界", StringComparison.Ordinal) || value.Contains("档案", StringComparison.Ordinal)
			|| value.Contains("时间线", StringComparison.Ordinal) || value.Contains("分析", StringComparison.Ordinal)
			|| value.Contains("碎裂", StringComparison.Ordinal) || value.Contains("真相", StringComparison.Ordinal)
			|| value.Contains("futum", StringComparison.Ordinal) || value.Contains("弗图姆", StringComparison.Ordinal)
			|| value.Contains("alephpro", StringComparison.Ordinal);
	}

	public async ValueTask DisposeAsync()
	{
		if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
		_watcher?.Dispose();
		CancellationTokenSource? debounce;
		Task? watchTask;
		lock (_gate) { debounce = _watchDebounce; _watchDebounce = null; watchTask = _watchTask; _watchTask = null; }
		debounce?.Cancel();
		if (watchTask is not null)
		{
			try { await watchTask.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
			catch (OperationCanceledException) { }
			catch (TimeoutException) { }
		}
		debounce?.Dispose();
	}

	private sealed record KnowledgeDocument(long Id, string Path, string ContentHash, string UpdatedAt);
	private sealed record KnowledgeChunkRow(long Id, string ChunkKey, string ContentHash);
	private sealed record KnowledgeRow(long Id, string? Heading, string? Subheading, string Content, string? KnowledgeType, KnowledgeAwareness Awareness);
}
