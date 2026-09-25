using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using NLog;
using NLog.Config;
using NLog.Targets;
using Nori.Core.Data;

namespace Nori.Core.Logging;

/// <summary>日志来源；保持桥接协议兼容。</summary>
public enum LogSource { Frontend, Backend }

/// <summary>日志容量和维护策略。</summary>
public sealed record FileLoggerOptions
{
	public int QueueCapacity { get; init; } = 2048;
	public int MemoryCapacity { get; init; } = 500;
	public long FileBytes { get; init; } = 8 * 1024 * 1024;
	public long TotalBytes { get; init; } = 64 * 1024 * 1024;
	public TimeSpan Retention { get; init; } = TimeSpan.FromDays(7);
	public TimeSpan MaintenanceInterval { get; init; } = TimeSpan.FromMinutes(5);
}

/// <summary>本次运行的日志健康状态，不包含路径和异常正文。</summary>
public sealed record LoggingStatus(string MinimumLevel, long DroppedCount, long WriteFailureCount, string? LastError, bool Stopped);

/// <summary>单后台写入器；业务线程只更新有界内存和队列。</summary>
public sealed class FileLogger : IDisposable, IAsyncDisposable
{
	private sealed record Pending(LogEntry? Entry, TaskCompletionSource<bool>? Barrier = null);
	private static readonly JsonSerializerOptions JsonOptions = new()
	{
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
		Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
	};
	private readonly string _directory;
	private readonly FileLoggerOptions _options;
	private readonly Lock _gate = new();
	private readonly Queue<LogEntry> _memory = new();
	private readonly Channel<Pending> _queue;
	private readonly CancellationTokenSource _stop = new();
	private readonly Task _worker;
	private readonly string _sessionId = Guid.NewGuid().ToString("N");
	private int _minimumLevel;
	private long _sequence;
	private long _dropped;
	private long _failures;
	private string? _lastError;
	private int _disposed;
	private Task? _disposeTask;

	public FileLogger(string? directory = null) : this(directory, "info") { }

	public FileLogger(string? directory, string minimumLevel, FileLoggerOptions? options = null)
	{
		_directory = PhysicalLogDirectory(Path.GetFullPath(directory ?? new AppStoragePaths(Environment.CurrentDirectory).LogsDirectory));
		_options = options ?? new FileLoggerOptions();
		if (_options.QueueCapacity < 1 || _options.MemoryCapacity < 1 || _options.FileBytes < 1024
			|| _options.TotalBytes < _options.FileBytes || _options.Retention <= TimeSpan.Zero || _options.MaintenanceInterval <= TimeSpan.Zero)
			throw new ArgumentOutOfRangeException(nameof(options), "日志容量和保留时间无效");
		_minimumLevel = ParseLevel(minimumLevel).Ordinal;
		_queue = Channel.CreateBounded<Pending>(new BoundedChannelOptions(_options.QueueCapacity)
		{
			SingleReader = true, FullMode = BoundedChannelFullMode.Wait, AllowSynchronousContinuations = false,
		});
		_worker = Task.Run(ConsumeAsync);
	}

	/// <summary>兼容旧初始化入口；后台任务在构造后启动。</summary>
	public void Initialize() { }

	/// <summary>调整当前运行的最低级别，不持久化设置。</summary>
	public void SetMinimumLevel(string level)
	{
		if (!IsLevel(level)) throw new ArgumentException("日志级别无效", nameof(level));
		Volatile.Write(ref _minimumLevel, ParseLevel(level).Ordinal);
	}

	public LoggingStatus GetStatus() => new(LogLevel.FromOrdinal(Volatile.Read(ref _minimumLevel)).Name.ToLowerInvariant(),
		Interlocked.Read(ref _dropped), Interlocked.Read(ref _failures), Volatile.Read(ref _lastError), Volatile.Read(ref _disposed) != 0);

	public bool IsEnabled(string level) => Volatile.Read(ref _disposed) == 0 && ParseLevel(level).Ordinal >= Volatile.Read(ref _minimumLevel);

	/// <summary>写入固定事件描述；异常仅提取类型和自有代码位置，禁止传入用户正文。</summary>
	public void Write(LogSource source, string level, string message, string? category = null, string? eventId = null,
		Exception? exception = null, string? windowLabel = null, string? operationId = null,
		[CallerFilePath] string callerFile = "", [CallerMemberName] string callerMember = "")
	{
		LogLevel normalized = ParseLevel(level);
		if (normalized.Ordinal < Volatile.Read(ref _minimumLevel)) return;
		LogEntry entry = LogEntry.Create(source, normalized.Name.ToLowerInvariant(), message) with
		{
			SessionId = _sessionId,
			Category = LogEntry.SafeIdentifier(category ?? Path.GetFileNameWithoutExtension(callerFile)),
			EventId = LogEntry.SafeIdentifier(eventId ?? callerMember),
			WindowLabel = LogEntry.SafeIdentifier(windowLabel),
			OperationId = Guid.TryParse(operationId, out Guid operation) ? operation.ToString("N") : null,
			ExceptionType = exception is null ? null : LogEntry.SafeIdentifier(exception.GetType().FullName),
			ExceptionSite = LogEntry.ExceptionLocation(exception),
		};
		lock (_gate)
		{
			if (_disposed != 0) { Interlocked.Increment(ref _dropped); return; }
			entry = entry with { Sequence = ++_sequence };
			_memory.Enqueue(entry);
			while (_memory.Count > _options.MemoryCapacity) _memory.Dequeue();
			if (!_queue.Writer.TryWrite(new Pending(entry))) Interlocked.Increment(ref _dropped);
		}
	}

