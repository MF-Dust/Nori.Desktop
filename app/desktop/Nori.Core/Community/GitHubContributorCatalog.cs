using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Nori.Core.Network;

namespace Nori.Core.Community;

/// <summary>一位 GitHub 贡献者：登录名与头像图片字节。</summary>
public sealed record GitHubContributor(string Login, byte[] Avatar);

/// <summary>
/// 项目仓库的 GitHub 贡献者目录。
///
/// 贡献者列表与头像来自 GitHub 公网接口，下载后连同头像写入本地缓存，24 小时内不重复下载。
/// 匿名接口每小时只有 60 次额度，所以缓存过期时先返回旧缓存再在后台刷新；网络被禁用 (安全模式)
/// 时只读缓存。失败只影响关于页的头像展示。
/// </summary>
public sealed partial class GitHubContributorCatalog : IAsyncDisposable
{
	/// <summary>贡献者所属仓库。仓库已改名，公网客户端不跟随重定向，必须使用当前名称。</summary>
	public const string Repository = "MF-Dust/Nori.Desktop";

	/// <summary>贡献者列表接口地址。</summary>
	public static readonly Uri SourceUri = new($"https://api.github.com/repos/{Repository}/contributors?per_page=100");

	/// <summary>头像请求边长 (像素)；界面按约 40px 显示，留出高 DPI 余量。</summary>
	internal const int AvatarSize = 96;

	internal const int MaxContributors = 100;
	internal const long MaxAvatarBytes = 256L * 1024;
	private const long MaxListBytes = 2L * 1024 * 1024;
	private const long MaxCacheBytes = 48L * 1024 * 1024;
	private const int CacheVersion = 1;
	private const int AvatarConcurrency = 6;
	private const string AvatarHost = "avatars.githubusercontent.com";
	private static readonly TimeSpan ListTimeout = TimeSpan.FromSeconds(20);
	private static readonly TimeSpan AvatarTimeout = TimeSpan.FromSeconds(15);
	private static readonly TimeSpan FailureBackoff = TimeSpan.FromMinutes(10);

	private readonly HttpClient _http;
	private readonly string _cachePath;
	private readonly Func<bool> _networkEnabled;
	private readonly Action<string>? _warn;
	private readonly TimeProvider _time;
	private readonly Lock _gate = new();
	private readonly CancellationTokenSource _lifetime = new();
	private ContributorSnapshot? _snapshot;
	private Task? _cacheLoad;
	private Task<ContributorSnapshot>? _refresh;
	private Task? _disposeTask;
	private bool _disposed;
	private DateTimeOffset _lastFailure = DateTimeOffset.MinValue;
	private string _lastFailureMessage = string.Empty;

	/// <summary>创建贡献者目录。</summary>
	/// <param name="http">公网 HTTP 客户端。</param>
	/// <param name="cachePath">缓存文件路径 (位于包内数据目录)。</param>
	/// <param name="networkEnabled">是否允许联网；安全模式返回 false。</param>
	/// <param name="warn">失败日志回调 (只写脱敏摘要)。</param>
	/// <param name="time">时间源，测试可替换。</param>
	public GitHubContributorCatalog(HttpClient http, string cachePath, Func<bool> networkEnabled, Action<string>? warn = null, TimeProvider? time = null)
	{
		_http = http ?? throw new ArgumentNullException(nameof(http));
		_cachePath = string.IsNullOrWhiteSpace(cachePath) ? throw new ArgumentException("缓存路径不能为空", nameof(cachePath)) : cachePath;
		_networkEnabled = networkEnabled ?? (() => true);
		_warn = warn;
		_time = time ?? TimeProvider.System;
	}

	/// <summary>缓存有效期。</summary>
	public TimeSpan MaxAge { get; init; } = TimeSpan.FromHours(24);

	/// <summary>
	/// 取得贡献者：有缓存时立即返回 (过期则后台刷新)；没有缓存时等待首次下载，失败抛出可读异常。
	/// 网络被禁用且没有缓存时返回空列表。
	/// </summary>
	public async Task<IReadOnlyList<GitHubContributor>> GetAsync(CancellationToken cancellationToken = default)
	{
		await EnsureCacheLoadedAsync(cancellationToken).ConfigureAwait(false);
		ContributorSnapshot? current;
		lock (_gate) current = _snapshot;
		if (!_networkEnabled()) return current?.Items ?? [];

		bool stale = current is null || _time.GetUtcNow() - current.FetchedAt > MaxAge;
		if (!stale) return current!.Items;

		Task<ContributorSnapshot>? refresh = StartRefresh();
		if (current is not null) return current.Items;
		if (refresh is null)
		{
			string message;
			lock (_gate) message = _lastFailureMessage;
			throw new InvalidOperationException(message.Length > 0 ? message : "暂时无法获取 GitHub 贡献者，请稍后再试");
		}
		ContributorSnapshot loaded = await refresh.WaitAsync(cancellationToken).ConfigureAwait(false);
		return loaded.Items;
	}

