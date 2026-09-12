using System.Text.Json;

namespace Nori.Desktop.Settings.Pages;

/// <summary>MCP 工具定义的脱敏本地表示。</summary>
public sealed record McpToolItem(
	string Name,
	string Description,
	string PermissionLevel,
	string Category,
	bool Enabled,
	string? ServerId,
	JsonElement InputSchema);

/// <summary>MCP 服务器状态的脱敏本地表示。</summary>
public sealed record McpServerItem(
	string Id,
	string Name,
	string Status,
	string? ErrorMessage,
	IReadOnlyList<McpToolItem> Tools,
	int ResourceCount,
	bool HasEnvironment,
	string? SecretIssue);

/// <summary>MCP 服务编辑表单。</summary>
public sealed record McpServerDraft(
	string Id,
	string Name,
	string Transport,
	string Command,
	IReadOnlyList<string> Arguments,
	IReadOnlyDictionary<string, string> Environment,
	string? Url,
	bool Enabled,
	bool AutoConnect);

/// <summary>MCP 与工具设置页的状态和宿主命令编排。</summary>
public sealed class McpSettingsViewModel : SettingsPageViewModelBase
{
	private IReadOnlyList<McpServerItem> _servers = [];
	private IReadOnlyList<McpToolItem> _builtinTools = [];
	private string _searchText = "";
	private bool _showTools;

	/// <summary>创建 MCP ViewModel。</summary>
	public McpSettingsViewModel(SettingsService service) : base(service) { }

	/// <summary>已配置服务器。</summary>
	public IReadOnlyList<McpServerItem> Servers
	{
		get => _servers;
		private set => SetProperty(ref _servers, value);
	}

	/// <summary>内置工具。</summary>
	public IReadOnlyList<McpToolItem> BuiltinTools
	{
		get => _builtinTools;
		private set => SetProperty(ref _builtinTools, value);
	}

	/// <summary>过滤文本。</summary>
	public string SearchText
	{
		get => _searchText;
		set
		{
			if (!SetProperty(ref _searchText, value ?? "")) return;
			NotifyChanged(nameof(FilteredServers));
			NotifyChanged(nameof(FilteredTools));
		}
	}

	/// <summary>当前是否显示工具列表。</summary>
	public bool ShowTools
	{
		get => _showTools;
		set => SetProperty(ref _showTools, value);
	}

	/// <summary>过滤后的服务器。</summary>
	public IReadOnlyList<McpServerItem> FilteredServers =>
		Servers.Where(item => Matches(item.Id, item.Name, item.ErrorMessage)).ToArray();

	/// <summary>过滤后的工具。</summary>
	public IReadOnlyList<McpToolItem> FilteredTools =>
		BuiltinTools.Where(item => Matches(item.Name, item.Description)).ToArray();

	/// <inheritdoc />
	public override async Task RefreshAsync(CancellationToken cancellationToken = default)
	{
		IsBusy = true;
		try
		{
			JsonElement serverResult = await Service.ExecuteAsync("mcp_get_servers", cancellationToken: cancellationToken).ConfigureAwait(true);
			Servers = ParseServers(SettingsJson.RootArray(serverResult));
			JsonElement snapshot = await Service.GetSnapshotAsync(cancellationToken).ConfigureAwait(true);
			BuiltinTools = ParseBuiltinTools(SettingsJson.Array(snapshot, "tools"));
			NotifyChanged(nameof(FilteredServers));
			NotifyChanged(nameof(FilteredTools));
		}
		finally
		{
			IsBusy = false;
		}
	}

