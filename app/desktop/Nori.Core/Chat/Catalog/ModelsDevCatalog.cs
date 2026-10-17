using Nori.Core.Network;

namespace Nori.Core.Chat.Catalog;

/// <summary>
/// models.dev 模型能力目录。
///
/// 目录来自公网 (https://models.dev/api.json)，下载后写入本地精简缓存，24 小时内不重复下载。
/// 网络被禁用 (安全模式) 时只读缓存；下载失败沿用旧缓存。任何失败都不影响对话本身 ——
/// 查不到能力时调用方回退到保守默认值。
/// </summary>
public sealed class ModelsDevCatalog : IModelCapabilityProvider, IAsyncDisposable
{
	/// <summary>目录地址。</summary>
	public static readonly Uri SourceUri = new("https://models.dev/api.json");

	/// <summary>目录原文体积上限：当前约 5 MB，留出增长余量。</summary>
	private const long MaxSourceBytes = 48L * 1024 * 1024;

	/// <summary>没有任何缓存时，首次查询最多等待下载的时长。</summary>
	private static readonly TimeSpan FirstLoadWait = TimeSpan.FromSeconds(8);

	private readonly HttpClient _http;
	private readonly string _cachePath;
	private readonly Func<bool> _networkEnabled;
	private readonly Action<string>? _warn;
	private readonly TimeProvider _time;
	private readonly Lock _gate = new();
	private readonly CancellationTokenSource _lifetime = new();
	private ModelsDevIndex? _index;
	private Task? _cacheLoad;
	private Task? _warmup;
	private Task? _refresh;
	private Task? _disposeTask;
	private bool _disposed;
	private DateTimeOffset _lastFailure = DateTimeOffset.MinValue;

	/// <summary>创建目录。</summary>
	/// <param name="http">公网 HTTP 客户端。</param>
	/// <param name="cachePath">精简缓存文件路径 (位于包内数据目录)。</param>
	/// <param name="networkEnabled">是否允许联网；安全模式返回 false。</param>
	/// <param name="warn">失败日志回调 (只写脱敏摘要)。</param>
	/// <param name="time">时间源，测试可替换。</param>
	public ModelsDevCatalog(HttpClient http, string cachePath, Func<bool> networkEnabled, Action<string>? warn = null, TimeProvider? time = null)
	{
		_http = http ?? throw new ArgumentNullException(nameof(http));
		_cachePath = string.IsNullOrWhiteSpace(cachePath) ? throw new ArgumentException("缓存路径不能为空", nameof(cachePath)) : cachePath;
		_networkEnabled = networkEnabled ?? (() => true);
		_warn = warn;
		_time = time ?? TimeProvider.System;
	}

	/// <summary>缓存有效期。</summary>
	public TimeSpan MaxAge { get; init; } = TimeSpan.FromHours(24);

	/// <summary>当前已加载目录的下载时间；未加载时为 null。</summary>
	public DateTimeOffset? FetchedAt
	{
		get
		{
			lock (_gate) return _index?.FetchedAt;
		}
	}

	/// <inheritdoc />
	public async Task<ModelCapabilities?> FindAsync(LlmProvider protocol, string? baseUrl, string model, CancellationToken cancellationToken = default)
	{
		if (string.IsNullOrWhiteSpace(model)) return null;
		ModelsDevIndex? index = await GetIndexAsync(cancellationToken).ConfigureAwait(false);
		return index?.Find(protocol, baseUrl, model);
	}

	/// <summary>
	/// 取得目录：先读本地缓存；过期则在后台刷新。完全没有目录时等待首次下载 (有上限)。
	/// </summary>
	public async Task<ModelsDevIndex?> GetIndexAsync(CancellationToken cancellationToken = default)
	{
		await EnsureCacheLoadedAsync(cancellationToken).ConfigureAwait(false);
		ModelsDevIndex? index;
		lock (_gate) index = _index;

		bool stale = index is null || _time.GetUtcNow() - index.FetchedAt > MaxAge;
		if (!stale || !_networkEnabled()) return index;

		Task refresh = StartRefresh(force: false);
		if (index is not null) return index;

		// 首次使用：没有任何数据，短暂等待下载，超时则本轮按未知处理。
		try
		{
			await refresh.WaitAsync(FirstLoadWait, cancellationToken).ConfigureAwait(false);
		}
		catch (Exception) when (!cancellationToken.IsCancellationRequested && !_lifetime.IsCancellationRequested)
		{
			// 超时或下载失败都回退为未知能力；刷新任务已记录失败原因。
			return null;
		}
		lock (_gate) return _index;
	}

	/// <summary>立即刷新目录 (设置页的手动刷新)。失败时抛出可读异常。</summary>
	public async Task RefreshAsync(CancellationToken cancellationToken = default)
	{
		if (!_networkEnabled()) throw new InvalidOperationException("安全模式已禁用联网，无法更新模型目录");
		await EnsureCacheLoadedAsync(cancellationToken).ConfigureAwait(false);
		await StartRefresh(force: true, cancellationToken).WaitAsync(cancellationToken).ConfigureAwait(false);
	}

