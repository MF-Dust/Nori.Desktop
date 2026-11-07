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
	JsonElement InputSchema)
{
	/// <summary>卡片上显示的短名称。注册表操作仍使用 <see cref="Name"/>。</summary>
	public string DisplayName { get; init; } = "";

	/// <summary>界面标题。没有短名称时退回注册名。</summary>
	public string Title => DisplayName.Length > 0 ? DisplayName : Name;
}

/// <summary>一组工具。标签可以是资源键，也可以是服务名。</summary>
public sealed record McpToolGroup(string Key, string Label, IReadOnlyList<McpToolItem> Tools);

/// <summary>MCP 服务器状态的脱敏本地表示。</summary>
public sealed record McpServerItem(
	string Id,
	string Name,
	string Status,
	string? ErrorMessage,
	IReadOnlyList<McpToolItem> Tools,
	int ResourceCount,
	bool HasEnvironment,
	string? SecretIssue,
	bool HasHeaders,
	string? HeaderSecretIssue);

/// <summary>导入预览中的一台服务器，不含秘密值。</summary>
public sealed record McpImportServerPreviewItem(
	string Name,
	string Transport,
	string? Command,
	string? Url,
	int ArgumentCount,
	IReadOnlyList<string> EnvironmentNames,
	IReadOnlyList<string> HeaderNames);

/// <summary>一次 MCP 导入预览。</summary>
public sealed record McpImportPreviewItem(string Token, IReadOnlyList<McpImportServerPreviewItem> Servers);

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
	bool AutoConnect)
{
	/// <summary>显式清除环境变量；默认保留宿主已有秘密。</summary>
	public bool ClearEnvironment { get; init; }

	/// <summary>本次填写的请求头。空白且未清除时不上传。</summary>
	public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

	/// <summary>显式清除已保存的请求头。</summary>
	public bool ClearHeaders { get; init; }
}

/// <summary>MCP 与工具设置页的状态和宿主命令编排。</summary>
public sealed class McpSettingsViewModel : SettingsPageViewModelBase
{
	private static readonly string[] StatusKeys = ["all", "connected", "disconnected", "error"];
	private static readonly string[] BuiltinOrder = ["time", "system", "network", "memory", "pet", "workspace", "vision", "other"];

	private IReadOnlyList<McpServerItem> _servers = [];
	private IReadOnlyList<McpToolItem> _builtinTools = [];
	private IReadOnlyList<McpToolGroup> _toolGroups = [];
	private string _searchText = "";
	private string _selectedCategory = "all";
	private bool _showTools;
	private bool _safeMode;

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

	/// <summary>按分组整理后的工具。</summary>
	public IReadOnlyList<McpToolGroup> ToolGroups
	{
		get => _toolGroups;
		private set => SetProperty(ref _toolGroups, value);
	}

	/// <summary>过滤文本。</summary>
	public string SearchText
	{
		get => _searchText;
		set
		{
			if (!SetProperty(ref _searchText, value ?? "")) return;
			NotifyChanged(nameof(FilteredServers));
			NotifyChanged(nameof(FilteredToolGroups));
		}
	}

	/// <summary>服务器状态或工具分组。</summary>
	public string SelectedCategory
	{
		get => _selectedCategory;
		set
		{
			string next = string.IsNullOrWhiteSpace(value) ? "all" : value;
			if (!SetProperty(ref _selectedCategory, next)) return;
			NotifyChanged(nameof(FilteredServers));
			NotifyChanged(nameof(FilteredToolGroups));
		}
	}

	/// <summary>当前是否显示工具列表。</summary>
	public bool ShowTools
	{
		get => _showTools;
		set
		{
			if (!SetProperty(ref _showTools, value)) return;
			SelectedCategory = "all";
			NotifyChanged(nameof(FilteredServers));
			NotifyChanged(nameof(FilteredToolGroups));
		}
	}

	/// <summary>安全模式。连接、测试和网址导入不可用。</summary>
	public bool SafeMode
	{
		get => _safeMode;
		private set => SetProperty(ref _safeMode, value);
	}

	/// <summary>服务器状态筛选项。</summary>
	public static IReadOnlyList<string> ServerStatusKeys => StatusKeys;

	/// <summary>工具分组筛选项，随当前快照变化。</summary>
	public IReadOnlyList<string> ToolCategoryKeys => ["all", .. ToolGroups.Select(group => group.Key)];

	/// <summary>已连接的服务器数量。</summary>
	public int ConnectedCount => Servers.Count(server => string.Equals(server.Status, "connected", StringComparison.OrdinalIgnoreCase));

