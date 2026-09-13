using System.Collections.Frozen;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using Nori.Desktop.Bridge;

namespace Nori.Desktop.Settings;

/// <summary>
/// 原生设置窗口共享服务。
///
/// 设置窗口与既有 WebView 共用 BridgeCommands 的业务实现，但通过明确的 native
/// settings 上下文和命令白名单隔离来源权限。所有桥接调用放到后台执行，UI 文件选择
/// 等操作仍由既有命令回切 Avalonia UI 线程。
/// </summary>
public sealed class SettingsService : IDisposable
{
	/// <summary>原生插件操作的风险确认配置键。</summary>
	internal const string PluginTrustConfigKey = "plugins_native_trust_confirmed";

	private static readonly FrozenSet<string> AllowedCommands = new[]
	{
		"ai_test_connection",
		"llm_fetch_models",
		"llm_test_connection",
		"settings_update_ai",
		"settings_update_ai_providers",
		"settings_test_ai",
		"settings_update_embedding",
		"settings_test_embedding",
		"embedding_test_connection",
		"settings_update_voice",
		"settings_ack_voice_notice",
		"indextts_pick_template",
		"indextts_clone_voice",
		"tts_test",
		"tts_stop",
		"stt_start",
		"stt_stop",
		"settings_update_workspace",
		"settings_pick_workspace",
		"settings_update_proactive",
		"reminder_add",
		"reminder_cancel",
		"reminder_update",
		"reminder_snooze",
		"reminder_complete",
		"reminder_list",
		"settings_update_automation",
		"automation_get_snapshot",
		"automation_update_settings",
		"automation_browser_status",
		"automation_probe_vision",
		"automation_browser_start",
		"automation_browser_stop",
		"automation_browser_start_task",
		"automation_browser_get_result",
		"automation_browser_stop_task",
		"automation_stop_task",
		"automation_stop_all",
		"automation_audit_list",
		"approval_respond",
		"skills_marketplace",
		"skills_install_marketplace",
		"skills_toggle",
		"skills_install_url",
		"skills_save_custom",
		"skills_uninstall",
		"skills_export",
		"skills_import_json",
		"mcp_get_servers",
		"mcp_save_server",
		"mcp_delete_server",
		"mcp_connect_server",
		"mcp_disconnect_server",
		"mcp_list_tools",
		"mcp_test_server",
		"mcp_call_tool",
		"mcp_import_url",
		"tools_set_enabled",
		"tools_execute_manual",
		"plugin_list",
		"plugin_install_local",
		"plugin_enable",
		"plugin_disable",
		"plugin_uninstall",
		"settings_get_plugin_trust",
		"settings_set_plugin_trust",
		"get_recent_logs",
		"clear_recent_logs",
		"get_diagnostic_info",
		"export_diagnostics",
		"open_log_folder",
		"clipboard_write_text",
		"open_url",
		"run_gc_collect",
		"write_log",
		"debug_crash_test",
		"settings_update_general",
		"model_set_behavior",
		"updater_check",
		"updater_install",
		"updater_cancel",
		"updater_restart",
	}.ToFrozenSet(StringComparer.Ordinal);

	private readonly AppServices _services;
	private readonly SettingsContext _context;
	private readonly BridgeCommandRouter _router;
	private int _disposed;

	/// <summary>设置状态变化通知；调用方负责在 UI 线程刷新控件。</summary>
	public event Action? StateChanged;

	/// <summary>创建与指定原生窗口绑定的设置服务。</summary>
	public SettingsService(AppServices services, Window owner)
	{
		_services = services ?? throw new ArgumentNullException(nameof(services));
		_context = new SettingsContext(owner ?? throw new ArgumentNullException(nameof(owner)));
		_router = new BridgeCommandRouter(services);
		if (services.Runtime is { } runtime) runtime.StateChanged += OnRuntimeStateChanged;
	}

	/// <summary>允许原生设置窗口执行的命令集合。</summary>
	public static IReadOnlySet<string> Commands => AllowedCommands;

