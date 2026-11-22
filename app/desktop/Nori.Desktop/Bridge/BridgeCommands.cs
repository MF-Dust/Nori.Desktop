using System.Text.Json;
using Nori.Core.Agent;
using Nori.Core.Automation;
using Nori.Core.Configuration;
using Nori.Core.Logging;
using Nori.Core.Live2D;
using Nori.Core.Memory;
using Nori.Core.Resources;
using Nori.Core.Skills;
using Nori.Core.Voice.Realtime;
using Nori.Desktop.Appearance;
using Nori.Desktop.Automation;
using Nori.Desktop.Diagnostics;
using Nori.Desktop.Chat;
using Nori.Desktop.Runtime;
using Nori.Desktop.Settings;
using Nori.Desktop.Windows;
using Nori.PluginRuntime;
using MemoryService = Nori.Desktop.Memory.MemoryService;
using ModelService = Nori.Desktop.Models.ModelService;

namespace Nori.Desktop.Bridge;

/// <summary>
/// 桥接命令分发器。
/// 秘密只写不读。命令名保持 snake_case 且动词开头。
/// </summary>
public sealed partial class BridgeCommands
{
	private readonly AppServices _services;
	private readonly IUiDispatcher _uiDispatcher;

	public BridgeCommands(AppServices services) : this(services, AvaloniaUiDispatcher.Instance)
	{
	}

	/// <summary>测试可注入 UI 调度器的构造函数</summary>
	public BridgeCommands(AppServices services, IUiDispatcher uiDispatcher)
	{
		_services = services;
		_uiDispatcher = uiDispatcher;
	}

	private object ChangeFocus(Action update)
	{
		update();
		Runtime.PublishFocus();
		return Runtime.DescribeFocus();
	}

	private AppRuntime Runtime => _services.Runtime
		?? throw new InvalidOperationException("应用运行时尚未就绪");

	private AutomationRuntime Automation => _services.Automation
		?? throw new InvalidOperationException("自动化运行时尚未就绪");

	/// <summary>
	/// 分发一次命令调用。
	/// </summary>
	public async Task<object?> InvokeAsync(
		IBridgeSource source,
		string cmd,
		JsonElement args,
		CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();
		NativeBridgePolicy.Validate(source, cmd);
		if (_services.SafeMode && cmd is ("focus_start" or "focus_resume"))
			throw new InvalidOperationException("安全模式已禁用开始或继续专注");
		if (_services.SafeMode && IsNetworkCommand(cmd, args))
		{
			throw new InvalidOperationException("安全模式已禁用联网和外部服务，请退出安全模式后重试");
		}
		if (source is WindowContext { Self: { } owner } && NativeBridgePolicy.RequiresVisibleWindow(cmd)
			&& !await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => owner.IsVisible))
			throw new InvalidOperationException("来源窗口不可见");
		if (cmd.StartsWith("plugin_", StringComparison.Ordinal))
		{
			PluginRuntimeHost pluginRuntime = _services.PluginRuntime
				?? throw new InvalidOperationException("插件运行时尚未就绪");
			if (cmd is not "plugin_list"
				&& !_services.SafeMode
				&& !_services.Config.GetBoolOr(SettingsService.PluginTrustConfigKey, false))
			{
				throw new UnauthorizedAccessException("请先确认进程内插件运行风险");
			}
			return await pluginRuntime.InvokeManagementAsync(
				new PluginManagementSource(source.Self),
				cmd,
				args,
				cancellationToken).ConfigureAwait(false);
		}
		object? result = cmd switch
		{
		// ---- 自动化宿主接线 ----
		/// <summary>读取自动化状态快照。调用：invoke("automation_get_snapshot")</summary>
		"automation_get_snapshot" => Automation.GetSnapshot(),

		/// <summary>更新自动化设置。调用：invoke("automation_update_settings", {enabled?, desktopEnabled?, allowPointer?, allowKeyboard?, allowScroll?})</summary>
		"automation_update_settings" => UpdateAutomationSettings(args),

		/// <summary>读取脱敏审计记录。调用：invoke("automation_audit_list", {limit?})</summary>
		"automation_audit_list" => AutomationAuditList(ClampLimit(OptionalInt(args, "limit"), 50)),

		/// <summary>探测桌面视觉能力。调用：invoke("automation_probe_vision")</summary>
		"automation_probe_vision" => Automation.ProbeVision(),

		/// <summary>停止指定自动化任务。调用：invoke("automation_stop_task", {taskId: "..."})</summary>
		"automation_stop_task" => Automation.StopTask(ParseGuid(args, "taskId")),

		/// <summary>停止全部自动化任务。调用：invoke("automation_stop_all")</summary>
		"automation_stop_all" => await Automation.StopAllAsync(cancellationToken).ConfigureAwait(false),

		// ---- 插件风险确认 ----
		/// <summary>读取进程内插件风险确认状态。调用：invoke("settings_get_plugin_trust")</summary>
		"settings_get_plugin_trust" => new {confirmed = _services.Config.GetBoolOr(SettingsService.PluginTrustConfigKey, false)},

		/// <summary>保存进程内插件风险确认状态。调用：invoke("settings_set_plugin_trust", {confirmed: true})</summary>
		"settings_set_plugin_trust" => SetPluginTrust(args),

		// ---- AI 设置 ----
		/// <summary>拉取模型列表。调用：invoke("llm_fetch_models", {provider?, baseUrl, apiKey?})</summary>
		"llm_fetch_models" => await FetchModelsAsync(args),
		/// <summary>
		/// 查询模型能力 (models.dev)：上下文窗口、输出上限、支持的思考档位与多模态，以及按当前设置换算的上下文上限。
		/// 前端调用：invoke("llm_model_info", {provider?, baseUrl?, model?}) → {found, contextWindow, reasoningEfforts, ...}
		/// </summary>
		"llm_model_info" => await GetModelInfoAsync(args, cancellationToken),

		/// <summary>测试 AI 连接。调用：invoke("ai_test_connection", {target: "chat" | "embedding", provider?, baseUrl?, apiKey?, model?, dimensions?})</summary>
		"ai_test_connection" => await TestAiConnectionAsync(args, cancellationToken),

		/// <summary>更新 AI 提供商配置。调用：invoke("settings_update_ai_providers", {chat?: {...}, embedding?: {...}, persona?})</summary>
		"settings_update_ai_providers" => Run(() =>
		{
			UpdateUnifiedAiSettings(args);
			Runtime.InvalidateSnapshot();
		}),

		/// <summary>更新语音配置。调用：invoke("settings_update_voice", {...})</summary>
		"settings_update_voice" => await UpdateVoiceSettingsAsync(args),