	/// <summary>过滤后的服务器。</summary>
	public IReadOnlyList<McpServerItem> FilteredServers =>
		Servers.Where(server => MatchesStatus(server.Status) && MatchesServer(server)).ToArray();

	/// <summary>过滤后的工具分组。服务名命中时保留其下全部工具。</summary>
	public IReadOnlyList<McpToolGroup> FilteredToolGroups
	{
		get
		{
			string query = SearchText.Trim();
			List<McpToolGroup> groups = [];
			foreach (McpToolGroup group in ToolGroups)
			{
				if (!string.Equals(SelectedCategory, "all", StringComparison.OrdinalIgnoreCase)
					&& !string.Equals(group.Key, SelectedCategory, StringComparison.OrdinalIgnoreCase)) continue;
				bool groupMatch = query.Length == 0 || group.Label.Contains(query, StringComparison.OrdinalIgnoreCase) || group.Key.Contains(query, StringComparison.OrdinalIgnoreCase);
				IReadOnlyList<McpToolItem> tools = groupMatch
					? group.Tools
					: group.Tools.Where(tool => tool.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
						|| tool.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
						|| tool.Description.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
				if (tools.Count > 0) groups.Add(group with {Tools = tools});
			}
			return groups;
		}
	}

	/// <inheritdoc />
	public override async Task RefreshAsync(CancellationToken cancellationToken = default)
	{
		IsBusy = true;
		try
		{
			JsonElement serverResult = await Service.ExecuteAsync("mcp_get_servers", cancellationToken: cancellationToken).ConfigureAwait(true);
			Servers = ParseServers(SettingsJson.RootArray(serverResult));
			JsonElement snapshot = await Service.GetSnapshotAsync(cancellationToken).ConfigureAwait(true);
			SafeMode = SettingsJson.Bool(SettingsJson.Object(snapshot, "app"), "safeMode");
			IReadOnlyList<McpToolItem> tools = ParseSnapshotTools(SettingsJson.Array(snapshot, "tools"));
			BuiltinTools = tools.Where(tool => string.Equals(tool.Category, "builtin", StringComparison.OrdinalIgnoreCase)).ToArray();
			ToolGroups = BuildGroups(tools);
			NotifyChanged(nameof(FilteredServers));
			NotifyChanged(nameof(FilteredToolGroups));
			NotifyChanged(nameof(ConnectedCount));
			NotifyChanged(nameof(ToolCategoryKeys));
		}
		finally
		{
			IsBusy = false;
		}
	}

	/// <summary>保存服务器配置。空白的环境变量和请求头保留已有密文。</summary>
	public async Task SaveServerAsync(McpServerDraft draft, CancellationToken cancellationToken = default)
	{
		await ExecuteConfigCommandAsync("mcp_save_server", draft, cancellationToken).ConfigureAwait(true);
		await RefreshAsync(cancellationToken).ConfigureAwait(true);
	}

	/// <summary>测试尚未保存的服务器配置。留空的秘密字段沿用已保存密文。</summary>
	public async Task<McpServerItem?> TestServerAsync(McpServerDraft draft, CancellationToken cancellationToken = default)
	{
		if (SafeMode) throw new InvalidOperationException("安全模式下不能测试 MCP 连接。");
		JsonElement result = await ExecuteConfigCommandAsync("mcp_test_server", draft, cancellationToken).ConfigureAwait(true);
		return ParseServer(result);
	}

	/// <summary>预览本地 MCP 配置文本，不写入。</summary>
	public async Task<McpImportPreviewItem> PreviewTextAsync(string text, CancellationToken cancellationToken = default)
	{
		JsonElement result = await ExecuteAsync("mcp_import_preview", new {text}, cancellationToken).ConfigureAwait(true);
		return ParsePreview(result);
	}

	/// <summary>从公开地址拉取配置并预览，不写入。</summary>
	public async Task<McpImportPreviewItem> PreviewUrlAsync(string url, CancellationToken cancellationToken = default)
	{
		if (SafeMode) throw new InvalidOperationException("安全模式下不能从网址导入 MCP。");
		if (!TryValidateHttpUrl(url, out string normalized)) throw new InvalidOperationException("导入地址必须是公开的 HTTP 或 HTTPS 地址。");
		JsonElement result = await ExecuteAsync("mcp_import_url", new {url = normalized}, cancellationToken).ConfigureAwait(true);
		return ParsePreview(result);
	}

	/// <summary>确认导入。新服务器停用且不自动连接。</summary>
	public async Task CommitImportAsync(string token, CancellationToken cancellationToken = default)
	{
		await ExecuteAsync("mcp_import_commit", new {token}, cancellationToken).ConfigureAwait(true);
		await RefreshAsync(cancellationToken).ConfigureAwait(true);
	}

	/// <summary>连接服务器。</summary>
	public async Task ConnectAsync(McpServerItem server, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(server);
		if (SafeMode) throw new InvalidOperationException("安全模式下不能连接 MCP。");
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

	/// <summary>启用或停用工具。使用注册表中的完整名称。</summary>
	public async Task ToggleToolAsync(McpToolItem tool, bool enabled, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(tool);
		await ExecuteAsync("tools_set_enabled", new {name = tool.Name, enabled}, cancellationToken).ConfigureAwait(true);
		await RefreshAsync(cancellationToken).ConfigureAwait(true);
	}

	/// <summary>只测试 safe 工具，避免设置页绕过对话里的逐次授权。</summary>
	public async Task<JsonElement> ExecuteToolAsync(McpToolItem tool, string jsonArguments, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(tool);
		if (tool.ServerId is {Length: > 0} || !string.Equals(tool.PermissionLevel, "safe", StringComparison.OrdinalIgnoreCase))
			throw new InvalidOperationException("该工具需要在对话里逐次授权，不能在设置页直接测试。");
		JsonElement parsed = SettingsJson.Parse(string.IsNullOrWhiteSpace(jsonArguments) ? "{}" : jsonArguments);
		if (parsed.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("工具参数必须是 JSON 对象。");
		object arguments = SettingsJson.Deserialize<Dictionary<string, object?>>(parsed) ?? [];
		return await ExecuteAsync("tools_execute_manual", new {name = tool.Name, arguments}, cancellationToken).ConfigureAwait(true);
	}

	/// <summary>读取可编辑元数据；环境变量和请求头始终只写不读。</summary>
	public async Task<McpServerDraft> LoadDraftAsync(McpServerItem server, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(server);
		JsonElement config = await Service.GetMcpServerConfigAsync(server.Id, cancellationToken).ConfigureAwait(true);
		if (!SettingsJson.IsObject(config)) throw new InvalidOperationException("MCP 服务配置已不存在，请刷新列表。");
		return new McpServerDraft(
			server.Id,
			SettingsJson.String(config, "name", server.Name),
			SettingsJson.String(config, "transport", "stdio"),
			SettingsJson.String(config, "command"),
			SettingsJson.Array(config, "args").Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString() ?? "").ToArray(),
			new Dictionary<string, string>(StringComparer.Ordinal),
			SettingsJson.NullableString(config, "url"),
			SettingsJson.Bool(config, "enabled"),
			SettingsJson.Bool(config, "autoConnect"));
	}

	/// <summary>从工具参数 schema 生成可编辑示例。</summary>
	public static string CreateToolArguments(McpToolItem tool)
	{
		Dictionary<string, object?> sample = new(StringComparer.Ordinal);
		JsonElement properties = SettingsJson.Object(tool.InputSchema, "properties");
		if (properties.ValueKind == JsonValueKind.Object)
		{
			foreach (JsonProperty property in properties.EnumerateObject())
			{
				sample[property.Name] = SettingsJson.String(property.Value, "type") switch
				{
					"number" or "integer" => 1,
					"boolean" => true,
					"array" => Array.Empty<object>(),
					"object" => new Dictionary<string, object?>(),
					_ => "",
				};
			}
		}
		return JsonSerializer.Serialize(sample, new JsonSerializerOptions {WriteIndented = true});
	}

	/// <summary>一行一个启动参数，保留空格和路径。</summary>
	public static IReadOnlyList<string> ParseArgumentLines(string text)
	{
		List<string> arguments = [];
		foreach (string line in text.Split(["\r\n", "\n"], StringSplitOptions.None))
		{
			if (line.Length == 0) continue;
			arguments.Add(line);
		}
		return arguments;
	}

	/// <summary>把参数写回编辑框，一行一个。</summary>
	public static string FormatArgumentLines(IReadOnlyList<string> arguments) => string.Join('\n', arguments);

	/// <summary>解析只写环境变量，保留值中的空白和等号。</summary>
	public static IReadOnlyDictionary<string, string> ParseEnvironment(string text) =>
		ParsePairs(text, "环境变量每行必须采用 NAME=value 格式。");

	/// <summary>解析只写请求头，保留值中的空白和等号。</summary>
	public static IReadOnlyDictionary<string, string> ParseHeaders(string text) =>
		ParsePairs(text, "请求头每行必须采用 Name=value 格式。");

	/// <summary>创建默认服务器草稿。新服务不自动连接。</summary>
	public static McpServerDraft NewDraft(string name) => new(
		$"mcp_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds():x}",
		string.IsNullOrWhiteSpace(name) ? "MCP 服务" : name,
		"stdio",
		"npx",
		["-y", "@modelcontextprotocol/server-filesystem"],
		new Dictionary<string, string>(StringComparer.Ordinal),
		null,
		true,
		false);

	private async Task<JsonElement> ExecuteConfigCommandAsync(string command, McpServerDraft draft, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(draft);
		if (string.IsNullOrWhiteSpace(draft.Name)) throw new InvalidOperationException("MCP 服务名称不能为空。");
		string transport = draft.Transport.Trim().ToLowerInvariant();
		if (transport is not ("stdio" or "http" or "sse")) throw new InvalidOperationException("MCP 传输协议必须是 Stdio、HTTP 或 SSE。");
		bool remote = transport is "http" or "sse";
		string normalizedUrl = "";
		if (remote && !TryValidateHttpUrl(draft.Url, out normalizedUrl))
			throw new InvalidOperationException(transport == "sse" ? "SSE 地址必须是 HTTP 或 HTTPS 地址。" : "HTTP 地址必须是 HTTP 或 HTTPS 地址。");
		bool clearHeaders = draft.ClearHeaders || !remote;
		object args = new
		{
			id = string.IsNullOrWhiteSpace(draft.Id) ? $"mcp_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds():x}" : draft.Id.Trim(),
			name = draft.Name.Trim(),
			transport,
			command = transport == "stdio" ? draft.Command.Trim() : null,
			args = transport == "stdio" ? draft.Arguments.ToArray() : Array.Empty<string>(),
			env = draft.ClearEnvironment ? new Dictionary<string, string>(StringComparer.Ordinal) : draft.Environment.Count == 0 ? null : new Dictionary<string, string>(draft.Environment, StringComparer.Ordinal),
			headers = clearHeaders ? new Dictionary<string, string>(StringComparer.Ordinal) : draft.Headers.Count == 0 ? null : new Dictionary<string, string>(draft.Headers, StringComparer.Ordinal),
			url = remote ? normalizedUrl : null,
			enabled = draft.Enabled,
			autoConnect = SafeMode ? false : draft.AutoConnect,
		};
		return await ExecuteAsync(command, args, cancellationToken).ConfigureAwait(true);
	}

	private static IReadOnlyDictionary<string, string> ParsePairs(string text, string error)
	{
		Dictionary<string, string> values = new(StringComparer.Ordinal);
		foreach (string line in text.Split(["\r\n", "\n"], StringSplitOptions.None))
		{
			if (string.IsNullOrWhiteSpace(line)) continue;
			int separator = line.IndexOf('=');
			if (separator <= 0 || string.IsNullOrWhiteSpace(line[..separator]))
				throw new InvalidOperationException(error);
			values[line[..separator].Trim()] = line[(separator + 1)..];
		}
		return values;
	}

	private bool MatchesStatus(string status)
	{
		if (string.Equals(SelectedCategory, "all", StringComparison.OrdinalIgnoreCase)) return true;
		if (!StatusKeys.Contains(SelectedCategory, StringComparer.OrdinalIgnoreCase)) return true;
		return string.Equals(status, SelectedCategory, StringComparison.OrdinalIgnoreCase);
	}

	private bool MatchesServer(McpServerItem server)
	{
		string query = SearchText.Trim();
		if (query.Length == 0) return true;
		if (server.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || server.Id.Contains(query, StringComparison.OrdinalIgnoreCase)) return true;
		if (server.ErrorMessage?.Contains(query, StringComparison.OrdinalIgnoreCase) == true) return true;
		return server.Tools.Any(tool => tool.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
			|| tool.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
			|| tool.Description.Contains(query, StringComparison.OrdinalIgnoreCase));
	}

	private IReadOnlyList<McpToolGroup> BuildGroups(IReadOnlyList<McpToolItem> tools)
	{
		List<McpToolGroup> groups = [];
		foreach (string key in BuiltinOrder)
		{
			McpToolItem[] matched = tools.Where(tool => tool.ServerId is null && BuiltinGroup(tool.Name) == key).ToArray();
			if (matched.Length > 0) groups.Add(new McpToolGroup(key, "mcp.group." + key, matched));
		}
		foreach (IGrouping<string, McpToolItem> server in tools.Where(tool => tool.ServerId is {Length: > 0}).GroupBy(tool => tool.ServerId!, StringComparer.Ordinal))
		{
			string label = Servers.FirstOrDefault(item => item.Id == server.Key)?.Name ?? server.Key;
			groups.Add(new McpToolGroup("mcp:" + server.Key, label, server.ToArray()));
		}
		return groups;
	}

	private static string BuiltinGroup(string name) => name switch
	{
		"getTime" or "getDate" or "setReminder" or "listReminders" => "time",
		"getSystemInfo" or "getBatteryStatus" or "getClipboardText" or "setClipboardText" or "openUrl" or "refreshDevices" or "calculate" => "system",
		"getWeather" or "searchWeb" or "fetchWebPage" => "network",
		"remember" or "addMemory" or "forgetMemory" or "searchMemory" => "memory",
		"playMotion" or "setExpression" or "setEmotion" => "pet",
		"listFiles" or "readFile" or "searchFiles" or "findFiles" or "writeFile" or "editFile" or "runTask" => "workspace",
		"readScreen" or "generateImage" => "vision",
		_ => "other",
	};

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
			SettingsJson.NullableString(value, "secretIssue"),
			SettingsJson.Bool(value, "hasHeaders"),
			SettingsJson.NullableString(value, "headerSecretIssue"));
	}

