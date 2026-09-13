/**
 * 后端运行时客户端
 *
 * WebView 唯一的业务桥接入口: 只读状态快照 + 领域命令 + 事件订阅。
 * 组件不允许直接 invoke 业务命令或触碰配置键 —— 一切经由这里。
 */
import {ref} from "vue"
import {invoke} from "../host/invoke"
import {listen, type UnlistenFn} from "../host/event"
import {feedback} from "../feedback"
import type {
	AgentEventPayload,
	AutomationAuditRecordDto,
	AutomationBrowserStatusDto,
	AutomationBrowserTaskStartDto,
	AutomationDesktopTaskStartDto,
	AutomationDesktopTaskArgs,
	AutomationDesktopWindowDto,
	AutomationSettingsDto,
	BehaviorsState,
	BrowserActionDto,
	BrowserTaskResultDto,
	EmbeddingConnectionTestArgs,
	HistoryMessage,
	InteractionConfig,
	JsonObject,
	JsonValue,
	McpServerConfigArgs,
	McpServerStatusInfo,
	McpToolResultDto,
	MemoryAddArgs,
	MemoryAtom,
	MemoryAtomListArgs,
	MemoryExportResult,
	MemoryImportCommitItem,
	MemoryImportCommitResult,
	MemoryImportConflictStrategy,
	MemoryImportPreviewResult,
	MemoryItem,
	MemoryIndexStatus,
	MemoryListPage,
	MemoryOverview,
	MemoryPage,
	MemoryRecallDebug,
	MemorySettings,
	MemorySource,
	MemoryUpdateArgs,
	ModelMeta,
	PlatformState,
	ProviderConnectionTestResult,
	ReminderItemDto,
	ReminderSnoozeArgs,
	ReminderUpdateArgs,
	SettingsTestAiArgs,
	SettingsTestAiResult,
	SkillDto,
	SkillMarketplaceDto,
	SkillRecordDto,
	SkillRecordInput,
	UiSnapshot,
	UpdaterCheckResultDto,
	UpdaterInstallResultDto,
	VisionProbeResult,
} from "./types"

export type {
	AgentEventPayload,
	AgentState,
	ApprovalRequestDto,
	AppInfo,
	AutomationActionKindDto,
	AutomationApprovalDto,
	AutomationAuditRecordDto,
	AutomationBrowserState,
	AutomationBrowserStatusDto,
	AutomationBrowserTaskStartDto,
	AutomationCapabilityDto,
	AutomationDesktopTaskArgs,
	AutomationDesktopTaskStartDto,
	AutomationDesktopWindowDto,
	AutomationSettingsDto,
	AutomationState,
	AutomationTaskDto,
	AutomationTaskLifecycleState,
	AutomationTaskStatusDto,
	AutomationTaskStatus,
	BehaviorsState,
	BrowserActionDto,
	BrowserTaskResultDto,
	EmbeddingConnectionTestArgs,
	EmbeddingState,
	EmotionDto,
	GeneralState,
	HistoryMessage,
	InteractionAction,
	InteractionActionMode,
	InteractionConfig,
	InteractionReactionMode,
	InteractionRect,
	InteractionRegion,
	JsonObject,
	JsonValue,
	McpContentItemDto,
	McpServerConfigArgs,
	McpServerStatusInfo,
	McpToolListItem,
	McpToolResultDto,
	MemoryAddArgs,
	MemoryAtom,
	MemoryAtomListArgs,
	MemoryExportResult,
	MemoryImportCommitItem,
	MemoryImportCommitResult,
	MemoryImportConflictStrategy,
	MemoryImportPreviewItem,
	MemoryImportPreviewResult,
	MemoryItem,
	MemoryIndexStatus,
	MemoryListPage,
	MemoryOverview,
	MemoryRecallDebug,
	MemoryPage,
	MemorySettings,
	MemorySource,
	MemoryState,
	MemoryUpdateArgs,
	ModelItem,
	ModelMeta,
	ModelsState,
	PetState,
	PlatformState,
	ProviderConnectionTestResult,
	ProactiveState,
	ReminderDto,
	ReminderItemDto,
	ReminderSnoozeArgs,
	ReminderStatus,
	ReminderUpdateArgs,
	SettingsTestAiArgs,
	SettingsTestAiResult,
	SkillDto,
	SkillMarketplaceDto,
	SkillRecordDto,
	SkillRecordInput,
	TelemetryState,
	SecretIssueDto,
	ToolDto,
	UsageMetrics,
	VisionProbeResult,
	VoiceState,
	UiSnapshot,
} from "./types"