	public IReadOnlyList<LogEntry> RecentLogs() { lock (_gate) return _memory.ToArray(); }

	/// <summary>仅清理内存，序号和健康计数继续递增。</summary>
	public void ClearRecentLogs() { lock (_gate) _memory.Clear(); }

	/// <summary>等待此前入队的记录刷新；超时返回 false，调用方取消继续传播。</summary>
	public async Task<bool> FlushAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default)
	{
		using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		deadline.CancelAfter(timeout ?? TimeSpan.FromSeconds(1));
		try
		{
			if (Volatile.Read(ref _disposed) != 0)
			{
				await _worker.WaitAsync(deadline.Token).ConfigureAwait(false);
				return Volatile.Read(ref _lastError) is null;
			}
			TaskCompletionSource<bool> barrier = new(TaskCreationOptions.RunContinuationsAsynchronously);
			await _queue.Writer.WriteAsync(new Pending(null, barrier), deadline.Token).ConfigureAwait(false);
			return await barrier.Task.WaitAsync(deadline.Token).ConfigureAwait(false);
		}
		catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return false; }
		catch (ChannelClosedException) { return false; }
	}

	public ValueTask DisposeAsync()
	{
		lock (_gate)
		{
			_disposeTask ??= StopAsync();
			return new ValueTask(_disposeTask);
		}
	}

	private async Task StopAsync()
	{
		Volatile.Write(ref _disposed, 1);
		_queue.Writer.TryComplete();
		try { await _worker.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false); }
		catch (TimeoutException) { _stop.Cancel(); }
		if (_worker.IsCompleted) _stop.Dispose();
		else _ = _worker.ContinueWith(_ => _stop.Dispose(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
	}

	public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

	/// <summary>日志队列不可用时的独立有界兜底；只能传入已验证的日志目录。</summary>
	public static Task WriteEmergencyAsync(string directory, string message)
		=> Task.Run(() =>
		{
			try
			{
				string logDirectory = PhysicalLogDirectory(Path.GetFullPath(directory));
				Directory.CreateDirectory(logDirectory);
				if (IsReparsePoint(logDirectory)) return;
				directory = logDirectory;
				string path = Path.Combine(directory, $"nori_{DateTime.Now:yyyy-MM-dd}_{Guid.Empty:N}.jsonl");
				if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return;
				using FileStream stream = new(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read);
				if (stream.Length > 1024 * 1024) stream.SetLength(0);
				stream.Seek(0, SeekOrigin.End);
				LogEntry entry = LogEntry.Create(LogSource.Backend, "fatal", message) with { Category = "Crash", EventId = "crash.emergency" };
				using StreamWriter writer = new(stream, new UTF8Encoding(false));
				writer.WriteLine(JsonSerializer.Serialize(entry, JsonOptions));
			}
			catch (Exception) { /* 兜底失败不能覆盖原始崩溃。 */ }
		});

	private async Task ConsumeAsync()
	{
		using LogFactory factory = new() { ThrowExceptions = true, AutoShutdown = false };
		Pending? active = null;
		try
		{
			LoggingConfiguration configuration = new(factory);
			FileTarget file = new("jsonl")
			{
				FileName = Path.Combine(_directory, $"nori_${{shortdate}}_{_sessionId}.jsonl"),
				Layout = "${message}", Encoding = new UTF8Encoding(false), KeepFileOpen = true, AutoFlush = true,
				ArchiveAboveSize = _options.FileBytes, ArchiveSuffixFormat = "_{0:000}",
			};
			configuration.AddRule(LogLevel.Trace, LogLevel.Fatal, file);
			factory.Configuration = configuration;
			Logger writer = factory.GetLogger("nori");
			DateTime nextMaintenance = DateTime.MinValue;
			do
			{
				if (DateTime.UtcNow >= nextMaintenance)
				{
					TryMaintain();
					nextMaintenance = DateTime.UtcNow + _options.MaintenanceInterval;
				}
				int processed = 0;
				while (processed++ < _options.QueueCapacity && _queue.Reader.TryRead(out Pending? pending))
				{
					active = pending;
					if (pending.Barrier is not null)
					{
						TryMaintain();
						pending.Barrier.TrySetResult(Volatile.Read(ref _lastError) is null);
						active = null;
						continue;
					}
					bool written = false;
					while (!written && !_stop.IsCancellationRequested)
					{
						try
						{
							EnsureSafeDirectory();
							writer.Log(ParseLevel(pending.Entry!.Level), JsonSerializer.Serialize(pending.Entry, JsonOptions));
							Volatile.Write(ref _lastError, null);
							written = true;
						}
						catch (Exception failure)
						{
							RecordFailure(failure);
							if (Volatile.Read(ref _disposed) != 0) break;
							await Task.Delay(TimeSpan.FromSeconds(1), _stop.Token).ConfigureAwait(false);
						}
					}
					if (!written) Interlocked.Increment(ref _dropped);
					active = null;
					if (processed % 64 == 0) TryMaintain();
				}
				if (processed > 1) TryMaintain();
				if (Volatile.Read(ref _disposed) != 0 && _queue.Reader.Count == 0) break;
			} while (await WaitForWorkAsync().ConfigureAwait(false));
		}
		catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
		catch (Exception failure) { RecordFailure(failure); }
		finally
		{
			if (active?.Entry is not null) Interlocked.Increment(ref _dropped);
			active?.Barrier?.TrySetResult(false);
			_queue.Writer.TryComplete();
			while (_queue.Reader.TryRead(out Pending? remaining))
			{
				remaining.Barrier?.TrySetResult(false);
				if (remaining.Entry is not null) Interlocked.Increment(ref _dropped);
			}
		}
	}

	private async Task<bool> WaitForWorkAsync()
	{
		using CancellationTokenSource wake = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
		wake.CancelAfter(TimeSpan.FromMilliseconds(250));
		try { return await _queue.Reader.WaitToReadAsync(wake.Token).ConfigureAwait(false); }
		catch (OperationCanceledException) when (!_stop.IsCancellationRequested) { return true; }
	}

	private void RecordFailure(Exception failure)
	{
		Interlocked.Increment(ref _failures);
		Volatile.Write(ref _lastError, LogEntry.SafeIdentifier(failure.GetType().Name));
	}

	private void EnsureSafeDirectory()
	{
		Directory.CreateDirectory(_directory);
		if (IsReparsePoint(_directory)) throw new InvalidOperationException("日志目录不能是符号链接");
	}

	/// <summary>
	/// 解析日志目录。macOS 的 /var 等系统链接要落到真实目录，不能从文件系统根拒绝；
	/// 目录本身仍不能是链接，避免日志被指到别处。
	/// </summary>
	private static string PhysicalLogDirectory(string fullPath)
	{
		string name = Path.GetFileName(fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
		string? parent = Path.GetDirectoryName(fullPath);
		if (string.IsNullOrEmpty(parent) || string.IsNullOrEmpty(name))
			throw new InvalidOperationException("日志目录无效");
		string directory = Path.Combine(AppStoragePaths.ResolvePhysicalPath(parent), name);
		if ((File.Exists(directory) || Directory.Exists(directory)) && IsReparsePoint(directory))
			throw new InvalidOperationException("日志目录不能是符号链接");
		return directory;
	}

	private static bool IsReparsePoint(string path) => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

	private void TryMaintain()
	{
		try
		{
			EnsureSafeDirectory();
			FileInfo[] files = new DirectoryInfo(_directory).GetFiles()
				.Where(file => (file.Attributes & FileAttributes.ReparsePoint) == 0 && IsOwnedFile(file.Name))
				.OrderBy(file => file.LastWriteTimeUtc).ToArray();
			long total = files.Where(file => file.Extension == ".jsonl").Sum(file => file.Length);
			foreach (FileInfo file in files)
			{
				if (file.Name == $"nori_{DateTime.Now:yyyy-MM-dd}_{_sessionId}.jsonl") continue;
				if (file.LastWriteTimeUtc >= DateTime.UtcNow - _options.Retention
					&& (file.Extension != ".jsonl" || total <= _options.TotalBytes)) continue;
				long bytes = file.Length;
				file.Delete();
				if (file.Extension == ".jsonl") total -= bytes;
			}
		}
		catch (Exception failure) { RecordFailure(failure); }
	}

	private static bool IsOwnedFile(string name) => System.Text.RegularExpressions.Regex.IsMatch(name,
		@"^(?:(?:backend|frontend)_\d{4}-\d{2}-\d{2}\.log|nori_\d{4}-\d{2}-\d{2}_[a-f0-9]{32}(?:_\d+)?\.jsonl)$",
		System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

	public static bool IsLevel(string? level) => level is "trace" or "debug" or "info" or "warn" or "error" or "fatal";

	private static LogLevel ParseLevel(string? raw) => raw?.Trim().ToLowerInvariant() switch
	{
		"trace" => LogLevel.Trace, "debug" => LogLevel.Debug, "warn" or "warning" => LogLevel.Warn,
		"error" => LogLevel.Error, "fatal" => LogLevel.Fatal, _ => LogLevel.Info,
	};
}