	private static IReadOnlyList<McpToolItem> ParseSnapshotTools(IEnumerable<JsonElement> values)
	{
		List<McpToolItem> tools = [];
		foreach (JsonElement value in values)
		{
			string category = SettingsJson.String(value, "category");
			string name = SettingsJson.String(value, "name");
			if (category.Equals("plugin", StringComparison.OrdinalIgnoreCase) || name.StartsWith("plugin__", StringComparison.Ordinal)) continue;
			if (category.Equals("mcp", StringComparison.OrdinalIgnoreCase) || name.StartsWith("mcp__", StringComparison.Ordinal))
			{
				if (!TrySplitMcpName(name, out string serverId, out string toolName)) continue;
				tools.Add(ParseTool(value, serverId, "mcp") with {DisplayName = toolName});
				continue;
			}
			tools.Add(ParseTool(value, null, "builtin") with {DisplayName = name});
		}
		return tools;
	}

	private static bool TrySplitMcpName(string name, out string serverId, out string toolName)
	{
		serverId = "";
		toolName = "";
		const string prefix = "mcp__";
		if (!name.StartsWith(prefix, StringComparison.Ordinal)) return false;
		string rest = name[prefix.Length..];
		int split = rest.LastIndexOf("__", StringComparison.Ordinal);
		if (split <= 0 || split + 2 >= rest.Length) return false;
		serverId = rest[..split];
		toolName = rest[(split + 2)..];
		return serverId.Length > 0 && toolName.Length > 0;
	}

