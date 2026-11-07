using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Nori.Core.Configuration;
using Nori.Core.Security;
using ModelContextProtocol.Client;

namespace Nori.Core.Mcp;

/// <summary>
/// MCP 服务器集中管理器 (配置管理、连接池调度与工具分发)
/// </summary>
public sealed class McpManager(HttpClient httpClient, ConfigStore configStore) : IAsyncDisposable
{
	public const string KeyMcpServers = "mcp_servers";

	private const int MaxPendingImports = 8;
	private static readonly TimeSpan ImportPreviewLifetime = TimeSpan.FromMinutes(10);

	private readonly HttpClient _httpClient = httpClient;
	private readonly ConfigStore _configStore = configStore;
	private readonly McpEnvironmentStore _environmentStore = new(configStore);
	private readonly McpHeaderStore _headerStore = new(configStore);
	private readonly ConcurrentDictionary<string, OfficialMcpConnection> _activeClients = new();
	private readonly ConcurrentDictionary<string, McpServerStatusInfo> _serverStatuses = new();
	private readonly ConcurrentBag<Task> _backgroundTasks = new();
	private readonly SemaphoreSlim _lock = new(1, 1);
	private readonly Lock _importGate = new();
	private readonly Dictionary<string, PendingImport> _pendingImports = new(StringComparer.Ordinal);
	private readonly CancellationTokenSource _lifetimeCts = new();
	private bool _disposed;

	private sealed record PendingImport(IReadOnlyList<McpServerConfig> Servers, DateTimeOffset ExpiresAt);

	/// <summary>
	/// 获取所有已配置的 MCP 服务器及其当前运行时状态
	/// </summary>
	public async Task<IReadOnlyList<McpServerStatusInfo>> GetServersAsync()
	{
		List<McpServerConfig> configs = LoadConfigs();
		List<McpServerStatusInfo> results = [];

		foreach (McpServerConfig conf in configs)
		{
			_serverStatuses.TryGetValue(conf.Id, out McpServerStatusInfo? status);
			results.Add(OverlaySecrets(conf, status));
		}

		return await Task.FromResult(results);
	}

	/// <summary>
	/// 获取所有已配置的服务器元数据。
	/// 环境变量永远不会从这个前端可见的配置查询出口返回。
	/// </summary>
	public IReadOnlyList<McpServerConfig> GetServerConfigs() => LoadConfigs(includeEnvironment: false);

	/// <summary>
	/// 保存/更新服务器配置并根据启用状态自动连接
	/// </summary>
	public async Task<McpServerStatusInfo> SaveServerAsync(McpServerConfig config)
	{
		config = McpConfigValidator.Validate(config);
		await _lock.WaitAsync();
		try
		{
			List<McpServerConfig> configs = LoadConfigs();
			int existingIndex = configs.FindIndex(c => c.Id == config.Id);
			if (existingIndex >= 0)
			{
				// null 表示前端未回传秘密字段, 必须保留已有密文；显式空字典才表示清除。
				McpServerConfig existing = configs[existingIndex];
				if (config.Env is null) config = config with {Env = existing.Env};
				if (string.Equals(config.Transport, McpTransportType.Stdio, StringComparison.OrdinalIgnoreCase))
					config = config with {Headers = null};
				else if (config.Headers is null)
					config = config with {Headers = existing.Headers};
				config = McpConfigValidator.Validate(config);
				configs[existingIndex] = config;
			}
			else
			{
				configs.Add(config);
			}
			SaveConfigs(configs);

			// 若原先已在运行则先断开
			if (_activeClients.TryRemove(config.Id, out OfficialMcpConnection? oldClient))
			{
				await oldClient.DisposeAsync();
			}

			if (config.Enabled && config.AutoConnect)
			{
				return await ConnectServerInternalAsync(config);
			}

			McpServerStatusInfo disconnectedStatus = SecretStatus(config, "disconnected", null);
			_serverStatuses[config.Id] = disconnectedStatus;
			return disconnectedStatus;
		}
		finally
		{
			_lock.Release();
		}
	}

