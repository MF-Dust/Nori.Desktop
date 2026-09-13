import type {
	AutomationAuditRecordDto,
	AutomationBrowserStatusDto,
	AutomationBrowserTaskStartDto,
	AutomationDesktopTaskStartDto,
	AutomationDesktopTaskArgs,
	AutomationDesktopWindowDto,
	AutomationSettingsDto,
	BrowserActionDto,
	BrowserTaskResultDto,
	EmbeddingConnectionTestArgs,
	HistoryMessage,
	InteractionConfig,
	JsonObject,
	JsonValue,
	McpServerConfigArgs,
	McpToolListItem,
	McpToolResultDto,
	MemoryAddArgs,
	MemoryAtom,
	MemoryAtomListArgs,
	MemoryExportResult,
	MemoryImportCommitItem,
	MemoryImportCommitResult,
	MemoryImportConflictStrategy,
	MemoryImportPreviewResult,
	MemoryIndexStatus,
	MemoryItem,
	MemoryOverview,
	MemoryPage,
	MemoryRecallDebug,
	MemorySettings,
	MemorySource,
	MemoryUpdateArgs,
	ModelMeta,
	McpServerStatusInfo,
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
} from "../runtime/types"
import type {PluginInfo, PluginInstallResult, PluginUninstallResult} from "../plugins"

export type CommandArgs = JsonObject
export type EmptyCommandArgs = undefined

/** `settings_update_workspace` 的返回：写入后的工作目录状态。 */
export interface WorkspaceSettingsResult {
	/** 当前工作目录的绝对路径；空串表示未启用文件工具。 */
	root: string
	/** 该目录当前是否可用。与 `root` 分开：目录被删除或移动后配置仍在，但工具已不再注册。 */
	available: boolean
	/** 单轮回复中连续调用工具的次数上限。 */
	maxToolIterations: number
}

/** `settings_pick_workspace` 的返回；用户取消选择时为 `null`。 */
export type WorkspacePickResult = {root: string} | null

/** 前端实际使用的宿主命令契约。C# 仍会再次校验参数和来源窗口。 */
export interface BridgeCommandMap {
	ui_get_snapshot: {args: EmptyCommandArgs; result: UiSnapshot}
	write_log: {args: {level: "info" | "warn" | "error" | "debug"; message: string}; result: void}
	get_recent_logs: {args: EmptyCommandArgs; result: {time: string; level: string; source: string; message: string}[]}
	clear_recent_logs: {args: EmptyCommandArgs; result: void}
	get_diagnostic_info: {args: EmptyCommandArgs; result: Record<string, string>}
	export_diagnostics: {args: EmptyCommandArgs; result: {fileName: string; bytes: number; skipped: string[]} | null}
	open_log_folder: {args: EmptyCommandArgs; result: void}
	run_gc_collect: {args: EmptyCommandArgs; result: {released_bytes: number}}
	debug_crash_test: {args: {mode: string}; result: void}
	get_system_language: {args: EmptyCommandArgs; result: string}
	exit_app: {args: EmptyCommandArgs; result: void}
	clipboard_write_text: {args: {text: string}; result: void}
	open_url: {args: {url: string}; result: void}
	window_open_settings: {args: {page?: string}; result: void}
	window_open_memory: {args: {page?: MemoryPage}; result: void}
	window_open_models: {args: EmptyCommandArgs; result: void}
	window_open_chat: {args: EmptyCommandArgs; result: void}

	updater_check: {args: EmptyCommandArgs; result: UpdaterCheckResultDto}
	updater_install: {args: EmptyCommandArgs; result: UpdaterInstallResultDto}
	updater_cancel: {args: EmptyCommandArgs; result: boolean}
	updater_restart: {args: EmptyCommandArgs; result: void}

