using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks.Sources;
using Nori.Core.Data;
using Nori.Core.Update;
using Xunit;

namespace Nori.Core.Tests;

/// <summary>真实部署上下文、网络信任边界与状态机回归。</summary>
public sealed class UpdateServiceTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), $"nori-update-{Guid.NewGuid():N}");
	private readonly AppStoragePaths _paths;
	private readonly string _executable;
	private readonly byte[] _package;
	private readonly JsonObject _manifest;
	private readonly JsonObject _release;
	private int _requests;

	public UpdateServiceTests()
	{
		_paths = new(_root);
		_paths.EnsureCreated();
		string launcher = OperatingSystem.IsMacOS() ? Path.Combine(_root, "Nori.app", "Contents", "MacOS", "Nori") : Path.Combine(_root, OperatingSystem.IsWindows() ? "Nori.exe" : "Nori");
		Directory.CreateDirectory(Path.GetDirectoryName(launcher)!);
		File.WriteAllText(launcher, "启动器");
		string slot = Path.Combine(_root, "app-1.0.0-7");
		Directory.CreateDirectory(slot);
		_executable = Path.Combine(slot, "Nori.Desktop.exe");
		File.WriteAllText(_executable, "当前程序");
		File.WriteAllText(Path.Combine(slot, "deployment.json"), SlotJson("1.0.0", 7));
		File.WriteAllText(Path.Combine(_root, ".current"), "app-9.0.0-0\n");
		using MemoryStream memory = new();
		using (ZipArchive archive = new(memory, ZipArchiveMode.Create, true))
		{
			using (StreamWriter writer = new(archive.CreateEntry("app-1.1.0-0/deployment.json").Open())) writer.Write(SlotJson("1.1.0", 0));
			using (StreamWriter writer = new(archive.CreateEntry("app-1.1.0-0/Nori.Desktop.exe").Open())) writer.Write("新程序");
		}
		_package = memory.ToArray();
		_manifest = new()
		{
			["schema_version"] = 1, ["product_version"] = "1.1.0-test", ["numeric_version"] = "1.1.0", ["revision"] = 0,
			["rid"] = "win-x64", ["release_tag"] = "v1.1.0-test", ["package_name"] = "nori.zip",
			["download_url"] = AssetUrl("nori.zip"), ["sha256"] = Convert.ToHexString(SHA256.HashData(_package)),
			["size_bytes"] = _package.Length, ["archive_type"] = "zip", ["entrypoint"] = "Nori.Desktop.exe", ["launcher_protocol"] = 1,
		};
		_release = new()
		{
			["tag_name"] = "v1.1.0-test", ["prerelease"] = false, ["draft"] = false, ["body"] = "新版发布",
			["assets"] = new JsonArray(
				new JsonObject { ["name"] = "UPDATE-win-x64.json", ["browser_download_url"] = AssetUrl("UPDATE-win-x64.json"), ["size"] = 1000 },
				new JsonObject { ["name"] = "nori.zip", ["browser_download_url"] = AssetUrl("nori.zip"), ["size"] = _package.Length }),
		};
	}

	private static string SlotJson(string version, int revision) => JsonSerializer.Serialize(new
	{
		schema_version = 1, product_version = version + "-test", numeric_version = version, revision, rid = "win-x64", entrypoint = "Nori.Desktop.exe",
	});
	private static string AssetUrl(string name) => $"https://github.com/{UpdateService.DefaultRepository}/releases/download/v1.1.0-test/{name}";
	private HttpResponseMessage Respond(HttpRequestMessage request)
	{
		_requests++;
		string url = request.RequestUri!.AbsoluteUri;
		return new(HttpStatusCode.OK)
		{
			Content = url.EndsWith("/latest", StringComparison.Ordinal) ? new StringContent(_release.ToJsonString())
				: url.EndsWith(".json", StringComparison.Ordinal) ? new StringContent(_manifest.ToJsonString()) : new ByteArrayContent(_package),
		};
	}
	private UpdateService Create(HttpClient client, string product = "1.0.0-test", bool safe = false, string rid = "win-x64", string? executable = null) =>
		new(_paths, rid, product, safe, null, UpdateService.DefaultRepository, client, executable ?? _executable);

	[Theory]
	[InlineData("Dev", false, "win-x64", "开发环境")]
	[InlineData("1.0.0", true, "win-x64", "安全模式")]
	[InlineData("1.0.0", false, "unsupported", "不在正式更新支持列表")]
	public async Task DisallowedEnvironment_HasReasonAndNeverConnects(string product, bool safe, string rid, string reason)
	{
		using HttpClient client = new(new Handler(Respond));
		using UpdateService service = Create(client, product, safe, rid);
		Assert.Contains(reason, service.CurrentStatus.UnavailableReason);
		Assert.Contains(reason, (await Assert.ThrowsAsync<InvalidOperationException>(() => service.CheckForUpdateAsync())).Message);
		service.StartScheduler();
		Assert.Equal(0, _requests);
	}

	[Fact]
	public async Task CurrentPointerCannotImpersonateRunningSlot()
	{
		using HttpClient client = new(new Handler(Respond));
		using UpdateService service = Create(client, executable: Environment.ProcessPath!);
		Assert.NotNull(service.CurrentStatus.UnavailableReason);
		await Assert.ThrowsAsync<InvalidOperationException>(() => service.CheckForUpdateAsync());
		Assert.Equal(0, _requests);
	}

	[Theory]
	[InlineData(403)]
	[InlineData(429)]
	public async Task RateLimitNeverBypassesApi(int code)
	{
		int requests = 0;
		using HttpClient client = new(new Handler(_ => { requests++; return new((HttpStatusCode)code); }));
		using UpdateService service = Create(client);
		Assert.Contains("403/429", (await Assert.ThrowsAsync<InvalidOperationException>(() => service.CheckForUpdateAsync())).Message);
		Assert.Equal(1, requests);
	}

	[Fact]
	public async Task CurrentManifestRevisionPreventsDowngrade_AndClearsStaleCandidate()
	{
		using HttpClient client = new(new Handler(Respond));
		using UpdateService service = Create(client);
		Assert.True((await service.CheckForUpdateAsync()).Available);
		_manifest["numeric_version"] = "1.0.0";
		_manifest["revision"] = 6;
		Assert.False((await service.CheckForUpdateAsync()).Available);
		await Assert.ThrowsAsync<InvalidOperationException>(() => service.DownloadAndInstallAsync());
		Assert.Equal(4, _requests);
	}

	[Fact]
	public async Task SuccessfulInstallNotifiesAndPreservesReadyState()
	{
		using HttpClient client = new(new Handler(Respond));
		using UpdateService service = Create(client);
		List<UpdaterState> states = [];
		service.StatusChanged += () => states.Add(service.CurrentStatus.State);
		UpdaterCheckResult check = await service.CheckForUpdateAsync();
		Assert.True(check.Available);
		Assert.Equal("新版发布", check.ReleaseNotes);
		SlotCommitResult installed = await service.DownloadAndInstallAsync();
		Assert.Equal("app-1.1.0-0", installed.SlotName);
		Assert.Equal("app-1.1.0-0", File.ReadAllText(Path.Combine(_root, ".current")).Trim());
		Assert.True(File.Exists(_executable));
		Assert.Contains(UpdaterState.Downloading, states);
		Assert.Contains(UpdaterState.Verifying, states);
		Assert.Contains(UpdaterState.Installing, states);
		Assert.Contains(UpdaterState.ReadyToRestart, states);
		await Assert.ThrowsAsync<InvalidOperationException>(() => service.CheckForUpdateAsync());
		await Assert.ThrowsAsync<InvalidOperationException>(() => service.DownloadAndInstallAsync());
		Assert.Equal(UpdaterState.ReadyToRestart, service.CurrentStatus.State);
		Assert.Empty(Directory.GetFiles(_paths.UpdatesDownloadDirectory));
	}

	[Fact]
	public void RestartWithoutCommittedUpdateIsRejected()
	{
		using HttpClient client = new(new Handler(Respond));
		using UpdateService service = Create(client);
		Assert.Contains("已就绪", Assert.Throws<InvalidOperationException>(service.LaunchRestart).Message);
		service.CancelActiveOperation();
		Assert.Equal(UpdaterState.Idle, service.CurrentStatus.State);
	}

	[Fact]
	public async Task PreCancelledCheckDoesNotLeaveActiveOperation()
	{
		using HttpClient client = new(new Handler(Respond));
		using UpdateService service = Create(client);
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.CheckForUpdateAsync(new CancellationToken(true)));
		Assert.True((await service.CheckForUpdateAsync()).Available);
	}

	[Fact]
	public async Task HashMismatchLeavesCurrentAndCleansDownload()
	{
		_manifest["sha256"] = new string('f', 64);
		using HttpClient client = new(new Handler(Respond));
		using UpdateService service = Create(client);
		await service.CheckForUpdateAsync();
		Assert.Contains("SHA-256", (await Assert.ThrowsAsync<InvalidOperationException>(() => service.DownloadAndInstallAsync())).Message);
		Assert.Empty(Directory.GetFiles(_paths.UpdatesDownloadDirectory));
		Assert.Equal("app-9.0.0-0", File.ReadAllText(Path.Combine(_root, ".current")).Trim());
	}

	[Theory]
	[InlineData("rid", "linux-x64")]
	[InlineData("release_tag", "v-other")]
	[InlineData("package_name", "other.zip")]
	[InlineData("download_url", "https://github.com/MF-Dust/Nori-Desktop-Pet/releases/download/other/nori.zip")]
	public async Task ManifestMustBindToSameReleaseAsset(string field, string value)
	{
		_manifest[field] = value;
		using HttpClient client = new(new Handler(Respond));
		using UpdateService service = Create(client);
		await Assert.ThrowsAsync<InvalidOperationException>(() => service.CheckForUpdateAsync());
	}

	[Fact]
	public async Task AssetSizeMismatchRejected()
	{
		_manifest["size_bytes"] = _package.Length + 1;
		using HttpClient client = new(new Handler(Respond));
		using UpdateService service = Create(client);
		await Assert.ThrowsAsync<InvalidOperationException>(() => service.CheckForUpdateAsync());
	}

	[Fact]
	public async Task MissingManifestProvidesManualLinkWithoutDownload()
	{
		_release["assets"] = new JsonArray();
		using HttpClient client = new(new Handler(Respond));
		using UpdateService service = Create(client);
		Assert.False((await service.CheckForUpdateAsync()).Available);
		Assert.Equal($"https://github.com/{UpdateService.DefaultRepository}/releases/tag/v1.1.0-test", service.CurrentStatus.ManualDownloadUrl);
		Assert.Equal(1, _requests);
		await Assert.ThrowsAsync<InvalidOperationException>(() => service.DownloadAndInstallAsync());
	}

	[Theory]
	[InlineData("https://user@github.com/MF-Dust/Nori-Desktop-Pet/releases/asset")]
	[InlineData("https://github.com:444/MF-Dust/Nori-Desktop-Pet/releases/asset")]
	[InlineData("https://localhost/asset")]
	[InlineData("http://github.com/MF-Dust/Nori-Desktop-Pet/releases/asset")]
	[InlineData("https://github.com/other/repo/releases/asset")]
	public async Task UnsafeRedirectRejectedBeforeSecondRequest(string location)
	{
		int requests = 0;
		using HttpClient client = new(new Handler(_ =>
		{
			requests++;
			HttpResponseMessage response = new(HttpStatusCode.Found);
			response.Headers.Location = new(location);
			return response;
		}));
		using UpdateService service = Create(client);
		await Assert.ThrowsAsync<InvalidOperationException>(() => service.CheckForUpdateAsync());
		Assert.Equal(1, requests);
	}

	[Fact]
	public async Task RedirectLimitIsFive()
	{
		int requests = 0;
		using HttpClient client = new(new Handler(request =>
		{
			requests++;
			HttpResponseMessage response = new(HttpStatusCode.Found);
			response.Headers.Location = request.RequestUri;
			return response;
		}));
		using UpdateService service = Create(client);
		await Assert.ThrowsAsync<InvalidOperationException>(() => service.CheckForUpdateAsync());
		Assert.Equal(6, requests);
	}

	[Fact]
	public async Task UnknownMetadataLengthIsBoundedWhileStreaming()
	{
		using CountingStream stream = new();
		using HttpClient client = new(new Handler(_ => new(HttpStatusCode.OK) { Content = new StreamContent(stream) }));
		using UpdateService service = Create(client);
		Assert.Contains("1 MiB", (await Assert.ThrowsAsync<InvalidOperationException>(() => service.CheckForUpdateAsync())).Message);
		Assert.InRange(stream.ReadBytes, 1024 * 1024 + 1, 1024 * 1024 + 8192);
	}

	[Fact]
	public async Task DownloadConsumesEachValueTaskExactlyOnce()
	{
		using SingleConsumptionStream stream = new(_package);
		using HttpClient client = new(new Handler(request => request.RequestUri!.AbsoluteUri.EndsWith(".zip", StringComparison.Ordinal)
			? new(HttpStatusCode.OK) { Content = new StreamContent(stream) } : Respond(request)));
		using UpdateService service = Create(client);
		await service.CheckForUpdateAsync();
		await service.DownloadAndInstallAsync();
		Assert.Equal(UpdaterState.ReadyToRestart, service.CurrentStatus.State);
		Assert.True(stream.Consumed > 1);
	}

	[Fact]
	public async Task MetadataTimeoutStillAppliesAfterHeaders()
	{
		TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
		using BlockingStream stream = new(started);
		using HttpClient client = new(new Handler(_ => new(HttpStatusCode.OK) { Content = new StreamContent(stream) }));
		using UpdateService service = Create(client);
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.CheckForUpdateAsync().WaitAsync(TimeSpan.FromSeconds(40)));
		Assert.True(started.Task.IsCompleted);
		Assert.Equal(UpdaterState.Error, service.CurrentStatus.State);
	}

	[Fact]
	public async Task BrokenCurrentManifestIsUnavailableInsteadOfCrashingConstruction()
	{
		File.WriteAllText(Path.Combine(Path.GetDirectoryName(_executable)!, "deployment.json"), "{}");
		using HttpClient client = new(new Handler(Respond));
		using UpdateService service = Create(client);
		Assert.NotNull(service.CurrentStatus.UnavailableReason);
		await Assert.ThrowsAsync<KeyNotFoundException>(() => service.CheckForUpdateAsync());
		Assert.Equal(0, _requests);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task CancelOrDisposeActiveReadTerminatesWithoutDisposedCts(bool dispose)
	{
		TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
		using BlockingStream stream = new(started);
		using HttpClient client = new(new Handler(_ => new(HttpStatusCode.OK) { Content = new StreamContent(stream) }));
		using UpdateService service = Create(client);
		Task check = service.CheckForUpdateAsync();
		await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
		await Assert.ThrowsAsync<InvalidOperationException>(() => service.CheckForUpdateAsync());
		if (dispose) service.Dispose(); else service.CancelActiveOperation();
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => check);
		if (!dispose) Assert.Equal(UpdaterState.Cancelled, service.CurrentStatus.State);
	}

	public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } }
	private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
	{
		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(respond(request));
	}
	private class CountingStream : Stream
	{
		public int ReadBytes { get; private set; }
		public override bool CanRead => true;
		public override bool CanSeek => false;
		public override bool CanWrite => false;
		public override long Length => throw new NotSupportedException();
		public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
		public override int Read(byte[] buffer, int offset, int count) { ReadBytes += count; return count; }
		public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) { ReadBytes += buffer.Length; return ValueTask.FromResult(buffer.Length); }
		public override void Flush() => throw new NotSupportedException();
		public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
		public override void SetLength(long value) => throw new NotSupportedException();
		public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
	}
	private sealed class SingleConsumptionStream(byte[] data) : CountingStream, IValueTaskSource<int>
	{
		private ManualResetValueTaskSourceCore<int> _source;
		private bool _consumed;
		private int _position;
		public int Consumed { get; private set; }
		public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
		{
			_source.Reset();
			_consumed = false;
			int count = Math.Min(buffer.Length, data.Length - _position);
			data.AsMemory(_position, count).CopyTo(buffer);
			_position += count;
			_source.SetResult(count);
			return new(this, _source.Version);
		}
		public int GetResult(short token)
		{
			if (_consumed) throw new InvalidOperationException("同一 ValueTask 被重复消费");
			_consumed = true;
			Consumed++;
			return _source.GetResult(token);
		}
		public ValueTaskSourceStatus GetStatus(short token) => _source.GetStatus(token);
		public void OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags) => _source.OnCompleted(continuation, state, token, flags);
	}

	private sealed class BlockingStream(TaskCompletionSource started) : CountingStream
	{
		public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
		{
			started.TrySetResult();
			await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
			return 0;
		}
	}
}
