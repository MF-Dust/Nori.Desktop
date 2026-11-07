using System.Text.Json;
using System.Text.Json.Nodes;
using Nori.Core.Configuration;
using Nori.Core.Data;
using Nori.Core.Mcp;
using Nori.Core.Security;
using Nori.Core.Tests.TestSupport;

namespace Nori.Core.Tests;

public class McpTests : IDisposable
{
	private readonly TempDatabase _tempDatabase = new("nori-mcp-test");
	private readonly NoriDatabase _database;
	private readonly ConfigStore _configStore;

	public McpTests()
	{
		_database = NoriDatabase.Open(_tempDatabase.Path);
		_configStore = new ConfigStore(_database, new FixedKeyStore());
		_configStore.InitDefaults("0.1.0");
	}

	public void Dispose()
	{
		_database.Dispose();
		_tempDatabase.Dispose();
		GC.SuppressFinalize(this);
	}

	[Fact]
	public async Task McpManager_服务器配置增删查()
	{
		using HttpClient httpClient = new();
		await using McpManager manager = new(httpClient, _configStore);

		McpServerConfig config1 = new()
		{
			Id = "fs-server",
			Name = "文件系统服务",
			Transport = McpTransportType.Stdio,
			Command = "npx",
			Args = ["-y", "@modelcontextprotocol/server-filesystem"],
			Enabled = false,
			AutoConnect = false,
		};

		McpServerStatusInfo status = await manager.SaveServerAsync(config1);
		Assert.Equal("fs-server", status.ServerId);
		Assert.Equal("disconnected", status.Status);

		IReadOnlyList<McpServerStatusInfo> servers = await manager.GetServersAsync();
		Assert.Single(servers);
		Assert.Equal("fs-server", servers[0].ServerId);
		Assert.Equal("文件系统服务", servers[0].Name);

		bool deleted = await manager.DeleteServerAsync("fs-server");
		Assert.True(deleted);

		IReadOnlyList<McpServerStatusInfo> serversAfterDelete = await manager.GetServersAsync();
		Assert.Empty(serversAfterDelete);
	}

	[Fact]
	public async Task MCP环境变量独立加密且状态不回传值()
	{
		using HttpClient httpClient = new();
		await using McpManager manager = new(httpClient, _configStore);
		McpServerConfig config = new()
		{
			Id = "secret-server",
			Name = "Secret Server",
			Command = "server",
			Env = new Dictionary<string, string> { ["API_TOKEN"] = "do-not-return" },
			Enabled = false,
			AutoConnect = false,
		};

		await manager.SaveServerAsync(config);

		string metadata = _configStore.RawValue(McpManager.KeyMcpServers);
		string secret = _configStore.RawValue(McpEnvironmentStore.KeyFor(config.Id));
		Assert.DoesNotContain("do-not-return", metadata, StringComparison.Ordinal);
		Assert.DoesNotContain("do-not-return", secret, StringComparison.Ordinal);
		Assert.StartsWith(SecretProtector.Prefix, secret, StringComparison.Ordinal);

		string status = JsonSerializer.Serialize((await manager.GetServersAsync())[0]);
		Assert.DoesNotContain("do-not-return", status, StringComparison.Ordinal);
		Assert.Contains("hasEnvironment", status, StringComparison.Ordinal);

		await manager.SaveServerAsync(config with {Name = "Renamed", Env = null});
		Assert.True(new McpEnvironmentStore(_configStore).HasConfiguredValues(config.Id));

		await manager.SaveServerAsync(config with {Name = "Renamed", Env = new Dictionary<string, string>()});
		Assert.False(new McpEnvironmentStore(_configStore).HasConfiguredValues(config.Id));
	}

	[Fact]
	public async Task 旧MCP明文环境变量会迁移到独立密文()
	{
		JsonNode legacy = JsonNode.Parse("[{\"id\":\"legacy-server\",\"name\":\"Legacy\",\"command\":\"server\",\"env\":{\"TOKEN\":\"legacy-secret\"},\"enabled\":false,\"autoConnect\":false}]")!;
		_configStore.Set(McpManager.KeyMcpServers, new ConfigValue.Json(legacy));
		using HttpClient httpClient = new();
		await using McpManager manager = new(httpClient, _configStore);

		await manager.GetServersAsync();

		Assert.DoesNotContain("legacy-secret", _configStore.RawValue(McpManager.KeyMcpServers), StringComparison.Ordinal);
		Assert.StartsWith(SecretProtector.Prefix, _configStore.RawValue(McpEnvironmentStore.KeyFor("legacy-server")), StringComparison.Ordinal);
	}

