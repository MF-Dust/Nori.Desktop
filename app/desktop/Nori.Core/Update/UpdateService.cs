using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Nori.Core.Configuration;
using Nori.Core.Data;
using Nori.Core.Network;

namespace Nori.Core.Update;

/// <summary>更新流程统一状态枚举。</summary>
public enum UpdaterState
{
	Idle, Checking, Available, UpToDate, Downloading, Verifying, Installing, ReadyToRestart, Error, Cancelled,
}

/// <summary>更新状态快照。</summary>
public sealed record UpdaterStatus(
	UpdaterState State, double Progress, long DownloadedBytes, long TotalBytes,
	string? Message, string CurrentVersion, string? AvailableVersion, string? ReleaseTag,
	string? ReleaseNotes, string? LastCheckedAt, SlotCommitResult? CommittedSlot)
{
	public string? UnavailableReason { get; init; }
	public string? ManualDownloadUrl { get; init; }
}

/// <summary>检查更新返回的结果。</summary>
public sealed record UpdaterCheckResult(
	bool Available, string CurrentVersion, string? LatestVersion, string? ReleaseTag,
	string? ReleaseNotes, DateTimeOffset? PublishedAt, UpdateManifest? Manifest);

/// <summary>仅正式发布的 GitHub 更新服务；下载和解压可取消，原子提交不可取消。</summary>
public sealed class UpdateService : IDisposable
{
	public const string DefaultRepository = "MF-Dust/Nori-Desktop-Pet";
	public const string ConfigKeyLastCheckedAt = "update_last_checked_at";
	public const string ConfigKeyAutoCheck = "auto_check_updates";
	private readonly AppStoragePaths _paths;
	private readonly ConfigStore? _config;
	private readonly string _rid;
	private readonly string _repository;
	private readonly string _productVersion;
	private readonly bool _safeMode;
	private readonly string _runningExecutable;
	private readonly HttpClient _httpClient;
	private readonly TimeSpan _metadataTimeout;
	private readonly NoriHttpClients? _ownedClients;
	private readonly object _stateLock = new();
	private CancellationTokenSource? _activeCts;
	private CancellationTokenSource? _schedulerCts;
	private UpdateManifest? _candidate;
	private UpdaterStatus _status;
	private bool _disposed;
	private long _lastProgressNotification;

	/// <summary>状态转换立即通知；下载进度通知最多每 250 毫秒一次。</summary>
	public event Action? StatusChanged;
	public UpdaterStatus CurrentStatus { get { lock (_stateLock) return _status; } }

	public UpdateService(AppStoragePaths paths, string rid, string productVersion, bool safeMode,
		ConfigStore? config = null, string repository = DefaultRepository, HttpClient? httpClient = null)
		: this(paths, rid, productVersion, safeMode, config, repository, httpClient, Environment.ProcessPath ?? "") { }

	internal UpdateService(AppStoragePaths paths, string rid, string productVersion, bool safeMode,
		ConfigStore? config, string repository, HttpClient? httpClient, string runningExecutable, TimeSpan? metadataTimeout = null)
	{
		_paths = paths;
		_rid = rid;
		_productVersion = productVersion;
		_safeMode = safeMode;
		_config = config;
		_repository = repository;
		_runningExecutable = runningExecutable;
		_metadataTimeout = metadataTimeout ?? TimeSpan.FromSeconds(30);
		if (httpClient is null)
		{
			_ownedClients = NoriHttpClients.Create(false, TimeSpan.FromMinutes(30),
				config?.GetBoolOr("allow_public_system_proxy", true) ?? true);
			_httpClient = _ownedClients.Public;
		}
		else _httpClient = httpClient;
		_httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Nori-Desktop-Pet/1.0");
		_status = new(UpdaterState.Idle, 0, 0, 0, null, productVersion, null, null, null,
			config?.GetStringOr(ConfigKeyLastCheckedAt, ""), null);
		try { ValidateEnvironment(); }
		catch (Exception exception) when (exception is InvalidOperationException or IOException or UnauthorizedAccessException or ArgumentException or JsonException or KeyNotFoundException or FormatException or OverflowException)
		{
			_status = _status with { UnavailableReason = exception.Message, Message = exception.Message };
		}
	}

