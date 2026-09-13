using System.Text.Json;
using System.Text.Json.Nodes;
using Nori.Desktop.Automation;
using Nori.Desktop.Windows;
using Nori.Desktop.Automation.Browser;
using Nori.Desktop.Settings;
using Nori.Desktop.Memory;
using Nori.PluginRuntime;

namespace Nori.Desktop.Bridge;

/// <summary>Bridge 命令所属领域；用于把传输、策略与具体命令实现逐步解耦。</summary>
public enum BridgeCommandDomain
{
	Application,
	Window,
	Automation,
	Ai,
	Voice,
	Model,
	Memory,
	Skills,
	Mcp,
	Tools,
	Plugins,
	Diagnostics,
	Other,
}

/// <summary>
/// Bridge 的领域路由边界。
///
/// 当前具体业务实现仍由 BridgeCommands 承担；插件管理是第一个独立领域 handler，
/// 只服务宿主主 WebView，不改变 PluginBridge 的插件侧白名单。
/// </summary>
public sealed class BridgeCommandRouter(AppServices services)
{
	private readonly AppServices _services = services;

	public async Task<object?> InvokeAsync(
		IBridgeSource source,
		string command,
		JsonElement args,
		CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();
		MemoryService.ValidateSourceCommand(source, command);
		BridgeCommandDomain domain = Classify(command);

		// 插件动作调用 (宿主聊天控制卡): 仅主窗口可调, 转发到活跃插件的 IPluginActionContribution。
		if (command == "plugin_action")
		{
			PluginRuntimeHost runtime = _services.PluginRuntime
				?? throw new InvalidOperationException("插件运行时尚未就绪");
			if (!string.Equals(source.Label, WindowLabels.Main, StringComparison.Ordinal))
				throw new InvalidOperationException("plugin_action 仅允许宿主主窗口调用");
			string pluginId = args.TryGetProperty("pluginId", out JsonElement pluginIdElement) ? pluginIdElement.GetString() ?? "" : "";
			string actionId = args.TryGetProperty("actionId", out JsonElement actionIdElement) ? actionIdElement.GetString() ?? "" : "";
			JsonNode? actionArgs = args.TryGetProperty("args", out JsonElement argsElement) && argsElement.ValueKind == JsonValueKind.Object
				? JsonNode.Parse(argsElement.GetRawText())
				: null;
			return await runtime.InvokePluginActionAsync(pluginId, actionId, actionArgs, cancellationToken).ConfigureAwait(false);
		}

		// 插件聊天卡片部件列表 (通用卡片槽挂载用): 仅主窗口。
		if (command == "plugin_widgets")
		{
			PluginRuntimeHost runtime = _services.PluginRuntime
				?? throw new InvalidOperationException("插件运行时尚未就绪");
			if (!string.Equals(source.Label, WindowLabels.Main, StringComparison.Ordinal))
				throw new InvalidOperationException("plugin_widgets 仅允许宿主主窗口调用");
			// 返回形状必须与前端 commands.ts 的类型契约一致: {widgets: [...]}
			return new
			{
				widgets = runtime.GetChatWidgets()
					.Select(widget => new { pluginId = widget.PluginId, title = widget.Title, entry = widget.EntryUrl.ToString() })
					.ToArray(),
			};
		}

		if (domain == BridgeCommandDomain.Plugins)
		{
			PluginRuntimeHost runtime = _services.PluginRuntime
				?? throw new InvalidOperationException("插件运行时尚未就绪");
			bool nativeSettings = source is INativeSettingsSource;
			bool sourceVisible = nativeSettings
				? source.IsVisible
				: _services.Windows.IsWindowVisible(source.Label);
			if (nativeSettings
				&& command is not "plugin_list"
				&& !_services.SafeMode
				&& !_services.Config.GetBoolOr(SettingsService.PluginTrustConfigKey, false))
			{
				throw new UnauthorizedAccessException("请先确认进程内插件运行风险");
			}
			return await runtime.InvokeManagementAsync(
				new PluginManagementSource(
					source.Label,
					sourceVisible,
					source.Self,
					nativeSettings),
				command,
				args,
				cancellationToken).ConfigureAwait(false);
		}

		// Browser Automation 是可选 feature pack。发布瘦身包不携带 driver 时，
		// 在路由边界就明确 fail-closed，而不是让传输层或 Playwright 内部报路径错误。
		if (domain == BridgeCommandDomain.Automation
			&& command.StartsWith("automation_browser_", StringComparison.Ordinal))
		{
			bool available = PlaywrightRuntimeAvailability.IsAvailable();
			if (!available && command is "automation_browser_start" or "automation_browser_start_task")
			{
				throw new InvalidOperationException(PlaywrightRuntimeAvailability.MissingReason);
			}
			if (!available && command == "automation_browser_status")
			{
				return BrowserUnavailableStatus();
			}
		}

		return await _services.Commands.InvokeAsync(source, command, args, cancellationToken).ConfigureAwait(false);
	}