/** 全局只读快照 (响应式) */
const SNAPSHOT = ref<UiSnapshot | null>(null)
const BOOTSTRAP_ERROR = ref<unknown | null>(null)
const BOOTSTRAP_LOADING = ref(false)
const REFRESH_ERROR = ref<unknown | null>(null)

let bootstrap: Promise<void> | null = null
let bootstrapUnlisten: UnlistenFn | null = null
let refreshInFlight: Promise<void> | null = null
let refreshQueued = false

/** 上一次见到的语言, 用于跳窗口同步语言切换 */
let lastLanguage: string | null = null
const languageHandlers = new Set<(language: string) => void>()

async function refreshCore(): Promise<void> {
	const NEXT_SNAPSHOT = await invoke("ui_get_snapshot")
	SNAPSHOT.value = NEXT_SNAPSHOT
	const LANGUAGE = NEXT_SNAPSHOT.general.language
	if (LANGUAGE && LANGUAGE !== lastLanguage) {
		const FIRST = lastLanguage === null
		lastLanguage = LANGUAGE
		// 首次拉取不回放 (main.ts 已经用它初始化 i18n), 只处理后续变更
		if (!FIRST) for (const handler of languageHandlers) handler(LANGUAGE)
	}
}

/**
 * 刷新快照: 同一时间只允许一次请求, 请求期间的后续调用合并为尾刷新。
 * 尾刷新仍由所有等待者等待, 所以调用方不会在过期快照上继续执行。
 */
function refresh(): Promise<void> {
	if (refreshInFlight) {
		refreshQueued = true
		return refreshInFlight
	}

	const RUN = (async () => {
		let firstError: unknown | null = null
		do {
			refreshQueued = false
			try {
				await refreshCore()
			} catch (error) {
				if (firstError === null) firstError = error
			}
		} while (refreshQueued)

		if (firstError !== null) throw firstError
	})()

	let PUBLIC: Promise<void>
	PUBLIC = RUN.then(
		() => {
			REFRESH_ERROR.value = null
		},
		error => {
			REFRESH_ERROR.value = error
			throw error
		},
	).finally(() => {
		if (refreshInFlight === PUBLIC) refreshInFlight = null
	})
	refreshInFlight = PUBLIC
	return PUBLIC
}

const startBootstrap = (): Promise<void> => {
	BOOTSTRAP_LOADING.value = true
	BOOTSTRAP_ERROR.value = null
	const RUN = (async () => {
		let currentUnlisten: UnlistenFn | null = null
		try {
			currentUnlisten = await listen<{version: number; topics: string[]}>("nori:state-changed", () => {
				void refresh().catch(error => {
					// 广播刷新不能产生未处理拒绝, 同时必须让用户知道状态可能已过期。
					feedback.error("同步运行状态失败", error)
				})
			})
			bootstrapUnlisten = currentUnlisten
			await refresh()
		} catch (error) {
			// 引导失败不能留下半成品监听, 否则重试会叠加广播回调。
			if (currentUnlisten) {
				currentUnlisten()
				if (bootstrapUnlisten === currentUnlisten) bootstrapUnlisten = null
			}
			throw error
		}
	})()
	bootstrap = RUN.catch(error => {
		BOOTSTRAP_ERROR.value = error
		throw error
	}).finally(() => {
		BOOTSTRAP_LOADING.value = false
	})
	return bootstrap
}

/**
 * 运行时客户端
 */