		/// <summary>
		/// 更新常规设置并同步窗口外观；启动画面设置在下次启动时生效。
		/// 前端调用：invoke("settings_update_general", {backgroundBlurEnabled: true, splashEnabled: true, splashTheme: "character", splashBackground: "", accent: "teal", uiScale: 100, bubbleFont: "rounded", bubbleSize: "standard", motion: "system"})
		/// </summary>
		"settings_update_general" => Run(() =>
		{
			ValidateSplashSettings(args);
			ValidateAppearanceSettings(args);
			UpdateBoolConfig(args, "splashEnabled", "splash_enabled");
			UpdateOptionalConfig(args, "splashTheme", "splash_theme");
			UpdateOptionalConfig(args, "splashBackground", "splash_background");
			UpdateOptionalConfig(args, "language", ConfigStore.KeyLanguage);
			UpdateBoolConfig(args, "petAutoSummon", "pet_auto_summon");
			UpdateBoolConfig(args, "quickChatEnabled", ConfigStore.KeyQuickChatEnabled);
			UpdateBoolConfig(args, "backgroundBlurEnabled", ConfigStore.KeyBackgroundBlurEnabled);
			if (args.TryGetProperty("backgroundBlurEnabled", out _))
			{
				bool blurEnabled = _services.Config.GetBoolOr(ConfigStore.KeyBackgroundBlurEnabled, true);
				Avalonia.Threading.Dispatcher.UIThread.Post(() => _services.Windows.UpdateBackgroundBlurEnabled(blurEnabled));
			}
			UpdateOptionalConfig(args, "accent", ConfigStore.KeyUiAccent);
			if (args.ValueKind == JsonValueKind.Object
				&& args.TryGetProperty("uiScale", out JsonElement uiScale)
				&& uiScale.ValueKind == JsonValueKind.Number
				&& uiScale.TryGetInt32(out int percent))
			{
				// 存为 Integer：SQLite 读回会按存储文本重新推断类型。
				_services.Config.Set(ConfigStore.KeyUiScale, new ConfigValue.Integer(percent));
			}
			UpdateOptionalConfig(args, "bubbleFont", ConfigStore.KeyBubbleFont);
			UpdateOptionalConfig(args, "bubbleSize", ConfigStore.KeyBubbleSize);
			UpdateOptionalConfig(args, "motion", ConfigStore.KeyMotion);
			if (HasProperty(args, "accent") || HasProperty(args, "uiScale") || HasProperty(args, "bubbleFont")
				|| HasProperty(args, "bubbleSize") || HasProperty(args, "motion"))
			{
				AppearancePreferences preferences = AppearancePreferences.Read(_services.Config);
				Avalonia.Threading.Dispatcher.UIThread.Post(() => _services.Windows.UpdateAppearance(preferences));
			}
			UpdateBoolConfig(args, "sidebarCollapsed", "ui_sidebar_collapsed");
			UpdateBoolConfig(args, "autoCheckUpdates", "auto_check_updates");
			UpdateTelemetryConsent(source, args);
			_services.Telemetry.Configure(_services.Config.GetTelemetryConsent() == TelemetryConsent.Granted);
			// 托盘菜单是另一棵原生控件树，语言改了要单独刷新。
			Avalonia.Threading.Dispatcher.UIThread.Post(Tray.TrayMenu.Refresh);
			Runtime.InvalidateSnapshot();
		}),

		/// <summary>
		/// 更新文件工具的工作目录与单轮工具次数上限。
		/// 前端调用：invoke("settings_update_workspace", {root?: string, maxToolIterations?: number})
		/// </summary>
		"settings_update_workspace" => Run(() => UpdateWorkspaceSettings(args)),

		/// <summary>
		/// 更新 runTask 可运行的具名任务清单，整份替换。
		/// 前端调用：invoke("settings_update_tasks", {tasks: {name: string, command: string}[]})
		/// </summary>
		"settings_update_tasks" => Run(() => UpdateTaskSettings(args)),

		/// <summary>
		/// 开关一条情绪表达通道。
		/// 前端调用：invoke("settings_update_expression", {channel: string, enabled: boolean})
		/// </summary>
		"settings_update_expression" => Run(() => UpdateExpressionChannel(args)),

		/// <summary>
		/// 换授权档位。
		/// 前端调用：invoke("settings_update_permission", {gear: "ask"|"session"|"trusted"|"bypass"})
		/// </summary>
		"settings_update_permission" => Run(() => UpdatePermissionGear(args)),

		/// <summary>
		/// 开关「待决授权也发系统通知」。
		/// 前端调用：invoke("settings_update_notifications", {enabled: boolean})
		/// </summary>
		"settings_update_notifications" => Run(() =>
		{
			UpdateBoolConfig(args, "enabled", ConfigStore.KeyToastApprovals);
			Runtime.SyncNotificationRegistration();
			Runtime.InvalidateSnapshot();
		}),

		/// <summary>
		/// 开关读屏。
		/// 前端调用：invoke("settings_update_screen", {enabled: boolean})
		/// </summary>
		"settings_update_screen" => Run(() =>
		{
			UpdateBoolConfig(args, "enabled", ConfigStore.KeyScreenReadingEnabled);
			Runtime.RebuildTools();
			Runtime.InvalidateSnapshot();
		}),

		/// <summary>
		/// 打开系统文件夹选择对话框，返回选中路径；用户取消时返回 null。
		/// 前端调用：invoke("settings_pick_workspace")
		/// </summary>
		"settings_pick_workspace" => await PickWorkspaceAsync(source),

		/// <summary>
		/// 打开系统图片选择框，返回所选启动画面背景的本地路径；用户取消返回 null，不写配置。
		/// 前端调用：invoke("settings_pick_splash_background")
		/// </summary>
		"settings_pick_splash_background" => await PickSplashBackgroundAsync(source),

		// ---- 自动更新 ----
		/// <summary>
		/// 检查是否有可用软件更新。
		/// 前端调用：invoke("updater_check")
		/// </summary>
		"updater_check" => await CheckUpdateAsync(cancellationToken),

		/// <summary>
		/// 下载并安装候选版本到新部署槽。
		/// 前端调用：invoke("updater_install")
		/// </summary>
		"updater_install" => await InstallUpdateAsync(cancellationToken),

		/// <summary>
		/// 取消当前正在进行的更新检查或安装。
		/// 前端调用：invoke("updater_cancel")
		/// </summary>
		"updater_cancel" => CancelUpdate(),

		/// <summary>
		/// 拉起启动器并安全退出当前宿主进程，由启动器等待旧进程退出后切换到新槽。
		/// 前端调用：invoke("updater_restart")
		/// </summary>
		"updater_restart" => RestartForUpdate(),