	/// <summary>启动后 30 秒检查，之后每 24 小时检查；重复调用不会启动多个调度器。</summary>
	public void StartScheduler(Func<UpdaterCheckResult, Task>? onUpdateFound = null)
	{
		lock (_stateLock)
		{
			if (_disposed || _schedulerCts is not null || _status.UnavailableReason is not null) return;
			_schedulerCts = new();
			_ = RunSchedulerAsync(_schedulerCts, onUpdateFound);
		}
	}

	/// <summary>检查最新正式版，保留已提交版本的重启能力。</summary>
	public async Task<UpdaterCheckResult> CheckForUpdateAsync(CancellationToken externalToken = default)
	{
		using CancellationTokenSource operation = BeginOperation(externalToken);
		try
		{
			operation.Token.ThrowIfCancellationRequested();
			lock (_stateLock) _candidate = null;
			SetStatus(CurrentStatus with { State = UpdaterState.Checking, Message = "正在检查更新...", AvailableVersion = null, ReleaseTag = null, ReleaseNotes = null, ManualDownloadUrl = null });
			SlotManifest current = ValidateEnvironment();
			using CancellationTokenSource metadataTimeout = CancellationTokenSource.CreateLinkedTokenSource(operation.Token);
			metadataTimeout.CancelAfter(_metadataTimeout);
			UpdateManifest? manifest = await FetchManifestAsync(metadataTimeout.Token);
			string now = DateTimeOffset.UtcNow.ToString("o");
			_config?.Set(ConfigKeyLastCheckedAt, new ConfigValue.Text(now));
			bool newer = manifest is not null && UpdateManifest.IsStrictlyNewer(current.NumericVersion, current.Revision, manifest.NumericVersion, manifest.Revision);
			lock (_stateLock) _candidate = newer ? manifest : null;
			SetStatus(CurrentStatus with
			{
				State = newer ? UpdaterState.Available : UpdaterState.UpToDate,
				Message = manifest is null ? "此发布没有自动更新清单，请前往 GitHub 手动下载" : newer ? "发现新版本" : "已是最新版本",
				AvailableVersion = manifest?.ProductVersion, ReleaseTag = manifest?.ReleaseTag ?? CurrentStatus.ReleaseTag,
				ReleaseNotes = manifest?.ReleaseNotes ?? CurrentStatus.ReleaseNotes, LastCheckedAt = now,
				Progress = 0, DownloadedBytes = 0, TotalBytes = manifest?.SizeBytes ?? 0,
			});
			return new(newer, _productVersion, manifest?.ProductVersion, CurrentStatus.ReleaseTag, CurrentStatus.ReleaseNotes, manifest?.PublishedAt, manifest);
		}
		catch (OperationCanceledException) { SetFailure(operation.IsCancellationRequested, "检查更新超时或已取消"); throw; }
		catch (Exception exception) { SetFailure(false, $"检查更新失败: {exception.Message}"); throw; }
		finally { EndOperation(operation); }
	}

