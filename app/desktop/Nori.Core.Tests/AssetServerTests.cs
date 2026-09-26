using System.Net;
using Nori.Core.Assets;

namespace Nori.Core.Tests;

/// <summary>
/// 回环资源服务的端到端行为: 真的起一个 Kestrel, 真的用 HttpClient 打
/// </summary>
public class AssetServerTests : IAsyncLifetime
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), $"nori-srv-{Guid.NewGuid():N}");
	private string _appRoot = "";
	private string _extraRoot = "";
	private AssetServer _server = null!;
	private readonly HttpClient _client = new();

	public async Task InitializeAsync()
	{
		_appRoot = Path.Combine(_root, "dist");
		_extraRoot = Path.Combine(_root, "extra", "demo");
		Directory.CreateDirectory(_appRoot);
		Directory.CreateDirectory(Path.Combine(_extraRoot, "web"));
		await File.WriteAllTextAsync(Path.Combine(_appRoot, "index.html"), "<!doctype html><title>nori</title>");
		await File.WriteAllTextAsync(Path.Combine(_extraRoot, "web", "index.html"), "plugin");
		await File.WriteAllTextAsync(Path.Combine(_extraRoot, "plugin.json"), "private");
		await File.WriteAllTextAsync(Path.Combine(_extraRoot, "manifest.json"), "private manifest");
		// 根目录之外的"机密"文件, 用来验证穿越被挡住
		await File.WriteAllTextAsync(Path.Combine(_root, "secret.txt"), "TOP SECRET");

		_server = await AssetServer.StartAsync(new AssetServerOptions
		{
			AppRoot = _appRoot,
			AdditionalRoutes = [new TestFileRoute(_extraRoot)],
		});
	}

	public async Task DisposeAsync()
	{
		_client.Dispose();
		await _server.DisposeAsync();
		try
		{
			Directory.Delete(_root, true);
		}
		catch (IOException)
		{
		}
		GC.SuppressFinalize(this);
	}

	[Fact]
	public async Task 前端入口可访问且带窗口参数()
	{
		HttpResponseMessage response = await _client.GetAsync(new Uri(_server.WindowUrl("pet")));
		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		Assert.Equal("text/html; charset=utf-8", response.Content.Headers.ContentType?.ToString());
		Assert.Contains("nori", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
		Assert.Contains("window=pet", _server.WindowUrl("pet"), StringComparison.Ordinal);
	}

	[Fact]
	public async Task 附加资源可取但私有清单不可取()
	{
		HttpResponseMessage asset = await _client.GetAsync(new Uri(_server.PublicUrl("extra", "demo/web/index.html")));
		Assert.Equal(HttpStatusCode.OK, asset.StatusCode);
		Assert.Equal("plugin", await asset.Content.ReadAsStringAsync());

		HttpResponseMessage manifest = await _client.GetAsync(new Uri($"{_server.Origin}{_server.Prefix}/extra/demo/manifest.json"));
		Assert.Equal(HttpStatusCode.NotFound, manifest.StatusCode);
	}

	[Theory]
	[InlineData("%2e%2e/manifest.json")]
	[InlineData("web/../manifest.json")]
	public async Task 附加资源路径穿越被挡住(string relativePath)
	{
		HttpResponseMessage response = await _client.GetAsync(new Uri($"{_server.Origin}{_server.Prefix}/extra/demo/{relativePath}"));
		Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
		Assert.DoesNotContain("private manifest", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
	}

	[Fact]
	public async Task 旧nori_assets挂载已移除()
	{
		HttpResponseMessage response = await _client.GetAsync(new Uri($"{_server.Origin}{_server.Prefix}/nori-assets/live2d/arg-nori/ARGNori.model3.json"));
		Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
	}

	[Theory]
	[InlineData("/app/../secret.txt")]
	[InlineData("/app/assets/../../secret.txt")]
	[InlineData("/app/%2e%2e/secret.txt")]
	public async Task 路径穿越被挡住(string path)
	{
		HttpResponseMessage response = await _client.GetAsync(new Uri($"{_server.Origin}{_server.Prefix}{path}"));
		Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
		Assert.DoesNotContain("TOP SECRET", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
	}

	[Fact]
	public async Task 缺少随机前缀时拿不到任何东西()
	{
		HttpResponseMessage response = await _client.GetAsync(new Uri($"{_server.Origin}/app/index.html"));
		Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
	}

	[Fact]
	public async Task 不存在的应用资源返回404()
	{
		HttpResponseMessage response = await _client.GetAsync(new Uri($"{_server.Origin}{_server.Prefix}/app/missing.js"));
		Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
	}

	[Fact]
	public async Task 非回环Host被拒绝()
	{
		using HttpRequestMessage request = new(HttpMethod.Get, new Uri($"{_server.Origin}{_server.Prefix}/{"app"}/index.html"));
		request.Headers.Host = "evil.example.com";
		HttpResponseMessage response = await _client.SendAsync(request);
		Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
	}

	[Fact]
	public void 服务只绑回环地址() =>
		Assert.StartsWith("http://127.0.0.1:", _server.Origin, StringComparison.Ordinal);

	// ---- 一次性媒体端点 (TTS 下发 / 录音上传) ----

	[Fact]
	public async Task 音频token只能取一次且第二次404()
	{
		string token = _server.Media.PublishAudio([1, 2, 3], "audio/mpeg");
		Uri url = new(_server.MediaUrl(token));

		HttpResponseMessage first = await _client.GetAsync(url);
		Assert.Equal(HttpStatusCode.OK, first.StatusCode);
		Assert.Equal("audio/mpeg", first.Content.Headers.ContentType?.ToString());
		Assert.Equal(new byte[] {1, 2, 3}, await first.Content.ReadAsByteArrayAsync());

		HttpResponseMessage second = await _client.GetAsync(url);
		Assert.Equal(HttpStatusCode.NotFound, second.StatusCode);
	}

	[Fact]
	public async Task 未知音频token返回404()
	{
		HttpResponseMessage response = await _client.GetAsync(new Uri(_server.MediaUrl("00112233445566778899aabbccddeeff")));
		Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
	}

	[Fact]
	public async Task 非法token形状被直接拒绝()
	{
		foreach (string bad in new[] {"..", "with-dash", "with%2Fslash"})
		{
			HttpResponseMessage response = await _client.GetAsync(new Uri($"{_server.Origin}{_server.Prefix}/media/{bad}"));
			Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
		}
	}

	[Fact]
	public async Task 录音上传经票据回传给等待方()
	{
		string token = _server.Media.CreateUploadTicket();
		Task<byte[]> waiting = _server.Media.WaitForUploadAsync(token, TimeSpan.FromSeconds(5));

		using ByteArrayContent body = new([7, 7, 7]);
		HttpResponseMessage response = await _client.PostAsync(new Uri(_server.MediaUrl(token)), body);

		Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
		Assert.Equal(new byte[] {7, 7, 7}, await waiting);
	}

	[Fact]
	public async Task 无票据的上传被拒()
	{
		using ByteArrayContent body = new([1]);
		HttpResponseMessage response = await _client.PostAsync(
			new Uri(_server.MediaUrl("ffffffffffffffffffffffffffffffff")), body);
		Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
	}

	[Fact]
	public async Task 媒体端点同样受Host头与前缀保护()
	{
		string token = _server.Media.PublishAudio([1], "audio/mpeg");

		// 缺前缀
		HttpResponseMessage noPrefix = await _client.GetAsync(new Uri($"{_server.Origin}/media/{token}"));
		Assert.Equal(HttpStatusCode.NotFound, noPrefix.StatusCode);

		// 伪造 Host
		using HttpRequestMessage request = new(HttpMethod.Get, new Uri(_server.MediaUrl(token)));
		request.Headers.Host = "evil.example.com";
		HttpResponseMessage forged = await _client.SendAsync(request);
		Assert.Equal(HttpStatusCode.Forbidden, forged.StatusCode);

		// 上面两次都没消耗掉 token, 正常请求仍能取到
		HttpResponseMessage ok = await _client.GetAsync(new Uri(_server.MediaUrl(token)));
		Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
	}

	private sealed class TestFileRoute(string root) : IAssetRoute
	{
		public string Segment => "extra";

		public AssetRouteFile? Resolve(string relativePath)
		{
			string[] parts = relativePath.Split('/', 2, StringSplitOptions.RemoveEmptyEntries);
			if (parts.Length != 2 || parts[0] != "demo") return null;
			string path = parts[1];
			if (!path.Equals("icon.png", StringComparison.OrdinalIgnoreCase) &&
				!path.StartsWith("web/", StringComparison.OrdinalIgnoreCase) &&
				!path.StartsWith("assets/", StringComparison.OrdinalIgnoreCase) &&
				!path.StartsWith("locales/", StringComparison.OrdinalIgnoreCase)) return null;
			string? resolved = AssetPath.ResolveExact(root, path);
			return resolved is null ? null : new AssetRouteFile(resolved, AssetPath.MimeFor(path));
		}
	}
}
