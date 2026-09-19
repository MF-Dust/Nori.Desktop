using System.Net;
using Nori.Core.Assets;

namespace Nori.PluginRuntime.Tests;

public sealed class PluginAssetRouteReviewTests : IAsyncLifetime
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "nori-plugin-asset-review", Guid.NewGuid().ToString("N"));
	private readonly string _pluginId = "io." + new string('a', 70);
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
		await File.WriteAllTextAsync(Path.Combine(pluginRoot, "web", "index.html"), "long-plugin-id");
		await File.WriteAllTextAsync(Path.Combine(pluginRoot, "manifest.json"),
			$"{{\"schemaVersion\":1,\"id\":\"{_pluginId}\",\"name\":\"Long Asset Plugin\",\"description\":\"Asset route test\",\"version\":\"1.0.0\",\"authors\":[{{\"name\":\"Nori\"}}],\"apiVersion\":\"2.0\",\"minHostVersion\":\"1.0.0\",\"runtime\":{{\"kind\":\"dotnet\",\"assembly\":\"lib/missing.dll\",\"entryType\":\"Missing.Entry\"}},\"ui\":{{\"webRoot\":\"web\"}},\"capabilities\":[],\"optionalCapabilities\":[],\"platforms\":[],\"dependencies\":[]}}");
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

	[System.Diagnostics.CodeAnalysis.SuppressMessage("CodeQuality", "S2486", Justification = "测试夹具销毁只能尽力清理，不能让清理异常覆盖测试结果。")]
	public async Task DisposeAsync()
	{
		_client.Dispose();
		if (_server is not null) await _server.DisposeAsync();
		if (_runtime is not null) await _runtime.DisposeAsync();
		try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { }
	}

	[Fact]
	public async Task 超过64位但符合Manifest规范的插件ID可以读取公开资源()
	{
		HttpResponseMessage response = await _client.GetAsync(_server.PublicUrl("plugins", $"{_pluginId}/web/index.html"));

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		Assert.Equal("long-plugin-id", await response.Content.ReadAsStringAsync());
	}
}