	llm_fetch_models: {args: {provider: string; baseUrl: string; apiKey: string}; result: string[]}
	llm_test_connection: {args: {provider: string; baseUrl: string; apiKey: string; model: string}; result: ProviderConnectionTestResult}
	embedding_test_connection: {args: {baseUrl: string; apiKey: string; model: string; dimensions?: string}; result: ProviderConnectionTestResult}
	ai_test_connection: {args: {target: "chat" | "embedding"; provider?: string; baseUrl?: string; apiKey?: string; model?: string; dimensions?: string}; result: ProviderConnectionTestResult}
	settings_update_ai: {args: Partial<{provider: string; baseUrl: string; apiKey: string; model: string; persona: string}>; result: void}
	settings_update_embedding: {args: Partial<{model: string; baseUrl: string; apiKey: string; dimensions: string}>; result: void}
	settings_update_ai_providers: {args: {
		chat?: Partial<{provider: string; baseUrl: string; apiKey: string; model: string}>
		embedding?: Partial<{model: string; baseUrl: string; apiKey: string; dimensions: string}>
		persona?: string
	}; result: void}
	settings_test_ai: {args: SettingsTestAiArgs; result: SettingsTestAiResult}
	settings_test_embedding: {args: EmbeddingConnectionTestArgs; result: ProviderConnectionTestResult}
	settings_update_voice: {args: CommandArgs; result: void}
	settings_update_general: {args: CommandArgs; result: void}
	settings_update_proactive: {args: CommandArgs; result: void}
	settings_update_workspace: {args: Partial<{root: string; maxToolIterations: number}>; result: WorkspaceSettingsResult}
	settings_pick_workspace: {args: EmptyCommandArgs; result: WorkspacePickResult}
	settings_update_automation: {args: Partial<{enabled: boolean; desktopEnabled: boolean; browserEnabled: boolean}>; result: AutomationSettingsDto}
	automation_get_snapshot: {args: EmptyCommandArgs; result: UiSnapshot["automation"]}
	automation_update_settings: {args: Partial<{enabled: boolean; allowPointer: boolean; allowKeyboard: boolean; allowScroll: boolean; browserEnabled: boolean}>; result: AutomationSettingsDto}
	automation_browser_start: {args: EmptyCommandArgs; result: AutomationBrowserStatusDto}
	automation_browser_stop: {args: EmptyCommandArgs; result: AutomationBrowserStatusDto}
	automation_browser_status: {args: EmptyCommandArgs; result: AutomationBrowserStatusDto}
	automation_browser_start_task: {args: {actions: BrowserActionDto[]}; result: AutomationBrowserTaskStartDto}
	automation_browser_get_result: {args: {taskId: string}; result: BrowserTaskResultDto | null}
	automation_browser_stop_task: {args: {taskId: string}; result: boolean}
	automation_desktop_list_windows: {args: EmptyCommandArgs; result: AutomationDesktopWindowDto[]}
	automation_desktop_start: {args: AutomationDesktopTaskArgs; result: AutomationDesktopTaskStartDto}
	automation_desktop_stop: {args: {taskId: string}; result: boolean}
	automation_probe_vision: {args: EmptyCommandArgs; result: VisionProbeResult}
	automation_stop_task: {args: {taskId: string}; result: boolean}
	automation_stop_all: {args: EmptyCommandArgs; result: number}
	automation_audit_list: {args: {limit?: number}; result: AutomationAuditRecordDto[]}
	settings_ack_voice_notice: {args: EmptyCommandArgs; result: void}

	chat_start: {args: {text: string}; result: string}
	chat_cancel: {args: {sessionId: string}; result: boolean}
	approval_respond: {args: {requestId: string; approved: boolean}; result: boolean}
	approval_extend: {args: {requestId: string}; result: {deadlineUtc: string}}
	chat_history_page: {args: {limit?: number; beforeId?: number}; result: HistoryMessage[]}
	chat_clear: {args: EmptyCommandArgs; result: {remoteReset: boolean; note: string | null}}

	model_select: {args: {modelId: string}; result: void}
	complete_first_run: {args: {modelId: string; telemetryEnabled: boolean}; result: void}
	init_enter_main: {args: EmptyCommandArgs; result: void}
	get_init_config: {args: EmptyCommandArgs; result: CommandArgs}
	init_ready: {args: EmptyCommandArgs; result: {initStartPending: boolean}}
	model_import_local: {args: {resourceType: "live2d"; sourceKind: "zip" | "folder"}; result: string[] | null}
	indextts_pick_template: {args: EmptyCommandArgs; result: string | null}
	indextts_clone_voice: {args: {filePath?: string}; result: {voiceId: string}}
	model_get_meta: {args: {modelId: string}; result: ModelMeta}
	model_set_display: {args: {modelId: string} & CommandArgs; result: void}
	model_set_interactions: {args: {modelId: string; interactions: InteractionConfig}; result: void}
	model_set_behavior: {args: CommandArgs; result: void}
	model_list: {args: EmptyCommandArgs; result: UiSnapshot}
	pet_play_motion: {args: {name?: string}; result: boolean}
	pet_reload_model: {args: {modelId?: string}; result: void}
	pet_get_state: {args: EmptyCommandArgs; result: CommandArgs}

	tools_set_enabled: {args: {name: string; enabled: boolean}; result: void}
	tools_execute_manual: {args: {name: string; arguments?: CommandArgs | null}; result: JsonValue}

	memory_add: {args: MemoryAddArgs; result: MemoryItem}
	memory_list: {args: {limit?: number}; result: MemoryItem[]}
	memory_list_page: {args: {query?: string; kind?: string; status?: string; limit?: number; offset?: number}; result: {items: MemoryItem[]; total: number}}
	memory_get: {args: {id: number}; result: {item: MemoryItem; atoms: MemoryAtom[]; sources: MemorySource[]}}
	memory_update: {args: MemoryUpdateArgs; result: boolean}
	memory_delete: {args: {id: number; confirmToken: string}; result: boolean}
	memory_clear: {args: {confirmToken: string}; result: void}
	memory_archive: {args: {id: number}; result: boolean}
	memory_restore: {args: {id: number}; result: boolean}
	memory_overview: {args: EmptyCommandArgs; result: MemoryOverview}
	memory_atom_list: {args: MemoryAtomListArgs; result: MemoryAtom[]}
	memory_search_hybrid: {args: CommandArgs; result: MemoryItem[]}
	memory_knowledge_status: {args: EmptyCommandArgs; result: MemoryIndexStatus}
	memory_knowledge_reindex: {args: EmptyCommandArgs; result: MemoryIndexStatus}
	memory_knowledge_open: {args: EmptyCommandArgs; result: void}
	memory_recall_debug: {args: {query: string}; result: MemoryRecallDebug}
	memory_get_settings: {args: EmptyCommandArgs; result: MemorySettings}
	memory_update_settings: {args: {settings: CommandArgs}; result: MemorySettings}
	memory_reembed_all: {args: EmptyCommandArgs; result: number}
	memory_export: {args: EmptyCommandArgs; result: MemoryExportResult}
	memory_import_preview: {args: {fileContent: string; fileName?: string; fileSize?: number}; result: MemoryImportPreviewResult}
	memory_import_commit: {args: {previewToken?: string; items?: MemoryImportCommitItem[]; conflictStrategy?: MemoryImportConflictStrategy}; result: MemoryImportCommitResult}