	/// <summary>
	/// 删除指定 MCP 服务器配置并终止连接
	/// </summary>
	public async Task<bool> DeleteServerAsync(string serverId)
	{
		await _lock.WaitAsync();
		try
		{
			List<McpServerConfig> configs = LoadConfigs();
			int count = configs.RemoveAll(c => c.Id == serverId);
			if (count > 0)
			{
				SaveConfigs(configs);
			}
			if (ConfigValidation.IsValidMcpServerId(serverId))
			{
				_environmentStore.Delete(serverId);
				_headerStore.Delete(serverId);
			}

			if (_activeClients.TryRemove(serverId, out OfficialMcpConnection? client))
			{
				await client.DisposeAsync();
			}
			_serverStatuses.TryRemove(serverId, out _);

			return count > 0;
		}
		finally
		{
			_lock.Release();
		}
	}

	/// <summary>
	/// 主动连接指定服务器并拉取工具与资源
	/// </summary>
	public async Task<McpServerStatusInfo> ConnectServerAsync(string serverId)
	{
		if (string.IsNullOrWhiteSpace(serverId)) throw new InvalidOperationException("MCP 服务器 ID 不能为空");
		McpServerConfig? config = LoadConfigs().FirstOrDefault(c => c.Id == serverId);
		if (config is null)
		{
			throw new InvalidOperationException($"找不到 ID 为 {serverId} 的 MCP 服务器配置");
		}

		await _lock.WaitAsync();
		try
		{
			return await ConnectServerInternalAsync(config);
		}
		finally
		{
			_lock.Release();
		}
	}

	/// <summary>
	/// 主动断开指定服务器连接
	/// </summary>
	public async Task<McpServerStatusInfo> DisconnectServerAsync(string serverId)
	{
		await _lock.WaitAsync();
		try
		{
			if (_activeClients.TryRemove(serverId, out OfficialMcpConnection? client))
			{
				await client.DisposeAsync();
			}

			McpServerConfig? config = LoadConfigs().FirstOrDefault(c => c.Id == serverId);
			McpServerStatusInfo status = config is null
				? new McpServerStatusInfo {ServerId = serverId, Name = serverId, Status = "disconnected"}
				: SecretStatus(config, "disconnected", null);
			_serverStatuses[serverId] = status;
			return status;
		}
		finally
		{
			_lock.Release();
		}
	}

	/// <summary>
	/// 获取所有在线 MCP 服务器暴露的全部工具清单 (带命名空间与所属服务器标识)
	/// </summary>
	public async Task<IReadOnlyList<object>> GetAllToolsAsync()
	{
		List<object> allTools = [];
		foreach ((string serverId, McpServerStatusInfo status) in _serverStatuses)
		{
			if (status.Status != "connected") continue;

			foreach (McpToolDefinition tool in status.Tools)
			{
				allTools.Add(new
				{
					serverId,
					serverName = status.Name,
					toolName = tool.Name,
					fullName = $"mcp__{serverId}__{tool.Name}",
					description = McpConfigValidator.CapDescription(tool.Description),
					inputSchema = McpConfigValidator.CapSchema(tool.InputSchema),
				});
			}
		}
		return await Task.FromResult(allTools);
	}

	/// <summary>
	/// 调用指定 MCP 服务器上的工具
	/// </summary>
	public async Task<McpToolResult> CallToolAsync(string serverId, string toolName, JsonObject? arguments, CancellationToken cancellationToken = default)
	{
		if (string.IsNullOrWhiteSpace(serverId)) throw new InvalidOperationException("MCP 服务器 ID 不能为空");
		JsonObject safeArguments = McpConfigValidator.ValidateToolCall(toolName, arguments);
		if (!_activeClients.TryGetValue(serverId, out OfficialMcpConnection? client) || !client.IsConnected)
		{
			return new McpToolResult
			{
				IsError = true,
				Content = [new McpContentItem { Text = $"MCP 服务器 [{serverId}] 未连接或不在线" }],
			};
		}

		try
		{
			return await client.CallToolAsync(toolName, safeArguments, cancellationToken);
		}
		catch (Exception exception)
		{
			return new McpToolResult
			{
				IsError = true,
				Content = [new McpContentItem { Text = $"调用 MCP 工具失败: {SensitiveDataRedactor.Redact(exception.Message)}" }],
			};
		}
	}