	/// <summary>下载、校验并安装已确认的候选版本。</summary>
	public async Task<SlotCommitResult> DownloadAndInstallAsync(IProgress<double>? progress = null, CancellationToken externalToken = default)
	{
		using CancellationTokenSource operation = BeginOperation(externalToken);
		string? packagePath = null;
		try
		{
			UpdateManifest candidate;
			lock (_stateLock) candidate = _candidate ?? throw new InvalidOperationException("当前没有可安装的候选版本，请先检查更新");
			UpdateExtractor.EnsureNoReparsePoints(_paths.UpdatesDownloadDirectory, _paths.PackageRoot);
			Directory.CreateDirectory(_paths.UpdatesDownloadDirectory);
			long requiredSpace = candidate.SizeBytes + UpdateExtractor.MaxTotalBytes + 64L * 1024 * 1024;
			if (new DriveInfo(Path.GetPathRoot(_paths.PackageRoot)!).AvailableFreeSpace < requiredSpace)
				throw new InvalidOperationException("磁盘可用空间不足，需同时容纳更新包与展开后的部署槽");
			packagePath = Path.Combine(_paths.UpdatesDownloadDirectory, $"{Guid.NewGuid():N}.{candidate.ArchiveType}");
			SetStatus(CurrentStatus with { State = UpdaterState.Downloading, Message = "正在下载更新包...", Progress = 0, DownloadedBytes = 0, TotalBytes = candidate.SizeBytes });
			using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(operation.Token);
			timeout.CancelAfter(TimeSpan.FromMinutes(30));
			await DownloadAsync(candidate, packagePath, progress, timeout.Token);
			string runningSlot = ResolveRunningSlot();
			SetStatus(CurrentStatus with { State = UpdaterState.Installing, Message = "正在安装部署槽..." });
			SlotCommitResult result = await Task.Run(() => UpdateExtractor.ExtractAndCommitSlot(packagePath,
				_paths.PackageRoot, _rid, Path.GetFileName(runningSlot), candidate,
				Path.Combine(_paths.UpdatesStagingDirectory, Guid.NewGuid().ToString("N")), operation.Token), operation.Token);
			lock (_stateLock) _candidate = null;
			SetStatus(CurrentStatus with { State = UpdaterState.ReadyToRestart, Progress = 1, Message = "新版本已就绪，重启生效", CommittedSlot = result });
			return result;
		}
		catch (OperationCanceledException) { SetFailure(operation.IsCancellationRequested, "更新安装超时或已取消"); throw; }
		catch (Exception exception) { SetFailure(false, $"更新安装失败: {exception.Message}"); throw; }
		finally
		{
			try { if (packagePath is not null) File.Delete(packagePath); } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { Debug.WriteLine($"清理更新下载失败: {exception.Message}"); }
			EndOperation(operation);
		}
	}

	/// <summary>取消当前下载或检查；提交阶段不会中断。</summary>
	public void CancelActiveOperation() { lock (_stateLock) _activeCts?.Cancel(); }

	/// <summary>仅已就绪版本可请求根启动器等待当前进程退出后重启。</summary>
	public void LaunchRestart()
	{
		ValidateEnvironment();
		lock (_stateLock)
		{
			ObjectDisposedException.ThrowIf(_disposed, this);
			if (_status.State != UpdaterState.ReadyToRestart || _status.CommittedSlot is not { } slot || _activeCts is not null)
				throw new InvalidOperationException("当前没有已就绪的更新");
			UpdateExtractor.EnsureNoReparsePoints(Path.Combine(_paths.PackageRoot, ".current"), _paths.PackageRoot);
			if (File.ReadAllText(Path.Combine(_paths.PackageRoot, ".current")).Trim() != slot.SlotName)
				throw new InvalidOperationException("部署槽指针已改变，请重新启动应用");
		}
		using Process process = Process.GetCurrentProcess();
		ProcessStartInfo start = new(ResolveLauncher()) { WorkingDirectory = _paths.PackageRoot, UseShellExecute = false };
		start.ArgumentList.Add("--launcher-wait-pid");
		start.ArgumentList.Add(process.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
		start.ArgumentList.Add("--launcher-wait-start-ticks");
		start.ArgumentList.Add(process.StartTime.ToUniversalTime().Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture));
		using Process? child = Process.Start(start);
		if (child is null) throw new InvalidOperationException("无法启动 Nori 启动器");
	}

	private CancellationTokenSource BeginOperation(CancellationToken token)
	{
		ValidateEnvironment();
		lock (_stateLock)
		{
			ObjectDisposedException.ThrowIf(_disposed, this);
			if (_activeCts is not null) throw new InvalidOperationException("当前已有正在执行的更新任务");
			if (_status.CommittedSlot is not null) throw new InvalidOperationException("新版本已就绪，请先重启应用");
			return _activeCts = CancellationTokenSource.CreateLinkedTokenSource(token);
		}
	}

	private void EndOperation(CancellationTokenSource operation)
	{
		lock (_stateLock) { if (_activeCts == operation) _activeCts = null; }
	}