	skills_marketplace: {args: EmptyCommandArgs; result: SkillMarketplaceDto[]}
	skills_install_marketplace: {args: {skillId: string}; result: SkillDto}
	skills_toggle: {args: {id: string; enabled: boolean}; result: void}
	skills_install_url: {args: {url: string}; result: SkillRecordDto}
	skills_save_custom: {args: {skill: SkillRecordInput}; result: SkillRecordDto}
	skills_uninstall: {args: {id: string}; result: void}
	skills_export: {args: {id: string}; result: string}
	skills_import_json: {args: {json: string}; result: void}

	mcp_get_servers: {args: EmptyCommandArgs; result: McpServerStatusInfo[]}
	mcp_save_server: {args: McpServerConfigArgs; result: McpServerStatusInfo}
	mcp_delete_server: {args: {id: string}; result: boolean}
	mcp_connect_server: {args: {id: string}; result: McpServerStatusInfo}
	mcp_disconnect_server: {args: {id: string}; result: McpServerStatusInfo}
	mcp_list_tools: {args: EmptyCommandArgs; result: McpToolListItem[]}
	mcp_test_server: {args: McpServerConfigArgs; result: McpServerStatusInfo}
	mcp_call_tool: {args: {serverId: string; toolName: string; arguments?: CommandArgs | null; sessionId?: string}; result: McpToolResultDto}
	mcp_import_url: {args: {url: string}; result: McpServerStatusInfo[]}

	plugin_list: {args: EmptyCommandArgs; result: {plugins: PluginInfo[]}}
	plugin_install_local: {args: EmptyCommandArgs; result: PluginInstallResult}
	plugin_enable: {args: {id: string}; result: PluginInfo}
	plugin_disable: {args: {id: string}; result: PluginInfo}
	plugin_uninstall: {args: {id: string; deleteData: boolean}; result: PluginUninstallResult}
	plugin_widgets: {args: Record<string, never>; result: {widgets: {pluginId: string; title: string; entry: string}[]}}
	plugin_action: {
		args: {pluginId: string; actionId: string; args?: Record<string, unknown>}
		result: Record<string, unknown>
	}

	reminder_add: {args: {content: string; delayMinutes?: number}; result: ReminderItemDto}
	reminder_cancel: {args: {id: string}; result: boolean}
	reminder_update: {args: ReminderUpdateArgs; result: ReminderItemDto}
	reminder_snooze: {args: ReminderSnoozeArgs; result: ReminderItemDto}
	reminder_complete: {args: {id: string}; result: boolean}
	reminder_list: {args: EmptyCommandArgs; result: ReminderItemDto[]}
	tts_test: {args: {text?: string}; result: void}
	tts_stop: {args: EmptyCommandArgs; result: void}
	stt_start: {args: EmptyCommandArgs; result: void}
	stt_stop: {args: EmptyCommandArgs; result: {text: string}}

	audio_host_ready: {args: EmptyCommandArgs; result: void}
	audio_playback_finished: {args: {token: string; error?: string}; result: void}
	audio_level: {args: {level: number}; result: void}
	audio_record_ready: {args: {token: string}; result: void}
	audio_record_failed: {args: {token: string; error?: string}; result: void}
	audio_upload_failed: {args: {token: string; error?: string}; result: void}

	window_show: {args: {label: string}; result: void}
	window_hide: {args: {label: string}; result: void}
	window_close: {args: {label: string}; result: void}
	window_focus: {args: {label: string}; result: void}
	window_is_visible: {args: {label: string}; result: boolean}
	window_scale_factor: {args: {label: string}; result: number}
	window_outer_position: {args: {label: string}; result: {x: number; y: number}}
	window_outer_size: {args: {label: string}; result: {width: number; height: number}}
	window_set_size: {args: {label: string; width: number; height: number}; result: void}
	window_set_position: {args: {label: string; x: number; y: number}; result: void}
	window_start_drag: {args: {label: string}; result: void}
}

export type BridgeCommandName = keyof BridgeCommandMap
export type BridgeCommandArgs<K extends BridgeCommandName> = BridgeCommandMap[K]["args"]
export type BridgeCommandResult<K extends BridgeCommandName> = BridgeCommandMap[K]["result"]