		/// <summary>更新主动行为配置。调用：invoke("settings_update_proactive", {idleEnabled?, idleMinutes?, dailyGreeting?})</summary>
		"settings_update_proactive" => Run(() =>
		{
			UpdateBoolConfig(args, "idleEnabled", "proactive_idle_enabled");
			UpdateNumberConfig(args, "idleMinutes", "proactive_idle_minutes");
			UpdateBoolConfig(args, "dailyGreeting", "proactive_daily_greeting");
			Runtime.InvalidateSnapshot();
		}),

		/// <summary>启停指定工具。调用：invoke("tools_set_enabled", {name: "getTime", enabled: false})</summary>
		"tools_set_enabled" => Run(() =>
		{
			string name = Str(args, "name");
			bool enabled = OptionalBool(args, "enabled") ?? true;
			if (!Runtime.Tools.SetEnabled(name, enabled))
			{
				throw new InvalidOperationException($"未找到工具: {name}");
			}
			PersistDisabledTools();
			Runtime.InvalidateSnapshot();
		}),

		/// <summary>选择模型。调用：invoke("model_select", {modelId: "nori"})</summary>
		"model_select" => Run(() =>
		{
			string modelId = RequireKnownInstalledModel(Str(args, "modelId"));
			UpdateConfigDirect(ConfigStore.KeySelectedModel, modelId);
			ApplyPetConfig(ConfigStore.KeySelectedModel, modelId);
			_services.Logger.Write(LogSource.Backend, "info", $"启用模型: {modelId}");
			Runtime.InvalidateSnapshot();
		}),

		/// <summary>本地导入模型。调用：invoke("model_import_local", {resourceType?: "live2d"})</summary>
		"model_import_local" => await ImportLocalResourceAsync(source, args, cancellationToken),

		/// <summary>
		/// 从 static.nori.mom 下载并安装内置 Live2D 模型，已安装时覆盖为远端版本。
		/// 前端调用：invoke("model_download_remote", {modelId: "arg-nori"}) → "arg-nori"
		/// </summary>
		"model_download_remote" => await ModelDownloadRemoteAsync(args, cancellationToken),

		/// <summary>选择 IndexTTS 音频模板文件并写入配置。调用：invoke("indextts_pick_template", {})</summary>
		"indextts_pick_template" => await IndexTtsPickTemplateAsync(source, cancellationToken),

		/// <summary>上传模板音频克隆音色并返回 voice_id。调用：invoke("indextts_clone_voice", {filePath?})</summary>
		"indextts_clone_voice" => await IndexTtsCloneVoiceAsync(args, cancellationToken),

		/// <summary>读取模型元信息。调用：invoke("model_get_meta", {modelId: "arg-nori"})</summary>
		"model_get_meta" => GetModelMeta(args),

		/// <summary>写入模型互动配置。调用：invoke("model_set_interactions", {modelId, interactions})</summary>
		"model_set_interactions" => ModelSetInteractions(args),

		/// <summary>写入模型显示参数。调用：invoke("model_set_display", {modelId, scale?, expressions?})</summary>
		"model_set_display" => ModelSetDisplay(args),

		/// <summary>写入模型行为开关。调用：invoke("model_set_behavior", {autoBlink?: true, ...})</summary>
		"model_set_behavior" => ModelSetBehavior(args),

		// ---- 聊天 / Agent 会话 ----
		/// <summary>启动对话会话。调用：invoke("chat_start", {text: "你好呀"})</summary>
		"chat_start" => StartChatCommand(source, args),

		/// <summary>取消对话会话。调用：invoke("chat_cancel", {sessionId: "..."})</summary>
		"chat_cancel" => Runtime.CancelChat(source, Str(args, "sessionId")),

		/// <summary>响应工具或自动化审批。调用：invoke("approval_respond", {requestId: "...", approved: true})</summary>
		"approval_respond" => RespondApproval(source, args),

		/// <summary>
		/// 延长本来源的待决工具授权，不能超过工具调用的实际截止时间。
		/// 前端调用：invoke("approval_extend", {requestId: "..."})
		/// </summary>
		"approval_extend" => new
		{
			deadlineUtc = Runtime.ExtendApproval(source, Str(args, "requestId")),
		},

		/// <summary>分页读取聊天历史。调用：invoke("chat_history_page", {limit?: 50, beforeId?: 0})</summary>
		"chat_history_page" => GetHistoryPage(
			ClampLimit(OptionalInt(args, "limit"), 50),
			(long)(OptionalDouble(args, "beforeId") ?? 0)),

		/// <summary>清空聊天记录。调用：invoke("chat_clear")</summary>
		"chat_clear" => await ClearChatAsync(cancellationToken),

		/// <summary>
		/// 读取 AI 生成的图片 (仅限生成图片目录内的合法文件名)。
		/// 前端调用：invoke("chat_read_generated_image", {name: "20261011-120000-abcd1234.png"}) → {name, base64} | null
		/// </summary>
		"chat_read_generated_image" => await ReadGeneratedImageAsync(Str(args, "name"), cancellationToken),

		/// <summary>
		/// 用系统默认程序打开 AI 生成的图片。
		/// 前端调用：invoke("chat_open_generated_image", {name: "20261011-120000-abcd1234.png"})
		/// </summary>
		"chat_open_generated_image" => OpenGeneratedImage(Str(args, "name")),

		// ---- 记忆库 ----
		/// <summary>添加记忆。调用：invoke("memory_add", {content, type?, importance?, tags?})</summary>
		"memory_add" => await MemoryAddAsync(args),

		/// <summary>更新记忆。调用：invoke("memory_update", {id, content, importance?, tags?})</summary>
		"memory_update" => await MemoryUpdateAsync(args),

		/// <summary>永久删除记忆。调用：invoke("memory_delete", {id, confirmToken: "DELETE_MEMORY"})</summary>
		"memory_delete" => HardDeleteMemory(args),

		/// <summary>清空个人记忆。调用：invoke("memory_clear", {confirmToken: "CLEAR_PERSONAL_MEMORY"})</summary>
		"memory_clear" => ClearMemories(args),

		/// <summary>归档记忆。调用：invoke("memory_archive", {id})</summary>
		"memory_archive" => ArchiveMemory(args),

		/// <summary>恢复归档记忆。调用：invoke("memory_restore", {id})</summary>
		"memory_restore" => RestoreMemory(args),

		/// <summary>分页列出记忆。调用：invoke("memory_list_page", {query?, kind?, status?, limit?, offset?})</summary>
		"memory_list_page" => MemoryListPage(args),

		/// <summary>读取单条记忆详情。调用：invoke("memory_get", {id})</summary>
		"memory_get" => MemoryGet(args),

		/// <summary>列出记忆原子。调用：invoke("memory_atom_list", {memoryId?, status?, limit?, offset?})</summary>
		"memory_atom_list" => MemoryAtomList(args),

