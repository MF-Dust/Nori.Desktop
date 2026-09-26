using System.Text.Json;
using Nori.Core.Agent;
using Nori.Core.Automation;
using Nori.Core.Configuration;
using Nori.Core.Logging;
using Nori.Core.Live2D;
using Nori.Core.Memory;
using Nori.Core.Resources;
using Nori.Core.Skills;
using Nori.Desktop.Automation;
using Nori.Desktop.Diagnostics;
using Nori.Desktop.Chat;
using Nori.Desktop.Runtime;
using MemoryService = Nori.Desktop.Memory.MemoryService;
using ModelService = Nori.Desktop.Models.ModelService;
using Nori.Desktop.Windows;

namespace Nori.Desktop.Bridge;

/// <summary>
/// 桥接命令。
///
/// 生产入口是音频宿主的固定回报，以及设置、对话、模型、记忆四个原生窗口的白名单。
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
}