	/// <summary>保存服务器配置。</summary>
	public async Task SaveServerAsync(McpServerDraft draft, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(draft);
		if (string.IsNullOrWhiteSpace(draft.Name)) throw new ArgumentException("MCP 服务名称不能为空。", nameof(draft));
		string transport = draft.Transport.Trim().ToLowerInvariant();
		if (transport is not ("stdio" or "sse")) throw new ArgumentException("MCP 传输协议必须是 stdio 或 SSE。", nameof(draft));
		string normalizedUrl = "";
		if (transport == "sse" && !TryValidateHttpUrl(draft.Url, out normalizedUrl))
			throw new ArgumentException("SSE 地址必须是 HTTP 或 HTTPS 地址。", nameof(draft));
		object args = new
		{
			id = string.IsNullOrWhiteSpace(draft.Id) ? $"mcp_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds():x}" : draft.Id.Trim(),
			name = draft.Name.Trim(),
			transport,
			command = transport == "stdio" ? draft.Command.Trim() : null,
			args = transport == "stdio" ? draft.Arguments.ToArray() : Array.Empty<string>(),
			env = draft.Environment.Count == 0 ? null : new Dictionary<string, string>(draft.Environment, StringComparer.Ordinal),
			url = transport == "sse" ? normalizedUrl : null,
			enabled = draft.Enabled,
			autoConnect = draft.AutoConnect,
		};
		await ExecuteAsync("mcp_save_server", args, cancellationToken).ConfigureAwait(true);
		await RefreshAsync(cancellationToken).ConfigureAwait(true);
	}

	/// <summary>测试尚未保存的服务器配置。</summary>
	public async Task<McpServerItem?> TestServerAsync(McpServerDraft draft, CancellationToken cancellationToken = default)
	{
		JsonElement result = await ExecuteConfigCommandAsync("mcp_test_server", draft, cancellationToken).ConfigureAwait(true);
		return ParseServer(result);
	}

	/// <summary>导入公开 MCP 配置。</summary>
	public async Task ImportAsync(string url, CancellationToken cancellationToken = default)
	{
		if (!TryValidateHttpUrl(url, out string normalized)) throw new ArgumentException("导入地址必须是公开的 HTTP 或 HTTPS 地址。", nameof(url));
		await ExecuteAsync("mcp_import_url", new {url = normalized}, cancellationToken).ConfigureAwait(true);
		await RefreshAsync(cancellationToken).ConfigureAwait(true);
	}

	/// <summary>连接服务器。</summary>
	public async Task ConnectAsync(McpServerItem server, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(server);
		await ExecuteAsync("mcp_connect_server", new {id = server.Id}, cancellationToken).ConfigureAwait(true);
		await RefreshAsync(cancellationToken).ConfigureAwait(true);
	}

	/// <summary>断开服务器。</summary>
	public async Task DisconnectAsync(McpServerItem server, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(server);
		await ExecuteAsync("mcp_disconnect_server", new {id = server.Id}, cancellationToken).ConfigureAwait(true);
		await RefreshAsync(cancellationToken).ConfigureAwait(true);
	}

	/// <summary>删除服务器配置。</summary>
	public async Task DeleteAsync(McpServerItem server, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(server);
		await ExecuteAsync("mcp_delete_server", new {id = server.Id}, cancellationToken).ConfigureAwait(true);
		await RefreshAsync(cancellationToken).ConfigureAwait(true);
	}

	/// <summary>启用或停用内置工具。</summary>
	public async Task ToggleToolAsync(McpToolItem tool, bool enabled, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(tool);
		await ExecuteAsync("tools_set_enabled", new {name = tool.Name, enabled}, cancellationToken).ConfigureAwait(true);
		await RefreshAsync(cancellationToken).ConfigureAwait(true);
	}

	/// <summary>执行内置或 MCP 工具测试。</summary>
	public async Task<JsonElement> ExecuteToolAsync(McpToolItem tool, string jsonArguments, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(tool);
		JsonElement parsed = SettingsJson.Parse(string.IsNullOrWhiteSpace(jsonArguments) ? "{}" : jsonArguments);
		if (parsed.ValueKind != JsonValueKind.Object) throw new ArgumentException("工具参数必须是 JSON 对象。", nameof(jsonArguments));
		object arguments = SettingsJson.Deserialize<Dictionary<string, object?>>(parsed) ?? [];
		return tool.ServerId is {Length: > 0} serverId
			? await ExecuteAsync("mcp_call_tool", new {serverId, toolName = tool.Name, arguments}, cancellationToken).ConfigureAwait(true)
			: await ExecuteAsync("tools_execute_manual", new {name = tool.Name, arguments}, cancellationToken).ConfigureAwait(true);
	}