	private SlotManifest ValidateEnvironment()
	{
		if (_productVersion.Equals("Dev", StringComparison.OrdinalIgnoreCase) || Environment.GetEnvironmentVariable("NORI_DEV") == "1")
			throw new InvalidOperationException("开发环境 (Dev) 禁用自动更新");
		if (_safeMode) throw new InvalidOperationException("安全模式已禁用自动更新功能");
		if (_rid is not ("win-x64" or "linux-x64" or "osx-arm64")) throw new InvalidOperationException($"当前架构 ({_rid}) 不在正式更新支持列表中");
		string launcher = ResolveLauncher();
		UpdateExtractor.EnsureNoReparsePoints(launcher, _paths.PackageRoot);
		if (!File.Exists(launcher)) throw new InvalidOperationException("未找到可信的根启动器入口，拒绝执行更新");
		string slot = ResolveRunningSlot();
		SlotManifest manifest = UpdateExtractor.ReadAndValidateManifest(slot, _rid);
		if (!PathsEqual(_runningExecutable, Path.Combine(slot, manifest.Entrypoint))) throw new InvalidOperationException("当前进程不是部署清单声明的入口");
		if (Path.GetFileName(slot) != $"app-{manifest.NumericVersion}-{manifest.Revision}") throw new InvalidOperationException("运行槽名称与部署清单不匹配");
		return manifest;
	}

	private string ResolveLauncher()
	{
		string expected = OperatingSystem.IsMacOS() ? Path.Combine(_paths.PackageRoot, "Nori.app", "Contents", "MacOS", "Nori")
			: Path.Combine(_paths.PackageRoot, OperatingSystem.IsWindows() ? "Nori.exe" : "Nori");
		string? env = Environment.GetEnvironmentVariable("NORI_LAUNCHER_PATH");
		if (!string.IsNullOrWhiteSpace(env) && !PathsEqual(env, expected)) throw new InvalidOperationException("启动器路径不属于当前发布包");
		return expected;
	}

	private string ResolveRunningSlot()
	{
		DirectoryInfo? directory = new FileInfo(_runningExecutable).Directory;
		while (directory?.Parent is not null && !PathsEqual(directory.Parent.FullName, _paths.PackageRoot)) directory = directory.Parent;
		if (directory?.Parent is null || !directory.Name.StartsWith("app-", StringComparison.Ordinal)) throw new InvalidOperationException("当前进程不在发布包部署槽中");
		string? env = Environment.GetEnvironmentVariable("NORI_DEPLOYMENT_ROOT");
		if (!string.IsNullOrWhiteSpace(env) && !PathsEqual(env, directory.FullName)) throw new InvalidOperationException("部署环境变量与当前进程不一致");
		UpdateExtractor.EnsureNoReparsePoints(directory.FullName, _paths.PackageRoot);
		return directory.FullName;
	}

	private static bool PathsEqual(string left, string right) => string.Equals(Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar),
		Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

	private async Task<UpdateManifest?> FetchManifestAsync(CancellationToken token)
	{
		using JsonDocument document = JsonDocument.Parse(await DownloadMetadataAsync($"https://api.github.com/repos/{_repository}/releases/latest", token));
		JsonElement release = document.RootElement;
		if (release.GetProperty("prerelease").GetBoolean() || release.GetProperty("draft").GetBoolean()) throw new InvalidOperationException("最新发布属于预览版或草稿");
		string tag = release.GetProperty("tag_name").GetString()!;
		string notes = release.TryGetProperty("body", out JsonElement body) ? body.GetString() ?? "" : "";
		JsonElement[] assets = release.GetProperty("assets").EnumerateArray().ToArray();
		JsonElement[] manifests = assets.Where(asset => asset.GetProperty("name").GetString() == $"UPDATE-{_rid}.json").ToArray();
		if (manifests.Length == 0)
		{
			SetStatus(CurrentStatus with { ReleaseTag = tag, ReleaseNotes = notes, ManualDownloadUrl = $"https://github.com/{_repository}/releases/tag/{Uri.EscapeDataString(tag)}" });
			return null;
		}
		if (manifests.Length != 1 || manifests[0].GetProperty("size").GetInt64() > 1024 * 1024) throw new InvalidOperationException("更新清单重复或大小超出限制");
		string manifestUrl = manifests[0].GetProperty("browser_download_url").GetString()!;
		EnsureReleaseAssetUrl(manifestUrl, tag, $"UPDATE-{_rid}.json");
		UpdateManifest manifest = UpdateManifest.FromJson(await DownloadMetadataAsync(manifestUrl, token));
		if (manifest.ReleaseTag != tag || manifest.Rid != _rid) throw new InvalidOperationException("更新清单与发布标签或当前架构不一致");
		JsonElement[] packages = assets.Where(asset => asset.GetProperty("name").GetString() == manifest.PackageName).ToArray();
		if (packages.Length != 1 || packages[0].GetProperty("size").GetInt64() != manifest.SizeBytes
			|| packages[0].GetProperty("browser_download_url").GetString() != manifest.DownloadUrl)
			throw new InvalidOperationException("更新包名称、大小或地址与同一 Release 资产不一致");
		EnsureReleaseAssetUrl(manifest.DownloadUrl, tag, manifest.PackageName);
		return manifest with { ReleaseNotes = notes };
	}

