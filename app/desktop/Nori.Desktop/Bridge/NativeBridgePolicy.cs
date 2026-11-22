using System.Collections.Frozen;
using Nori.Desktop.Chat;
using Nori.Desktop.Windows;

namespace Nori.Desktop.Bridge;

/// <summary>原生窗口的命令权限在分发入口统一校验。</summary>
internal static class NativeBridgePolicy
{
	private static readonly FrozenSet<string> SettingsCommands = new[]
	{
		// 账户与云端同步。前三个只是打开窗口或清本机凭据；后两个会动数据，
		// 但删除与冲突处置留在同步窗口里，这里只是入口。
		"account_open",
		"account_sign_out",
		"cloud_open",
		"cloud_backup",
		"cloud_restore",
		"ai_test_connection",
		"llm_fetch_models",
		"llm_model_info",
		"settings_update_ai_providers",
		"settings_update_voice",
		"indextts_pick_template",
		"indextts_clone_voice",
		"tts_test",
		"tts_stop",
		"stt_start",
		"stt_stop",
		"realtime_voice_start",
		"realtime_voice_stop",
		"realtime_voice_interrupt",
		"realtime_voice_status",
		"voice_mic_test",
		"audio_list_devices",
		"settings_update_workspace",
		"settings_pick_workspace",
		"settings_pick_splash_background",
		"settings_update_tasks",
		"settings_update_notifications",
		"settings_update_permission",
		"settings_update_screen",
		"settings_update_expression",
		"settings_update_proactive",
		"reminder_add",
		"reminder_cancel",
		"reminder_update",
		"reminder_list",
		"focus_start",
		"focus_pause",
		"focus_resume",
		"focus_end",
		"focus_view",
		"automation_get_snapshot",
		"automation_update_settings",
		"automation_probe_vision",
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
		"skills_import_preview",
		"skills_import_commit",
		"skills_duplicate",
		"mcp_get_servers",
		"mcp_save_server",
		"mcp_delete_server",
		"mcp_connect_server",
		"mcp_disconnect_server",
		"mcp_test_server",
		"mcp_call_tool",
		"mcp_import_url",
		"mcp_import_preview",
		"mcp_import_commit",
		"tools_set_enabled",
		"tools_execute_manual",
		"plugin_list",
		"plugin_install_local",
		"plugin_enable",
		"plugin_disable",
		"plugin_reload",
		"plugin_uninstall",
		"settings_get_plugin_trust",
		"settings_set_plugin_trust",
		"get_recent_logs",
		"get_logging_status",
		"set_logging_level",
		"clear_recent_logs",
		"get_diagnostic_info",
		"export_diagnostics",
		"open_log_folder",
		"clipboard_write_text",
		"open_url",
		"settings_update_general",
		"model_set_behavior",
		"updater_check",
		"updater_install",
		"updater_cancel",
		"updater_restart",
		"about_list_contributors",
	}.ToFrozenSet(StringComparer.Ordinal);

	private static readonly FrozenSet<string> MemoryCommands = new[]
	{
		"memory_add", "memory_update", "memory_delete", "memory_clear",
		"memory_archive", "memory_restore", "memory_list_page", "memory_get",
		"memory_atom_list", "memory_knowledge_status", "memory_knowledge_reindex", "memory_knowledge_open",
		"memory_recall_debug", "memory_update_settings",
		"memory_reembed_all", "memory_export", "memory_import_preview", "memory_import_commit",
		"clipboard_write_text",
	}.ToFrozenSet(StringComparer.Ordinal);

	private static readonly FrozenSet<string> ModelsCommands = new[]
	{
		"model_select", "model_import_local", "model_download_remote", "model_get_meta",
		"model_set_display", "model_set_behavior", "model_set_interactions",
	}.ToFrozenSet(StringComparer.Ordinal);

	private static readonly FrozenSet<string> ChatCommands = new[]
	{
		"chat_start", "chat_cancel", "chat_history_page", "chat_clear",
		"approval_respond", "approval_extend", "stt_start", "stt_stop", "tts_stop",
		"realtime_voice_start", "realtime_voice_stop", "realtime_voice_interrupt", "realtime_voice_status",
		"clipboard_write_text", "open_url", "chat_read_generated_image", "chat_open_generated_image",
	}.ToFrozenSet(StringComparer.Ordinal);

	private static readonly FrozenSet<string> QuickChatCommands = new[]
	{
		"chat_start", "chat_cancel", "chat_history_page", "approval_respond", "approval_extend",
	}.ToFrozenSet(StringComparer.Ordinal);

	public static bool RequiresVisibleWindow(string command) =>
		command.StartsWith("plugin_", StringComparison.Ordinal)
		|| command is "settings_get_plugin_trust" or "settings_set_plugin_trust"
			or "automation_get_snapshot" or "automation_update_settings" or "automation_audit_list"
			or "automation_probe_vision" or "automation_stop_task" or "automation_stop_all"
			or "model_import_local" or "model_download_remote"
			or "memory_export" or "memory_import_preview" or "memory_import_commit"
			or "skills_install_marketplace" or "voice_mic_test";

	public static void Validate(IBridgeSource source, string command)
	{
		FrozenSet<string>? allowed = source switch
		{
			NativeChatContext { Surface: NativeChatSurface.QuickChat } => QuickChatCommands,
			NativeChatContext => ChatCommands,
			WindowContext { Label: WindowLabels.Settings } => SettingsCommands,
			WindowContext { Label: WindowLabels.Models } => ModelsCommands,
			WindowContext { Label: WindowLabels.Memory } => MemoryCommands,
			_ => null,
		};
		if (allowed is not null && !allowed.Contains(command))
			throw new InvalidOperationException($"当前窗口不允许执行命令: {command}");
		if (command == "audio_list_devices" && source is not WindowContext { Label: WindowLabels.Settings })
			throw new InvalidOperationException("只有设置窗口可以枚举音频设备");
		if (command == "voice_mic_test" && source is not WindowContext { Label: WindowLabels.Settings })
			throw new InvalidOperationException("只有设置窗口可以测试麦克风");
		if (allowed is null && source.Label != WindowLabels.Main && command is
			("realtime_voice_start" or "realtime_voice_stop" or "realtime_voice_interrupt" or "realtime_voice_status"
			or "focus_start" or "focus_pause" or "focus_resume" or "focus_end" or "focus_view"))
			throw new InvalidOperationException($"当前窗口不允许执行命令: {command}");
		if (source is NativeChatContext { IsVisible: false }
			&& command is not ("chat_history_page" or "chat_read_generated_image" or "chat_cancel" or "approval_respond" or "stt_stop" or "tts_stop"))
			throw new InvalidOperationException("对话窗口不可见");
	}
}