		/// <summary>读取知识文档状态。调用：invoke("memory_knowledge_status")</summary>
		"memory_knowledge_status" => Runtime.Knowledge.Status,

		/// <summary>重建知识文档索引。调用：invoke("memory_knowledge_reindex")</summary>
		"memory_knowledge_reindex" => await MemoryKnowledgeReindexAsync(cancellationToken),

		/// <summary>打开知识文档目录。调用：invoke("memory_knowledge_open")</summary>
		"memory_knowledge_open" => OpenKnowledgeFolder(),

		/// <summary>调试记忆召回。调用：invoke("memory_recall_debug", {query})</summary>
		"memory_recall_debug" => await MemoryRecallDebugAsync(args, cancellationToken),

		/// <summary>更新记忆配置。调用：invoke("memory_update_settings", {settings: {...}})</summary>
		"memory_update_settings" => UpdateMemorySettings(args),

		/// <summary>重建全部记忆向量。调用：invoke("memory_reembed_all")</summary>
		"memory_reembed_all" => await MemoryReembedAllAsync(cancellationToken),

		/// <summary>导出脱敏记忆文档。调用：invoke("memory_export")</summary>
		"memory_export" => MemoryExport(),

		/// <summary>预览记忆导入。调用：invoke("memory_import_preview", {fileContent})</summary>
		"memory_import_preview" => MemoryImportPreview(args),

		/// <summary>提交记忆导入。调用：invoke("memory_import_commit", {previewToken, conflictStrategy?})</summary>
		"memory_import_commit" => MemoryImportCommit(args),

		// ---- 技能 ----
		/// <summary>读取内置技能市场。调用：invoke("skills_marketplace")</summary>
		"skills_marketplace" => SkillServiceMarketplace(),

		/// <summary>从内置市场安装技能。调用：invoke("skills_install_marketplace", {skillId: "gaming-partner"})</summary>
		"skills_install_marketplace" => InstallMarketplaceSkill(args),

		/// <summary>切换技能启用状态。调用：invoke("skills_toggle", {id, enabled})</summary>
		"skills_toggle" => Run(() =>
		{
			if (!SkillsToggle(Str(args, "id"), OptionalBool(args, "enabled") ?? true))
			{
				throw new InvalidOperationException($"未找到技能: {Str(args, "id")}");
			}
			Runtime.InvalidateSnapshot();
		}),

		/// <summary>从 URL 安装技能。调用：invoke("skills_install_url", {url})</summary>
		"skills_install_url" => await SkillsInstallUrlAsync(args),

		/// <summary>保存自定义技能。调用：invoke("skills_save_custom", {skill: {...}})</summary>
		"skills_save_custom" => SkillsSaveCustom(args),

		/// <summary>卸载技能。调用：invoke("skills_uninstall", {id})</summary>
		"skills_uninstall" => Run(() =>
		{
			Runtime.Skills.Uninstall(Str(args, "id"));
			Runtime.InvalidateSnapshot();
		}),

		/// <summary>导出技能 JSON。调用：invoke("skills_export", {id})</summary>
		"skills_export" => Runtime.Skills.Export(Str(args, "id")),

		/// <summary>预览本地技能导入。调用：invoke("skills_import_preview", {text})</summary>
		"skills_import_preview" => Runtime.Skills.PreviewLocal(Str(args, "text")),

		/// <summary>提交本地技能导入。调用：invoke("skills_import_commit", {token, enabled})</summary>
		"skills_import_commit" => CommitSkillImport(args),

		/// <summary>复制技能。调用：invoke("skills_duplicate", {id})</summary>
		"skills_duplicate" => DuplicateSkill(args),

		// ---- MCP 服务 ----
		/// <summary>获取 MCP 服务器列表。调用：invoke("mcp_get_servers")</summary>
		"mcp_get_servers" => await _services.Mcp.GetServersAsync(),
		/// <summary>保存 MCP 服务器配置。调用：invoke("mcp_save_server", {id, name, transport, ...})</summary>
		"mcp_save_server" => await McpSaveServerAsync(args),
		/// <summary>删除 MCP 服务器配置。调用：invoke("mcp_delete_server", {id})</summary>
		"mcp_delete_server" => await McpDeleteServerAsync(args),
		/// <summary>连接 MCP 服务器。调用：invoke("mcp_connect_server", {id})</summary>
		"mcp_connect_server" => await McpConnectServerAsync(args),
		/// <summary>断开 MCP 服务器。调用：invoke("mcp_disconnect_server", {id})</summary>
		"mcp_disconnect_server" => await McpDisconnectServerAsync(args),
		/// <summary>测试 MCP 服务器连接。调用：invoke("mcp_test_server", {id, name, transport, ...})</summary>
		"mcp_test_server" => await _services.Mcp.TestServerAsync(ParseMcpConfig(args)),
		/// <summary>手动测试 safe MCP 工具。调用：invoke("mcp_call_tool", {serverId, toolName, arguments, sessionId?})</summary>
		"mcp_call_tool" => await CallMcpToolCoreAsync(source, args, cancellationToken),
		/// <summary>从 URL 预览 MCP 配置导入。调用：invoke("mcp_import_url", {url})</summary>
		"mcp_import_url" => await McpImportUrlAsync(args, cancellationToken),
		/// <summary>预览 MCP 配置文本。调用：invoke("mcp_import_preview", {text})</summary>
		"mcp_import_preview" => _services.Mcp.PreviewImport(Str(args, "text")),
		/// <summary>提交 MCP 配置导入。调用：invoke("mcp_import_commit", {token})</summary>
		"mcp_import_commit" => await McpImportCommitAsync(args),

		/// <summary>手动测试 safe 内置工具。调用：invoke("tools_execute_manual", {name, arguments})</summary>
		"tools_execute_manual" => await ToolsExecuteManualAsync(args),

		// ---- 账户与云端同步 ----
		/// <summary>打开账户登录窗口。调用：invoke("account_open")</summary>
		"account_open" => await OnUi(() => Run(_services.Windows.ShowAccount)),
		/// <summary>打开云端同步窗口。调用：invoke("cloud_open")</summary>
		"cloud_open" => await OnUi(() => Run(_services.Windows.ShowCloudSync)),
		/// <summary>退出登录。调用：invoke("account_sign_out")</summary>
		"account_sign_out" => await SignOutAsync(cancellationToken),
		/// <summary>备份至云端。调用：invoke("cloud_backup")</summary>
		"cloud_backup" => await CloudSyncAsync(
			token => _services.CloudSync.BackupAsync(overwrite: false, token), cancellationToken),
		/// <summary>从云端恢复。调用：invoke("cloud_restore")</summary>
		"cloud_restore" => await CloudSyncAsync(
			token => _services.CloudSync.RestoreAsync(token), cancellationToken),