	/// <summary>
	/// 执行一个设置领域命令并返回桥接层同形 JSON。
	/// 前端原生 UI 不应通过 WebView invoke 执行设置操作。
	/// </summary>
	public async Task<JsonElement> ExecuteAsync(
		string command,
		object? args = null,
		CancellationToken cancellationToken = default)
	{
		ThrowIfDisposed();
		if (string.IsNullOrWhiteSpace(command) || !AllowedCommands.Contains(command))
			throw new InvalidOperationException($"原生设置窗口不允许执行命令: {command}");

		JsonElement commandArgs = ToArgsElement(args);
		int snapshotVersion = _services.Runtime?.SnapshotVersion ?? 0;
		using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
			cancellationToken,
			_services.ShutdownToken);
		object? result = await Task.Run(
			async () =>
			{
				if (command is "settings_get_plugin_trust" or "settings_set_plugin_trust")
					return ExecutePluginTrust(command, commandArgs);
				return await _router.InvokeAsync(_context, command, commandArgs, linked.Token).ConfigureAwait(false);
			},
			linked.Token).ConfigureAwait(false);
		linked.Token.ThrowIfCancellationRequested();
		// 业务命令通常会通过 Runtime.InvalidateSnapshot 发出通知；只有没有改变快照
		// 版本的命令才由服务补发，避免一次保存触发两次设置页刷新。
		if (IsStateChangingCommand(command)
			&& (_services.Runtime is null || _services.Runtime.SnapshotVersion == snapshotVersion))
			RaiseStateChanged();
		return ToJsonElement(result);
	}

	/// <summary>读取与 WebView 相同的脱敏运行时快照。</summary>
	public async Task<JsonElement> GetSnapshotAsync(CancellationToken cancellationToken = default)
	{
		ThrowIfDisposed();
		using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
			cancellationToken,
			_services.ShutdownToken);
		object snapshot = await Task.Run(
			() => (_services.Runtime ?? throw new InvalidOperationException("应用运行时尚未就绪")).BuildSnapshot(),
			linked.Token).ConfigureAwait(false);
		linked.Token.ThrowIfCancellationRequested();
		return ToJsonElement(snapshot);
	}

	/// <summary>读取并反序列化设置快照的稳定外壳。</summary>
	public async Task<SettingsSnapshotDto?> GetSnapshotDtoAsync(CancellationToken cancellationToken = default)
	{
		JsonElement snapshot = await GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
		return snapshot.Deserialize<SettingsSnapshotDto>(BridgeJson.Options);
	}

	/// <summary>执行命令并反序列化为调用方指定的结果 DTO。</summary>
	public async Task<T?> ExecuteTypedAsync<T>(
		string command,
		object? args = null,
		CancellationToken cancellationToken = default)
	{
		JsonElement result = await ExecuteAsync(command, args, cancellationToken).ConfigureAwait(false);
		return result.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
			? default
			: result.Deserialize<T>(BridgeJson.Options);
	}

	/// <summary>更新 AI 对话与 Embedding 配置。</summary>
	public Task<JsonElement> UpdateAiAsync(SettingsAiPatchDto patch, CancellationToken cancellationToken = default) =>
		ExecuteAsync("settings_update_ai_providers", patch, cancellationToken);

	/// <summary>更新语音配置。</summary>
	public Task<JsonElement> UpdateVoiceAsync(SettingsVoicePatchDto patch, CancellationToken cancellationToken = default) =>
		ExecuteAsync("settings_update_voice", patch, cancellationToken);

	/// <summary>更新通用配置。</summary>
	public Task<JsonElement> UpdateGeneralAsync(SettingsGeneralPatchDto patch, CancellationToken cancellationToken = default) =>
		ExecuteAsync("settings_update_general", patch, cancellationToken);

	/// <summary>更新主动行为配置。</summary>
	public Task<JsonElement> UpdateProactiveAsync(SettingsProactivePatchDto patch, CancellationToken cancellationToken = default) =>
		ExecuteAsync("settings_update_proactive", patch, cancellationToken);

	/// <summary>更新自动化总开关。</summary>
	public Task<JsonElement> UpdateAutomationAsync(SettingsAutomationPatchDto patch, CancellationToken cancellationToken = default) =>
		ExecuteAsync("settings_update_automation", patch, cancellationToken);

	/// <summary>读取插件列表。</summary>
	public Task<JsonElement> ListPluginsAsync(CancellationToken cancellationToken = default) =>
		ExecuteAsync("plugin_list", cancellationToken: cancellationToken);

	/// <summary>读取原生 MCP 编辑器需要的配置元数据，不包含环境变量秘密。</summary>
	public async Task<JsonElement> GetMcpServerConfigAsync(string id, CancellationToken cancellationToken = default)
	{
		ThrowIfDisposed();
		bool visible = await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => _context.IsVisible);
		if (!visible) throw new InvalidOperationException("设置窗口不可见");
		using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _services.ShutdownToken);
		object config = await Task.Run(() => (object?)_services.Mcp.GetServerConfigs().FirstOrDefault(item => item.Id == id)
			?? throw new InvalidOperationException("MCP 服务不存在"), linked.Token).ConfigureAwait(false);
		linked.Token.ThrowIfCancellationRequested();
		return ToJsonElement(config);
	}

	/// <summary>读取 MCP 服务器列表。</summary>
	public Task<JsonElement> GetMcpServersAsync(CancellationToken cancellationToken = default) =>
		ExecuteAsync("mcp_get_servers", cancellationToken: cancellationToken);

	/// <summary>读取自动化运行时状态。</summary>
	public Task<JsonElement> GetAutomationSnapshotAsync(CancellationToken cancellationToken = default) =>
		ExecuteAsync("automation_get_snapshot", cancellationToken: cancellationToken);

	/// <summary>读取进程内插件风险确认状态。</summary>
	public Task<SettingsPluginTrustDto?> GetPluginTrustAsync(CancellationToken cancellationToken = default) =>
		ExecuteTypedAsync<SettingsPluginTrustDto>("settings_get_plugin_trust", cancellationToken: cancellationToken);

	/// <summary>保存进程内插件风险确认状态。</summary>
	public Task<JsonElement> SetPluginTrustAsync(bool confirmed, CancellationToken cancellationToken = default) =>
		ExecuteAsync("settings_set_plugin_trust", new {confirmed}, cancellationToken);

	/// <summary>检查命令是否属于原生设置权限范围。</summary>
	public static bool IsCommandAllowed(string command) =>
		!string.IsNullOrWhiteSpace(command) && AllowedCommands.Contains(command);

	private void OnRuntimeStateChanged() => RaiseStateChanged();

	private object ExecutePluginTrust(string command, JsonElement args)
	{
		if (!_context.IsVisible) throw new InvalidOperationException("设置窗口不可见");
		if (command == "settings_get_plugin_trust")
			return new {confirmed = _services.Config.GetBoolOr(PluginTrustConfigKey, false)};

		if (args.ValueKind != JsonValueKind.Object
			|| !args.TryGetProperty("confirmed", out JsonElement value)
			|| value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
		{
			throw new InvalidOperationException("缺少参数: confirmed");
		}

		bool confirmed = value.GetBoolean();
		_services.Config.Set(PluginTrustConfigKey, new Nori.Core.Configuration.ConfigValue.Text(confirmed ? "1" : "0"));
		if (_services.Runtime is { } runtime) runtime.InvalidateSnapshot("plugins");
		else RaiseStateChanged();
		return new {confirmed};
	}

	private void RaiseStateChanged()
	{
		if (Volatile.Read(ref _disposed) != 0) return;
		Action? handlers = StateChanged;
		if (handlers is null) return;
		foreach (Action handler in handlers.GetInvocationList().Cast<Action>())
		{
			try { handler(); }
			catch (Exception exception)
			{
				try { _services.Logger.Write(Nori.Core.Logging.LogSource.Backend, "warn", $"设置窗口状态通知失败: {exception.GetType().Name}"); }
				catch { }
			}
		}
	}

	private static bool IsStateChangingCommand(string command) =>
		command.StartsWith("settings_update_", StringComparison.Ordinal)
		|| command.StartsWith("settings_ack_", StringComparison.Ordinal)
		|| command is "indextts_clone_voice"
			or "tts_stop"
			or "stt_start"
			or "stt_stop"
			or "reminder_add"
			or "reminder_cancel"
			or "reminder_update"
			or "reminder_snooze"
			or "reminder_complete"
			or "skills_install_marketplace"
			or "skills_toggle"
			or "skills_install_url"
			or "skills_save_custom"
			or "skills_uninstall"
			or "skills_import_json"
			or "mcp_save_server"
			or "mcp_delete_server"
			or "mcp_connect_server"
			or "mcp_disconnect_server"
			or "mcp_import_url"
			or "tools_set_enabled"
			or "plugin_install_local"
			or "plugin_enable"
			or "plugin_disable"
			or "plugin_uninstall"
			or "clear_recent_logs"
			or "run_gc_collect"
			or "updater_check"
			or "updater_install"
			or "updater_cancel"
			or "updater_restart"
			or "model_set_behavior";

	private static JsonElement ToArgsElement(object? args)
	{
		if (args is JsonElement element)
			return element.ValueKind == JsonValueKind.Undefined ? EmptyObject() : element.Clone();
		if (args is JsonNode node) return ToJsonElement(node);
		return args is null ? EmptyObject() : ToJsonElement(args);
	}

	private static JsonElement EmptyObject() => ToJsonElement(new Dictionary<string, object?>());

	private static JsonElement ToJsonElement(object? value)
	{
		string json = JsonSerializer.Serialize(value, BridgeJson.Options);
		using JsonDocument document = JsonDocument.Parse(json);
		return document.RootElement.Clone();
	}

	private void ThrowIfDisposed()
	{
		ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
	}

	public void Dispose()
	{
		if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
		if (_services.Runtime is { } runtime) runtime.StateChanged -= OnRuntimeStateChanged;
		_context.Dispose();
		StateChanged = null;
	}
}
