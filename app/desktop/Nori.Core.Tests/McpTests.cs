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
}
