using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Threading;
using Nori.Core.Agent;
using Nori.Desktop.Bridge;
using Nori.Desktop.Diagnostics;
using Nori.Desktop.Windows;

namespace Nori.Desktop.Settings;

/// <summary>原生设置窗口共享服务。</summary>
public sealed class SettingsService : NativeBridgeServiceBase
{
	/// <summary>原生插件操作的风险确认配置键。</summary>
	internal const string PluginTrustConfigKey = "plugins_native_trust_confirmed";

	/// <summary>执行记录有新事件。可能在后台线程触发，界面必须自行切回 UI 线程。</summary>
	public event Action? AgentExecutionChanged;

	private void RaiseAgentExecutionChanged() => AgentExecutionChanged?.Invoke();

	/// <summary>创建与指定原生窗口绑定的设置服务。</summary>
	public SettingsService(AppServices services, Window owner)
		: base(services, new WindowContext(owner, WindowLabels.Settings))
	{
		services.AgentExecution.Changed += RaiseAgentExecutionChanged;
		if (services.PluginRuntime is { } plugins) plugins.ContributionsChanged += RaiseStateChanged;
	}

	protected override string StateNotificationFailureMessage => "设置窗口状态通知失败";

	protected override bool IsStateChangingCommand(string command) =>
		command.StartsWith("settings_update_", StringComparison.Ordinal)
		|| command is "indextts_clone_voice"
			or "tts_stop"
			or "stt_start"
			or "stt_stop"
			or "reminder_add"
			or "reminder_cancel"
			or "reminder_update"
			or "skills_install_marketplace"
			or "skills_toggle"
			or "skills_install_url"
			or "skills_save_custom"
			or "skills_uninstall"
			or "skills_import_commit"
			or "skills_duplicate"
			or "mcp_save_server"
			or "mcp_delete_server"
			or "mcp_connect_server"
			or "mcp_disconnect_server"
			or "mcp_import_commit"
			or "tools_set_enabled"
			or "plugin_install_local"
			or "plugin_enable"
			or "plugin_disable"
			or "plugin_reload"
			or "plugin_uninstall"
			or "clear_recent_logs"
			or "updater_check"
			or "updater_install"
			or "updater_cancel"
			or "updater_restart"
			or "model_set_behavior";

	/// <summary>读取不持有插件对象的页面元数据；设置侧栏页与独立窗口页分开。</summary>
	public IReadOnlyList<PluginPageInfo> PluginSettingsPages =>
		(Services.Windows as WindowManager)?.PluginPagesController?.SettingsPages ?? [];

	/// <summary>可由插件管理页打开的独立窗口页面。</summary>
	public IReadOnlyList<PluginPageInfo> PluginPages =>
		(Services.Windows as WindowManager)?.PluginPages ?? [];

	/// <summary>通过原生窗口管理器打开插件页面。</summary>
	public async Task OpenPluginPageAsync(string pluginId, string pageId)
	{
		ThrowIfDisposed();
		await Dispatcher.UIThread.InvokeAsync(() =>
		{
			(Services.Windows as WindowManager
				?? throw new InvalidOperationException("插件页面宿主尚未就绪")).OpenPluginPage(pluginId, pageId);
		});
	}

	/// <summary>读取原生 MCP 编辑器需要的配置元数据，不包含环境变量秘密。</summary>
	public async Task<JsonElement> GetMcpServerConfigAsync(string id, CancellationToken cancellationToken = default)
	{
		ThrowIfDisposed();
		using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, Services.ShutdownToken);
		object config = await Task.Run(() => (object?)Services.Mcp.GetServerConfigs().FirstOrDefault(item => item.Id == id)
			?? throw new InvalidOperationException("MCP 服务不存在"), linked.Token).ConfigureAwait(false);
		linked.Token.ThrowIfCancellationRequested();
		return JsonSerializer.SerializeToElement(config, BridgeJson.Options);
	}

	/// <summary>读取当前内存中的执行记录。不触发模型或网络。</summary>
	internal AgentExecutionSnapshot ReadAgentExecution()
	{
		ThrowIfDisposed();
		return Services.AgentExecution.Snapshot();
	}

	/// <summary>读取已有伴侣渲染统计。不加载模型，不创建视窗，也不启动渲染。</summary>
	internal Live2DPerformanceSnapshot ReadLive2DPerformance()
	{
		ThrowIfDisposed();
		return Live2DPerformanceSnapshot.Capture(Services.PetRuntime, Services.SafeMode, Live2DPerformanceSnapshot.TryWorkingSet());
	}

	protected override void OnDisposing()
	{
		Services.AgentExecution.Changed -= RaiseAgentExecutionChanged;
		AgentExecutionChanged = null;
		if (Services.PluginRuntime is { } plugins) plugins.ContributionsChanged -= RaiseStateChanged;
	}
}
