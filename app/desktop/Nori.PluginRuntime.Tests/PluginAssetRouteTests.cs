using System.Net;
using Nori.Core.Assets;

namespace Nori.PluginRuntime.Tests;

public sealed class PluginAssetRouteTests : IAsyncLifetime
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "nori-plugin-asset-tests", Guid.NewGuid().ToString("N"));
	private readonly string _pluginId = "io.nori.asset";
	private readonly HttpClient _client = new();
	private PluginRuntimeHost _runtime = null!;
	private AssetServer _server = null!;

	public async Task InitializeAsync()
	{
		string appRoot = Path.Combine(_root, "app");
		string resourcesRoot = Path.Combine(_root, "resources");
		string pluginRoot = Path.Combine(_root, "plugins", _pluginId, "1.0.0");
		Directory.CreateDirectory(appRoot);
		Directory.CreateDirectory(resourcesRoot);
		Directory.CreateDirectory(Path.Combine(pluginRoot, "web"));
		Directory.CreateDirectory(Path.Combine(_root, "plugins", _pluginId));
		await File.WriteAllTextAsync(Path.Combine(appRoot, "index.html"), "app");
		await File.WriteAllTextAsync(Path.Combine(pluginRoot, "web", "index.html"), "plugin asset");
		await File.WriteAllTextAsync(Path.Combine(pluginRoot, "web", "card.js"), "export const value = 1;");
		await File.WriteAllTextAsync(Path.Combine(pluginRoot, "manifest.json"),
			$"{{\"schemaVersion\":1,\"id\":\"{_pluginId}\",\"name\":\"Asset Plugin\",\"description\":\"Asset route test\",\"version\":\"1.0.0\",\"authors\":[{{\"name\":\"Nori\"}}],\"apiVersion\":\"2.0\",\"minHostVersion\":\"1.0.0\",\"runtime\":{{\"kind\":\"dotnet\",\"assembly\":\"lib/missing.dll\",\"entryType\":\"Missing.Entry\"}},\"ui\":{{\"webRoot\":\"web\"}},\"capabilities\":[],\"optionalCapabilities\":[],\"platforms\":[],\"dependencies\":[]}}");
		await File.WriteAllTextAsync(Path.Combine(_root, "plugins", _pluginId, PluginPackageInstaller.CurrentFileName), "{\"Version\":\"1.0.0\"}");

		_runtime = new PluginRuntimeHost(new PluginRuntimeHostOptions { DataDirectory = _root });
		_runtime.Discover();
		_server = await AssetServer.StartAsync(new AssetServerOptions
		{
			AppRoot = appRoot,
			ResourcesRoot = resourcesRoot,
			AdditionalRoutes = [_runtime.AssetRoute],
		});
	}

	public async Task DisposeAsync()
	{
		_client.Dispose();
		if (_server is not null) await _server.DisposeAsync();
		if (_runtime is not null) await _runtime.DisposeAsync();
		try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { } // NOSONAR: 测试夹具销毁阶段只能尽力清理，不能让清理异常覆盖测试结果。
	}

	[Fact]
	public async Task 插件公开资源可访问且manifest不可访问()
	{
		_runtime.Discover();
		HttpResponseMessage asset = await _client.GetAsync(_server.PublicUrl("plugins", $"{_pluginId}/web/index.html"));
		Assert.Equal(HttpStatusCode.OK, asset.StatusCode);
		Assert.Equal("plugin asset", await asset.Content.ReadAsStringAsync());

		HttpResponseMessage manifest = await _client.GetAsync(new Uri($"{_server.Origin}{_server.Prefix}/plugins/{_pluginId}/manifest.json"));
		Assert.Equal(HttpStatusCode.NotFound, manifest.StatusCode);
	}

	[Theory]
	[InlineData("web/../manifest.json")]
	[InlineData("%2e%2e/manifest.json")]
	[InlineData("web/%2e%2e/manifest.json")]
	public async Task 插件资源路径穿越被拒绝(string path)
	{
		HttpResponseMessage response = await _client.GetAsync(new Uri($"{_server.Origin}{_server.Prefix}/plugins/{_pluginId}/{path}"));
		Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
	}

	[Fact]
	public async Task 隔离卡片模块只向不透明来源开放无凭据读取()
	{
		using HttpRequestMessage request = new(HttpMethod.Get, _server.PublicUrl("plugins", $"{_pluginId}/web/card.js"));
		request.Headers.Add("Origin", "null");
		using HttpResponseMessage response = await _client.SendAsync(request);
		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		Assert.Equal("null", Assert.Single(response.Headers.GetValues("Access-Control-Allow-Origin")));
		Assert.Contains("Origin", response.Headers.Vary);
		Assert.False(response.Headers.Contains("Access-Control-Allow-Credentials"));
		Assert.Equal("export const value = 1;", await response.Content.ReadAsStringAsync());
	}

	[Theory]
	[InlineData("https://example.com")]
	[InlineData("http://localhost:1420")]
	public async Task 公开模块不对任意外部来源开放跨域(string origin)
	{
		using HttpRequestMessage request = new(HttpMethod.Get, _server.PublicUrl("plugins", $"{_pluginId}/web/card.js"));
		request.Headers.Add("Origin", origin);
		using HttpResponseMessage response = await _client.SendAsync(request);
		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
	}

	[Theory]
	[InlineData("POST")]
	[InlineData("OPTIONS")]
	public async Task 隔离卡片资源通道拒绝写入与预检扩权(string method)
	{
		using HttpRequestMessage request = new(new HttpMethod(method), _server.PublicUrl("plugins", $"{_pluginId}/web/card.js"));
		request.Headers.Add("Origin", "null");
		using HttpResponseMessage response = await _client.SendAsync(request);
		Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
		Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
	}

	[Fact]
	public async Task 隔离来源仍不能读取插件私有文件()
	{
		using HttpRequestMessage request = new(HttpMethod.Get, $"{_server.Origin}{_server.Prefix}/plugins/{_pluginId}/manifest.json");
		request.Headers.Add("Origin", "null");
		using HttpResponseMessage response = await _client.SendAsync(request);
		Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
		Assert.DoesNotContain("schemaVersion", await response.Content.ReadAsStringAsync());
	}

	[Fact]
	public async Task 插件路由使用同一随机前缀和Host保护()
	{
		HttpResponseMessage noPrefix = await _client.GetAsync(new Uri($"{_server.Origin}/plugins/{_pluginId}/web/index.html"));
		Assert.Equal(HttpStatusCode.NotFound, noPrefix.StatusCode);

		using HttpRequestMessage request = new(HttpMethod.Get, _server.PublicUrl("plugins", $"{_pluginId}/web/index.html"));
		request.Headers.Host = "evil.example.com";
		HttpResponseMessage forged = await _client.SendAsync(request);
		Assert.Equal(HttpStatusCode.Forbidden, forged.StatusCode);
	}
}