	[Fact]
	public async Task 导入预览不含秘密值且确认后停用()
	{
		using HttpClient httpClient = new();
		await using McpManager manager = new(httpClient, _configStore);
		const string document = """
			{"mcpServers":{"demo":{"type":"http","url":"https://example.com/mcp","env":{"API_TOKEN":"do-not-return"},"headers":{"Authorization":"Bearer secret-header"}}}}
			""";

		McpImportPreview preview = manager.PreviewImport(document);
		string previewJson = JsonSerializer.Serialize(preview);
		Assert.DoesNotContain("do-not-return", previewJson, StringComparison.Ordinal);
		Assert.DoesNotContain("secret-header", previewJson, StringComparison.Ordinal);
		Assert.Contains("API_TOKEN", previewJson, StringComparison.Ordinal);
		Assert.Contains("Authorization", previewJson, StringComparison.Ordinal);
		McpImportServerPreview server = Assert.Single(preview.Servers);
		Assert.Equal("http", server.Transport);
		Assert.Equal("https://example.com/mcp", server.Url);

		IReadOnlyList<McpServerStatusInfo> saved = await manager.CommitImportAsync(preview.Token);
		Assert.Equal("disconnected", Assert.Single(saved).Status);
		McpServerConfig stored = Assert.Single(manager.GetServerConfigs());
		Assert.False(stored.Enabled);
		Assert.False(stored.AutoConnect);
		Assert.Equal(McpTransportType.Http, stored.Transport);
		Assert.Null(stored.Env);
		Assert.Null(stored.Headers);
		string metadata = _configStore.RawValue(McpManager.KeyMcpServers);
		Assert.DoesNotContain("do-not-return", metadata, StringComparison.Ordinal);
		Assert.DoesNotContain("secret-header", metadata, StringComparison.Ordinal);
		Assert.DoesNotContain("secret-header", _configStore.RawValue(McpHeaderStore.KeyFor(stored.Id)), StringComparison.Ordinal);
		Assert.StartsWith(SecretProtector.Prefix, _configStore.RawValue(McpHeaderStore.KeyFor(stored.Id)), StringComparison.Ordinal);
		await Assert.ThrowsAsync<InvalidOperationException>(() => manager.CommitImportAsync(preview.Token));
	}

	[Fact]
	public void HTTP与请求头校验且旧SSE配置保持SSE()
	{
		Assert.Throws<InvalidOperationException>(() => McpConfigValidator.Validate(new McpServerConfig
		{
			Id = "bad-http",
			Name = "无效地址",
			Transport = McpTransportType.Http,
			Url = "ftp://example.com/mcp",
		}));
		Assert.Throws<InvalidOperationException>(() => McpConfigValidator.Validate(new McpServerConfig
		{
			Id = "bad-header",
			Name = "无效请求头",
			Transport = McpTransportType.Http,
			Url = "https://example.com/mcp",
			Headers = new Dictionary<string, string> {["Bad Header"] = "x"},
		}));
		McpServerConfig valid = McpConfigValidator.Validate(new McpServerConfig
		{
			Id = "ok-http",
			Name = "HTTP",
			Transport = McpTransportType.Http,
			Url = "https://example.com/mcp",
			Headers = new Dictionary<string, string> {["Authorization"] = "Bearer x"},
		});
		Assert.Equal(McpTransportType.Http, valid.Transport);
		Assert.Equal("Bearer x", valid.Headers?["Authorization"]);

		McpServerConfig sse = Assert.Single(McpConfigImporter.Parse("""
			{"name":"legacy","type":"sse","url":"https://example.com/sse"}
			"""));
		Assert.Equal(McpTransportType.Sse, sse.Transport);
		McpServerConfig detected = Assert.Single(McpConfigImporter.Parse("""
			{"name":"detected","url":"https://example.com/mcp"}
			"""));
		Assert.Equal(McpTransportType.Http, detected.Transport);
	}

	[Fact]
	public async Task 已保存的SSE配置仍可读取且测试沿用密文()
	{
		JsonNode legacy = JsonNode.Parse("""
			[{"id":"sse-server","name":"SSE","transport":"sse","url":"https://example.com/sse","enabled":false,"autoConnect":false}]
			""")!;
		_configStore.Set(McpManager.KeyMcpServers, new ConfigValue.Json(legacy));
		using HttpClient httpClient = new();
		await using McpManager manager = new(httpClient, _configStore);

		McpServerConfig loaded = Assert.Single(manager.GetServerConfigs());
		Assert.Equal(McpTransportType.Sse, loaded.Transport);
		Assert.Equal("https://example.com/sse", loaded.Url);

		await manager.SaveServerAsync(new McpServerConfig
		{
			Id = "header-server",
			Name = "Headers",
			Transport = McpTransportType.Http,
			Url = "https://example.com/mcp",
			Env = new Dictionary<string, string> {["API_TOKEN"] = "do-not-return"},
			Headers = new Dictionary<string, string> {["Authorization"] = "Bearer secret-header"},
			Enabled = false,
			AutoConnect = false,
		});
		McpServerConfig reused = manager.ApplyStoredSecrets(new McpServerConfig
		{
			Id = "header-server",
			Name = "Headers",
			Transport = McpTransportType.Http,
			Url = "https://example.com/mcp",
		});
		Assert.Equal("do-not-return", reused.Env?["API_TOKEN"]);
		Assert.Equal("Bearer secret-header", reused.Headers?["Authorization"]);
		McpServerConfig cleared = manager.ApplyStoredSecrets(reused with
		{
			Env = new Dictionary<string, string>(),
			Headers = new Dictionary<string, string>(),
		});
		Assert.Empty(cleared.Env!);
		Assert.Empty(cleared.Headers!);
	}
}