	/// <summary>构造缺少浏览器 feature pack 时的稳定降级状态。</summary>
	internal static object BrowserUnavailableStatus() => new
	{
		state = AutomationBrowserState.Stopped,
		enabled = false,
		available = false,
		unavailableReason = PlaywrightRuntimeAvailability.MissingReason,
		running = false,
	};

	/// <summary>纯命令名分类，不读取用户状态或参数。</summary>
	public static BridgeCommandDomain Classify(string command)
	{
		if (string.IsNullOrWhiteSpace(command)) return BridgeCommandDomain.Other;
		if (command.StartsWith("window_", StringComparison.Ordinal)) return BridgeCommandDomain.Window;
		if (command.StartsWith("automation_", StringComparison.Ordinal)) return BridgeCommandDomain.Automation;
		if (command.StartsWith("llm_", StringComparison.Ordinal)
			|| command.StartsWith("ai_", StringComparison.Ordinal)
			|| command.StartsWith("embedding_", StringComparison.Ordinal)
			|| command.StartsWith("settings_update_ai", StringComparison.Ordinal)
			|| command.StartsWith("settings_test_ai", StringComparison.Ordinal)
			|| command.StartsWith("settings_update_embedding", StringComparison.Ordinal)
			|| command.StartsWith("settings_test_embedding", StringComparison.Ordinal))
			return BridgeCommandDomain.Ai;
		if (command.StartsWith("tts_", StringComparison.Ordinal)
			|| command.StartsWith("stt_", StringComparison.Ordinal)
			|| command.StartsWith("audio_", StringComparison.Ordinal)
			|| command.StartsWith("settings_update_voice", StringComparison.Ordinal))
			return BridgeCommandDomain.Voice;
		if (command.StartsWith("model_", StringComparison.Ordinal) || command.StartsWith("pet_", StringComparison.Ordinal))
			return BridgeCommandDomain.Model;
		if (command.StartsWith("memory_", StringComparison.Ordinal)) return BridgeCommandDomain.Memory;
		if (command.StartsWith("skills_", StringComparison.Ordinal)) return BridgeCommandDomain.Skills;
		if (command.StartsWith("mcp_", StringComparison.Ordinal)) return BridgeCommandDomain.Mcp;
		if (command.StartsWith("tools_", StringComparison.Ordinal)) return BridgeCommandDomain.Tools;
		if (command.StartsWith("plugin_", StringComparison.Ordinal)) return BridgeCommandDomain.Plugins;
		if (command is "get_recent_logs" or "clear_recent_logs" or "get_diagnostic_info" or "export_diagnostics" or "open_log_folder" or "run_gc_collect" or "debug_crash_test")
			return BridgeCommandDomain.Diagnostics;
		if (command.StartsWith("chat_", StringComparison.Ordinal)
			|| command.StartsWith("approval_", StringComparison.Ordinal)
			|| command.StartsWith("reminder_", StringComparison.Ordinal)
			|| command.StartsWith("settings_", StringComparison.Ordinal)
			|| command is "ui_get_snapshot" or "exit_app" or "write_log" or "get_system_language" or "complete_first_run" or "init_ready" or "init_enter_main" or "get_init_config" or "clipboard_write_text" or "open_url")
			return BridgeCommandDomain.Application;
		return BridgeCommandDomain.Other;
	}
}