	private Task<ContributorSnapshot>? StartRefresh()
	{
		lock (_gate)
		{
			ObjectDisposedException.ThrowIf(_disposed, this);
			if (_refresh is {IsCompleted: false} running) return running;
			// 失败后一段时间内不重试，避免离线或额度用尽时每次打开关于页都去碰一次网络。
			if (_time.GetUtcNow() - _lastFailure < FailureBackoff) return null;
			_refresh = Task.Run(RefreshCoreAsync);
			// 有旧缓存的调用方不等待刷新；错误已记录，仍需观察无人等待的异常。
			_ = _refresh.ContinueWith(task => { _ = task.Exception; },
				CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
			return _refresh;
		}
	}

	private async Task<ContributorSnapshot> RefreshCoreAsync()
	{
		CancellationToken token = _lifetime.Token;
		try
		{
			ContributorSnapshot snapshot = await DownloadAsync(token).ConfigureAwait(false);
			lock (_gate) _snapshot = snapshot;
			await WriteCacheAsync(snapshot, token).ConfigureAwait(false);
			return snapshot;
		}
		catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
		catch (Exception exception)
		{
			string message = exception switch
			{
				OperationCanceledException => "连接 GitHub 超时，请检查网络后重试",
				HttpRequestException => "无法连接 GitHub，请检查网络后重试",
				InvalidOperationException or InvalidDataException => exception.Message,
				_ => "暂时无法获取 GitHub 贡献者，请稍后再试",
			};
			lock (_gate)
			{
				_lastFailure = _time.GetUtcNow();
				_lastFailureMessage = message;
			}
			_warn?.Invoke($"GitHub 贡献者更新失败: {exception.GetType().Name}: {Security.SensitiveDataRedactor.Redact(exception.Message)}");
			throw new InvalidOperationException(message, exception);
		}
	}

	private async Task<ContributorSnapshot> DownloadAsync(CancellationToken cancellationToken)
	{
		string json;
		using (CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
		{
			timeout.CancelAfter(ListTimeout);
			using HttpRequestMessage request = CreateRequest(SourceUri, "application/vnd.github+json");
			request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
			using HttpResponseMessage response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
			if (!response.IsSuccessStatusCode) throw new InvalidOperationException(DescribeFailure(response));
			json = await UrlAccessPolicy.ReadCappedTextAsync(response.Content, MaxListBytes, timeout.Token).ConfigureAwait(false);
		}

		IReadOnlyList<ContributorEntry> entries = ParseList(json);
		byte[]?[] avatars = new byte[entries.Count][];
		int failures = 0;
		await Parallel.ForEachAsync(Enumerable.Range(0, entries.Count),
			new ParallelOptions {MaxDegreeOfParallelism = AvatarConcurrency, CancellationToken = cancellationToken},
			async (index, token) =>
			{
				avatars[index] = await DownloadAvatarAsync(entries[index].AvatarUri, token).ConfigureAwait(false);
				if (avatars[index] is null) Interlocked.Increment(ref failures);
			}).ConfigureAwait(false);

		List<GitHubContributor> items = [];
		for (int index = 0; index < entries.Count; index++)
			if (avatars[index] is { } avatar) items.Add(new GitHubContributor(entries[index].Login, avatar));
		if (entries.Count > 0 && items.Count == 0) throw new InvalidOperationException("GitHub 贡献者头像下载失败，请稍后再试");
		if (failures > 0) _warn?.Invoke($"GitHub 贡献者头像有 {failures} 个下载失败，本次不显示");
		return new ContributorSnapshot(_time.GetUtcNow(), items);
	}

	private async Task<byte[]?> DownloadAvatarAsync(Uri uri, CancellationToken cancellationToken)
	{
		using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		timeout.CancelAfter(AvatarTimeout);
		try
		{
			using HttpRequestMessage request = CreateRequest(uri, "image/*");
			using HttpResponseMessage response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
			if (!response.IsSuccessStatusCode) return null;
			string? mediaType = response.Content.Headers.ContentType?.MediaType;
			if (mediaType is not ("image/png" or "image/jpeg" or "image/gif" or "image/webp")) return null;
			if (response.Content.Headers.ContentLength > MaxAvatarBytes) return null;
			return await ReadCappedBytesAsync(response.Content, MaxAvatarBytes, timeout.Token).ConfigureAwait(false);
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
		catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException or InvalidDataException or IOException)
		{
			return null;
		}
	}

	private static HttpRequestMessage CreateRequest(Uri uri, string accept)
	{
		HttpRequestMessage request = new(HttpMethod.Get, uri);
		request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(accept));
		// GitHub 接口拒绝没有 User-Agent 的请求。
		request.Headers.UserAgent.ParseAdd(Chat.ChatClientFactory.UserAgent);
		return request;
	}

	private static string DescribeFailure(HttpResponseMessage response)
	{
		int status = (int)response.StatusCode;
		bool rateLimited = response.StatusCode == HttpStatusCode.TooManyRequests
			|| (response.StatusCode == HttpStatusCode.Forbidden
				&& response.Headers.TryGetValues("X-RateLimit-Remaining", out IEnumerable<string>? remaining)
				&& remaining.FirstOrDefault() == "0");
		if (rateLimited) return "GitHub 接口访问次数已达上限，请稍后再试";
		if (response.StatusCode == HttpStatusCode.NotFound) return "未找到项目的 GitHub 仓库";
		return $"GitHub 贡献者获取失败 (HTTP {status})";
	}

	private static async Task<byte[]> ReadCappedBytesAsync(HttpContent content, long cap, CancellationToken cancellationToken)
	{
		await using Stream stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
		using MemoryStream output = new();
		byte[] buffer = new byte[16 * 1024];
		while (true)
		{
			int read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
			if (read <= 0) break;
			if (output.Length + read > cap) throw new InvalidDataException("头像超过大小上限");
			output.Write(buffer, 0, read);
		}
		byte[] bytes = output.ToArray();
		if (!IsSupportedImage(bytes)) throw new InvalidDataException("头像不是受支持的图片格式");
		return bytes;
	}

	/// <summary>解析 GitHub 贡献者列表：只保留真人账号，校验登录名与头像地址。</summary>
	internal static IReadOnlyList<ContributorEntry> ParseList(string json)
	{
		using JsonDocument document = JsonDocument.Parse(json);
		if (document.RootElement.ValueKind != JsonValueKind.Array) throw new InvalidDataException("GitHub 贡献者数据格式不正确");
		List<ContributorEntry> entries = [];
		HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
		foreach (JsonElement item in document.RootElement.EnumerateArray())
		{
			if (entries.Count >= MaxContributors) break;
			if (item.ValueKind != JsonValueKind.Object) continue;
			// 机器人账号 (Bot) 与匿名提交 (Anonymous) 不展示。
			if (StringProperty(item, "type") != "User") continue;
			string login = StringProperty(item, "login");
			if (!IsValidLogin(login) || !seen.Add(login)) continue;
			if (!Uri.TryCreate(StringProperty(item, "avatar_url"), UriKind.Absolute, out Uri? avatar) || !IsAllowedAvatarUri(avatar)) continue;
			string separator = avatar.Query.Length == 0 ? "?" : "&";
			entries.Add(new ContributorEntry(login, new Uri(avatar.AbsoluteUri + separator + "s=" + AvatarSize)));
		}
		return entries;
	}

	private static bool IsAllowedAvatarUri(Uri uri) =>
		uri.Scheme == Uri.UriSchemeHttps
		&& uri.IsDefaultPort
		&& uri.UserInfo.Length == 0
		&& string.Equals(uri.IdnHost, AvatarHost, StringComparison.OrdinalIgnoreCase)
		&& uri.Fragment.Length == 0;

	private static bool IsValidLogin(string login) => LoginPattern().IsMatch(login);

	/// <summary>按文件头确认是 PNG / JPEG / GIF / WebP，不把任意字节交给界面解码。</summary>
	internal static bool IsSupportedImage(ReadOnlySpan<byte> data) =>
		data.Length is > 12 and <= (int)MaxAvatarBytes
		&& (data.StartsWith((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A])
			|| data.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xD8, 0xFF])
			|| data.StartsWith("GIF8"u8)
			|| (data.StartsWith("RIFF"u8) && data[8..12].SequenceEqual("WEBP"u8)));

	[GeneratedRegex("^[A-Za-z0-9](?:[A-Za-z0-9-]{0,38})$", RegexOptions.CultureInvariant)]
	private static partial Regex LoginPattern();

	private static string StringProperty(JsonElement element, string name) =>
		element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;

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
		ContributorSnapshot? loaded = null;
		try
		{
			FileInfo file = new(_cachePath);
			if (file.Exists && file.Length <= MaxCacheBytes)
			{
				string json = await File.ReadAllTextAsync(_cachePath, cancellationToken).ConfigureAwait(false);
				loaded = ParseCache(json);
			}
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or FormatException)
		{
			// 缓存损坏按没有缓存处理，下一次下载会覆盖它。
			_warn?.Invoke($"GitHub 贡献者缓存无法读取: {exception.GetType().Name}");
		}
		lock (_gate) _snapshot ??= loaded;
	}

	/// <summary>解析本地缓存；版本或仓库不符时视为没有缓存。</summary>
	internal static ContributorSnapshot? ParseCache(string json)
	{
		using JsonDocument document = JsonDocument.Parse(json);
		JsonElement root = document.RootElement;
		if (root.ValueKind != JsonValueKind.Object
			|| !root.TryGetProperty("version", out JsonElement version) || version.ValueKind != JsonValueKind.Number || version.GetInt32() != CacheVersion
			|| StringProperty(root, "repository") != Repository
			|| !root.TryGetProperty("fetchedAt", out JsonElement fetchedAt) || !fetchedAt.TryGetDateTimeOffset(out DateTimeOffset fetched)
			|| !root.TryGetProperty("contributors", out JsonElement contributors) || contributors.ValueKind != JsonValueKind.Array)
			return null;
		List<GitHubContributor> items = [];
		foreach (JsonElement item in contributors.EnumerateArray())
		{
			if (items.Count >= MaxContributors) break;
			if (item.ValueKind != JsonValueKind.Object) continue;
			string login = StringProperty(item, "login");
			if (!IsValidLogin(login)) continue;
			byte[] avatar = Convert.FromBase64String(StringProperty(item, "avatar"));
			if (!IsSupportedImage(avatar)) continue;
			items.Add(new GitHubContributor(login, avatar));
		}
		return new ContributorSnapshot(fetched, items);
	}

	/// <summary>把快照序列化为缓存文本。</summary>
	internal static string ToCacheJson(ContributorSnapshot snapshot)
	{
		using MemoryStream stream = new();
		using (Utf8JsonWriter writer = new(stream))
		{
			writer.WriteStartObject();
			writer.WriteNumber("version", CacheVersion);
			writer.WriteString("repository", Repository);
			writer.WriteString("fetchedAt", snapshot.FetchedAt);
			writer.WriteStartArray("contributors");
			foreach (GitHubContributor contributor in snapshot.Items)
			{
				writer.WriteStartObject();
				writer.WriteString("login", contributor.Login);
				writer.WriteBase64String("avatar", contributor.Avatar);
				writer.WriteEndObject();
			}
			writer.WriteEndArray();
			writer.WriteEndObject();
		}
		return Encoding.UTF8.GetString(stream.ToArray());
	}

	private async Task WriteCacheAsync(ContributorSnapshot snapshot, CancellationToken cancellationToken)
	{
		try
		{
			string? directory = Path.GetDirectoryName(_cachePath);
			if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
			string temp = _cachePath + ".tmp";
			await File.WriteAllTextAsync(temp, ToCacheJson(snapshot), cancellationToken).ConfigureAwait(false);
			File.Move(temp, _cachePath, overwrite: true);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			// 写不了缓存只影响下次启动要重新下载，内存中的列表仍然可用。
			_warn?.Invoke($"GitHub 贡献者缓存写入失败: {exception.GetType().Name}");
		}
	}

	/// <summary>停止接收新任务，取消并等待读缓存与刷新结束后再释放令牌。</summary>
	public ValueTask DisposeAsync()
	{
		lock (_gate)
		{
			if (_disposeTask is null)
			{
				_disposed = true;
				_disposeTask = DisposeCoreAsync([_cacheLoad ?? Task.CompletedTask, _refresh ?? Task.CompletedTask]);
			}
			return new ValueTask(_disposeTask);
		}
	}

	private async Task DisposeCoreAsync(Task[] pending)
	{
		await _lifetime.CancelAsync().ConfigureAwait(false);
		try { await Task.WhenAll(pending).ConfigureAwait(false); }
		catch (Exception) { /* 刷新失败已由调用方或后台日志处理；此处只确保文件句柄已释放。 */ }
		finally { _lifetime.Dispose(); }
	}

	/// <summary>贡献者列表中的一项：登录名与头像地址。</summary>
	internal sealed record ContributorEntry(string Login, Uri AvatarUri);

	/// <summary>一次下载或缓存读取得到的贡献者快照。</summary>
	internal sealed record ContributorSnapshot(DateTimeOffset FetchedAt, IReadOnlyList<GitHubContributor> Items);
}