	private void EnsureReleaseAssetUrl(string url, string tag, string name)
	{
		VerifyAllowedUrl(url);
		Uri expected = new($"https://github.com/{_repository}/releases/download/{Uri.EscapeDataString(tag)}/{Uri.EscapeDataString(name)}");
		if (new Uri(url) != expected) throw new InvalidOperationException("更新资产地址不属于所选 Release");
	}

	private async Task<string> DownloadMetadataAsync(string url, CancellationToken token)
	{
		using HttpResponseMessage response = await SendAsync(url, token);
		if (response.Content.Headers.ContentLength > 1024 * 1024) throw new InvalidOperationException("更新元数据大小超过 1 MiB 限制");
		using Stream stream = await response.Content.ReadAsStreamAsync(token);
		using MemoryStream output = new();
		byte[] buffer = new byte[8192];
		int count;
		while ((count = await stream.ReadAsync(buffer.AsMemory(), token)) != 0)
		{
			if (output.Length + count > 1024 * 1024) throw new InvalidOperationException("更新元数据大小超过 1 MiB 限制");
			output.Write(buffer, 0, count);
		}
		return Encoding.UTF8.GetString(output.GetBuffer(), 0, checked((int)output.Length));
	}

	private async Task DownloadAsync(UpdateManifest manifest, string path, IProgress<double>? progress, CancellationToken token)
	{
		using HttpResponseMessage response = await SendAsync(manifest.DownloadUrl, token);
		if (response.Content.Headers.ContentLength is { } length && length != manifest.SizeBytes) throw new InvalidOperationException("更新包响应大小与清单不一致");
		using Stream input = await response.Content.ReadAsStreamAsync(token);
		await using FileStream output = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous);
		using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
		using CancellationTokenSource idle = CancellationTokenSource.CreateLinkedTokenSource(token);
		byte[] buffer = new byte[65536];
		long received = 0;
		while (true)
		{
			idle.CancelAfter(TimeSpan.FromSeconds(60));
			int count = await input.ReadAsync(buffer.AsMemory(), idle.Token);
			idle.CancelAfter(Timeout.InfiniteTimeSpan);
			if (count == 0) break;
			received += count;
			if (received > manifest.SizeBytes) throw new InvalidOperationException("实际下载字节数超过清单大小");
			await output.WriteAsync(buffer.AsMemory(0, count), token);
			hash.AppendData(buffer, 0, count);
			double ratio = (double)received / manifest.SizeBytes;
			progress?.Report(ratio);
			SetStatus(CurrentStatus with { Progress = ratio, DownloadedBytes = received });
		}
		if (received != manifest.SizeBytes) throw new InvalidOperationException("下载文件大小与清单声明不一致");
		SetStatus(CurrentStatus with { State = UpdaterState.Verifying, Message = "正在校验完整性..." });
		if (!Convert.ToHexString(hash.GetHashAndReset()).Equals(manifest.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("更新包 SHA-256 校验失败");
		await output.FlushAsync(token);
	}

	private async Task<HttpResponseMessage> SendAsync(string url, CancellationToken token)
	{
		for (int redirects = 0; ; redirects++)
		{
			VerifyAllowedUrl(url);
			using HttpRequestMessage request = new(HttpMethod.Get, url);
			request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(request.RequestUri!.Host == "api.github.com" ? "application/vnd.github+json" : "application/octet-stream"));
			using CancellationTokenSource headersTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
			headersTimeout.CancelAfter(TimeSpan.FromSeconds(30));
			HttpResponseMessage response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, headersTimeout.Token);
			try
			{
				if (response.StatusCode is HttpStatusCode.Forbidden or (HttpStatusCode)429) throw new InvalidOperationException("GitHub 请求频控超限 (HTTP 403/429)，请稍后重试");
				if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
				{
					if (redirects >= 5) throw new InvalidOperationException("HTTP 重定向次数超过 5 次");
					Uri location = response.Headers.Location ?? throw new InvalidOperationException("重定向缺少 Location 标头");
					url = new Uri(request.RequestUri!, location).AbsoluteUri;
					response.Dispose();
					continue;
				}
				response.EnsureSuccessStatusCode();
				return response;
			}
			catch { response.Dispose(); throw; }
		}
	}

	private void VerifyAllowedUrl(string url)
	{
		if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || uri.Scheme != Uri.UriSchemeHttps || uri.Port != 443 || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0)
			throw new InvalidOperationException("更新地址必须使用无用户凭据的标准 HTTPS");
		if (!UpdateManifest.IsAllowedHost(uri.Host)) throw new InvalidOperationException("更新目标不在 GitHub 白名单内");
		if (uri.Host == "api.github.com" && !uri.AbsolutePath.StartsWith($"/repos/{_repository}/", StringComparison.Ordinal)
			|| uri.Host == "github.com" && !uri.AbsolutePath.StartsWith($"/{_repository}/releases/", StringComparison.Ordinal))
			throw new InvalidOperationException("更新连接路径不属于授权仓库");
	}

	private async Task RunSchedulerAsync(CancellationTokenSource source, Func<UpdaterCheckResult, Task>? onUpdateFound)
	{
		try
		{
			await Task.Delay(TimeSpan.FromSeconds(30), source.Token);
			while (!source.IsCancellationRequested)
			{
				try
				{
					string? last = _config?.GetStringOr(ConfigKeyLastCheckedAt, "");
					bool due = !DateTimeOffset.TryParse(last, out DateTimeOffset checkedAt) || DateTimeOffset.UtcNow - checkedAt >= TimeSpan.FromHours(24) || checkedAt > DateTimeOffset.UtcNow;
					if ((_config?.GetBoolOr(ConfigKeyAutoCheck, true) ?? true) && due && CurrentStatus.CommittedSlot is null)
					{
						UpdaterCheckResult result = await CheckForUpdateAsync(source.Token);
						if (result.Available && onUpdateFound is not null) await onUpdateFound(result);
					}
				}
				catch (OperationCanceledException) when (source.IsCancellationRequested) { break; }
				catch (Exception) { /* 后台失败已记录在状态中，下个周期重试。 */ }
				await Task.Delay(TimeSpan.FromHours(24), source.Token);
			}
		}
		catch (OperationCanceledException) when (source.IsCancellationRequested) { }
		finally { lock (_stateLock) { if (_schedulerCts == source) _schedulerCts = null; source.Dispose(); } }
	}

	private void SetFailure(bool cancelled, string message) => SetStatus(CurrentStatus with { State = cancelled ? UpdaterState.Cancelled : UpdaterState.Error, Message = message });

	private void SetStatus(UpdaterStatus status)
	{
		bool notify;
		lock (_stateLock)
		{
			long now = Environment.TickCount64;
			notify = status.State != _status.State || status.State != UpdaterState.Downloading || now - _lastProgressNotification >= 250;
			_status = status;
			if (notify) _lastProgressNotification = now;
		}
		if (notify && StatusChanged is { } handlers)
		{
			foreach (Action handler in handlers.GetInvocationList())
			{
				try { handler(); }
				catch (Exception exception) { Debug.WriteLine($"更新状态通知失败: {exception.Message}"); }
			}
		}
	}

	public void Dispose()
	{
		lock (_stateLock)
		{
			if (_disposed) return;
			_disposed = true;
			_schedulerCts?.Cancel();
			_activeCts?.Cancel();
		}
		// CTS 由各自任务释放，避免任务 finally 操作已释放的对象。
		_ownedClients?.Dispose();
	}
}