	internal static McpToolItem ParseTool(JsonElement value, string? serverId, string fallbackCategory) => new(
		SettingsJson.String(value, "name"),
		SettingsJson.String(value, "description"),
		SettingsJson.String(value, "permissionLevel", "safe"),
		SettingsJson.String(value, "category", fallbackCategory),
		SettingsJson.Bool(value, "enabled", true),
		serverId,
		SettingsJson.Object(value, "inputSchema"));

	private static McpImportPreviewItem ParsePreview(JsonElement value)
	{
		string token = SettingsJson.String(value, "token");
		if (token.Length == 0) throw new InvalidOperationException("导入预览已失效。");
		McpImportServerPreviewItem[] servers = SettingsJson.Array(value, "servers").Select(item => new McpImportServerPreviewItem(
			SettingsJson.String(item, "name"),
			SettingsJson.String(item, "transport", "stdio"),
			SettingsJson.NullableString(item, "command"),
			SettingsJson.NullableString(item, "url"),
			SettingsJson.Int(item, "argumentCount"),
			ReadStrings(item, "environmentNames"),
			ReadStrings(item, "headerNames"))).ToArray();
		return new McpImportPreviewItem(token, servers);
	}

	private static IReadOnlyList<string> ReadStrings(JsonElement value, string name) =>
		SettingsJson.Array(value, name).Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString() ?? "").Where(item => item.Length > 0).ToArray();

	private static bool TryValidateHttpUrl(string? value, out string normalized)
	{
		normalized = value?.Trim() ?? "";
		return Uri.TryCreate(normalized, UriKind.Absolute, out Uri? uri)
			&& uri.Scheme is "http" or "https"
			&& uri.Host.Length > 0;
	}
}