	/// <summary>
	/// 测试指定服务器配置连接有效性并返回工具列表 (不持久化保存)
	/// </summary>
	public async Task<McpServerStatusInfo> TestServerAsync(McpServerConfig config)
	{
		string? blocked = DescribeReuseBlock(config);
		if (blocked is not null)
		{
			return SecretStatus(config, "error", blocked);
		}
		config = McpConfigValidator.Validate(ApplyStoredSecrets(config));
		OfficialMcpConnection? client = null;
		try
		{
			using CancellationTokenSource cts = new(TimeSpan.FromSeconds(15));
			client = await OfficialMcpConnection.ConnectAsync(config, _httpClient, cts.Token);
			IReadOnlyList<McpToolDefinition> tools = await client.ListToolsAsync(cts.Token);
			IReadOnlyList<McpResourceDefinition> resources = await client.ListResourcesAsync(cts.Token);

			return SecretStatus(config, "connected", null) with {Tools = tools, Resources = resources};
		}
		catch (Exception exception)
		{
			return SecretStatus(config, "error", exception.Message);
		}
		finally
		{
			if (client is not null)
			{
				await client.DisposeAsync();
			}
		}
	}

	/// <summary>
	/// 启动时异步尝试自动连接所有配置为启用的服务器
	/// </summary>
	public async Task AutoConnectEnabledAsync()
	{
		if (_disposed) return;
		List<McpServerConfig> configs = LoadConfigs();
		List<Task> startupTasks = [];
		foreach (McpServerConfig config in configs)
		{
			if (config.Enabled && config.AutoConnect)
			{
				Task task = Task.Run(async () =>
				{
					try
					{
						await _lock.WaitAsync(_lifetimeCts.Token);
						try
						{
							await ConnectServerInternalAsync(config);
						}
						finally
						{
							_lock.Release();
						}
					}
					catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
					{
					}
					catch
					{
						/* 异步自启失败已记录在状态中 */
					}
				}, _lifetimeCts.Token);
				_backgroundTasks.Add(task);
				startupTasks.Add(task);
			}
		}
		try { await Task.WhenAll(startupTasks).ConfigureAwait(false); }
		catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested) { }
	}

	public async ValueTask DisposeAsync()
	{
		if (_disposed) return;
		_disposed = true;
		_lifetimeCts.Cancel();
		try
		{
			Task all = Task.WhenAll(_backgroundTasks.ToArray());
			try { await all.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
			catch (TimeoutException) { /* 下方传输通道仍会执行有界的关闭操作 */ }
		}
		catch
		{
			// 退出取消不能阻塞其他资源的清理。
		}

		foreach ((string _, OfficialMcpConnection client) in _activeClients)
		{
			try { await client.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
			catch { /* 退出期间传输通道关闭属于预期行为。 */ }
		}
		_activeClients.Clear();
		_serverStatuses.Clear();
		_lock.Dispose();
		_lifetimeCts.Dispose();
	}

	/// <summary>解析导入文本并暂存。返回值只含名称和键名。</summary>
	public McpImportPreview PreviewImport(string text)
	{
		IReadOnlyList<McpServerConfig> servers = McpConfigImporter.Parse(text);
		string token = Guid.NewGuid().ToString("N");
		lock (_importGate)
		{
			PurgeExpiredImports();
			while (_pendingImports.Count >= MaxPendingImports)
			{
				string oldest = _pendingImports.OrderBy(pair => pair.Value.ExpiresAt).First().Key;
				_pendingImports.Remove(oldest);
			}
			_pendingImports[token] = new PendingImport(servers, DateTimeOffset.UtcNow.Add(ImportPreviewLifetime));
		}
		return new McpImportPreview
		{
			Token = token,
			Servers = servers.Select(ToPreview).ToArray(),
		};
	}

	/// <summary>按预览令牌写入。导入结果一律停用且不自动连接。令牌只能使用一次。</summary>
	public async Task<IReadOnlyList<McpServerStatusInfo>> CommitImportAsync(string token)
	{
		if (string.IsNullOrWhiteSpace(token)) throw new InvalidOperationException("预览已失效");
		PendingImport pending;
		lock (_importGate)
		{
			PurgeExpiredImports();
			if (!_pendingImports.Remove(token, out pending!)) throw new InvalidOperationException("预览已失效");
		}
		List<McpServerStatusInfo> saved = [];
		foreach (McpServerConfig server in pending.Servers)
		{
			saved.Add(await SaveServerAsync(server with {Enabled = false, AutoConnect = false}));
		}
		return saved;
	}

	/// <summary>测试时补回已保存的密文。空字典表示清除，不读取旧值。</summary>
	internal McpServerConfig ApplyStoredSecrets(McpServerConfig config)
	{
		if (!ConfigValidation.IsValidMcpServerId(config.Id)) return config;
		Dictionary<string, string>? environment = config.Env;
		Dictionary<string, string>? headers = config.Headers;
		if (environment is null)
		{
			IReadOnlyDictionary<string, string> stored = _environmentStore.Read(config.Id);
			if (stored.Count > 0) environment = new Dictionary<string, string>(stored);
		}
		if (headers is null && !string.Equals(config.Transport, McpTransportType.Stdio, StringComparison.OrdinalIgnoreCase))
		{
			IReadOnlyDictionary<string, string> stored = _headerStore.Read(config.Id);
			if (stored.Count > 0) headers = new Dictionary<string, string>(stored);
		}
		return config with {Env = environment, Headers = headers};
	}

	private async Task<McpServerStatusInfo> ConnectServerInternalAsync(McpServerConfig config)
	{
		config = McpConfigValidator.Validate(config);
		string? blocked = SecretBlockMessage(_environmentStore.GetIssue(config.Id), _headerStore.GetIssue(config.Id));
		if (blocked is not null)
		{
			McpServerStatusInfo blockedStatus = SecretStatus(config, "error", blocked);
			_serverStatuses[config.Id] = blockedStatus;
			return blockedStatus;
		}

		if (_activeClients.TryRemove(config.Id, out OfficialMcpConnection? existing))
		{
			await existing.DisposeAsync();
		}

		_serverStatuses[config.Id] = SecretStatus(config, "connecting", null);

		OfficialMcpConnection? client = null;
		try
		{
			using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(20));
			using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token, timeout.Token);
			client = await OfficialMcpConnection.ConnectAsync(config, _httpClient, cts.Token);
			IReadOnlyList<McpToolDefinition> tools = await client.ListToolsAsync(cts.Token);
			IReadOnlyList<McpResourceDefinition> resources = await client.ListResourcesAsync(cts.Token);

			_activeClients[config.Id] = client;

			McpServerStatusInfo connectedStatus = SecretStatus(config, "connected", null) with
			{
				Tools = tools,
				Resources = resources,
			};
			_serverStatuses[config.Id] = connectedStatus;
			return connectedStatus;
		}
		catch (Exception exception)
		{
			if (client is not null)
			{
				try { await client.DisposeAsync(); }
				catch { /* 保留原始连接错误 */ }
			}

			McpServerStatusInfo errorStatus = SecretStatus(config, "error", DescribeConnectionError(exception));
			_serverStatuses[config.Id] = errorStatus;
			return errorStatus;
		}
	}

	private static string DescribeConnectionError(Exception exception)
	{
		ClientTransportClosedException? transport = exception as ClientTransportClosedException
			?? (exception as AggregateException)?.Flatten().InnerExceptions.OfType<ClientTransportClosedException>().FirstOrDefault();
		if (transport?.Details is StdioClientCompletionDetails stdio)
		{
			// stderr 可能包含环境变量、访问令牌或用户输入, 只保留退出码。
			return $"MCP 进程已退出 (code={stdio.ExitCode?.ToString() ?? "unknown"})";
		}
		return Nori.Core.Security.SensitiveDataRedactor.Redact(exception.Message);
	}

	private List<McpServerConfig> LoadConfigs(bool includeEnvironment = true)
	{
		List<McpServerConfig> metadata = LoadMetadata();
		List<McpServerConfig> result = [];
		bool migrated = false;
		foreach (McpServerConfig original in metadata)
		{
			McpServerConfig config = original;
			if (original.Env is {Count: > 0})
			{
				// 旧版把 env 和元数据一起写入 mcp_servers; 先尝试加密, 无法加密时
				// 也要从元数据移除明文, 让该服务器按无环境变量 fail-closed 运行。
				try
				{
					_environmentStore.Save(original.Id, original.Env);
				}
				catch (Exception exception) when (exception is SecretKeyStoreException or InvalidOperationException or IOException or UnauthorizedAccessException)
				{
					_configStore.RecordSecretIssue(McpEnvironmentStore.KeyFor(original.Id), SecretIssueCategory.KeyStoreUnavailable);
				}
				config = config with {Env = null};
				migrated = true;
			}
			if (original.Headers is {Count: > 0})
			{
				try
				{
					_headerStore.Save(original.Id, original.Headers);
				}
				catch (Exception exception) when (exception is SecretKeyStoreException or InvalidOperationException or IOException or UnauthorizedAccessException)
				{
					_configStore.RecordSecretIssue(McpHeaderStore.KeyFor(original.Id), SecretIssueCategory.KeyStoreUnavailable);
				}
				config = config with {Headers = null};
				migrated = true;
			}

			if (includeEnvironment && config.Env is null)
			{
				IReadOnlyDictionary<string, string> environment = _environmentStore.Read(config.Id);
				config = config with {Env = environment.Count == 0 ? null : new Dictionary<string, string>(environment)};
			}
			else
			{
				config = config with {Env = null};
			}
			if (includeEnvironment && config.Headers is null
				&& !string.Equals(config.Transport, McpTransportType.Stdio, StringComparison.OrdinalIgnoreCase))
			{
				IReadOnlyDictionary<string, string> headers = _headerStore.Read(config.Id);
				config = config with {Headers = headers.Count == 0 ? null : new Dictionary<string, string>(headers)};
			}
			else
			{
				config = config with {Headers = null};
			}
			result.Add(config);
		}

		if (migrated) SaveMetadata(result);
		return result;
	}

	private List<McpServerConfig> LoadMetadata()
	{
		ConfigValue? val = _configStore.Get(KeyMcpServers);
		if (val is not ConfigValue.Json {Value: JsonArray array}) return [];
		try
		{
			List<McpServerConfig> parsed = array.Deserialize<List<McpServerConfig>>() ?? [];
			List<McpServerConfig> valid = [];
			foreach (McpServerConfig config in parsed)
			{
				try { valid.Add(McpConfigValidator.Validate(config)); }
				catch (InvalidOperationException) { /* 损坏或不安全配置不进入运行时连接池 */ }
			}
			return valid;
		}
		catch (JsonException)
		{
			return [];
		}
	}

	private void SaveConfigs(List<McpServerConfig> configs)
	{
		foreach (McpServerConfig config in configs)
		{
			_environmentStore.Save(config.Id, config.Env);
			if (string.Equals(config.Transport, McpTransportType.Stdio, StringComparison.OrdinalIgnoreCase))
				_headerStore.Delete(config.Id);
			else
				_headerStore.Save(config.Id, config.Headers);
		}
		SaveMetadata(configs);
	}

	private void SaveMetadata(List<McpServerConfig> configs)
	{
		List<McpServerConfig> metadata = configs.Select(config => config with {Env = null, Headers = null}).ToList();
		string json = JsonSerializer.Serialize(metadata);
		JsonNode? node = JsonNode.Parse(json);
		if (node is JsonArray array) _configStore.Set(KeyMcpServers, new ConfigValue.Json(array));
	}

	private McpServerStatusInfo OverlaySecrets(McpServerConfig config, McpServerStatusInfo? status)
	{
		SecretIssue? environment = _environmentStore.GetIssue(config.Id);
		SecretIssue? headers = _headerStore.GetIssue(config.Id);
		string? blocked = SecretBlockMessage(environment, headers);
		McpServerStatusInfo basis = status ?? new McpServerStatusInfo
		{
			ServerId = config.Id,
			Name = config.Name,
			Status = blocked is null ? "disconnected" : "error",
			ErrorMessage = blocked,
		};
		return basis with
		{
			HasEnvironment = config.Env is {Count: > 0},
			SecretIssue = environment?.Code,
			HasHeaders = config.Headers is {Count: > 0},
			HeaderSecretIssue = headers?.Code,
		};
	}

	private McpServerStatusInfo SecretStatus(McpServerConfig config, string status, string? error)
	{
		bool valid = ConfigValidation.IsValidMcpServerId(config.Id);
		return new McpServerStatusInfo
		{
			ServerId = config.Id,
			Name = string.IsNullOrWhiteSpace(config.Name) ? config.Id : config.Name,
			Status = status,
			ErrorMessage = error,
			HasEnvironment = config.Env is {Count: > 0},
			SecretIssue = valid ? _environmentStore.GetIssue(config.Id)?.Code : null,
			HasHeaders = config.Headers is {Count: > 0},
			HeaderSecretIssue = valid ? _headerStore.GetIssue(config.Id)?.Code : null,
		};
	}

	private string? DescribeReuseBlock(McpServerConfig config)
	{
		if (!ConfigValidation.IsValidMcpServerId(config.Id)) return null;
		SecretIssue? environment = config.Env is null ? _environmentStore.GetIssue(config.Id) : null;
		SecretIssue? headers = config.Headers is null
			&& !string.Equals(config.Transport, McpTransportType.Stdio, StringComparison.OrdinalIgnoreCase)
				? _headerStore.GetIssue(config.Id)
				: null;
		return SecretBlockMessage(environment, headers);
	}

	private static string? SecretBlockMessage(SecretIssue? environment, SecretIssue? headers)
	{
		bool envBlocked = environment?.RequiresUserAction == true;
		bool headerBlocked = headers?.RequiresUserAction == true;
		if (envBlocked && headerBlocked) return "MCP 环境变量和请求头密钥不可用, 请重新填写";
		if (envBlocked) return "MCP 环境变量密钥不可用, 请重新填写";
		if (headerBlocked) return "MCP 请求头密钥不可用, 请重新填写";
		return null;
	}

	private void PurgeExpiredImports()
	{
		DateTimeOffset now = DateTimeOffset.UtcNow;
		foreach (string key in _pendingImports.Where(pair => pair.Value.ExpiresAt <= now).Select(pair => pair.Key).ToArray())
			_pendingImports.Remove(key);
	}

	private static McpImportServerPreview ToPreview(McpServerConfig config) => new()
	{
		Name = config.Name,
		Transport = config.Transport,
		Command = config.Command,
		Url = config.Url,
		ArgumentCount = config.Args?.Length ?? 0,
		EnvironmentNames = config.Env?.Keys.OrderBy(name => name, StringComparer.Ordinal).ToArray() ?? [],
		HeaderNames = config.Headers?.Keys.OrderBy(name => name, StringComparer.Ordinal).ToArray() ?? [],
	};
}