		// ---- 定时提醒 ----
		/// <summary>添加倒计时提醒。调用：invoke("reminder_add", {content, delayMinutes})</summary>
		"reminder_add" => AddReminderCommand(args),

		/// <summary>取消提醒。调用：invoke("reminder_cancel", {id})</summary>
		"reminder_cancel" => CancelReminderCommand(args),

		/// <summary>更新提醒。调用：invoke("reminder_update", {id, content?, triggerTime?, delayMinutes?, repeatDaily?, timezone?, recurrenceJson?})</summary>
		"reminder_update" => UpdateReminderCommand(args),

		/// <summary>查询提醒列表。调用：invoke("reminder_list")</summary>
		"reminder_list" => Runtime.Proactive.ListReminders(),

		/// <summary>开始专注。调用：invoke("focus_start", {minutes: 25, notify: true, silentReminders: true})</summary>
		"focus_start" => ChangeFocus(() =>
			Runtime.Focus.Start(ReadFocusMinutes(args), OptionalBool(args, "notify") ?? true, OptionalBool(args, "silentReminders") ?? true)),

		/// <summary>暂停当前专注。调用：invoke("focus_pause")</summary>
		"focus_pause" => ChangeFocus(() => Runtime.Focus.Pause()),

		/// <summary>继续已暂停的专注。调用：invoke("focus_resume")</summary>
		"focus_resume" => ChangeFocus(() => Runtime.Focus.Resume()),

		/// <summary>提前结束当前专注。调用：invoke("focus_end")</summary>
		"focus_end" => ChangeFocus(() => Runtime.Focus.End()),

		/// <summary>读取专注状态。调用：invoke("focus_view")</summary>
		"focus_view" => Runtime.DescribeFocus(),

		// ---- 语音 ----
		/// <summary>试听 TTS。调用：invoke("tts_test", {text?})</summary>
		"tts_test" => await TtsTestAsync(args),

		/// <summary>停止 TTS 朗读。调用：invoke("tts_stop")</summary>
		"tts_stop" => Run(Runtime.Voice.Stop),

		/// <summary>开始语音识别录音。调用：invoke("stt_start")</summary>
		"stt_start" => await SttStartAsync(cancellationToken),

		/// <summary>停止录音并转写。调用：invoke("stt_stop")</summary>
		"stt_stop" => await SttStopAsync(cancellationToken),

		/// <summary>
		/// 开始实时监听。只有用户主动调用才会打开麦克风。
		/// 前端调用：invoke("realtime_voice_start")
		/// </summary>
		"realtime_voice_start" => await RealtimeVoiceStartAsync(cancellationToken),

		/// <summary>
		/// 停止实时监听，并放开麦克风。
		/// 前端调用：invoke("realtime_voice_stop")
		/// </summary>
		"realtime_voice_stop" => await RealtimeVoiceStopAsync(),

		/// <summary>
		/// 打断当前朗读。麦克风暂停时仍然可用；不能靠对着播放中的声音打断。
		/// 前端调用：invoke("realtime_voice_interrupt") → {interrupted}
		/// </summary>
		"realtime_voice_interrupt" => RealtimeVoiceInterrupt(),

		/// <summary>
		/// 读取实时语音状态。不含录音、识别正文或密钥。
		/// 前端调用：invoke("realtime_voice_status")
		/// </summary>
		"realtime_voice_status" => RealtimeVoiceStatusPayload(),

		/// <summary>
		/// 运行本地麦克风电平自检，只检测输入电平，不上传也不保存录音。
		/// 前端调用：invoke("voice_mic_test")
		/// </summary>
		"voice_mic_test" => await VoiceMicTestAsync(cancellationToken),

		// ---- 系统能力 ----
		/// <summary>用系统默认浏览器打开网址。调用：invoke("open_url", {url: "https://..."})</summary>
		"open_url" => Run(() => ShellOpen.OpenUrl(Str(args, "url"))),

		/// <summary>写入系统剪贴板。调用：invoke("clipboard_write_text", {text: "..."})</summary>
		"clipboard_write_text" => await WriteClipboardAsync(source, Str(args, "text")),

		// ---- 调试 ----
		/// <summary>读取近期内存日志。调用：invoke("get_recent_logs")</summary>
		"get_recent_logs" => _services.Logger.RecentLogs().Select(entry => new
		{
			time = entry.Time,
			level = entry.Level,
			source = entry.Source == LogSource.Frontend ? "frontend" : "backend",
			message = entry.Message,
			timestamp = entry.Timestamp,
			sessionId = entry.SessionId,
			sequence = entry.Sequence,
			category = entry.Category,
			eventId = entry.EventId,
			windowLabel = entry.WindowLabel,
			operationId = entry.OperationId,
			exceptionType = entry.ExceptionType,
			exceptionSite = entry.ExceptionSite,
		}).ToArray(),
		/// <summary>读取日志健康状态。调用：invoke("get_logging_status")</summary>
		"get_logging_status" => GetLoggingStatus(),
		/// <summary>临时调整日志级别。调用：invoke("set_logging_level", {level: "debug"})</summary>
		"set_logging_level" => SetLoggingLevel(args),
		/// <summary>清空内存日志。调用：invoke("clear_recent_logs")</summary>
		"clear_recent_logs" => Run(_services.Logger.ClearRecentLogs),
		/// <summary>读取诊断信息。调用：invoke("get_diagnostic_info")</summary>
		"get_diagnostic_info" => DiagnosticInfo.Build(_services.PetRuntime, _services.Paths, _services.SafeMode),
		/// <summary>
		/// 读取项目 GitHub 贡献者头像 (关于页)。有缓存时立即返回，过期在后台刷新；安全模式只读缓存。
		/// 前端调用：invoke("about_list_contributors") → {contributors: [{login, avatar}]}，avatar 为 base64 图片
		/// </summary>
		"about_list_contributors" => await ListContributorsAsync(cancellationToken),
		/// <summary>导出脱敏诊断包。调用：invoke("export_diagnostics")</summary>
		"export_diagnostics" => await ExportDiagnosticsAsync(source, cancellationToken),
		/// <summary>打开日志目录。调用：invoke("open_log_folder")</summary>
		"open_log_folder" => Run(OpenLogFolder),