	/// <summary>创建默认服务器草稿。</summary>
	public static McpServerDraft NewDraft(string name) => new(
		$"mcp_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds():x}",
		string.IsNullOrWhiteSpace(name) ? "MCP 服务" : name,
		"stdio",
		"npx",
		["-y", "@modelcontextprotocol/server-filesystem"],
		new Dictionary<string, string>(StringComparer.Ordinal),
		null,
		true,
		true);

	private async Task<JsonElement> ExecuteConfigCommandAsync(string command, McpServerDraft draft, CancellationToken cancellationToken)
	{
		string transport = draft.Transport.Trim().ToLowerInvariant();
		if (transport is not ("stdio" or "sse")) throw new ArgumentException("MCP 传输协议必须是 stdio 或 SSE。", nameof(draft));
		string normalizedUrl = "";
		if (transport == "sse" && !TryValidateHttpUrl(draft.Url, out normalizedUrl)) throw new ArgumentException("SSE 地址必须是 HTTP 或 HTTPS 地址。", nameof(draft));
		object args = new
		{
			id = string.IsNullOrWhiteSpace(draft.Id) ? $"mcp_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds():x}" : draft.Id.Trim(),
			name = draft.Name.Trim(),
			transport,
			command = transport == "stdio" ? draft.Command.Trim() : null,
			args = transport == "stdio" ? draft.Arguments.ToArray() : Array.Empty<string>(),
			env = draft.Environment.Count == 0 ? null : new Dictionary<string, string>(draft.Environment, StringComparer.Ordinal),
			url = transport == "sse" ? normalizedUrl : null,
			enabled = draft.Enabled,
			autoConnect = draft.AutoConnect,
		};
		return await ExecuteAsync(command, args, cancellationToken).ConfigureAwait(true);
	}

	private bool Matches(params string?[] values)
	{
		string query = SearchText.Trim();
		return query.Length == 0 || values.Any(value => value?.Contains(query, StringComparison.OrdinalIgnoreCase) == true);
	}

	private static IReadOnlyList<McpServerItem> ParseServers(IEnumerable<JsonElement> values) => values.Select(ParseServer).Where(item => item is not null).Cast<McpServerItem>().ToArray();

	private static McpServerItem? ParseServer(JsonElement value)
	{
		string id = SettingsJson.String(value, "serverId", SettingsJson.String(value, "id"));
		if (id.Length == 0) return null;
		IReadOnlyList<McpToolItem> tools = SettingsJson.Array(value, "tools").Select(item => ParseTool(item, SettingsJson.String(value, "serverId", id), "mcp")).ToArray();
		return new McpServerItem(
			id,
			SettingsJson.String(value, "name", id),
			SettingsJson.String(value, "status", "disconnected"),
			SettingsJson.NullableString(value, "errorMessage"),
			tools,
			SettingsJson.Array(value, "resources").Count(),
			SettingsJson.Bool(value, "hasEnvironment"),
			SettingsJson.NullableString(value, "secretIssue"));
	}

	private static IReadOnlyList<McpToolItem> ParseBuiltinTools(IEnumerable<JsonElement> values) => values
		.Where(value => string.Equals(SettingsJson.String(value, "category"), "builtin", StringComparison.OrdinalIgnoreCase))
		.Select(value => ParseTool(value, null, "builtin"))
		.ToArray();

	private static McpToolItem ParseTool(JsonElement value, string? serverId, string fallbackCategory) => new(
		SettingsJson.String(value, "name"),
		SettingsJson.String(value, "description"),
		SettingsJson.String(value, "permissionLevel", "safe"),
		SettingsJson.String(value, "category", fallbackCategory),
		SettingsJson.Bool(value, "enabled", true),
		serverId,
		SettingsJson.Object(value, "inputSchema"));

	private static bool TryValidateHttpUrl(string? value, out string normalized)
	{
		normalized = value?.Trim() ?? "";
		return Uri.TryCreate(normalized, UriKind.Absolute, out Uri? uri)
			&& uri.Scheme is "http" or "https"
			&& uri.Host.Length > 0;
	}
}
