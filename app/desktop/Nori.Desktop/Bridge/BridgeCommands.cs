using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.Input.Platform;
using Nori.Core.Agent;
using Nori.Core.Automation;
using Nori.Core.Chat;
using Nori.Core.Configuration;
using Nori.Core.Logging;
using Nori.Core.Live2D;
using Nori.Core.Memory;
using Nori.Core.Mcp;
using Nori.Core.Platform;
using Nori.Core.Resources;
using Nori.Core.Skills;
using Nori.Core.Tools;
using Nori.Desktop.Automation;
using Nori.Desktop.Diagnostics;
using Nori.Desktop.Chat;
using Nori.Desktop.Runtime;
using MemoryService = Nori.Desktop.Memory.MemoryService;
using ModelService = Nori.Desktop.Models.ModelService;
using Nori.Desktop.Telemetry;
using Nori.Desktop.Windows;

namespace Nori.Desktop.Bridge;

/// <summary>
/// 桥接命令。
///
/// 生产入口是音频宿主的固定回报，以及设置、对话、模型、记忆四个原生窗口的白名单。
/// 秘密只写不读。命令名保持 snake_case 且动词开头。
/// </summary>
public sealed class BridgeCommands
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
		_uiDispatcher = uiDispatcher ?? throw new ArgumentNullException(nameof(uiDispatcher));
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
		MemoryService.ValidateSourceCommand(source, cmd);
		ModelService.ValidateSourceCommand(source, cmd);
		NativeChatService.ValidateSourceCommand(source, cmd);
		if (_services.SafeMode && IsNetworkCommand(cmd, args))
		{
			throw new InvalidOperationException("安全模式已禁用联网和外部服务，请退出安全模式后重试");
		}
		object? result = cmd switch
		{
		// invoke("write_log", {level: "info", message: "xxx"})
		"write_log" => WriteFrontendLog(source, args),

		// invoke("settings_ack_voice_notice")
		"settings_ack_voice_notice" => RequireMain(source, () =>
			Run(() =>
			{
				UpdateConfigDirect("voice_notice_pending", "0");
				Runtime.InvalidateSnapshot();
			})),

		// ---- 自动化宿主接线 ----
		/// invoke("automation_get_snapshot")
		"automation_get_snapshot" => source is INativeSettingsSource
			? await RequireVisibleMainAsync(source, () => Automation.GetSnapshot())
			: throw new InvalidOperationException("自动化快照仅允许原生设置窗口读取"),

		/// 更新自动化设置: invoke("automation_update_settings", {enabled?, allowPointer?, allowKeyboard?, allowScroll?, browserEnabled?})
		"automation_update_settings" => await RequireVisibleMainAsync(source, () => UpdateAutomationSettings(args)),

		/// 兼容设置页自动化总开关: invoke("settings_update_automation", {enabled?, desktopEnabled?, browserEnabled?})
		"settings_update_automation" => await RequireVisibleMainAsync(source, () => UpdateFrontendAutomationSettings(args)),

		/// 启动浏览器自动化: invoke("automation_browser_start")
		"automation_browser_start" => await AutomationBrowserStartAsync(source, cancellationToken),

		/// 停止浏览器自动化: invoke("automation_browser_stop")
		"automation_browser_stop" => await AutomationBrowserStopAsync(source, cancellationToken),

		/// invoke("automation_browser_start_task", {actions})
		"automation_browser_start_task" => await AutomationBrowserStartTaskAsync(source, args, cancellationToken),

		/// invoke("automation_browser_get_result", {taskId})
		"automation_browser_get_result" => await RequireVisibleMainAsync(source, () => BrowserTaskResultDto(ParseGuid(args, "taskId"))),

		/// invoke("automation_browser_stop_task", {taskId})
		"automation_browser_stop_task" => await RequireVisibleMainAsync(source, () => Automation.StopBrowserTask(ParseGuid(args, "taskId"))),

		/// invoke("automation_audit_list", {limit?})
		"automation_audit_list" => await RequireVisibleMainAsync(source, () => AutomationAuditList(ClampLimit(OptionalInt(args, "limit"), 50))),

		/// 查询浏览器自动化状态: invoke("automation_browser_status")
		"automation_browser_status" => await RequireVisibleMainAsync(source, () => Automation.GetBrowserStatus()),

		/// invoke("automation_probe_vision")
		"automation_probe_vision" => await RequireVisibleMainAsync(source, () => Automation.ProbeVision()),

		/// invoke("automation_stop_task", {taskId: "..."})
		"automation_stop_task" => await RequireVisibleMainAsync(source, () => Automation.StopTask(ParseGuid(args, "taskId"))),

		/// invoke("automation_stop_all")
		"automation_stop_all" => await AutomationStopAllAsync(source, cancellationToken),

		// ---- AI 设置 ----
		// invoke("llm_fetch_models", {provider, baseUrl, apiKey})
		"llm_fetch_models" => await FetchModelsWithSourceCheckAsync(source, args),
		/// invoke("ai_test_connection", {target: "chat" | "embedding", provider?, baseUrl?, apiKey?, model?, dimensions?})
		"ai_test_connection" => await TestAiConnectionAsync(source, args, cancellationToken),

		/// invoke("settings_update_ai_providers", {chat?: {...}, embedding?: {...}, persona?})
		"settings_update_ai_providers" => RequireLabel(source, WindowLabels.FirstRun, WindowLabels.Main, () =>
			Run(() =>
			{
				UpdateUnifiedAiSettings(args);
				Runtime.InvalidateSnapshot();
			})),

		// invoke("settings_update_voice", {...})
		"settings_update_voice" => RequireMain(source, () =>
			Run(() =>
			{
				UpdateOptionalConfig(args, "volume", "audio_volume");
				UpdateOptionalConfig(args, "ttsProvider", "tts_provider");
				UpdateOptionalConfig(args, "ttsBaseUrl", "tts_base_url");
				UpdateOptionalConfig(args, "ttsModel", "tts_model");
				UpdateSecretConfig(args, "ttsApiKey", "tts_api_key");
				UpdateOptionalConfig(args, "ttsVoice", "tts_voice");
				UpdateOptionalConfig(args, "ttsSpeed", "tts_speed");
				UpdateBoolConfig(args, "ttsAutoPlay", "tts_auto_play");
				UpdateOptionalConfig(args, "gptsovitsBaseUrl", "gptsovits_base_url");
				UpdateOptionalConfig(args, "gptsovitsRefAudio", "gptsovits_ref_audio");
				UpdateOptionalConfig(args, "gptsovitsPromptText", "gptsovits_prompt_text");
				UpdateOptionalConfig(args, "gptsovitsPromptLang", "gptsovits_prompt_lang");
				UpdateOptionalConfig(args, "indexttsTemplateAudio", "indextts_template_audio");
				UpdateOptionalConfig(args, "indexttsEmoAlpha", "indextts_emo_alpha");
				UpdateOptionalConfig(args, "sttProvider", "stt_provider");
				UpdateOptionalConfig(args, "sttBaseUrl", "stt_base_url");
				UpdateSecretConfig(args, "sttApiKey", "stt_api_key");
				if (_services.Runtime?.Voice is not null)
				{
					string raw = _services.Config.GetStringOr("audio_volume", "1");
					if (double.TryParse(raw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double vol))
					{
						_services.Runtime.Voice.SetVolume(vol);
					}
					if (HasTtsConfigurationChange(args) || HasString(args, "ttsModel")) _services.Runtime.Voice.NotifyConfigurationChanged();
				}
				Runtime.InvalidateSnapshot();
			})),

		/// <summary>
		/// 更新常规设置并同步窗口外观。
		/// 前端调用：invoke("settings_update_general", {backgroundBlurEnabled: true})
		/// </summary>
		"settings_update_general" => RequireLabel(source, WindowLabels.FirstRun, WindowLabels.Main, () =>
			Run(() =>
			{
				UpdateOptionalConfig(args, "language", ConfigStore.KeyLanguage);
				UpdateBoolConfig(args, "petAutoSummon", "pet_auto_summon");
				UpdateBoolConfig(args, "quickChatEnabled", ConfigStore.KeyQuickChatEnabled);
				UpdateBoolConfig(args, "backgroundBlurEnabled", ConfigStore.KeyBackgroundBlurEnabled);
				if (args.TryGetProperty("backgroundBlurEnabled", out _))
				{
					bool blurEnabled = _services.Config.GetBoolOr(ConfigStore.KeyBackgroundBlurEnabled, true);
					Avalonia.Threading.Dispatcher.UIThread.Post(() => _services.Windows.UpdateBackgroundBlurEnabled(blurEnabled));
				}
				UpdateBoolConfig(args, "sidebarCollapsed", "ui_sidebar_collapsed");
				UpdateBoolConfig(args, "autoCheckUpdates", "auto_check_updates");
				UpdateTelemetryConsent(source, args);
				_services.Telemetry.Configure(_services.Config.GetTelemetryConsent() == TelemetryConsent.Granted);
				// 托盘菜单是另一棵原生控件树, 不吃前端快照。语言改了要单独推给它,
				// 否则会一直停在启动那一刻的语言上。
				Avalonia.Threading.Dispatcher.UIThread.Post(Tray.TrayMenu.Refresh);
				Runtime.InvalidateSnapshot();
			})),

		/// <summary>
		/// 更新文件工具的工作目录与单轮工具次数上限。
		/// 前端调用：invoke("settings_update_workspace", {root?: string, maxToolIterations?: number})
		/// </summary>
		"settings_update_workspace" => RequireMain(source, () => Run(() => UpdateWorkspaceSettings(args))),

		/// <summary>
		/// 更新 runTask 可运行的具名任务清单，整份替换。
		/// 前端调用：invoke("settings_update_tasks", {tasks: {name: string, command: string}[]})
		/// </summary>
		"settings_update_tasks" => RequireMain(source, () => Run(() => UpdateTaskSettings(args))),

		/// <summary>
		/// 开关读屏。
		/// 前端调用：invoke("settings_update_screen", {enabled: boolean})
		/// </summary>
		/// <summary>
		/// 开关一条情绪表达通道。
		/// 前端调用：invoke("settings_update_expression", {channel: string, enabled: boolean})
		/// </summary>
		"settings_update_expression" => RequireMain(source, () => Run(() => UpdateExpressionChannel(args))),

		/// <summary>
		/// 换授权档位。
		/// 前端调用：invoke("settings_update_permission", {gear: "ask"|"session"|"trusted"|"bypass"})
		/// </summary>
		"settings_update_permission" => RequireMain(source, () => Run(() => UpdatePermissionGear(args))),

		/// <summary>
		/// 开关「待决授权也发系统通知」。
		/// 前端调用：invoke("settings_update_notifications", {enabled: boolean})
		/// </summary>
		"settings_update_notifications" => RequireMain(source, () => Run(() =>
		{
			UpdateBoolConfig(args, "enabled", ConfigStore.KeyToastApprovals);
			// 关掉时要把开始菜单快捷方式和注册表项清掉 —— 用户关的是「别在我机器上留东西」，
			// 只停止发送等于留了一半。
			Runtime.SyncNotificationRegistration();
			Runtime.InvalidateSnapshot();
		})),

		"settings_update_screen" => RequireMain(source, () => Run(() =>
		{
			UpdateBoolConfig(args, "enabled", ConfigStore.KeyScreenReadingEnabled);
			// 工具注册表按开关构建，不重建则要到下次启动才生效。
			Runtime.RebuildTools();
			Runtime.InvalidateSnapshot();
		})),

		/// <summary>
		/// 打开系统文件夹选择对话框，返回选中路径；用户取消时返回 null。
		/// 前端调用：invoke("settings_pick_workspace")
		/// </summary>
		"settings_pick_workspace" => await PickWorkspaceAsync(source),

		// ---- 自动更新 ----
		/// <summary>
		/// 检查是否有可用软件更新。
		/// 前端调用：invoke("updater_check")
		/// </summary>
		"updater_check" => await RequireMainAsync(source, async () =>
		{
			var update = _services.Update ?? throw new InvalidOperationException("更新服务尚未初始化");
			var check = await update.CheckForUpdateAsync(cancellationToken);
			Runtime.InvalidateSnapshot();
			return (object?)new
			{
				available = check.Available,
				currentVersion = check.CurrentVersion,
				latestVersion = check.LatestVersion,
				releaseTag = check.ReleaseTag,
				releaseNotes = check.ReleaseNotes,
				publishedAt = check.PublishedAt?.ToString("o"),
			};
		}),

		/// <summary>
		/// 下载并安装候选版本到新部署槽。
		/// 前端调用：invoke("updater_install")
		/// </summary>
		"updater_install" => await RequireMainAsync(source, async () =>
		{
			var update = _services.Update ?? throw new InvalidOperationException("更新服务尚未初始化");
			var commit = await update.DownloadAndInstallAsync(progress: null, cancellationToken);
			Runtime.InvalidateSnapshot();
			return (object?)new
			{
				success = true,
				slotName = commit.SlotName,
				productVersion = commit.Manifest.ProductVersion,
			};
		}),

		/// <summary>
		/// 取消当前正在进行的更新检查或安装。
		/// 前端调用：invoke("updater_cancel")
		/// </summary>
		"updater_cancel" => RequireMain(source, () =>
		{
			var update = _services.Update ?? throw new InvalidOperationException("更新服务尚未初始化");
			update.CancelActiveOperation();
			Runtime.InvalidateSnapshot();
			return (object?)true;
		}),

		/// <summary>
		/// 拉起启动器并安全退出当前宿主进程，由启动器等待旧进程退出后切换到新槽。
		/// 前端调用：invoke("updater_restart")
		/// </summary>
		"updater_restart" => await RequireMainAsync(source, () =>
		{
			var update = _services.Update ?? throw new InvalidOperationException("更新服务尚未初始化");
			update.LaunchRestart();
			_services.Windows.Shutdown();
			return Task.FromResult<object?>(null);
		}),

		// invoke("settings_update_proactive", {idleEnabled?, idleMinutes?, dailyGreeting?})
		"settings_update_proactive" => RequireMain(source, () =>
			Run(() =>
			{
				UpdateBoolConfig(args, "idleEnabled", "proactive_idle_enabled");
				UpdateNumberConfig(args, "idleMinutes", "proactive_idle_minutes");
				UpdateBoolConfig(args, "dailyGreeting", "proactive_daily_greeting");
				Runtime.InvalidateSnapshot();
			})),

		// invoke("tools_set_enabled", {name: "getTime", enabled: false})
		"tools_set_enabled" => RequireMain(source, () =>
			Run(() =>
			{
				string name = Str(args, "name");
				bool enabled = OptionalBool(args, "enabled") ?? true;
				if (!Runtime.Tools.SetEnabled(name, enabled))
				{
					throw new InvalidOperationException($"未找到工具: {name}");
				}
				PersistDisabledTools();
				Runtime.InvalidateSnapshot();
			})),

		// invoke("model_select", {modelId: "nori"})
		"model_select" => RequireLabel(source, WindowLabels.FirstRun, WindowLabels.Main, () =>
			Run(() =>
			{
				string modelId = RequireKnownInstalledModel(Str(args, "modelId"));
				UpdateConfigDirect(ConfigStore.KeySelectedModel, modelId);
				ApplyPetConfig(ConfigStore.KeySelectedModel, modelId);
				_services.Logger.Write(LogSource.Backend, "info", $"启用模型: {modelId}");
				Runtime.InvalidateSnapshot();
			})),

		// invoke("model_import_local", {resourceType?: "live2d"})
		"model_import_local" => await ModelImportLocalAsync(source, args, cancellationToken),

		// invoke("indextts_pick_template", {}) — 选择 IndexTTS 音频模板文件并写入配置
		"indextts_pick_template" => await IndexTtsPickTemplateAsync(source, args, cancellationToken),

		// invoke("indextts_clone_voice", {filePath?}) — 上传模板音频克隆音色并返回 voice_id
		"indextts_clone_voice" => await IndexTtsCloneVoiceAsync(source, args, cancellationToken),

		// invoke("model_get_meta", {modelId: "arg-nori"})
		"model_get_meta" => RequireMain(source, () =>
		{
			string modelId = RequireKnownInstalledModel(Str(args, "modelId"));
			string dir = _services.Resources.ResourceDir(ResourceType.Live2D, modelId);
			Nori.Core.Live2D.Model3MetaInfo meta = Nori.Core.Live2D.Model3Meta.Read(dir);
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
		}),

		/// invoke("model_set_interactions", {modelId, interactions})
		"model_set_interactions" => await ModelSetInteractionsAsync(source, args),

		// invoke("model_set_display", {modelId, scale?, expressions?})
		"model_set_display" => await ModelSetDisplayAsync(source, args),

		// invoke("model_set_behavior", {autoBlink?: true, maxFps?: 60, ...})
		"model_set_behavior" => await ModelSetBehaviorAsync(source, args),

		// ---- 聊天 / Agent 会话 ----
		// invoke("chat_start", {text: "你好呀"})
		"chat_start" => RequireMain(source, () =>
		{
			string text = Str(args, "text").Trim();
			if (text.Length == 0) throw new InvalidOperationException("消息内容不能为空");
			return Runtime.StartChat(source, text);
		}),

		// invoke("chat_cancel", {sessionId: "..."})
		"chat_cancel" => RequireMain(source, () => Runtime.CancelChat(source, Str(args, "sessionId"))),

		// invoke("approval_respond", {requestId: "...", approved: true})
		"approval_respond" => RequireMain(source, () => RespondApproval(source, args)),

		/// <summary>
		/// 延长本来源的待决工具授权，不能超过工具调用的实际截止时间。
		/// 前端调用：invoke("approval_extend", {requestId: "..."})
		/// </summary>
		"approval_extend" => RequireMain(source, () => new
		{
			deadlineUtc = Runtime.ExtendApproval(source, Str(args, "requestId")),
		}),

		// invoke("chat_history_page", {limit?: 50, beforeId?: 0})
		"chat_history_page" => RequireMain(source, () => GetHistoryPage(
			ClampLimit(OptionalInt(args, "limit"), 50),
			(long)(OptionalDouble(args, "beforeId") ?? 0))),

		// invoke("chat_clear")
		"chat_clear" => await ClearChatAsync(source, cancellationToken),

		// ---- 记忆库 ----
		/// invoke("memory_add", {content, type?, importance?, tags?})
		"memory_add" => await MemoryAddAsync(source, args),

		/// invoke("memory_update", {id, content, importance?, tags?})
		"memory_update" => await MemoryUpdateAsync(source, args),

		/// invoke("memory_delete", {id, confirmToken: "DELETE_MEMORY"})
		"memory_delete" => RequireMain(source, () => HardDeleteMemory(source, args)),

		/// invoke("memory_clear", {confirmToken: "CLEAR_PERSONAL_MEMORY"})
		"memory_clear" => RequireMain(source, () => ClearMemories(source, args)),

		/// invoke("memory_archive", {id})
		"memory_archive" => RequireMain(source, () => ArchiveMemory(args)),

		/// invoke("memory_restore", {id})
		"memory_restore" => RequireMain(source, () => RestoreMemory(args)),

		/// invoke("memory_list_page", {query?, kind?, status?, limit?, offset?})
		"memory_list_page" => RequireMain(source, () => MemoryListPage(args)),

		/// invoke("memory_get", {id})
		"memory_get" => RequireMain(source, () => MemoryGet(args)),

		/// invoke("memory_atom_list", {memoryId?, status?, limit?, offset?})
		"memory_atom_list" => RequireMain(source, () => MemoryAtomList(args)),

		/// invoke("memory_knowledge_status")
		"memory_knowledge_status" => RequireMain(source, () => Runtime.Knowledge.Status),

		/// invoke("memory_knowledge_reindex")
		"memory_knowledge_reindex" => await MemoryKnowledgeReindexAsync(source, cancellationToken),

		/// invoke("memory_knowledge_open")
		"memory_knowledge_open" => RequireMain(source, () => OpenKnowledgeFolder()),

		/// invoke("memory_recall_debug", {query})
		"memory_recall_debug" => await MemoryRecallDebugAsync(source, args, cancellationToken),

		/// invoke("memory_update_settings", {settings: {...}})
		"memory_update_settings" => RequireMain(source, () => UpdateMemorySettings(args)),

		/// invoke("memory_reembed_all")
		"memory_reembed_all" => await MemoryReembedAllAsync(source, cancellationToken),

		/// invoke("memory_export")
		"memory_export" => await RequireVisibleMainAsync(source, MemoryExport),

		/// invoke("memory_import_preview", {fileContent, fileName?, fileSize?})
		"memory_import_preview" => await RequireVisibleMainAsync(source, () => MemoryImportPreview(args)),

		/// invoke("memory_import_commit", {previewToken, conflictStrategy?})；忽略客户端 items。
		"memory_import_commit" => await RequireVisibleMainAsync(source, () => MemoryImportCommit(args)),

		// ---- 技能 ----
		// invoke("skills_marketplace")
		"skills_marketplace" => RequireMain(source, () => SkillServiceMarketplace()),

		/// 从内置市场安装技能: invoke("skills_install_marketplace", {skillId: "gaming-partner"}) → 脱敏 SkillDto
		"skills_install_marketplace" => await RequireVisibleMainAsync(source, () =>
		{
			string skillId = Str(args, "skillId").Trim();
			if (skillId.Length == 0) throw new InvalidOperationException("技能 ID 不能为空");
			SkillRecord installed = Runtime.Skills.InstallFromMarketplace(skillId);
			Runtime.InvalidateSnapshot();
			return RedactedSkillDto(installed);
		}),

		// invoke("skills_toggle", {id, enabled})
		"skills_toggle" => RequireMain(source, () =>
			Run(() =>
			{
				if (!SkillsToggle(Str(args, "id"), OptionalBool(args, "enabled") ?? true))
				{
					throw new InvalidOperationException($"未找到技能: {Str(args, "id")}");
				}
				Runtime.InvalidateSnapshot();
			})),

		// invoke("skills_install_url", {url})
		"skills_install_url" => await SkillsInstallUrlAsync(source, args),

		// invoke("skills_save_custom", {skill: {...}})
		"skills_save_custom" => await SkillsSaveCustomAsync(source, args),

		// invoke("skills_uninstall", {id})
		"skills_uninstall" => RequireMain(source, () =>
			Run(() =>
			{
				Runtime.Skills.Uninstall(Str(args, "id"));
				Runtime.InvalidateSnapshot();
			})),

		// invoke("skills_export", {id}) → JSON 字符串
		"skills_export" => RequireMain(source, () => Runtime.Skills.Export(Str(args, "id"))),

		// ---- MCP ----
		// invoke("mcp_get_servers")
		"mcp_get_servers" => await McpGetServersAsync(source),
		// invoke("mcp_save_server", {id, name, transport, command, args, env, url, enabled, autoConnect})
		"mcp_save_server" => await McpSaveServerAsync(source, args),
		// invoke("mcp_delete_server", {id})
		"mcp_delete_server" => await McpDeleteServerAsync(source, args),
		// invoke("mcp_connect_server", {id})
		"mcp_connect_server" => await McpConnectServerAsync(source, args),
		// invoke("mcp_disconnect_server", {id})
		"mcp_disconnect_server" => await McpDisconnectServerAsync(source, args),
		// invoke("mcp_test_server", {id, name, transport, command, args, env, url, enabled, autoConnect})
		"mcp_test_server" => await McpTestServerAsync(source, args),
		// invoke("mcp_call_tool", {serverId, toolName, arguments, sessionId?})
		"mcp_call_tool" => await McpCallToolAsync(source, args, cancellationToken),
		// invoke("mcp_import_url", {url})
		"mcp_import_url" => await McpImportUrlAsync(source, args, cancellationToken),

		// invoke("tools_execute_manual", {name, arguments}) — 设置页手动测试, 仅放行 safe 工具
		"tools_execute_manual" => await ToolsExecuteManualAsync(source, args),

		// ---- 定时提醒 ----
		/// invoke("reminder_add", {content, delayMinutes}) 添加倒计时提醒
		"reminder_add" => RequireMain(source, () =>
		{
			double delayMinutes = ReadReminderNumber(args, "delayMinutes", allowMissing: true) ?? 15;
			Nori.Core.Proactive.ReminderItem item = Runtime.Proactive.AddReminder(Str(args, "content"), delayMinutes);
			Runtime.InvalidateSnapshot();
			return item;
		}),

		/// invoke("reminder_cancel", {id}) 取消提醒
		"reminder_cancel" => RequireMain(source, () =>
		{
			bool cancelled = Runtime.Proactive.CancelReminder(Str(args, "id"));
			if (cancelled) Runtime.InvalidateSnapshot();
			return cancelled;
		}),

		/// invoke("reminder_update", {id, content?, triggerTime?, delayMinutes?, repeatDaily?, timezone?, recurrenceJson?}) 更新提醒
		"reminder_update" => RequireMain(source, () =>
		{
			object result = UpdateReminder(args);
			Runtime.InvalidateSnapshot();
			return result;
		}),

		/// invoke("reminder_list") 查询提醒状态
		"reminder_list" => RequireMain(source, () => Runtime.Proactive.ListReminders()),

		// ---- 语音 ----
		// invoke("tts_test", {text?})
		"tts_test" => await TtsTestAsync(source, args),

		// invoke("tts_stop")
		"tts_stop" => RequireMain(source, () => Run(Runtime.Voice.Stop)),

		// invoke("stt_start")
		"stt_start" => await SttStartAsync(source, cancellationToken),

		// invoke("stt_stop") → {text}
		"stt_stop" => await SttStopAsync(source, cancellationToken),

		// ---- 前端音频宿主回报 (WebAudio / MediaRecorder 下沉后的反向通道) ----
		// invoke("audio_host_ready")
		"audio_host_ready" => RequireLabel(source, WindowLabels.AudioHost, () => Run(Runtime.MarkAudioHostReady)),

		// invoke("audio_playback_finished", {token, error?})
		"audio_playback_finished" => RequireLabel(source, WindowLabels.AudioHost, () =>
			Run(() => Runtime.ReportPlaybackFinished(Str(args, "token"), OptionalStr(args, "error")))),

		// invoke("audio_level", {level: 0.42})
		"audio_level" => RequireLabel(source, WindowLabels.AudioHost, () =>
			Run(() => Runtime.ReportAudioLevel(Num(args, "level")))),

		// invoke("audio_record_ready", {token})
		"audio_record_ready" => RequireLabel(source, WindowLabels.AudioHost, () =>
			Run(() => Runtime.ReportRecordingReady(Str(args, "token")))),

		// invoke("audio_record_failed", {token, error?})
		"audio_record_failed" => RequireLabel(source, WindowLabels.AudioHost, () =>
			Run(() => Runtime.ReportRecordingFailed(Str(args, "token"), OptionalStr(args, "error")))),

		// invoke("audio_upload_failed", {token, error?})
		"audio_upload_failed" => RequireLabel(source, WindowLabels.AudioHost, () =>
			Run(() => Runtime.ReportRecordingFailed(Str(args, "token"), OptionalStr(args, "error")))),

		// ---- 插件替代 ----
		// invoke("open_url", {url: "https://..."})
		"open_url" => Run(() => ShellOpen.OpenUrl(Str(args, "url"))),

		// invoke("clipboard_write_text", {text: "..."})
		"clipboard_write_text" => await WriteClipboardAsync(source, Str(args, "text")),

		// ---- 调试 ----
		"get_recent_logs" => RequireMain(source, () => _services.Logger.RecentLogs().Select(entry => new
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
		}).ToArray()),
		"get_logging_status" => RequireMain(source, () => GetLoggingStatus()),
		"set_logging_level" => RequireMain(source, () => SetLoggingLevel(args)),
		"clear_recent_logs" => RequireMain(source, () => Run(_services.Logger.ClearRecentLogs)),
		"get_diagnostic_info" => RequireMain(source, () => DiagnosticInfo.Build(_services.PetRuntime, _services.Paths, _services.SafeMode)),
		// invoke("export_diagnostics") → {fileName, bytes, skipped}
		"export_diagnostics" => await ExportDiagnosticsAsync(source, cancellationToken),
		"open_log_folder" => RequireMain(source, () => Run(OpenLogFolder)),
		"run_gc_collect" => RequireMain(source, RunGcCollect),
		"debug_crash_test" => RequireMain(source, () => Run(() => DebugCrashTest(Str(args, "mode")))),

			_ => throw new InvalidOperationException($"未知的命令: {cmd}"),
		};
		// 原生窗口修改已经提交时返回真实成功，避免关闭期间的取消把已写入误报为失败。
		bool committedNativeWrite = (source is INativeMemorySource && MemoryService.IsStateChangingCommand(cmd))
			|| (source is INativeModelSource && ModelService.IsStateChangingCommand(cmd))
			|| (source is INativeChatSource && cmd is "chat_start" or "chat_clear" or "approval_respond" or "approval_extend");
		if (!committedNativeWrite) cancellationToken.ThrowIfCancellationRequested();
		return result;
	}

	private static bool IsNetworkCommand(string command, JsonElement args)
	{
		if (command is
			"llm_fetch_models"
			or "ai_test_connection"
			or "chat_start"
			or "memory_reembed_all"
			or "memory_recall_debug"
			or "memory_knowledge_reindex"
			or "skills_install_url"
			or "mcp_get_servers"
			or "mcp_connect_server"
			or "mcp_test_server"
			or "mcp_call_tool"
			or "mcp_import_url"
			or "updater_check"
			or "updater_install"
			or "tts_test"
			or "stt_start"
			or "stt_stop"
			or "open_url") return true;

		// 安全模式仍允许手动保存不自动连接的 MCP 配置，但不能借此触发自动连接。
		if (command == "mcp_save_server"
			&& args.ValueKind == JsonValueKind.Object
			&& OptionalBool(args, "enabled") == true
			&& OptionalBool(args, "autoConnect") == true) return true;
		return false;
	}

	/// <summary>main 或已通过领域白名单校验的原生窗口校验 (无返回值场景)</summary>
	private static void RequireMainVoid(IBridgeSource source)
	{
		if (source is not INativeSettingsSource and not INativeMemorySource and not INativeModelSource and not INativeChatSource
			&& source.Label != WindowLabels.Main)
		{
			throw new InvalidOperationException($"命令只能由 {WindowLabels.Main} 窗口调用");
		}
	}

	/// <summary>在 UI 线程读取可见性后校验来源; Avalonia 的 Window.IsVisible 只能在 UI 线程访问。</summary>
	private async Task<object?> RequireVisibleMainAsync(IBridgeSource source, Func<object?> factory)
	{
		RequireMainVoid(source);
		bool visible = await OnUi(() => (object?)source.IsVisible) is true;
		if (!visible) throw new InvalidOperationException("main 窗口不可见");
		return factory();
	}

	private async Task RequireVisibleMainVoidAsync(IBridgeSource source)
	{
		RequireMainVoid(source);
		bool visible = await OnUi(() => (object?)source.IsVisible) is true;
		if (!visible) throw new InvalidOperationException("main 窗口不可见");
	}

	private AutomationSettingsSnapshot UpdateAutomationSettings(JsonElement args) => Automation.UpdateSettings(
		OptionalBool(args, "enabled"),
		OptionalBool(args, "allowPointer"),
		OptionalBool(args, "allowKeyboard"),
		OptionalBool(args, "allowScroll"),
		OptionalBool(args, "browserEnabled"));

	private AutomationSettingsSnapshot UpdateFrontendAutomationSettings(JsonElement args)
	{
		bool? desktopEnabled = OptionalBool(args, "desktopEnabled");
		return Automation.UpdateSettings(
			OptionalBool(args, "enabled"),
			desktopEnabled,
			desktopEnabled,
			desktopEnabled,
			OptionalBool(args, "browserEnabled"));
	}

	private async Task<object?> AutomationBrowserStartAsync(IBridgeSource source, CancellationToken cancellationToken)
	{
		await RequireVisibleMainVoidAsync(source);
		return await Automation.StartBrowserAsync(cancellationToken).ConfigureAwait(false);
	}

	private async Task<object?> AutomationBrowserStopAsync(IBridgeSource source, CancellationToken cancellationToken)
	{
		await RequireVisibleMainVoidAsync(source);
		return await Automation.StopBrowserAsync(cancellationToken).ConfigureAwait(false);
	}

	/// <summary>启动受限浏览器 DOM 任务。前端调用: invoke("automation_browser_start_task", {actions})</summary>
	private async Task<object?> AutomationBrowserStartTaskAsync(IBridgeSource source, JsonElement args, CancellationToken cancellationToken)
	{
		await RequireVisibleMainVoidAsync(source);
		if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty("actions", out JsonElement actions))
			throw new InvalidOperationException("缺少参数: actions");
		BrowserAutomationTaskPlan plan = BrowserAutomationTaskPlan.Parse(actions);
		return await Automation.StartBrowserTaskAsync(plan, cancellationToken).ConfigureAwait(false);
	}

	/// <summary>读取短期浏览器结果。前端调用: invoke("automation_browser_get_result", {taskId})</summary>
	private object? BrowserTaskResultDto(Guid taskId)
	{
		BrowserAutomationTaskResult? result = Automation.GetBrowserTaskResult(taskId);
		if (result is null) return null;
		return new
		{
			taskId = result.TaskId,
			success = result.Succeeded,
			summary = result.Succeeded ? "浏览器任务已完成" : null,
			data = result.VisibleText,
			error = result.FailureCode,
			finishedAt = result.FinishedAt,
		};
	}

	/// <summary>读取脱敏审计记录。前端调用: invoke("automation_audit_list", {limit?})</summary>
	private object AutomationAuditList(int limit) => _services.AutomationAudit.List(limit).Select(record => new
	{
		id = record.Id,
		taskId = record.TaskId,
		timestamp = record.Timestamp,
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

	private async Task<object?> AutomationStopAllAsync(IBridgeSource source, CancellationToken cancellationToken)
	{
		await RequireVisibleMainVoidAsync(source);
		return await Automation.StopAllAsync(cancellationToken).ConfigureAwait(false);
	}

	private bool RespondApproval(IBridgeSource source, JsonElement args)
	{
		bool approved = OptionalBool(args, "approved") ?? false;
		if (source is INativeChatSource && approved && !source.IsVisible)
			throw new InvalidOperationException("对话窗口不可见，无法批准工具执行");
		return Runtime.RespondApproval(source, Str(args, "requestId"), approved);
	}

	/// <summary>
	/// 清空聊天记录。
	///
	/// 对话交给 LuoLiCore 时，上下文记在对端，本地表只是一份副本。**先重置远端、成功了才删
	/// 本地**：反过来的话，删完本地远端却没清，她下一句仍然接得上前面聊过的内容，而界面上
	/// 什么都没有了，用户只会以为清空没生效 —— 这种不一致比清不掉难查得多。
	///
	/// 因此远端重置失败时整条命令失败，本地记录原样留着，两边仍然一致。连不上对端又确实要
	/// 清本地的话，关掉 LuoLiCore 再清。
	/// </summary>
	private async Task<object?> ClearChatAsync(IBridgeSource source, CancellationToken cancellationToken) =>
		await RequireMainAsync(source, async () =>
		{
			// 与 AgentEngine 的生成及最终落库共用闸门，含尚未进入后台执行的已接受请求。
			using AgentSessionLease lease = Runtime.Engine.ReserveSession("chat-clear", cancellationToken);
			// 安全模式禁用一切外部调用（AGENTS.md §1「Safe Mode」），重置远端会话是一次
			// 真正的出网请求，所以这里跳过它。
			//
			// 不把 chat_clear 整条加进 IsNetworkCommand：清空本地聊天记录本身是纯本地操作，
			// 安全模式没有理由连它一起禁掉 —— 那会让人在排障时连清个记录都做不到。
			//
			// 代价是两边会不一致：安全模式下对话本来就不走远端（chat_start 已被挡），远端的
			// 上下文停在进入安全模式那一刻，清完本地它还记着。因此把这件事**报给调用方**，
			// 由界面提示一句，而不是让用户以为清干净了 —— 这种不一致查起来比清不掉贵得多。
			// 判据要在动手之前取：作废之后再问「有没有远端会话」恒为否。
			bool hadRemoteSession = Runtime.Engine.HasRemoteContext;
			bool remoteReset = false;
			if (_services.SafeMode)
			{
				// 安全模式不出网，但本地记着的会话必须作废 —— 否则退出安全模式之后那段「已经
				// 清掉」的上下文会原样回来。这一步不联网。
				Runtime.Engine.ForgetRemoteSession();
			}
			else
			{
				remoteReset = await Runtime.Engine.ResetRemoteContextAsync(cancellationToken);
			}

			_services.Chat.ClearHistory();
			Runtime.NotifyChatHistoryChanged();
			return new ClearChatResult(
				remoteReset,
				_services.SafeMode && hadRemoteSession
					// 用户这一侧已经干净了：本地空了，那段会话也不会再被用到。留着说一句是因为
					// 旧会话的内容仍留在对端服务器上 —— 删它需要联网，而安全模式的前提是不联网。
					? "安全模式下未联系外部服务；本地会话已作废，重新启用后会开一段新的对话"
					: null);
		});

	/// <summary>
	/// 清空聊天记录的结果。
	///
	/// <paramref name="RemoteReset"/> 为 false 有两种原因：没接外部后端（无事可做），或者安全
	/// 模式跳过了那次调用（<paramref name="Note"/> 会说明）。两者对界面的意义不同。
	/// </summary>
	private sealed record ClearChatResult(bool RemoteReset, string? Note);

	private async Task<object?> MemoryAddAsync(IBridgeSource source, JsonElement args)
	{
		RequireMainVoid(source);
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

	private async Task<object?> MemoryUpdateAsync(IBridgeSource source, JsonElement args)
	{
		RequireMainVoid(source);
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

	private async Task<object?> MemoryReembedAllAsync(IBridgeSource source, CancellationToken cancellationToken)
	{
		RequireMainVoid(source);
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

	private object? HardDeleteMemory(IBridgeSource source, JsonElement args)
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

	private object? ClearMemories(IBridgeSource source, JsonElement args)
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

	private async Task<object?> MemoryKnowledgeReindexAsync(IBridgeSource source, CancellationToken cancellationToken)
	{
		RequireMainVoid(source);
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

	private async Task<object?> MemoryRecallDebugAsync(IBridgeSource source, JsonElement args, CancellationToken cancellationToken)
	{
		RequireMainVoid(source);
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

	private async Task<object?> SkillsInstallUrlAsync(IBridgeSource source, JsonElement args)
	{
		RequireMainVoid(source);
		object skill = await Runtime.Skills.InstallFromUrlAsync(Str(args, "url"));
		Runtime.InvalidateSnapshot();
		return skill;
	}

	private async Task<object?> SkillsSaveCustomAsync(IBridgeSource source, JsonElement args)
	{
		RequireMainVoid(source);
		SkillRecord skill = args.GetProperty("skill").Deserialize<SkillRecord>(BridgeJson.Options)
			?? throw new InvalidOperationException("技能数据不能为空");
		object saved = Runtime.Skills.SaveCustom(skill);
		Runtime.InvalidateSnapshot();
		return saved;
	}

	private async Task<object?> McpGetServersAsync(IBridgeSource source)
	{
		RequireMainVoid(source);
		IReadOnlyList<McpServerStatusInfo> servers = await _services.Mcp.GetServersAsync();
		// 原生设置已订阅状态通知，读取列表不能再触发同一页面的刷新。
		// 连接、断开与配置写入命令仍负责更新工具注册表并广播状态。
		if (source is not INativeSettingsSource)
		{
			await Runtime.RefreshMcpToolsAsync();
			Runtime.InvalidateSnapshot();
		}
		return servers;
	}

	private async Task<object?> ToolsExecuteManualAsync(IBridgeSource source, JsonElement args)
	{
		RequireMainVoid(source);
		string name = Str(args, "name");
		RegisteredTool? tool = Runtime.Tools.Get(name)
			?? throw new InvalidOperationException($"未找到工具: {name}");
		if (tool.PermissionLevel != "safe")
		{
			throw new InvalidOperationException($"{name} 标记为 {tool.PermissionLevel}, 手动测试仅支持 safe 工具");
		}
		JsonNode? toolArgs = null;
		if (args.TryGetProperty("arguments", out JsonElement argElem) && argElem.ValueKind == JsonValueKind.Object)
		{
			toolArgs = JsonNode.Parse(argElem.GetRawText());
		}
		ToolResult result = await Runtime.Tools.ExecuteAsync(name, toolArgs);
		if (result.Error is not null) throw new InvalidOperationException(result.Error);
		return result.Result;
	}

	private object UpdateReminder(JsonElement args)
	{
		string id = Str(args, "id");
		string? content = OptionalReminderPatchString(args, "content", allowNull: false);
		bool? repeatDaily = OptionalReminderBool(args, "repeatDaily");
		string? timezone = OptionalReminderPatchString(args, "timezone", allowNull: false);
		string? recurrenceJson = OptionalReminderPatchString(args, "recurrenceJson", allowNull: true);
		(long? TriggerAt, double? DelayMinutes) time = ReadReminderUpdateTime(args);
		if (content is null && repeatDaily is null && timezone is null && recurrenceJson is null
			&& time.TriggerAt is null && time.DelayMinutes is null)
			throw new InvalidOperationException("提醒更新至少需要一个字段");
		if (time.DelayMinutes is { } delay)
			return Runtime.Proactive.UpdateReminderAfter(id, content, delay, repeatDaily, timezone, recurrenceJson);
		return Runtime.Proactive.UpdateReminder(id, content, time.TriggerAt, repeatDaily, timezone, recurrenceJson);
	}

	private static (long? TriggerAt, double? DelayMinutes) ReadReminderUpdateTime(JsonElement args)
	{
		bool hasDelay = HasProperty(args, "delayMinutes");
		bool hasTriggerTime = HasProperty(args, "triggerTime");
		bool hasTriggerAt = HasProperty(args, "triggerAt");
		if (hasDelay && (hasTriggerTime || hasTriggerAt))
			throw new InvalidOperationException("只能指定一种提醒时间");
		if (hasTriggerTime && hasTriggerAt)
			throw new InvalidOperationException("只能指定一种提醒时间");
		if (hasDelay) return (null, ReadReminderNumber(args, "delayMinutes")!.Value);
		if (hasTriggerTime) return (ReadReminderTimestamp(args, "triggerTime"), null);
		if (hasTriggerAt) return (ReadReminderTimestamp(args, "triggerAt"), null);
		return (null, null);
	}

	private static string? OptionalReminderPatchString(JsonElement args, string name, bool allowNull)
	{
		if (!HasProperty(args, name)) return null;
		JsonElement value = args.GetProperty(name);
		if (value.ValueKind == JsonValueKind.Null && allowNull) return "";
		if (value.ValueKind != JsonValueKind.String) throw new InvalidOperationException($"参数 {name} 无效");
		return value.GetString() ?? "";
	}

	private static bool? OptionalReminderBool(JsonElement args, string name)
	{
		if (!HasProperty(args, name)) return null;
		JsonElement value = args.GetProperty(name);
		if (value.ValueKind is JsonValueKind.True or JsonValueKind.False) return value.GetBoolean();
		throw new InvalidOperationException($"参数 {name} 必须是布尔值");
	}

	private static double? ReadReminderNumber(JsonElement args, string name, bool allowMissing = false)
	{
		if (!HasProperty(args, name))
		{
			if (allowMissing) return null;
			throw new InvalidOperationException($"缺少参数: {name}");
		}
		JsonElement value = args.GetProperty(name);
		if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out double number) || !double.IsFinite(number))
			throw new InvalidOperationException($"参数 {name} 必须是有限数字");
		return number;
	}

	private static long ReadReminderTimestamp(JsonElement args, string name)
	{
		if (!HasProperty(args, name)) throw new InvalidOperationException($"缺少参数: {name}");
		JsonElement value = args.GetProperty(name);
		if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out long timestamp))
			throw new InvalidOperationException($"参数 {name} 必须是整数时间");
		return timestamp;
	}

	private static bool HasProperty(JsonElement args, string name) =>
		args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out _);

	private async Task<object?> TtsTestAsync(IBridgeSource source, JsonElement args)
	{
		RequireMainVoid(source);
		string text = OptionalStr(args, "text") is {Length: > 0} custom ? custom : "你好呀！我是 Nori，这是一条声音播放测试~";
		await Runtime.Voice.SpeakAsync(text);
		return null;
	}

	private async Task<object?> SttStartAsync(IBridgeSource source, CancellationToken cancellationToken)
	{
		RequireMainVoid(source);
		await Runtime.Voice.StartListeningAsync(cancellationToken);
		return null;
	}

	private async Task<object?> SttStopAsync(IBridgeSource source, CancellationToken cancellationToken)
	{
		RequireMainVoid(source);
		string text = await Runtime.Voice.StopListeningAndTranscribeAsync(cancellationToken);
		return new {text};
	}

	private async Task<object?> ModelImportLocalAsync(
		IBridgeSource source,
		JsonElement args,
		CancellationToken cancellationToken)
	{
		RequireLabel(source, WindowLabels.FirstRun, WindowLabels.Main, () => (object?)true);
		if (source is INativeModelSource) await RequireVisibleMainVoidAsync(source);
		return await ImportLocalResourceAsync(source, args, cancellationToken);
	}

	/// <summary>读取已保存的模型表情选择，优先模型键并兼容旧全局键。</summary>
	private IReadOnlyList<string> ReadSelectedExpressions(string modelId) =>
		ReadExpressionConfig($"l2d_expression_{modelId}") ?? ReadExpressionConfig("l2d_expression") ?? [];

	private string[]? ReadExpressionConfig(string key)
	{
		ConfigValue? value = _services.Config.Get(key);
		if (value is null) return null;
		try
		{
			string[]? expressions = value switch
			{
				ConfigValue.Json json => json.Value.Deserialize<string[]>(BridgeJson.Options),
				ConfigValue.Text text => JsonSerializer.Deserialize<string[]>(text.Value, BridgeJson.Options),
				_ => null,
			};
			return expressions?.Where(expression => !string.IsNullOrWhiteSpace(expression)).ToArray();
		}
		catch (JsonException)
		{
			return null;
		}
	}

	/// <summary>读取指定模型的互动配置; 损坏配置按空配置处理并记录日志。</summary>
	private PetInteractionConfig ReadInteractionConfig(string modelId)
	{
		if (_services.Config.Get(PetInteractionConfig.StorageKey(modelId)) is not ConfigValue.Json {Value: JsonNode node})
		{
			return PetInteractionConfig.Empty;
		}

		try
		{
			return PetInteractionConfig.Parse(node.ToJsonString(PetInteractionJson.Options));
		}
		catch (Exception exception)
		{
			_services.Logger.Write(LogSource.Backend, "warn", $"读取模型互动配置失败 [{modelId}]: {exception.GetType().Name}");
			return PetInteractionConfig.Empty;
		}
	}

	/// <summary>
	/// 写入指定模型的互动配置。
	/// 前端调用: invoke("model_set_interactions", {modelId, interactions})
	/// </summary>
	private async Task<object?> ModelSetInteractionsAsync(IBridgeSource source, JsonElement args)
	{
		RequireMainVoid(source);
		string modelId = RequireKnownInstalledModel(Str(args, "modelId"));
		if (!args.TryGetProperty("interactions", out JsonElement interactionsElement)
			|| interactionsElement.ValueKind != JsonValueKind.Object)
		{
			throw new InvalidOperationException("互动配置不能为空");
		}

		PetInteractionConfig config;
		try
		{
			config = PetInteractionConfig.Parse(interactionsElement.GetRawText());
		}
		catch (Exception exception) when (exception is JsonException or InvalidOperationException)
		{
			throw new InvalidOperationException($"互动配置无效: {exception.Message}", exception);
		}

		string dir = _services.Resources.ResourceDir(ResourceType.Live2D, modelId);
		Model3MetaInfo meta = Model3Meta.Read(dir);
		config.ValidateBindings(meta.Motions, meta.Expressions);
		_services.Config.Set(PetInteractionConfig.StorageKey(modelId), new ConfigValue.Json(config.ToJsonNode()));
		_services.PetRuntime?.SetInteractionConfig(modelId, config);
		Runtime.InvalidateSnapshot();
		await Task.CompletedTask;
		return null;
	}

	/// <summary>
	/// 模型显示参数写入 (缩放按模型存储, 表情列表同)
	/// </summary>
	private async Task<object?> ModelSetDisplayAsync(IBridgeSource source, JsonElement args)
	{
		RequireMainVoid(source);
		string modelId = RequireKnownInstalledModel(Str(args, "modelId"));
		if (args.TryGetProperty("scale", out JsonElement scaleElem))
		{
			ApplyDisplayKey($"l2d_scale_{modelId}", ReadFiniteNumber(scaleElem, "模型缩放", 0.1f, 4.0f));
		}
		if (args.TryGetProperty("opacity", out JsonElement opacityElem))
		{
			ApplyDisplayKey($"l2d_opacity_{modelId}", ReadFiniteNumber(opacityElem, "模型透明度", 0.0f, 1.0f));
		}
		if (args.TryGetProperty("renderScale", out JsonElement renderScaleElem))
		{
			ApplyDisplayKey($"l2d_render_scale_{modelId}", ReadFiniteNumber(renderScaleElem, "渲染倍率",
				Live2DRenderSettings.MinRenderScale, Live2DRenderSettings.MaxRenderScale));
		}
		if (args.TryGetProperty("qualityMode", out JsonElement qualityElem))
		{
			string qualityMode = qualityElem.ValueKind == JsonValueKind.String ? qualityElem.GetString() ?? "" : "";
			Live2DQualityMode mode = Live2DRenderSettings.ParseQualityMode(qualityMode)
				?? throw new InvalidOperationException("质量模式只能是 adaptive、quality 或 eco");
			ApplyDisplayKey($"l2d_quality_mode_{modelId}", Live2DRenderSettings.QualityModeToStorage(mode));
		}
		if (args.TryGetProperty("maxFps", out JsonElement maxFpsElem))
		{
			ApplyDisplayKey($"l2d_max_fps_{modelId}", ReadInteger(maxFpsElem, "最大帧率", 0, Live2DRenderSettings.MaxExplicitFps));
		}
		if (args.TryGetProperty("shadow", out JsonElement shadowElem))
		{
			if (shadowElem.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
				throw new InvalidOperationException("阴影开关必须是布尔值");
			ApplyDisplayKey($"l2d_shadow_{modelId}", shadowElem.GetBoolean() ? "1" : "0");
		}
		if (args.TryGetProperty("expressions", out JsonElement expElem))
		{
			if (expElem.ValueKind != JsonValueKind.Array || expElem.GetArrayLength() > 64)
				throw new InvalidOperationException("表情列表格式无效或数量超过上限");
			HashSet<string> available = Model3Meta.Read(_services.Resources.ResourceDir(ResourceType.Live2D, modelId))
				.Expressions.ToHashSet(StringComparer.OrdinalIgnoreCase);
			foreach (JsonElement expression in expElem.EnumerateArray())
			{
				string name = expression.ValueKind == JsonValueKind.String ? expression.GetString() ?? "" : "";
				if (name.Length == 0 || name.Length > 128 || !available.Contains(name))
					throw new InvalidOperationException($"模型不包含表情: {name}");
			}
			ApplyDisplayKey($"l2d_expression_{modelId}", expElem.GetRawText());
		}
		await Task.CompletedTask;
		Runtime.InvalidateSnapshot();
		return null;
	}

	private static string ReadFiniteNumber(JsonElement element, string label, float min, float max)
	{
		if (element.ValueKind != JsonValueKind.Number || !element.TryGetSingle(out float value)
			|| !float.IsFinite(value) || value < min || value > max)
		{
			throw new InvalidOperationException($"{label}必须在 {min} 到 {max} 之间");
		}
		return value.ToString(System.Globalization.CultureInfo.InvariantCulture);
	}

	private static string ReadInteger(JsonElement element, string label, int min, int max)
	{
		if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt32(out int value)
			|| value < min || value > max)
		{
			throw new InvalidOperationException($"{label}必须是 {min} 到 {max} 之间的整数");
		}
		return value.ToString(System.Globalization.CultureInfo.InvariantCulture);
	}

	private void ApplyDisplayKey(string key, string storage)
	{
		UpdateConfigDirect(key, storage);
		ApplyPetConfig(key, storage);
	}

	/// <summary>行为开关批量写入并热应用</summary>
	private async Task<object?> ModelSetBehaviorAsync(IBridgeSource source, JsonElement args)
	{
		RequireMainVoid(source);
		SetBehaviorKey(args, "clickInteraction", "l2d_click_interaction");
		SetBehaviorKey(args, "clickThrough", "l2d_click_through");
		SetBehaviorKey(args, "autoBlink", "l2d_auto_blink");
		SetBehaviorKey(args, "eyeTracking", "l2d_eye_tracking");
		SetBehaviorKey(args, "idleEyeAnimation", "l2d_idle_eye_animation");
		SetBehaviorKey(args, "idleAnimation", "l2d_idle_animation");
		SetBehaviorKey(args, "expressionEnabled", "l2d_expression_enabled");
		SetBehaviorKey(args, "lipSync", "l2d_lip_sync");
		SetBehaviorKey(args, "beatSync", "l2d_beat_sync");
		SetBehaviorKey(args, "aiInteraction", PetInteractionConfig.AiEnabledKey);
		await Task.CompletedTask;
		Runtime.InvalidateSnapshot();
		return null;
	}

	// ===================================================================
	// 授权辅助
	// ===================================================================

	/// <summary>
	/// 校验来源窗口后执行工厂函数
	/// </summary>
	private static object? RequireLabel(IBridgeSource source, string allowed, Func<object?> factory)
	{
		if (!IsLabelAllowed(source, allowed))
		{
			throw new InvalidOperationException($"命令只能由 {allowed} 窗口调用");
		}
		return factory();
	}

	private static object? RequireLabel(IBridgeSource source, string allowedA, string allowedB, Func<object?> factory)
	{
		if (!IsLabelAllowed(source, allowedA) && !IsLabelAllowed(source, allowedB))
		{
			throw new InvalidOperationException($"命令只能由 {allowedA}/{allowedB} 窗口调用");
		}
		return factory();
	}

	private static object? RequireMain(IBridgeSource source, Func<object?> factory) => RequireLabel(source, WindowLabels.Main, factory);

	private static bool IsLabelAllowed(IBridgeSource source, string allowed) =>
		source is INativeSettingsSource
		|| ((source is INativeMemorySource or INativeModelSource or INativeChatSource) && allowed == WindowLabels.Main)
		|| source.Label == allowed;

	private static async Task<object?> RequireMainAsync(IBridgeSource source, Func<Task<object?>> factory)
	{
		RequireLabel(source, WindowLabels.Main, () => (object?)null);
		return await factory();
	}

	/// <summary>校验已知模型 ID 且确认本地模型资源已安装。</summary>
	private string RequireKnownInstalledModel(string value)
	{
		string modelId = SupportedModelIds.Normalize(value)
			?? throw new InvalidOperationException("只支持 arg-nori 或 nori 模型");
		if (!IsKnownInstalledModel(modelId)) throw new InvalidOperationException($"模型尚未安装: {modelId}");
		return modelId;
	}

	private bool IsKnownInstalledModel(string modelId)
	{
		try
		{
			return SupportedModelIds.Normalize(modelId) is not null
				&& _services.Resources.IsInstalled(ResourceType.Live2D, modelId);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ResourceException)
		{
			_services.Logger.Write(LogSource.Backend, "warn", $"检查模型资源失败 [{modelId}]: {exception.GetType().Name}");
			return false;
		}
	}

	// ===================================================================
	// 配置写入 (内部直写, 不再暴露给前端通用入口)
	// ===================================================================

	/// <summary>
	/// 文件工具的工作目录与工具轮数上限。
	///
	/// 目录在此处即时校验是否存在。写入不存在的路径不会报错，但该组工具会静默不注册，表现为
	/// 配置项有值而工具不可用，排查成本高。空串为合法取值，表示禁用该功能。
	/// </summary>
	/// <summary>
	/// 开关一条情绪表达通道。
	///
	/// 通道键由前端给出而不是在此处枚举：通道清单在 AppRuntime，两处各维护一份必然漂。
	/// 键必须带 expression_ 前缀，挡住拿这条命令去改任意配置项。
	/// </summary>
	private void UpdateExpressionChannel(JsonElement args)
	{
		if (args.ValueKind != JsonValueKind.Object
			|| !args.TryGetProperty("channel", out JsonElement channel)
			|| channel.GetString() is not {Length: > 0} key
			|| !key.StartsWith(ConfigStore.KeyExpressionPrefix, StringComparison.Ordinal))
		{
			throw new InvalidOperationException("channel 必须是情绪表达通道的键");
		}

		UpdateBoolConfig(args, "enabled", key);

		// 关掉会改桌面设置的通道时立刻还原，不等退出 —— 用户关它多半就是想让桌面变回去。
		if (!_services.Config.GetBoolOr(key, false)) Runtime.RestoreDesktopState();
		Runtime.InvalidateSnapshot();
	}

	private void UpdateTaskSettings(JsonElement args)
	{
		if (args.ValueKind != JsonValueKind.Object
			|| !args.TryGetProperty("tasks", out JsonElement list)
			|| list.ValueKind != JsonValueKind.Array)
		{
			throw new InvalidOperationException("tasks 必须是数组");
		}

		List<WorkspaceTask> parsed = [];
		foreach (JsonElement entry in list.EnumerateArray())
		{
			if (entry.ValueKind != JsonValueKind.Object) continue;
			parsed.Add(new WorkspaceTask
			{
				Name = entry.TryGetProperty("name", out JsonElement name) ? name.GetString() ?? "" : "",
				Command = entry.TryGetProperty("command", out JsonElement command) ? command.GetString() ?? "" : "",
			});
		}

		// 改配置之前先记下当前的授权面，改完才能算出哪些已经不再需要。
		IReadOnlyList<string> granted = Runtime.CurrentGrantPaths();

		// 严格校验后再落库：非法条目在读取侧是被跳过的，静默丢一条比当场报错难查得多。
		_services.Config.Set(ConfigStore.KeyWorkspaceTasks, WorkspaceTaskList.Write(WorkspaceTaskList.Validate(parsed)));

		Runtime.RebuildTools();
		Runtime.ReleaseStaleGrants(granted);
		Runtime.InvalidateSnapshot();
	}

	/// <summary>
	/// 整份替换 runTask 的任务清单。
	///
	/// 增量更新需要稳定标识，而任务只有名字这一个键，改名就无法与新增区分。整份替换让
	/// 界面持有唯一真相，代价是并发编辑会互相覆盖 —— 设置窗口是单实例，不存在这种情形。
	/// </summary>
	/// <summary>
	/// 换授权档位。
	///
	/// 选到完全放行时**在这里**按下到期时刻，而不是读取时再算 —— 到期时刻要跟着
	/// 「哪一次点的」走。读取时才算的话，改一次别的设置就等于又续了四小时。
	/// </summary>
	private void UpdatePermissionGear(JsonElement args)
	{
		if (args.ValueKind != JsonValueKind.Object
			|| !args.TryGetProperty("gear", out JsonElement value)
			|| value.ValueKind != JsonValueKind.String)
		{
			throw new InvalidOperationException("缺少 gear");
		}

		string raw = (value.GetString() ?? "").Trim();
		PermissionGear gear = ToolPermissionPolicy.Parse(raw);
		// Parse 认不出的值会退到最严的一档，那对配置读取是对的，但在这里会把一次
		// 拼错的调用变成一次静默的"改成逐次确认"。写入侧要严格。
		if (!string.Equals(ToolPermissionPolicy.Format(gear), raw, StringComparison.Ordinal))
		{
			throw new InvalidOperationException($"不认识的档位: {raw}");
		}

		_services.Config.Set(ToolPermissionPolicy.KeyGear, new ConfigValue.Text(ToolPermissionPolicy.Format(gear)));
		_services.Config.Set(
			ToolPermissionPolicy.KeyBypassUntil,
			new ConfigValue.Text(gear == PermissionGear.Bypass
				? ToolPermissionPolicy.FormatDeadline(ToolPermissionPolicy.BypassDeadline(DateTimeOffset.UtcNow))
				: string.Empty));
		Runtime.InvalidateSnapshot();
	}

	private void UpdateWorkspaceSettings(JsonElement args)
	{
		// 同上：授权面要在改配置之前取。
		IReadOnlyList<string> granted = Runtime.CurrentGrantPaths();

		if (args.ValueKind == JsonValueKind.Object
			&& args.TryGetProperty("root", out JsonElement rootValue)
			&& rootValue.ValueKind == JsonValueKind.String)
		{
			string root = (rootValue.GetString() ?? "").Trim();
			if (root.Length > 0 && !Directory.Exists(root))
			{
				throw new InvalidOperationException("这个文件夹不存在，换一个吧");
			}

			_services.Config.Set(ConfigStore.KeyWorkspaceRoot, new ConfigValue.Text(root));
		}

		if (args.ValueKind == JsonValueKind.Object
			&& args.TryGetProperty("maxToolIterations", out JsonElement iterations)
			&& iterations.ValueKind == JsonValueKind.Number
			&& iterations.TryGetInt32(out int parsed))
		{
			// 存为 Integer 而非 Text：文本路径上 "0"/"1" 会被解析为布尔并渲染为 "false"/"true"。
			_services.Config.Set(
				ConfigStore.KeyAgentMaxToolIterations,
				new ConfigValue.Integer(Math.Clamp(
					parsed,
					Nori.Core.Agent.AgentEngine.MinToolIterations,
					Nori.Core.Agent.AgentEngine.MaxToolIterationsLimit)));
		}

		// 工具注册表依赖工作目录构建，变更后必须重建，否则要到下次启动才生效。
		Runtime.RebuildTools();

		// 沙箱授权是磁盘上的 ACL，换了工作目录不释放的话旧目录上的 ACE 会永久残留。
		Runtime.ReleaseStaleGrants(granted);
		Runtime.InvalidateSnapshot();
	}

	/// <summary>
	/// 选文件夹。返回选中的路径，用户取消时返回 null。
	///
	/// 仅返回选择结果，不写配置：写入仍由 `settings_update_workspace` 承担，配置只有一个写入点。
	/// </summary>
	private async Task<object?> PickWorkspaceAsync(IBridgeSource source)
	{
		RequireMainVoid(source);
		Avalonia.Controls.Window? self = source.Self ?? throw new InvalidOperationException("来源窗口不可用");
		string? picked = await _uiDispatcher.InvokeTaskAsync(async () =>
		{
			IReadOnlyList<IStorageFolder> folders = await self.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
			{
				Title = "选择可访问的文件夹",
				AllowMultiple = false,
			});
			return folders.Count > 0 ? folders[0].Path.LocalPath : null;
		});

		return picked is null ? null : new { root = picked };
	}

	private void UpdateConfigDirect(string key, string value) =>
		_services.Config.Set(key, new ConfigValue.Text(value));

	/// <summary>更新聊天与 Embedding 配置。</summary>
	private void UpdateUnifiedAiSettings(JsonElement args)
	{
		JsonElement chat = args;
		bool hasNestedChat = TryGetObject(args, "chat", out JsonElement nestedChat);
		if (hasNestedChat) chat = nestedChat;
		string? persona = OptionalStr(args, "persona") ?? OptionalStr(chat, "persona");
		bool hasChatPatch = hasNestedChat
			|| HasAnyString(args, "provider", "baseUrl", "apiKey", "model", "persona")
			|| persona is not null;
		if (hasChatPatch)
		{
			_services.AiSettings.UpdateChat(new AiChatSettingsPatch(
				Provider: OptionalStr(chat, "provider"),
				BaseUrl: OptionalStr(chat, "baseUrl"),
				ApiKey: OptionalStr(chat, "apiKey"),
				Model: OptionalStr(chat, "model"),
				Persona: persona,
				ApiKeySpecified: HasString(chat, "apiKey")));
		}

		JsonElement embedding = args;
		bool hasNestedEmbedding = TryGetObject(args, "embedding", out JsonElement nestedEmbedding);
		if (hasNestedEmbedding) embedding = nestedEmbedding;
		bool hasFlatEmbedding = HasAnyString(args, "embeddingBaseUrl", "embeddingApiKey", "embeddingModel", "embeddingDimensions");
		if (hasNestedEmbedding || hasFlatEmbedding)
		{
			_services.AiSettings.UpdateEmbedding(BuildEmbeddingPatch(
				hasNestedEmbedding ? embedding : args,
				hasNestedEmbedding ? null : "embedding"));
			Runtime.QueueEmbeddingRebuild();
		}
	}

	private static bool TryGetObject(JsonElement args, string name, out JsonElement value)
	{
		value = default;
		return args.ValueKind == JsonValueKind.Object
			&& args.TryGetProperty(name, out value)
			&& value.ValueKind == JsonValueKind.Object;
	}

	private static AiEmbeddingSettingsPatch BuildEmbeddingPatch(JsonElement args, string? prefix)
	{
		string Name(string name) => prefix is null ? name : prefix + char.ToUpperInvariant(name[0]) + name[1..];
		return new AiEmbeddingSettingsPatch(
			BaseUrl: OptionalStr(args, prefix is null ? "baseUrl" : Name("baseUrl")),
			ApiKey: OptionalStr(args, prefix is null ? "apiKey" : Name("apiKey")),
			Model: OptionalStr(args, prefix is null ? "model" : Name("model")),
			Dimensions: OptionalStr(args, prefix is null ? "dimensions" : Name("dimensions")),
			ApiKeySpecified: HasString(args, prefix is null ? "apiKey" : Name("apiKey")));
	}

	private static bool HasString(JsonElement args, string name) =>
		args.ValueKind == JsonValueKind.Object
		&& args.TryGetProperty(name, out JsonElement value)
		&& value.ValueKind == JsonValueKind.String;

	private static bool HasAnyString(JsonElement args, params string[] names) => names.Any(name => HasString(args, name));

	/// <summary>可选字段更新: 参数缺失时不动配置</summary>
	private void UpdateOptionalConfig(JsonElement args, string argName, string configKey)
	{
		if (args.ValueKind == JsonValueKind.Object
			&& args.TryGetProperty(argName, out JsonElement value)
			&& value.ValueKind == JsonValueKind.String
			&& value.GetString() is { } text)
		{
			UpdateConfigDirect(configKey, text);
		}
	}

	/// <summary>
	/// 秘密字段更新: 缺省不变; 显式空串表示清除; 非空则写入 (DPAPI 由 ConfigStore 自动加密)
	/// </summary>
	private void UpdateSecretConfig(JsonElement args, string argName, string configKey)
	{
		if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(argName, out JsonElement value)) return;
		if (value.ValueKind != JsonValueKind.String) return;
		string? text = value.GetString();
		if (text is {Length: > 0})
		{
			UpdateConfigDirect(configKey, text);
			_services.Logger.Write(LogSource.Backend, "info", $"已更新敏感配置: {configKey}");
		}
		else
		{
			_services.Config.Delete(configKey);
		}
	}

	private void UpdateBoolConfig(JsonElement args, string argName, string configKey)
	{
		if (args.ValueKind == JsonValueKind.Object
			&& args.TryGetProperty(argName, out JsonElement value)
			&& value.ValueKind is JsonValueKind.True or JsonValueKind.False)
		{
			UpdateConfigDirect(configKey, value.GetBoolean() ? "1" : "0");
		}
	}

	/// <summary>保存遥测三态同意; 首次运行的默认开启只在完成向导时确认。</summary>
	private void UpdateTelemetryConsent(IBridgeSource source, JsonElement args)
	{
		bool? enabled = OptionalBool(args, "telemetryEnabled");
		if (enabled is null) return;

		if (source.Label == WindowLabels.FirstRun)
		{
			_services.Config.SetTelemetryConsent(enabled.Value ? TelemetryConsent.Unset : TelemetryConsent.Denied);
		}
		else
		{
			_services.Config.SetTelemetryConsent(enabled.Value ? TelemetryConsent.Granted : TelemetryConsent.Denied);
		}
	}

	private void UpdateNumberConfig(JsonElement args, string argName, string configKey)
	{
		if (args.ValueKind == JsonValueKind.Object
			&& args.TryGetProperty(argName, out JsonElement value)
			&& value.ValueKind == JsonValueKind.Number)
		{
			UpdateConfigDirect(configKey, value.GetRawText());
		}
	}

	/// <summary>行为开关写入并热应用到伴侣视窗。</summary>
	private void SetBehaviorKey(JsonElement args, string argName, string configKey)
	{
		if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(argName, out JsonElement value)) return;
		if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
			throw new InvalidOperationException($"{argName} 必须是布尔值");
		string storage = value.GetBoolean() ? "1" : "0";
		UpdateConfigDirect(configKey, storage);
		ApplyPetConfig(configKey, storage);
	}

	private void ApplyPetConfig(string key, string storage)
	{
		_services.PetRuntime?.ApplyConfig(key, storage);
		if (key == "l2d_click_through") _uiDispatcher.Post(() => _services.Windows.Pet?.ReapplyInputState());
	}

	/// <summary>持久化工具禁用清单</summary>
	private void PersistDisabledTools()
	{
		IReadOnlyList<string> disabled = Runtime.Tools.DisabledNames();
		string json = JsonSerializer.Serialize(disabled);
		JsonNode? node = JsonNode.Parse(json);
		if (node is not null)
		{
			_services.Config.Set("tools_disabled", new ConfigValue.Json(node));
		}
	}

	// ===================================================================
	// 聊天历史
	// ===================================================================

	/// <summary>
	/// 分页读取聊天历史 (服务端规范化旧协议 JSON, 前端不再解析业务内容)
	/// </summary>
	private object GetHistoryPage(int limit, long beforeId) => _services.Chat
		.GetHistory(limit, beforeId, excludeToolFeedback: true)
		.Select(row => new
		{
			id = row.Id,
			role = row.Role,
			content = row.Role == "assistant" ? AgentHistory.ExtractDisplayText(row.Content) : row.Content,
			createdAt = row.CreatedAt,
		})
		.ToArray();

	// ===================================================================
	// LLM / MCP / 技能辅助
	// ===================================================================

	private async Task<object?> FetchModelsWithSourceCheckAsync(IBridgeSource source, JsonElement args)
	{
		RequireLabel(source, WindowLabels.FirstRun, WindowLabels.Main, () => (object?)true);
		AiChatSettings chat = _services.AiSettings.Read().Chat;
		string apiKey = OptionalStr(args, "apiKey") ?? chat.ApiKey;
		return await _services.Llm.FetchModelsAsync(
			OptionalStr(args, "provider") ?? chat.Provider.AsString(), Str(args, "baseUrl"), apiKey);
	}

	private async Task<object?> TestAiConnectionAsync(IBridgeSource source, JsonElement args, CancellationToken cancellationToken)
	{
		string target = OptionalStr(args, "target") ?? "chat";
		if (string.Equals(target, "embedding", StringComparison.OrdinalIgnoreCase))
		{
			return await TestEmbeddingConnectionAsync(source, args, cancellationToken);
		}
		return await TestLlmConnectionAsync(source, args, cancellationToken);
	}

	private async Task<ProviderConnectionTestResult> TestLlmConnectionAsync(IBridgeSource source, JsonElement args, CancellationToken cancellationToken)
	{
		RequireMainVoid(source);
		AiChatSettings chat = _services.AiSettings.Read().Chat;
		string provider = OptionalStr(args, "provider") ?? chat.Provider.AsString();
		string baseUrl = OptionalStr(args, "baseUrl") ?? chat.BaseUrl;
		string apiKey = OptionalStr(args, "apiKey") ?? chat.ApiKey;
		string model = OptionalStr(args, "model") ?? chat.Model;
		ProviderConnectionTester tester = new(_services.Http, _services.Embedding);
		return await tester.TestLlmAsync(provider, baseUrl, apiKey, model, cancellationToken);
	}

	private async Task<ProviderConnectionTestResult> TestEmbeddingConnectionAsync(IBridgeSource source, JsonElement args, CancellationToken cancellationToken)
	{
		RequireMainVoid(source);
		AiEmbeddingSettings embedding = _services.AiSettings.Read().Embedding;
		string baseUrl = OptionalStr(args, "baseUrl") ?? embedding.BaseUrl;
		string apiKey = OptionalStr(args, "apiKey") ?? embedding.ApiKey;
		string model = OptionalStr(args, "model") ?? embedding.Model;
		int? dimensions = OptionalInt(args, "dimensions") ?? embedding.Dimensions;
		if (dimensions is null && int.TryParse(OptionalStr(args, "dimensions"), out int supplied))
			dimensions = supplied > 0 ? supplied : null;
		ProviderConnectionTester tester = new(_services.Http, _services.Embedding);
		return await tester.TestEmbeddingAsync(baseUrl, apiKey, model, dimensions, cancellationToken);
	}

	private object SkillServiceMarketplace() => Nori.Core.Skills.SkillPresets.All.Select(skill => new
	{
		id = skill.Id,
		name = skill.Name,
		description = skill.Description,
		author = skill.Author,
		version = skill.Version,
		icon = skill.Icon,
		tags = skill.Tags,
		category = skill.Category,
		instructions = skill.Instructions,
		tools = skill.Tools,
		source = skill.Source,
	}).ToArray();

	/// <summary>构建不含技能指令正文和远程地址的脱敏 DTO。</summary>
	private static object RedactedSkillDto(SkillRecord skill) => new
	{
		id = skill.Id,
		name = skill.Name,
		description = skill.Description,
		author = skill.Author,
		version = skill.Version,
		icon = skill.Icon,
		tags = skill.Tags.ToArray(),
		category = skill.Category,
		instructions = "",
		enabled = skill.Enabled,
		source = skill.Source,
	};

	private bool SkillsToggle(string id, bool enabled) => Runtime.Skills.Toggle(id, enabled);

	private async Task<object?> McpImportUrlAsync(
		IBridgeSource source,
		JsonElement args,
		CancellationToken cancellationToken)
	{
		RequireMainVoid(source);
		string url = Str(args, "url");
		Nori.Core.Network.UrlAccessPolicy.EnsurePublicHttp(new Uri(url));
		using HttpResponseMessage response = await Nori.Core.Network.UrlAccessPolicy.GetWithSafeRedirectsAsync(
			_services.PublicHttp, new Uri(url), allowPrivate: false, cancellationToken: cancellationToken);
		if (!response.IsSuccessStatusCode)
		{
			throw new HttpRequestException($"HTTP {(int)response.StatusCode}: {response.ReasonPhrase}");
		}
		string text = await Nori.Core.Network.UrlAccessPolicy.ReadCappedTextAsync(
			response.Content, Nori.Core.Network.UrlAccessPolicy.MaxResponseBytes, cancellationToken);

		List<McpServerConfig> imported = [];
		try
		{
			using JsonDocument document = JsonDocument.Parse(text);
			JsonElement root = document.RootElement.Clone();

			if (root.TryGetProperty("mcpServers", out JsonElement serversElem) && serversElem.ValueKind == JsonValueKind.Object)
			{
				foreach (JsonProperty server in serversElem.EnumerateObject())
				{
					imported.Add(BuildImportedConfig(server.Name, server.Value));
				}
			}
			else if (root.ValueKind == JsonValueKind.Array)
			{
				foreach (JsonElement item in root.EnumerateArray())
				{
					imported.Add(BuildImportedConfig(OptionalGetString(item, "name") ?? "导入的 MCP 服务", item));
				}
			}
			else
			{
				imported.Add(BuildImportedConfig(
					OptionalGetString(root, "name") ?? OptionalGetString(root, "id") ?? "导入的 MCP 服务", root));
			}
		}
		catch (JsonException exception)
		{
			throw new InvalidOperationException($"未识别的 MCP 配置文件结构: {exception.Message}");
		}

		List<McpServerStatusInfo> results = [];
		foreach (McpServerConfig config in imported)
		{
			cancellationToken.ThrowIfCancellationRequested();
			results.Add(await _services.Mcp.SaveServerAsync(config));
		}

		InvalidateMcpSnapshot();
		return results;
	}

	private static string? OptionalGetString(JsonElement element, string name) =>
		element.ValueKind == JsonValueKind.Object
			&& element.TryGetProperty(name, out JsonElement value)
			&& value.ValueKind == JsonValueKind.String
				? value.GetString()
				: null;

	private static McpServerConfig BuildImportedConfig(string name, JsonElement source)
	{
		static string[] ReadArgs(JsonElement element) =>
			element.ValueKind == JsonValueKind.Object
				&& element.TryGetProperty("args", out JsonElement argsElem)
				&& argsElem.ValueKind == JsonValueKind.Array
					? argsElem.EnumerateArray().OfType<JsonElement>().Where(a => a.ValueKind == JsonValueKind.String).Select(a => a.GetString()!).ToArray()
					: [];

		return new McpServerConfig
		{
			Id = $"mcp_import_{Guid.NewGuid().ToString("N")[..8]}",
			Name = name,
			Transport = OptionalGetString(source, "url") is not null ? McpTransportType.Sse : McpTransportType.Stdio,
			Command = OptionalGetString(source, "command") ?? "npx",
			Args = ReadArgs(source),
			Url = OptionalGetString(source, "url"),
			Enabled = false,
			AutoConnect = false,
		};
	}

	private async Task<object?> McpSaveServerAsync(IBridgeSource source, JsonElement args)
	{
		RequireMainVoid(source);
		object result = await _services.Mcp.SaveServerAsync(ParseMcpConfig(args));
		await Runtime.RefreshMcpToolsAsync();
		InvalidateMcpSnapshot();
		return result;
	}

	private async Task<object?> McpDeleteServerAsync(IBridgeSource source, JsonElement args)
	{
		RequireMainVoid(source);
		bool deleted = await _services.Mcp.DeleteServerAsync(Str(args, "id"));
		await Runtime.RefreshMcpToolsAsync();
		InvalidateMcpSnapshot();
		return deleted;
	}

	private async Task<object?> McpConnectServerAsync(IBridgeSource source, JsonElement args)
	{
		RequireMainVoid(source);
		object result = await _services.Mcp.ConnectServerAsync(Str(args, "id"));
		await Runtime.RefreshMcpToolsAsync();
		InvalidateMcpSnapshot();
		return result;
	}

	private async Task<object?> McpDisconnectServerAsync(IBridgeSource source, JsonElement args)
	{
		RequireMainVoid(source);
		object result = await _services.Mcp.DisconnectServerAsync(Str(args, "id"));
		await Runtime.RefreshMcpToolsAsync();
		InvalidateMcpSnapshot();
		return result;
	}

	private async Task<object?> McpTestServerAsync(IBridgeSource source, JsonElement args)
	{
		RequireMainVoid(source);
		return await _services.Mcp.TestServerAsync(ParseMcpConfig(args));
	}

	private async Task<object?> McpCallToolAsync(
		IBridgeSource source,
		JsonElement args,
		CancellationToken cancellationToken)
	{
		RequireMainVoid(source);
		return await CallMcpToolCoreAsync(source, args, cancellationToken);
	}
	private void InvalidateMcpSnapshot() => Runtime.InvalidateSnapshot();

	// ===================================================================
	// 伴侣状态
	// ===================================================================

	/// <summary>
	/// 选择 IndexTTS 音频模板文件并写入配置 (仅保存路径, 不克隆)。
	/// </summary>
	private async Task<object?> IndexTtsPickTemplateAsync(
		IBridgeSource source,
		JsonElement args,
		CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		Avalonia.Controls.Window? self = source.Self ?? throw new InvalidOperationException("来源窗口不可用");
		string? filePath = await _uiDispatcher.InvokeTaskAsync(async () =>
		{
			IReadOnlyList<IStorageFile> files = await self.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
			{
				Title = "选择 IndexTTS 音频模板 (wav/mp3, 5-30 秒)",
				AllowMultiple = false,
				FileTypeFilter =
				[
					new FilePickerFileType("音频模板 (wav/mp3)") {Patterns = ["*.wav", "*.mp3"]},
				],
			});
			return files.Count > 0 ? files[0].Path.LocalPath : null;
		});
		if (string.IsNullOrWhiteSpace(filePath)) return null;
		_services.Config.Set("indextts_template_audio", new Nori.Core.Configuration.ConfigValue.Text(filePath));
		Runtime.InvalidateSnapshot();
		return filePath!;
	}

	/// <summary>
	/// 上传 IndexTTS 音频模板克隆音色，返回克隆后的 voice_id。
	/// 未传 filePath 时使用配置中的模板路径。
	/// </summary>
	private async Task<object?> IndexTtsCloneVoiceAsync(
		IBridgeSource source,
		JsonElement args,
		CancellationToken cancellationToken)
	{
		RequireMainVoid(source);
		string templatePath = OptionalStr(args, "filePath") ?? "";
		if (string.IsNullOrWhiteSpace(templatePath))
		{
			templatePath = _services.Config.GetStringOr("indextts_template_audio", "");
		}
		if (string.IsNullOrWhiteSpace(templatePath)) throw new InvalidOperationException("请先选择 IndexTTS 音频模板文件");

		Nori.Core.Voice.IndexTtsProvider provider =
			new(_services.Http, _services.Config, _services.Paths);
		string voiceId = await provider.CloneVoiceAsync(templatePath, cancellationToken);
		Runtime.InvalidateSnapshot();
		return new {voiceId};
	}

	/// <summary>
	/// 从本地 ZIP 文件或目录导入资源
	/// </summary>
	private async Task<object?> ImportLocalResourceAsync(
		IBridgeSource source,
		JsonElement args,
		CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		string sourceKind = (OptionalStr(args, "sourceKind") ?? "zip").Trim().ToLowerInvariant();
		if (sourceKind is not ("zip" or "folder")) throw new InvalidOperationException("导入来源只能是 zip 或 folder");

		string? filePath = OptionalStr(args, "filePath");
		if (string.IsNullOrWhiteSpace(filePath))
		{
			Avalonia.Controls.Window? self = source.Self ?? throw new InvalidOperationException("来源窗口不可用");
			filePath = await _uiDispatcher.InvokeTaskAsync(async () =>
			{
				if (sourceKind == "folder")
				{
					IReadOnlyList<IStorageFolder> folders = await self.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
					{
						Title = "选择 Live2D 模型文件夹",
						AllowMultiple = false,
					});
					return folders.Count > 0 ? folders[0].Path.LocalPath : null;
				}

				IReadOnlyList<IStorageFile> files = await self.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
				{
					Title = "选择 Live2D 资源文件 (.zip)",
					AllowMultiple = false,
					FileTypeFilter =
					[
						new FilePickerFileType("Live2D 压缩包 (*.zip)") {Patterns = ["*.zip"]},
					],
				});
				return files.Count > 0 ? files[0].Path.LocalPath : null;
			});
		}

		if (string.IsNullOrWhiteSpace(filePath)) return null;
		if (sourceKind == "folder" && !Directory.Exists(filePath)) throw new InvalidOperationException("选择的 Live2D 文件夹不存在");
		if (sourceKind == "zip" && (!File.Exists(filePath) || !filePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)))
			throw new InvalidOperationException("请选择有效的 Live2D ZIP 文件");

		const ResourceType type = ResourceType.Live2D;
		IReadOnlyList<string> imported = await Task.Run(
			() => _services.Resources.Import(type, filePath, cancellationToken),
			cancellationToken);
		_services.Logger.Write(LogSource.Backend, "info", $"成功导入本地 Live2D 资源: {string.Join(", ", imported)}");
		Runtime.InvalidateSnapshot();
		return imported;
	}

	private async Task<object?> CallMcpToolCoreAsync(
		IBridgeSource source,
		JsonElement args,
		CancellationToken cancellationToken)
	{
		string serverId = Str(args, "serverId");
		string toolName = Str(args, "toolName");
		JsonObject? toolArgs = null;
		if (args.TryGetProperty("arguments", out JsonElement argElem) && argElem.ValueKind == JsonValueKind.Object)
		{
			toolArgs = JsonNode.Parse(argElem.GetRawText()) as JsonObject;
		}

		// 带 sessionId 的 MCP 调用可被宿主取消注册表取消
		string? sessionId = OptionalStr(args, "sessionId");
		McpToolResult result;
		if (string.IsNullOrEmpty(sessionId))
		{
			result = await _services.Mcp.CallToolAsync(serverId, toolName, toolArgs, cancellationToken);
		}
		else
		{
			CancellationTokenSource registered = _services.AgentOperations.Register(source.Label, sessionId, cancellationToken);
			try
			{
				result = await _services.Mcp.CallToolAsync(serverId, toolName, toolArgs, registered.Token);
			}
			finally
			{
				_services.AgentOperations.Complete(source.Label, sessionId, registered);
			}
		}

		if (result.IsError) throw new InvalidOperationException(result.AsText());
		return result;
	}

	// ===================================================================
	// 调试 / 外壳
	// ===================================================================

	/// <summary>在资源管理器中打开日志目录 (只开放固定目录)</summary>
	private void OpenLogFolder()
	{
		Directory.CreateDirectory(_services.Paths.LogsDirectory);
		ShellOpen.OpenDataDirectory(_services.Paths.LogsDirectory, _services.Paths.DataRoot);
	}

	/// <summary>弹出保存位置并在后台生成脱敏诊断 ZIP。</summary>
	private async Task<object?> ExportDiagnosticsAsync(IBridgeSource source, CancellationToken cancellationToken)
	{
		RequireMainVoid(source);
		Window self = source.Self ?? throw new InvalidOperationException("来源窗口不可用");
		string? targetPath = await _uiDispatcher.InvokeTaskAsync(async () =>
		{
			IStorageFile? file = await self.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
			{
				Title = "导出 Nori 诊断信息",
				SuggestedFileName = $"nori-diagnostics-{DateTime.UtcNow:yyyyMMdd-HHmmss}.zip",
				ShowOverwritePrompt = true,
				FileTypeChoices =
				[
					new FilePickerFileType("诊断压缩包 (*.zip)") {Patterns = ["*.zip"]},
				],
			});
			return file?.Path.LocalPath;
		});

		if (string.IsNullOrWhiteSpace(targetPath)) return null;
		DiagnosticExporter.Result result = await Task.Run(
			() => DiagnosticExporter.Export(targetPath, _services.Logger, _services.PetRuntime, _services.Paths, _services.SafeMode, cancellationToken, _services.AgentTrace),
			cancellationToken);
		return new {fileName = result.FileName, bytes = result.Bytes, skipped = result.Skipped};
	}

	private object? RunGcCollect()
	{
		long before = GC.GetTotalMemory(false);
		GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, true);
		long after = GC.GetTotalMemory(true);
		long released = Math.Max(0, before - after);
		_services.Logger.Write(LogSource.Backend, "info", $"调试垃圾回收完成: 释放 {released} 字节");
		return new {released_bytes = released};
	}

	private object? DebugCrashTest(string mode)
	{
		if (SentryTelemetry.IsProductionBuild)
			throw new InvalidOperationException("生产环境不支持调试崩溃测试");

		switch (mode)
		{
			case "ui_thread":
				_uiDispatcher.Post(() => throw new InvalidOperationException("调试崩溃测试: UI 线程未处理异常"));
				break;
			case "background_thread":
				new Thread(() => throw new InvalidOperationException("调试崩溃测试: 后台线程未处理异常")).Start();
				break;
			case "unobserved_task":
				_ = Task.Run(() => throw new InvalidOperationException("调试崩溃测试: 未观察任务异常"));
				break;
			default:
				throw new InvalidOperationException($"未知的崩溃测试模式: {mode}");
		}
		return null;
	}

	/// <summary>读取日志健康状态。前端调用：invoke("get_logging_status")</summary>
	private object GetLoggingStatus() => _services.Logger.GetStatus();

	/// <summary>临时调整日志级别。前端调用：invoke("set_logging_level", {level: "debug"})</summary>
	private object? SetLoggingLevel(JsonElement args)
	{
		string level = Str(args, "level");
		if (!FileLogger.IsLevel(level)) throw new InvalidOperationException("日志级别无效");
		_services.Logger.SetMinimumLevel(level);
		return null;
	}

	/// <summary>记录白名单事件，窗口身份由宿主赋值。前端调用：invoke("write_log", {level: "error", eventId: "audio.error", message: ""})</summary>
	private object? WriteFrontendLog(IBridgeSource source, JsonElement args)
	{
		string level = Str(args, "level").Trim().ToLowerInvariant();
		if (!FileLogger.IsLevel(level))
			throw new InvalidOperationException("日志级别无效");
		string eventId = OptionalStr(args, "eventId") ?? "";
		bool audioHost = source.Label == WindowLabels.AudioHost;
		if (audioHost
			? eventId is not ("audio.error" or "logging.suppressed")
			: eventId is not ("audio.error" or "logging.suppressed" or "diagnostics.test"))
			throw new InvalidOperationException("日志事件无效");
		string message = eventId switch
		{
			"logging.suppressed" => $"重复前端事件已被限流：{Math.Clamp(OptionalInt(args, "suppressedCount") ?? 1, 1, 1_000_000)}",
			"diagnostics.test" => "调试日志链路正常",
			_ => "前端音频操作失败",
		};
		string errorType = OptionalStr(args, "errorType") ?? "";
		if (errorType is "Error" or "TypeError" or "RangeError" or "ReferenceError" or "SyntaxError" or "URIError" or "EvalError" or "AggregateError")
			message += $"：{errorType}";
		bool native = source.Label == "settings";
		_services.Logger.Write(native ? LogSource.Backend : LogSource.Frontend, level, message,
			native ? "NativeSettings" : "Frontend", eventId, windowLabel: source.Label);
		return null;
	}

	private async Task<object?> WriteClipboardAsync(IBridgeSource source, string text)
	{
		await OnUiAsync(async () =>
		{
			Avalonia.Input.Platform.IClipboard clipboard = TopLevel.GetTopLevel(source.Self)?.Clipboard
				?? throw new InvalidOperationException("剪贴板不可用");
			await clipboard.SetTextAsync(text);
		});
		return null;
	}

	// ===================================================================
	// 通用辅助
	// ===================================================================

	private static string Str(JsonElement args, string name) =>
		args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
			? value.GetString() ?? ""
			: throw new InvalidOperationException($"缺少参数: {name}");

	private static Guid ParseGuid(JsonElement args, string name)
	{
		string value = Str(args, name);
		return Guid.TryParse(value, out Guid result) && result != Guid.Empty
			? result
			: throw new InvalidOperationException($"参数 {name} 无效");
	}

	private static bool RequiredBool(JsonElement args, string name) =>
		args.ValueKind == JsonValueKind.Object
			&& args.TryGetProperty(name, out JsonElement value)
		&& value.ValueKind is JsonValueKind.True or JsonValueKind.False
			? value.GetBoolean()
			: throw new InvalidOperationException($"缺少参数: {name}");

	private static bool? OptionalBool(JsonElement args, string name)
	{
		if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out JsonElement value))
		{
			return value.ValueKind switch
			{
				JsonValueKind.True => true,
				JsonValueKind.False => false,
				_ => null,
			};
		}
		return null;
	}

	private static string? OptionalStr(JsonElement args, string name) =>
		args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
			? value.GetString()
			: null;

	private static bool HasTtsConfigurationChange(JsonElement args)
	{
		if (args.ValueKind != JsonValueKind.Object) return false;
		string[] keys = [
			"ttsProvider", "ttsBaseUrl", "ttsApiKey", "ttsVoice", "ttsSpeed",
			"gptsovitsBaseUrl", "gptsovitsRefAudio", "gptsovitsPromptText", "gptsovitsPromptLang",
		];
		return keys.Any(key => args.TryGetProperty(key, out _));
	}

	private static double Num(JsonElement args, string name) =>
		args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number
			? value.GetDouble()
			: throw new InvalidOperationException($"缺少参数: {name}");

	private static double? OptionalDouble(JsonElement args, string name) =>
		args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number
			? value.GetDouble()
			: null;

	private static int? OptionalInt(JsonElement args, string name) =>
		args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number
			? value.GetInt32()
			: null;

	private static int ClampLimit(int? value, int fallback) => Math.Clamp(value ?? fallback, 1, 100);

	private static McpServerConfig ParseMcpConfig(JsonElement args)
	{
		McpServerConfig? config = args.Deserialize<McpServerConfig>(BridgeJson.Options);
		return config ?? throw new InvalidOperationException("无法解析 MCP 服务器配置");
	}

	private static object? Run(Func<object?> action)
	{
		action();
		return null;
	}

	private static object? Run(Action action)
	{
		action();
		return null;
	}

	private Task<T> OnUi<T>(Func<T> action) => _uiDispatcher.InvokeAsync(action);

	private Task OnUiAsync(Func<Task> action) => _uiDispatcher.InvokeTaskAsync(action);
}