			_ => throw new InvalidOperationException($"未知的命令: {cmd}"),
		};
		// 已提交的写命令返回真实成功，避免关闭期间的取消把已写入误报为失败。
		bool committedWrite = MemoryService.IsMutatingCommand(cmd)
			|| ModelService.IsMutatingCommand(cmd)
			|| cmd is "chat_start" or "chat_clear" or "approval_respond" or "approval_extend";
		if (!committedWrite) cancellationToken.ThrowIfCancellationRequested();
		return result;
	}

	private object SetPluginTrust(JsonElement args)
	{
		bool confirmed = RequiredBool(args, "confirmed");
		_services.Config.Set(SettingsService.PluginTrustConfigKey, new ConfigValue.Text(confirmed ? "1" : "0"));
		_services.Runtime?.InvalidateSnapshot();
		return new {confirmed};
	}

	private async Task<object?> CheckUpdateAsync(CancellationToken cancellationToken)
	{
		var update = _services.Update ?? throw new InvalidOperationException("更新服务尚未初始化");
		var check = await update.CheckForUpdateAsync(cancellationToken);
		Runtime.InvalidateSnapshot();
		return new
		{
			available = check.Available,
			currentVersion = check.CurrentVersion,
			latestVersion = check.LatestVersion,
			releaseTag = check.ReleaseTag,
			releaseNotes = check.ReleaseNotes,
			publishedAt = check.PublishedAt?.ToString("o"),
		};
	}

	private async Task<object?> InstallUpdateAsync(CancellationToken cancellationToken)
	{
		var update = _services.Update ?? throw new InvalidOperationException("更新服务尚未初始化");
		var commit = await update.DownloadAndInstallAsync(progress: null, cancellationToken);
		Runtime.InvalidateSnapshot();
		return new
		{
			success = true,
			slotName = commit.SlotName,
			productVersion = commit.Manifest.ProductVersion,
		};
	}

	private object CancelUpdate()
	{
		var update = _services.Update ?? throw new InvalidOperationException("更新服务尚未初始化");
		update.CancelActiveOperation();
		Runtime.InvalidateSnapshot();
		return true;
	}

	private object? RestartForUpdate()
	{
		var update = _services.Update ?? throw new InvalidOperationException("更新服务尚未初始化");
		update.LaunchRestart();
		_services.Windows.Shutdown();
		return null;
	}

	private object GetModelMeta(JsonElement args)
	{
		string modelId = RequireKnownInstalledModel(Str(args, "modelId"));
		string dir = _services.Resources.ResourceDir(ResourceType.Live2D, modelId);
		Model3MetaInfo meta = Model3Meta.Read(dir);
		IReadOnlyDictionary<string, ConfigValue> display = _services.Config.GetMany(Live2DModelConfig.DisplayKeys(modelId));
		float scale = Live2DModelConfig.ReadFloat(display, Live2DModelConfig.ScaleKey, modelId, 1f);
		float opacity = Live2DModelConfig.ReadFloat(display, Live2DModelConfig.OpacityKey, modelId, 1f);
		float renderScale = Live2DModelConfig.ReadFloat(display, Live2DModelConfig.RenderScaleKey, modelId, 2f);
		string qualityMode = Live2DModelConfig.ReadPreferredText(display, Live2DModelConfig.QualityModeKey, modelId, "adaptive");
		int maxFps = (int)Live2DModelConfig.ReadFloat(display, Live2DModelConfig.MaxFpsKey, modelId, 0f);
		bool shadow = Live2DModelConfig.ReadBool(display, Live2DModelConfig.ShadowKey, modelId, true);
		return new
		{
			modelId,
			scale,
			opacity,
			renderScale,
			qualityMode,
			maxFps,
			shadow,
			selectedExpressions = ReadSelectedExpressions(modelId),
			expressions = meta.Expressions,
			motions = meta.Motions.Select(group => new {group = group.Group, names = group.Names}),
			interactions = ReadInteractionConfig(modelId),
		};
	}

	private string StartChatCommand(IBridgeSource source, JsonElement args)
	{
		string text = Str(args, "text").Trim();
		if (text.Length == 0) throw new InvalidOperationException("消息内容不能为空");
		return Runtime.StartChat(source, text);
	}

	private object InstallMarketplaceSkill(JsonElement args)
	{
		string skillId = Str(args, "skillId").Trim();
		if (skillId.Length == 0) throw new InvalidOperationException("技能 ID 不能为空");
		SkillRecord installed = Runtime.Skills.InstallFromMarketplace(skillId);
		Runtime.InvalidateSnapshot();
		return RedactedSkillDto(installed);
	}

	private object CommitSkillImport(JsonElement args)
	{
		SkillRecord saved = Runtime.Skills.CommitLocal(Str(args, "token"), OptionalBool(args, "enabled") ?? false);
		Runtime.InvalidateSnapshot();
		return RedactedSkillDto(saved);
	}

	private object DuplicateSkill(JsonElement args)
	{
		SkillRecord copy = Runtime.Skills.Duplicate(Str(args, "id"));
		Runtime.InvalidateSnapshot();
		return RedactedSkillDto(copy);
	}

	private object AddReminderCommand(JsonElement args)
	{
		double delayMinutes = ReadReminderNumber(args, "delayMinutes", allowMissing: true) ?? 15;
		Nori.Core.Proactive.ReminderItem item = Runtime.Proactive.AddReminder(Str(args, "content"), delayMinutes);
		Runtime.InvalidateSnapshot();
		return item;
	}

	private bool CancelReminderCommand(JsonElement args)
	{
		bool cancelled = Runtime.Proactive.CancelReminder(Str(args, "id"));
		if (cancelled) Runtime.InvalidateSnapshot();
		return cancelled;
	}

	private object UpdateReminderCommand(JsonElement args)
	{
		object result = UpdateReminder(args);
		Runtime.InvalidateSnapshot();
		return result;
	}

	private AutomationSettingsSnapshot UpdateAutomationSettings(JsonElement args)
	{
		bool? desktopEnabled = OptionalBool(args, "desktopEnabled");
		return Automation.UpdateSettings(
			OptionalBool(args, "enabled"),
			OptionalBool(args, "allowPointer") ?? desktopEnabled,
			OptionalBool(args, "allowKeyboard") ?? desktopEnabled,
			OptionalBool(args, "allowScroll") ?? desktopEnabled);
	}

	/// <summary>读取脱敏审计记录。前端调用: invoke("automation_audit_list", {limit?})</summary>
	private object AutomationAuditList(int limit) => _services.AutomationAudit.List(limit).Select(record => new
	{
		id = record.Id,
		taskId = record.TaskId,
		timestamp = record.Timestamp,
		// 迁移到插件前的历史浏览器审计记录仍按 browser 展示。
		taskKind = record.TaskKind == AutomationAuditTaskKind.Browser ? "browser" : "desktop",
		actionCategory = record.Category switch
		{
			AutomationAuditEventCategory.Navigate => "navigate",
			AutomationAuditEventCategory.Click => "click",
			AutomationAuditEventCategory.Fill => "fill",
			AutomationAuditEventCategory.Scroll => "scroll",
			AutomationAuditEventCategory.Wait => "wait",
			AutomationAuditEventCategory.ReadVisibleText => "read_visible_text",
			AutomationAuditEventCategory.SafePage => "safe_page",
			AutomationAuditEventCategory.Approval => "approval",
			_ => "task",
		},
		outcome = record.Outcome switch
		{
			AutomationAuditOutcome.Queued => "queued",
			AutomationAuditOutcome.Running => "running",
			AutomationAuditOutcome.Succeeded => "succeeded",
			AutomationAuditOutcome.Failed => "failed",
			AutomationAuditOutcome.Cancelled => "cancelled",
			AutomationAuditOutcome.Rejected => "rejected",
			AutomationAuditOutcome.Requested => "requested",
			AutomationAuditOutcome.Approved => "approved",
			AutomationAuditOutcome.Denied => "denied",
			AutomationAuditOutcome.TimedOut => "timed_out",
			AutomationAuditOutcome.Paused => "paused",
			_ => "failed",
		},
		failureReason = record.FailureCode,
		failureCode = record.FailureCode,
		durationMs = record.DurationMilliseconds,
	}).ToArray();

	private async Task<object?> MemoryAddAsync(JsonElement args)
	{
		MemoryKind? kind = OptionalStr(args, "kind") is { } kindText ? MemoryKindExtensions.Parse(kindText) : null;
		MemoryItem item = await Runtime.Memory.AddAsync(
			Str(args, "content"),
			OptionalStr(args, "type") ?? kind?.ToStorage() ?? "manual",
			OptionalDouble(args, "importance") ?? 0.5,
			OptionalStr(args, "tags"),
			"manual",
			kind);
		Runtime.InvalidateSnapshot();
		return item;
	}

	private async Task<object?> MemoryUpdateAsync(JsonElement args)
	{
		MemoryKind? kind = OptionalStr(args, "kind") is { } kindText ? MemoryKindExtensions.Parse(kindText) : null;
		bool updated = await Runtime.Memory.UpdateAsync(
			(long)Num(args, "id"),
			Str(args, "content"),
			OptionalDouble(args, "importance"),
			OptionalStr(args, "tags"),
			kind,
			OptionalStr(args, "canonicalSummary"),
			OptionalStr(args, "personaSummary"),
			OptionalDouble(args, "confidence"));
		if (updated) Runtime.InvalidateSnapshot();
		return updated;
	}

	private async Task<object?> MemoryReembedAllAsync(CancellationToken cancellationToken)
	{
		int count = await Runtime.Memory.ReembedAllAsync(cancellationToken);
		Runtime.InvalidateSnapshot();
		return count;
	}

	/// <summary>导出白名单化的 nori-memory-v1 文档。前端调用: invoke("memory_export")</summary>
	private object MemoryExport()
	{
		MemoryTransferExport exported = Runtime.Memory.ExportTransfer();
		return new
		{
			fileName = exported.FileName,
			version = exported.Version,
			totalCount = exported.TotalCount,
			activeCount = exported.ActiveCount,
			archivedCount = exported.ArchivedCount,
			sanitizedFields = exported.SanitizedFields,
			exportedAt = exported.ExportedAt,
			content = exported.Content,
		};
	}

	/// <summary>预览 nori-memory-v1 导入而不写库。前端调用: invoke("memory_import_preview", {fileContent})</summary>
	private object MemoryImportPreview(JsonElement args)
	{
		MemoryTransferPreview preview = Runtime.Memory.PreviewTransfer(OptionalStr(args, "fileContent") ?? "");
		return new
		{
			valid = preview.IsValid,
			totalCount = preview.TotalCount,
			newCount = preview.AcceptedCount,
			duplicateCount = preview.DuplicateCount,
			conflictCount = preview.ConflictCount,
			errorCount = preview.Errors.Sum(error => error.Count),
			errors = preview.Errors.Select(error => MemoryTransferException.MessageFor(error.Category)).ToArray(),
			items = preview.Items.Select(item => new
			{
				id = item.ItemIndex,
				contentSummary = item.ContentSummary,
				kind = item.Kind,
				importance = item.Importance,
				confidence = item.Confidence,
				tags = item.Tags,
				conflictType = item.ConflictReason switch
				{
					MemoryTransferConflictReason.DuplicateInPayload => "duplicate",
					MemoryTransferConflictReason.Existing => "conflict",
					_ => "none",
				},
				conflictReason = item.ConflictReason switch
				{
					MemoryTransferConflictReason.DuplicateInPayload => "导入文件中存在相同记忆",
					MemoryTransferConflictReason.Existing => "本地已有相同记忆",
					_ => null,
				},
			}).ToArray(),
			previewToken = preview.PreviewToken,
			sanitizedNotice = "仅展示受限摘要；不会导入向量、来源正文或内部状态",
		};
	}

	/// <summary>使用一次性预览令牌提交导入。前端调用: invoke("memory_import_commit", {previewToken, conflictStrategy})</summary>
	private object MemoryImportCommit(JsonElement args)
	{
		MemoryTransferConflictStrategy strategy = ParseMemoryTransferConflictStrategy(OptionalStr(args, "conflictStrategy"));
		// 刻意不读取 args.items：提交只能使用服务端令牌保存的已校验预览。
		MemoryTransferCommitResult result = Runtime.Memory.CommitTransfer(OptionalStr(args, "previewToken"), strategy);
		if (result.Succeeded) Runtime.InvalidateSnapshot();
		MemoryTransferError? error = result.Errors.FirstOrDefault();
		return new
		{
			success = result.Succeeded,
			importedCount = result.AddedCount,
			updatedCount = result.UpdatedCount,
			skippedCount = result.SkippedCount,
			errorCount = result.Errors.Sum(entry => entry.Count),
			message = error is null ? null : MemoryTransferException.MessageFor(error.Category),
		};
	}

	private static MemoryTransferConflictStrategy ParseMemoryTransferConflictStrategy(string? value) => value?.Trim().ToLowerInvariant() switch
	{
		null or "" or "skip" => MemoryTransferConflictStrategy.Skip,
		"overwrite" => MemoryTransferConflictStrategy.Overwrite,
		"create_copy" => MemoryTransferConflictStrategy.CreateCopy,
		_ => throw new InvalidOperationException("导入冲突处理方式无效"),
	};

	private object? HardDeleteMemory(JsonElement args)
	{
		if (args.ValueKind != JsonValueKind.Object || OptionalStr(args, "confirmToken") != "DELETE_MEMORY")
			throw new InvalidOperationException("删除记忆需要明确确认");
		bool deleted = Runtime.Memory.Delete((long)Num(args, "id"));
		if (deleted) Runtime.InvalidateSnapshot();
		return deleted;
	}

	private object? ArchiveMemory(JsonElement args)
	{
		bool archived = Runtime.Memory.Archive((long)Num(args, "id"));
		if (archived) Runtime.InvalidateSnapshot();
		return archived;
	}

	private object? RestoreMemory(JsonElement args)
	{
		bool restored = Runtime.Memory.Restore((long)Num(args, "id"));
		if (restored) Runtime.InvalidateSnapshot();
		return restored;
	}

	private object? ClearMemories(JsonElement args)
	{
		if (OptionalStr(args, "confirmToken") != "CLEAR_PERSONAL_MEMORY")
			throw new InvalidOperationException("清空记忆需要明确确认");
		Runtime.Memory.Clear();
		Runtime.Memory.ClearCache();
		Runtime.InvalidateSnapshot();
		return null;
	}

	private object MemoryListPage(JsonElement args)
	{
		string query = OptionalStr(args, "query")?.Trim() ?? "";
		string? kind = OptionalStr(args, "kind");
		string? status = OptionalStr(args, "status");
		int limit = ClampLimit(OptionalInt(args, "limit"), 50);
		int offset = Math.Max(0, OptionalInt(args, "offset") ?? 0);
		IEnumerable<MemoryItem> items = Runtime.Memory.Store.GetAll(100000);
		if (query.Length > 0)
		{
			items = items.Where(item => item.Content.Contains(query, StringComparison.OrdinalIgnoreCase)
				|| (item.CanonicalSummary?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)
				|| (item.PersonaSummary?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)
				|| (item.Tags?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false));
		}
		if (kind is not null) items = items.Where(item => item.Kind.Equals(MemoryKindExtensions.Parse(kind).ToStorage(), StringComparison.OrdinalIgnoreCase));
		if (status is not null) items = items.Where(item => item.Status.Equals(status, StringComparison.OrdinalIgnoreCase));
		List<MemoryItem> filtered = items.OrderByDescending(item => item.UpdatedAt).ToList();
		return new {items = filtered.Skip(offset).Take(limit).ToArray(), total = filtered.Count};
	}

	private object MemoryGet(JsonElement args)
	{
		long id = (long)Num(args, "id");
		MemoryItem item = Runtime.Memory.Get(id) ?? throw new InvalidOperationException("未找到记忆");
		return new {item, atoms = Runtime.Memory.GetAtoms(id, limit: 100), sources = Runtime.Memory.GetSources(id)};
	}

	private object MemoryAtomList(JsonElement args)
	{
		long? memoryId = args.TryGetProperty("memoryId", out JsonElement memoryIdElement) && memoryIdElement.ValueKind == JsonValueKind.Number
			? memoryIdElement.GetInt64() : null;
		MemoryStatus? status = OptionalStr(args, "status") is { } statusText ? MemoryStatusExtensions.Parse(statusText) : null;
		return Runtime.Memory.GetAtoms(memoryId, status, ClampLimit(OptionalInt(args, "limit"), 50), Math.Max(0, OptionalInt(args, "offset") ?? 0));
	}

	private async Task<object?> MemoryKnowledgeReindexAsync(CancellationToken cancellationToken)
	{
		MemoryIndexStatus status = await Runtime.Knowledge.ReindexAsync(cancellationToken).ConfigureAwait(false);
		Runtime.InvalidateSnapshot();
		return status;
	}

	private object? OpenKnowledgeFolder()
	{
		string directory = System.IO.Path.GetDirectoryName(Runtime.Knowledge.Path) ?? _services.Paths.DataRoot;
		ShellOpen.OpenDataDirectory(directory, _services.Paths.DataRoot);
		return null;
	}

	private async Task<object?> MemoryRecallDebugAsync(JsonElement args, CancellationToken cancellationToken)
	{
		string query = Str(args, "query");
		IReadOnlyList<(string Role, string Content)> recent = AgentHistory.NormalizeRecent(_services.Chat.GetHistory(8, 0));
		MemoryContext context = await Runtime.Memory.BuildContextAsync(query, recent, cancellationToken, true, false).ConfigureAwait(false);
		return new {trace = context.Debug, personal = context.Personal, atoms = context.Atoms, knowledge = context.Knowledge, echoes = context.Echoes};
	}

	private object? UpdateMemorySettings(JsonElement args)
	{
		JsonElement settings = args.TryGetProperty("settings", out JsonElement nested) && nested.ValueKind == JsonValueKind.Object ? nested : args;
		SetMemoryBool(settings, "enabled", "memory_enabled");
		SetMemoryBool(settings, "reflectionEnabled", "memory_reflection_enabled");
		SetMemoryBool(settings, "decayEnabled", "memory_decay_enabled");
		SetMemoryBool(settings, "archiveEnabled", "memory_archive_enabled");
		SetMemoryBool(settings, "knowledgeEnabled", "memory_knowledge_enabled");
		SetMemoryBool(settings, "knowledgeWatch", "memory_knowledge_watch");
		SetMemoryBool(settings, "debugRetrieval", "memory_debug_retrieval");
		SetMemoryInt(settings, "reflectionRounds", "memory_reflection_rounds", 1, 32);
		SetMemoryInt(settings, "reflectionMinChars", "memory_reflection_min_chars", 100, 20000);
		SetMemoryInt(settings, "recallTopK", "memory_recall_top_k", 1, 20);
		SetMemoryInt(settings, "keywordTopK", "memory_keyword_top_k", 1, 100);
		SetMemoryInt(settings, "vectorTopK", "memory_vector_top_k", 1, 100);
		SetMemoryInt(settings, "rrfK", "memory_rrf_k", 1, 500);
		SetMemoryDouble(settings, "minSimilarity", "memory_min_similarity");
		SetMemoryDouble(settings, "sourceRetentionThreshold", "memory_source_retention_threshold");
		SetMemoryDouble(settings, "archiveThreshold", "memory_archive_threshold");
		Runtime.InvalidateSnapshot();
		return Runtime.Memory.Settings;
	}

	private void SetMemoryBool(JsonElement args, string name, string key)
	{
		bool? value = OptionalBool(args, name);
		if (value is not null) UpdateConfigDirect(key, value.Value ? "true" : "false");
	}

	private void SetMemoryInt(JsonElement args, string name, string key, int min, int max)
	{
		if (!args.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.Number) return;
		int number = Math.Clamp(value.GetInt32(), min, max);
		UpdateConfigDirect(key, number.ToString(System.Globalization.CultureInfo.InvariantCulture));
	}

	private void SetMemoryDouble(JsonElement args, string name, string key)
	{
		if (!args.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.Number) return;
		double number = Math.Clamp(value.GetDouble(), 0, 1);
		UpdateConfigDirect(key, number.ToString(System.Globalization.CultureInfo.InvariantCulture));
	}
}