export const RUNTIME = {
	/** 只读快照 */
	snapshot: SNAPSHOT,

	/** 首次引导失败原因 (由 App 展示可重试兜底页) */
	bootstrapError: BOOTSTRAP_ERROR,

	/** 首次引导是否正在执行 */
	bootstrapLoading: BOOTSTRAP_LOADING,

	/** 最近一次快照刷新失败原因 */
	refreshError: REFRESH_ERROR,

	/**
	 * 引导: 建立全局状态变更订阅并拉取首份快照。
	 * 幂等, 多窗口/多组件可重复调用。
	 */
	init(): Promise<void> {
		return bootstrap ?? startBootstrap()
	},

	/** 手动刷新快照 (同一时间只发一个请求, 持续排空尾刷新) */
	refresh,

	/** 清除失败引导并重新建立快照与事件订阅 */
	async retryInit(): Promise<void> {
		if (BOOTSTRAP_LOADING.value && bootstrap) {
			await bootstrap
			return
		}
		bootstrapUnlisten?.()
		bootstrapUnlisten = null
		bootstrap = null
		BOOTSTRAP_ERROR.value = null
		await startBootstrap()
	},

	/**
	 * 当前平台能力 (快照未到位时给最保守的假设)
	 *
	 * 组件不要自己猜平台: 拖拽手柄、穿透开关、眼神跟随这类 UI 全部看这里。
	 */
	platform(): PlatformState {
		return SNAPSHOT.value?.platform ?? {
			os: "unknown",
			sessionType: "unknown",
			supportsGlobalCursor: false,
			supportsWindowDrag: false,
			supportsHitThrough: false,
			supportsTopmost: false,
			supportsTray: false,
		}
	},

	// ------------------------------------------------------------------
	// 事件订阅
	// ------------------------------------------------------------------

	/** Agent 会话事件 (仅发起会话的窗口会收到) */
	onAgentEvent(handler: (payload: AgentEventPayload) => void): Promise<UnlistenFn> {
		return listen<AgentEventPayload>("nori:agent-event", ({payload}) => handler(payload))
	},

	/** 主动交互消息 (挂机关怀/日程问候/提醒触发) */
	onProactiveMessage(handler: (text: string) => void): Promise<UnlistenFn> {
		return listen<{text: string}>("nori:proactive-message", ({payload}) => handler(payload.text))
	},

	/**
	 * 语言变更 (任一窗口改了语言后, 每个窗口都靠它重放 setLanguage)
	 *
	 * 返回取消注册的函数
	 */
	onLanguageChanged(handler: (language: string) => void): () => void {
		languageHandlers.add(handler)
		return () => languageHandlers.delete(handler)
	},

	// ------------------------------------------------------------------
	// 聊天 / Agent
	// ------------------------------------------------------------------

	startChat(text: string): Promise<string> {
		return invoke("chat_start", {text})
	},
	cancelChat(sessionId: string): Promise<boolean> {
		return invoke("chat_cancel", {sessionId})
	},
	respondApproval(requestId: string, approved: boolean): Promise<boolean> {
		return invoke("approval_respond", {requestId, approved})
	},
	historyPage(limit = 50, beforeId = 0): Promise<HistoryMessage[]> {
		return invoke("chat_history_page", {limit, beforeId})
	},
	clearChat(): Promise<void> {
		return invoke("chat_clear")
	},

	// ------------------------------------------------------------------
	// 设置
	// ------------------------------------------------------------------

	updateAi(patch: Partial<{provider: string; baseUrl: string; apiKey: string; model: string; persona: string}>): Promise<void> {
		return invoke("settings_update_ai", patch)
	},
	updateEmbedding(patch: Partial<{model: string; baseUrl: string; apiKey: string; dimensions: string}>): Promise<void> {
		return invoke("settings_update_embedding", patch)
	},
	updateAiProviders(patch: {
		chat?: Partial<{provider: string; baseUrl: string; apiKey: string; model: string}>
		embedding?: Partial<{model: string; baseUrl: string; apiKey: string; dimensions: string}>
		persona?: string
	}): Promise<void> {
		return invoke("settings_update_ai_providers", patch)
	},
	testAiConnection(args: {
		target: "chat" | "embedding"
		provider?: string
		baseUrl?: string
		apiKey?: string
		model?: string
		dimensions?: string
	}): Promise<ProviderConnectionTestResult> {
		return invoke("ai_test_connection", args)
	},
	testAiProviders(args: SettingsTestAiArgs): Promise<SettingsTestAiResult> {
		return invoke("settings_test_ai", args)
	},
	testEmbeddingSettings(args: EmbeddingConnectionTestArgs): Promise<ProviderConnectionTestResult> {
		return invoke("settings_test_embedding", args)
	},
	updateVoice(patch: Partial<{
		volume: string
		ttsProvider: string
		ttsBaseUrl: string
		ttsModel: string
		ttsApiKey: string
		ttsVoice: string
		ttsSpeed: string
		ttsAutoPlay: boolean
		gptsovitsBaseUrl: string
		gptsovitsRefAudio: string
		gptsovitsPromptText: string
		gptsovitsPromptLang: string
		indexttsTemplateAudio: string
		indexttsEmoAlpha: string
		sttProvider: string
		sttBaseUrl: string
		sttApiKey: string
	}>): Promise<void> {
		return invoke("settings_update_voice", patch)
	},
	pickIndexTtsTemplate(): Promise<string | null> {
		return invoke("indextts_pick_template")
	},
	cloneIndexTtsVoice(filePath?: string): Promise<{voiceId: string}> {
		return invoke("indextts_clone_voice", {filePath})
	},
	updateGeneral(patch: Partial<{language: string; petAutoSummon: boolean; sidebarCollapsed: boolean; autoCheckUpdates: boolean; telemetryEnabled: boolean}>): Promise<void> {
		return invoke("settings_update_general", patch)
	},
	checkUpdate(): Promise<UpdaterCheckResultDto> {
		return invoke("updater_check")
	},
	installUpdate(): Promise<UpdaterInstallResultDto> {
		return invoke("updater_install")
	},
	cancelUpdate(): Promise<boolean> {
		return invoke("updater_cancel")
	},
	restartApp(): Promise<void> {
		return invoke("updater_restart")
	},
	updateProactive(patch: Partial<{idleEnabled: boolean; idleMinutes: number; dailyGreeting: boolean}>): Promise<void> {
		return invoke("settings_update_proactive", patch)
	},
	updateAutomation(patch: Partial<{enabled: boolean; desktopEnabled: boolean; browserEnabled: boolean}>): Promise<AutomationSettingsDto> {
		return invoke("settings_update_automation", patch)
	},
	/** 请求宿主显示独立的原生设置窗口，并可指定初始页面。 */
	openSettings(page?: string): Promise<void> {
		return invoke("window_open_settings", page ? {page} : undefined)
	},
	/** 请求宿主显示独立的原生记忆窗口。 */
	openMemory(page?: MemoryPage): Promise<void> {
		return invoke("window_open_memory", page ? {page} : undefined)
	},
	stopAutomationTask(taskId: string): Promise<boolean> {
		return invoke("automation_stop_task", {taskId})
	},
	stopAllAutomation(): Promise<number> {
		return invoke("automation_stop_all")
	},
	automationBrowserStartTask(actions: BrowserActionDto[]): Promise<AutomationBrowserTaskStartDto> {
		return invoke("automation_browser_start_task", {actions})
	},
	automationBrowserGetResult(taskId: string): Promise<BrowserTaskResultDto | null> {
		return invoke("automation_browser_get_result", {taskId})
	},
	automationBrowserStopTask(taskId: string): Promise<boolean> {
		return invoke("automation_browser_stop_task", {taskId})
	},
	automationBrowserStart(): Promise<AutomationBrowserStatusDto> {
		return invoke("automation_browser_start")
	},
	automationBrowserStop(): Promise<AutomationBrowserStatusDto> {
		return invoke("automation_browser_stop")
	},
	automationDesktopListWindows(): Promise<AutomationDesktopWindowDto[]> {
		return invoke("automation_desktop_list_windows")
	},
	automationDesktopStart(args: AutomationDesktopTaskArgs): Promise<AutomationDesktopTaskStartDto> {
		return invoke("automation_desktop_start", args)
	},
	automationDesktopStop(taskId: string): Promise<boolean> {
		return invoke("automation_desktop_stop", {taskId})
	},
	automationAuditList(limit = 50): Promise<AutomationAuditRecordDto[]> {
		return invoke("automation_audit_list", {limit})
	},
	respondAutomationApproval(requestId: string, approved: boolean): Promise<boolean> {
		return invoke("approval_respond", {requestId, approved})
	},
	getAutomationSnapshot(): Promise<UiSnapshot["automation"]> {
		return invoke("automation_get_snapshot")
	},
	probeVisionCapability(): Promise<VisionProbeResult> {
		return invoke("automation_probe_vision")
	},
	ackVoiceNotice(): Promise<void> {
		return invoke("settings_ack_voice_notice")
	},
	fetchModels(provider: string, baseUrl: string, apiKey: string): Promise<string[]> {
		return invoke("llm_fetch_models", {provider, baseUrl, apiKey})
	},
	testLlmConnection(provider: string, baseUrl: string, apiKey: string, model: string): Promise<ProviderConnectionTestResult> {
		return invoke("ai_test_connection", {target: "chat", provider, baseUrl, apiKey, model})
	},
	testEmbeddingConnection(baseUrl: string, apiKey: string, model: string, dimensions?: string): Promise<ProviderConnectionTestResult> {
		return invoke("ai_test_connection", {target: "embedding", baseUrl, apiKey, model, dimensions})
	},

	toolsSetEnabled(name: string, enabled: boolean): Promise<void> {
		return invoke("tools_set_enabled", {name, enabled})
	},
	toolsExecuteManual(name: string, args: JsonObject = {}): Promise<JsonValue> {
		return invoke("tools_execute_manual", {name, arguments: args})
	},

	// ------------------------------------------------------------------
	// 模型
	// ------------------------------------------------------------------

	selectModel(modelId: string): Promise<void> {
		return invoke("model_select", {modelId})
	},
	completeFirstRun(modelId: string, telemetryEnabled: boolean): Promise<void> {
		return invoke("complete_first_run", {modelId, telemetryEnabled})
	},

	/**
	 * init 窗口把主界面切换交给宿主；宿主会按模型有效性与 pet_auto_summon 决定伴侣视窗显隐。
	 */
	initEnterMain(): Promise<void> {
		return invoke("init_enter_main")
	},

	/**
	 * init 窗口就绪握手
	 *
	 * 返回 initStartPending=true 说明向导已经广播过 nori:init-start (早于本页订阅),
	 * 调用方应直接执行初始化流程, 不能再等事件
	 */
	initReady(): Promise<{initStartPending: boolean}> {
		return invoke("init_ready")
	},
	importLocalModel(sourceKind: "zip" | "folder" = "zip"): Promise<string[] | null> {
		return invoke("model_import_local", {resourceType: "live2d", sourceKind})
	},
	modelMeta(modelId: string): Promise<ModelMeta> {
		return invoke("model_get_meta", {modelId})
	},
	setModelDisplay(modelId: string, patch: {
		scale?: number
		expressions?: string[]
		opacity?: number
		shadow?: boolean
		renderScale?: number
		qualityMode?: "adaptive" | "quality" | "eco"
		maxFps?: number
	}): Promise<void> {
		return invoke("model_set_display", {modelId, ...patch})
	},
	setModelInteractions(modelId: string, interactions: InteractionConfig): Promise<void> {
		return invoke("model_set_interactions", {modelId, interactions})
	},
	setModelBehavior(patch: Partial<BehaviorsState>): Promise<void> {
		return invoke("model_set_behavior", patch)
	},

	// ------------------------------------------------------------------
	// 记忆库
	// ------------------------------------------------------------------

	memoryAdd(content: string, importance = 0.8, tags?: string, kind?: string): Promise<MemoryItem> {
		const ARGS: MemoryAddArgs = {content, importance}
		if (tags !== undefined) ARGS.tags = tags
		if (kind !== undefined) ARGS.kind = kind
		return invoke("memory_add", ARGS)
	},
	memoryList(limit = 50): Promise<MemoryItem[]> {
		return invoke("memory_list", {limit})
	},
	memoryListPage(query?: string, kind?: string, status?: string, limit = 50, offset = 0): Promise<MemoryListPage> {
		return invoke("memory_list_page", {query, kind, status, limit, offset})
	},
	memoryGet(id: number): Promise<{item: MemoryItem; atoms: MemoryAtom[]; sources: MemorySource[]}> {
		return invoke("memory_get", {id})
	},
	memoryUpdate(id: number, content: string, importance?: number, tags?: string, patch?: {kind?: string; canonicalSummary?: string; personaSummary?: string; confidence?: number}): Promise<boolean> {
		const ARGS: MemoryUpdateArgs = {id, content}
		if (importance !== undefined) ARGS.importance = importance
		if (tags !== undefined) ARGS.tags = tags
		if (patch) Object.assign(ARGS, patch)
		return invoke("memory_update", ARGS)
	},
	memoryArchive(id: number): Promise<boolean> {
		return invoke("memory_archive", {id})
	},
	memoryRestore(id: number): Promise<boolean> {
		return invoke("memory_restore", {id})
	},
	memoryDelete(id: number): Promise<boolean> {
		return invoke("memory_delete", {id, confirmToken: "DELETE_MEMORY"})
	},
	memoryClear(): Promise<void> {
		return invoke("memory_clear", {confirmToken: "CLEAR_PERSONAL_MEMORY"})
	},
	memoryOverview(): Promise<MemoryOverview> {
		return invoke("memory_overview")
	},
	memoryAtoms(memoryId?: number, status?: string, limit = 50, offset = 0): Promise<MemoryAtom[]> {
		const ARGS: MemoryAtomListArgs = {limit, offset}
		if (memoryId !== undefined) ARGS.memoryId = memoryId
		if (status !== undefined) ARGS.status = status
		return invoke("memory_atom_list", ARGS)
	},
	memorySearch(keyword: string, limit = 20): Promise<MemoryItem[]> {
		return invoke("memory_search_hybrid", {keyword, limit})
	},
	memoryKnowledgeStatus(): Promise<MemoryIndexStatus> {
		return invoke("memory_knowledge_status")
	},
	memoryKnowledgeReindex(): Promise<MemoryIndexStatus> {
		return invoke("memory_knowledge_reindex")
	},
	memoryKnowledgeOpen(): Promise<void> {
		return invoke("memory_knowledge_open")
	},
	memoryRecallDebug(query: string): Promise<MemoryRecallDebug> {
		return invoke("memory_recall_debug", {query})
	},
	memoryGetSettings(): Promise<MemorySettings> {
		return invoke("memory_get_settings")
	},
	memoryUpdateSettings(settings: Partial<MemorySettings>): Promise<MemorySettings> {
		return invoke("memory_update_settings", {settings})
	},
	memoryReembed(): Promise<number> {
		return invoke("memory_reembed_all")
	},
	memoryExport(): Promise<MemoryExportResult> {
		return invoke("memory_export")
	},
	memoryImportPreview(fileContent: string, fileName?: string, fileSize?: number): Promise<MemoryImportPreviewResult> {
		return invoke("memory_import_preview", {fileContent, fileName, fileSize})
	},
	memoryImportCommit(options: {previewToken?: string; items?: MemoryImportCommitItem[]; conflictStrategy?: MemoryImportConflictStrategy} = {}): Promise<MemoryImportCommitResult> {
		return invoke("memory_import_commit", options)
	},

	// ------------------------------------------------------------------
	// 技能
	// ------------------------------------------------------------------

	skillsMarketplace(): Promise<SkillMarketplaceDto[]> {
		return invoke("skills_marketplace")
	},
	skillsInstallMarketplace(skillId: string): Promise<SkillDto> {
		return invoke("skills_install_marketplace", {skillId})
	},
	skillsToggle(id: string, enabled: boolean): Promise<void> {
		return invoke("skills_toggle", {id, enabled})
	},
	skillsInstallUrl(url: string): Promise<SkillRecordDto> {
		return invoke("skills_install_url", {url})
	},
	skillsSaveCustom(skill: SkillRecordInput): Promise<SkillRecordDto> {
		return invoke("skills_save_custom", {skill})
	},
	skillsUninstall(id: string): Promise<void> {
		return invoke("skills_uninstall", {id})
	},
	skillsExport(id: string): Promise<string> {
		return invoke("skills_export", {id})
	},
	skillsImportJson(json: string): Promise<void> {
		return invoke("skills_import_json", {json})
	},

	// ------------------------------------------------------------------
	// MCP
	// ------------------------------------------------------------------

	mcpGetServers(): Promise<McpServerStatusInfo[]> {
		return invoke("mcp_get_servers")
	},
	mcpSaveServer(config: McpServerConfigArgs): Promise<McpServerStatusInfo> {
		return invoke("mcp_save_server", config)
	},
	mcpDeleteServer(id: string): Promise<boolean> {
		return invoke("mcp_delete_server", {id})
	},
	mcpConnect(id: string): Promise<McpServerStatusInfo> {
		return invoke("mcp_connect_server", {id})
	},
	mcpDisconnect(id: string): Promise<McpServerStatusInfo> {
		return invoke("mcp_disconnect_server", {id})
	},
	mcpTestServer(config: McpServerConfigArgs): Promise<McpServerStatusInfo> {
		return invoke("mcp_test_server", config)
	},
	mcpCallTool(serverId: string, toolName: string, args: JsonObject = {}): Promise<McpToolResultDto> {
		return invoke("mcp_call_tool", {serverId, toolName, arguments: args})
	},
	mcpImportUrl(url: string): Promise<McpServerStatusInfo[]> {
		return invoke("mcp_import_url", {url})
	},

	// ------------------------------------------------------------------
	// 提醒
	// ------------------------------------------------------------------

	reminderAdd(content: string, delayMinutes: number): Promise<ReminderItemDto> {
		return invoke("reminder_add", {content, delayMinutes})
	},
	reminderCancel(id: string): Promise<boolean> {
		return invoke("reminder_cancel", {id})
	},
	reminderUpdate(args: ReminderUpdateArgs): Promise<ReminderItemDto> {
		return invoke("reminder_update", args)
	},
	reminderSnooze(args: ReminderSnoozeArgs): Promise<ReminderItemDto> {
		return invoke("reminder_snooze", args)
	},
	reminderComplete(id: string): Promise<boolean> {
		return invoke("reminder_complete", {id})
	},
	reminderList(): Promise<ReminderItemDto[]> {
		return invoke("reminder_list")
	},

	// ------------------------------------------------------------------
	// 语音
	// ------------------------------------------------------------------

	ttsTest(text?: string): Promise<void> {
		return invoke("tts_test", text ? {text} : {})
	},
	ttsStop(): Promise<void> {
		return invoke("tts_stop")
	},
	sttStart(): Promise<void> {
		return invoke("stt_start")
	},
	sttStop(): Promise<{text: string}> {
		return invoke("stt_stop")
	},

	// ------------------------------------------------------------------
	// 伴侣 / 日志 / 调试
	// ------------------------------------------------------------------

	getRecentLogs(): Promise<{time: string; level: string; source: string; message: string}[]> {
		return invoke("get_recent_logs")
	},
	clearRecentLogs(): Promise<void> {
		return invoke("clear_recent_logs")
	},
	getDiagnosticInfo(): Promise<Record<string, string>> {
		return invoke("get_diagnostic_info")
	},
	exportDiagnostics(): Promise<{fileName: string; bytes: number; skipped: string[]} | null> {
		return invoke("export_diagnostics")
	},
	openLogFolder(): Promise<void> {
		return invoke("open_log_folder")
	},
	runGcCollect(): Promise<{released_bytes: number}> {
		return invoke("run_gc_collect")
	},
	debugCrashTest(mode: string): Promise<void> {
		return invoke("debug_crash_test", {mode})
	},
	petPlayMotion(name?: string): Promise<boolean> {
		return invoke("pet_play_motion", name ? {name} : {})
	},
	writeLog(level: "info" | "warn" | "error", message: string): Promise<void> {
		return invoke("write_log", {level, message})
	},
	exitApp(): Promise<void> {
		return invoke("exit_app")
	},
	copyText(text: string): Promise<void> {
		return invoke("clipboard_write_text", {text})
	},
	openUrl(url: string): Promise<void> {
		return invoke("open_url", {url})
	},
}