	/// <summary>在后台预热目录，不等待结果。</summary>
	public void Warmup()
	{
		lock (_gate)
		{
			ObjectDisposedException.ThrowIf(_disposed, this);
			if (_warmup is not {IsCompleted: false}) _warmup = Task.Run(WarmupCoreAsync);
		}
	}

	private async Task WarmupCoreAsync()
	{
		try
		{
			await GetIndexAsync(_lifetime.Token).ConfigureAwait(false);
		}
		catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
		catch (ObjectDisposedException) when (_disposed) { }
		catch (Exception exception)
		{
			_warn?.Invoke($"模型目录预热失败: {exception.GetType().Name}");
		}
	}

	private Task StartRefresh(bool force, CancellationToken cancellationToken = default)
	{
		lock (_gate)
		{
			ObjectDisposedException.ThrowIf(_disposed, this);
			if (_refresh is {IsCompleted: false} running) return running;
			// 失败后 10 分钟内不重试，避免离线时每条消息都去碰一次网络。
			if (!force && _time.GetUtcNow() - _lastFailure < TimeSpan.FromMinutes(10)) return Task.CompletedTask;
			_refresh = Task.Run(async () =>
			{
				using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, cancellationToken);
				try
				{
					await DownloadAsync(linked.Token).ConfigureAwait(false);
				}
				catch (OperationCanceledException) when (linked.IsCancellationRequested) { throw; }
				catch (Exception exception)
				{
					lock (_gate) _lastFailure = _time.GetUtcNow();
					_warn?.Invoke($"模型目录更新失败: {exception.GetType().Name}: {SanitizedMessage(exception)}");
					throw;
				}
			});
			// 过期缓存的调用方不等待刷新；错误已记录，仍需观察无人等待的异常。
			_ = _refresh.ContinueWith(task => { _ = task.Exception; },
				CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
			return _refresh;
		}
	}

	private async Task DownloadAsync(CancellationToken cancellationToken)
	{
		using HttpResponseMessage response = await UrlAccessPolicy.GetWithSafeRedirectsAsync(
			_http, SourceUri, allowPrivate: false, cancellationToken: cancellationToken).ConfigureAwait(false);
		if (!response.IsSuccessStatusCode)
			throw new InvalidOperationException($"模型目录下载失败 (HTTP {(int)response.StatusCode})");
		string json = await UrlAccessPolicy.ReadCappedTextAsync(response.Content, MaxSourceBytes, cancellationToken).ConfigureAwait(false);
		ModelsDevIndex index = ModelsDevIndex.ParseSource(json, _time.GetUtcNow());
		lock (_gate) _index = index;
		await WriteCacheAsync(index, cancellationToken).ConfigureAwait(false);
	}

	private Task EnsureCacheLoadedAsync(CancellationToken cancellationToken)
	{
		lock (_gate)
		{
			ObjectDisposedException.ThrowIf(_disposed, this);
			_cacheLoad ??= LoadCacheAsync(_lifetime.Token);
			return _cacheLoad.WaitAsync(cancellationToken);
		}
	}

	private async Task LoadCacheAsync(CancellationToken cancellationToken)
	{
		ModelsDevIndex? loaded = null;
		try
		{
			if (File.Exists(_cachePath))
			{
				string json = await File.ReadAllTextAsync(_cachePath, cancellationToken).ConfigureAwait(false);
				loaded = ModelsDevIndex.ParseCache(json);
			}
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException)
		{
			// 缓存损坏按没有缓存处理，下一次下载会覆盖它。
			_warn?.Invoke($"模型目录缓存无法读取: {exception.GetType().Name}");
		}
		lock (_gate) _index ??= loaded;
	}

	private async Task WriteCacheAsync(ModelsDevIndex index, CancellationToken cancellationToken)
	{
		try
		{
			string? directory = Path.GetDirectoryName(_cachePath);
			if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
			string temp = _cachePath + ".tmp";
			await File.WriteAllTextAsync(temp, index.ToCacheJson(), cancellationToken).ConfigureAwait(false);
			File.Move(temp, _cachePath, overwrite: true);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			// 写不了缓存只影响下次启动要重新下载，内存中的目录仍然可用。
			_warn?.Invoke($"模型目录缓存写入失败: {exception.GetType().Name}");
		}
	}

	/// <summary>停止接收新任务，取消并等待读缓存、预热和刷新全部结束后再释放令牌。</summary>
	public ValueTask DisposeAsync()
	{
		lock (_gate)
		{
			if (_disposeTask is null)
			{
				_disposed = true;
				_disposeTask = DisposeCoreAsync([_cacheLoad ?? Task.CompletedTask, _warmup ?? Task.CompletedTask, _refresh ?? Task.CompletedTask]);
			}
			return new ValueTask(_disposeTask);
		}
	}

	private async Task DisposeCoreAsync(Task[] pending)
	{
		await _lifetime.CancelAsync().ConfigureAwait(false);
		try { await Task.WhenAll(pending).ConfigureAwait(false); }
		catch (Exception) { /* 下载失败已由刷新调用方或后台日志处理；此处只确保文件句柄已释放。 */ }
		finally { _lifetime.Dispose(); }
	}

	private static string SanitizedMessage(Exception exception) =>
		Security.SensitiveDataRedactor.Redact(exception.Message);
}
