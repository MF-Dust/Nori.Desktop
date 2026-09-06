/**
 * 后端 UI 状态快照与运行时事件 DTO
 *
 * 与 Nori.Desktop Runtime/AppRuntime.BuildSnapshot 及事件载荷一一对应。
 * 前端不再持有业务真相: 这里全部是只读投影。
 */

/** 可通过 Bridge 传输的 JSON 值；用于工具参数等本身由外部定义的结构。 */
export type JsonValue = string | number | boolean | null | JsonObject | JsonValue[]

/** 可通过 Bridge 传输的 JSON 对象。 */
export interface JsonObject {
	[key: string]: JsonValue
}

// ===================================================================
// 自动更新
// ===================================================================

/** 统一自动更新状态 */
export type UpdaterState =
	| "idle"
	| "checking"
	| "available"
	| "uptodate"
	| "downloading"
	| "verifying"
	| "installing"
	| "readytorestart"
	| "error"
	| "cancelled"

/** 检查更新返回结果 */
export interface UpdaterCheckResultDto {
	available: boolean
	currentVersion: string
	latestVersion?: string | null
	releaseTag?: string | null
	releaseNotes?: string | null
	publishedAt?: string | null
}

/** 安装更新返回结果 */
export interface UpdaterInstallResultDto {
	success: boolean
	slotName: string
	productVersion: string
}

/** 更新运行状态快照 */
export interface UpdaterStatusDto {
	state: UpdaterState
	progress: number
	downloadedBytes: number
	totalBytes: number
	message?: string | null
	currentVersion: string
	availableVersion?: string | null
	releaseTag?: string | null
	releaseNotes?: string | null
	lastCheckedAt?: string | null
	unavailableReason?: string | null
	manualDownloadUrl?: string | null
}

// ===================================================================
// 快照
// ===================================================================

/** 应用信息 */
export interface AppInfo {
	/** 后端 UiSnapshot.app.appVersion; 与 ProductVersion.Current 对应 */
	appVersion: string
	/** 显式产品版本；旧宿主只提供 appVersion 时前端回退使用它。 */
	productVersion?: string
	platform: string
	debugCrashTestsAvailable: boolean
	/** 是否通过 --safe-mode 启动 */
	safeMode: boolean
}

/** 通用设置 */
export interface GeneralState {
	language: string
	petAutoSummon: boolean
	/** 主界面侧边栏是否折叠 */
	sidebarCollapsed: boolean
	/** 启动时是否自动检查更新 */
	autoCheckUpdates?: boolean
}

/** 伴侣窗口状态 (宿主显隐的唯一真相, 托盘切换后同样同步) */
export interface PetState {
	visible: boolean
}

/** 运行会话类型 (Linux 下区分 x11 / wayland) */
export type SessionType = "windows" | "macos" | "x11" | "wayland" | "unknown"

/**
 * 平台能力
 *
 * 前端一切与平台相关的 UI 都由这些标志驱动: 不支持就明确禁用并给出说明,
 * 不再靠 try/catch 静默吞掉 PlatformNotSupportedException。
 */
export interface PlatformState {
	os: "windows" | "macos" | "linux" | "unknown"
	sessionType: SessionType
	/** 能否读取窗口外的全局光标 (眼神跟随) */
	supportsGlobalCursor: boolean
	/** 能否从 HTML 标题栏发起原生窗口拖动 */
	supportsWindowDrag: boolean
	/** 能否按伴侣模型交互范围做点击空透 */
	supportsHitThrough: boolean
	/** 能否置顶窗口 */
	supportsTopmost: boolean
	/** 系统托盘是否可用 */
	supportsTray: boolean
}

/** 遥测状态 (不包含 DSN 或任何用户身份) */
export interface TelemetryState {
	consent: "unset" | "granted" | "denied"
	/** 只有 granted 才为 true; unset 时 UI 可视觉开启但远程 SDK 必须关闭 */
	enabled: boolean
	available: boolean
}

/** 敏感配置问题摘要 (只含键名和分类) */
export interface SecretIssueDto {
	key: string
	category: string
	requiresUserAction: boolean
}

/** 聊天 Provider 配置状态 (秘密已脱敏) */
export interface AiChatState {
	configured: boolean
	provider: string
	baseUrl: string
	model: string
	persona: string
	hasApiKey: boolean
}

/** AI 大脑状态 (包含扁平兼容字段与统一 chat/embedding 嵌套结构) */
export interface AiState extends AiChatState {
	chat?: AiChatState
	embedding?: EmbeddingState
}

/** Provider 连接测试结果 (不包含密钥或请求正文) */
export interface ProviderConnectionTestResult {
	success: boolean
	provider: string
	latencyMs: number
	category: string
	message: string
}

/** Embedding 连接测试参数；dimensions 兼容数字和旧版字符串。 */
export interface EmbeddingConnectionTestArgs {
	baseUrl?: string
	apiKey?: string
	model?: string
	dimensions?: string | number
}

/** 双 Provider 连接测试参数 (settings_test_ai) */
export interface SettingsTestAiArgs extends EmbeddingConnectionTestArgs {
	provider?: string
	embedding?: EmbeddingConnectionTestArgs
}

/** 双 Provider 连接测试返回值 (settings_test_ai) */
export interface SettingsTestAiResult {
	llm: ProviderConnectionTestResult
	embedding: ProviderConnectionTestResult
}

/** 模型目录条目 */
export interface ModelItem {
	id: string
	installed: boolean
}

/** 模型目录状态 */
export interface ModelsState {
	selected: string
	items: ModelItem[]
	loadError?: string | null
	scale: number
	expressions: string[]
}

/** Live2D 行为开关 */
export interface BehaviorsState {
	clickInteraction: boolean
	clickThrough?: boolean
	autoBlink: boolean
	eyeTracking: boolean
	idleEyeAnimation: boolean
	idleAnimation: boolean
	expressionEnabled: boolean
	lipSync: boolean
	shadow: boolean
	beatSync: boolean
	renderScale: number
	maxFps: number
	aiInteraction: boolean
}

/** 语音配置 (秘密已脱敏) */
export interface VoiceState {
	volume: number
	ttsProvider: string
	ttsBaseUrl: string
	ttsModel: string
	hasTtsApiKey: boolean
	ttsVoice: string
	ttsSpeed: number
	ttsAutoPlay: boolean
	gptsovitsBaseUrl: string
	gptsovitsRefAudio: string
	gptsovitsPromptText: string
	gptsovitsPromptLang: string
	indexttsTemplateAudio: string
	indexttsEmoAlpha: number
	sttProvider: string
	sttBaseUrl: string
	hasSttApiKey: boolean
	noticePending: boolean
	speaking: boolean
}

/** Embedding 配置 (秘密已脱敏) */
export interface EmbeddingState {
	configured: boolean
	model: string
	baseUrl: string
	dimensions: string
	hasApiKey: boolean
}

/** 长期记忆设置快照 */
export interface MemorySettings {
	enabled: boolean
	reflectionEnabled: boolean
	reflectionRounds: number
	reflectionMinChars: number
	recallTopK: number
	keywordTopK: number
	vectorTopK: number
	rrfK: number
	minSimilarity: number
	decayEnabled: boolean
	archiveEnabled: boolean
	sourceRetentionThreshold: number
	archiveThreshold: number
	knowledgeEnabled: boolean
	knowledgeWatch: boolean
	debugRetrieval: boolean
}

/** 知识/向量索引状态 */
export interface MemoryIndexStatus {
	state: "ready" | "checking" | "rebuilding" | "partial" | "failed"
	processed: number
	total: number
	lastError?: string
	lastMaintenanceAt?: string
	lastReflectionAt?: string
}

/** 记忆快照 */
export interface MemoryState {
	enabled: boolean
	reflectionEnabled: boolean
	decayEnabled: boolean
	archiveEnabled: boolean
	active: number
	atoms: number
	archived: number
	total: number
	knowledgePath: string
	knowledgeChunks: number
	indexState: MemoryIndexStatus["state"]
	indexProcessed: number
	indexTotal: number
	lastError?: string
	lastReflection?: string
	lastMaintenance?: string
	ftsAvailable: boolean
	reflectionRounds: number
	reflectionMinChars: number
	recallTopK: number
	keywordTopK: number
	vectorTopK: number
	rrfK: number
	minSimilarity: number
	sourceRetentionThreshold: number
	archiveThreshold: number
	knowledgeEnabled: boolean
	knowledgeWatch: boolean
	debugRetrieval: boolean
}

/** 记忆事实原子 */
export interface MemoryAtom {
	id: number
	parentMemoryId: number
	atomType: string
	content: string
	importance: number
	confidence: number
	status: string
	createdAt: string
	lastAccessedAt?: string
	lastReinforcedAt?: string
	ttlDays?: number
	expiresAt?: string
	reinforcementCount: number
	decayType: string
	entities?: string
	supersededBy?: number
}

/** 记忆来源消息 */
export interface MemorySource {
	id: number
	memoryId: number
	role: string
	content: string
	messageTime?: string
	sequence: number
}

/** 提醒事项 */
export interface ReminderDto {
	id: string
	content: string
	triggerTime: number
	/** 是否每日重复 (后端旧兼容字段/只读) */
	repeatDaily?: boolean
	/** 领取状态: pending、claimed 或 fired */
	status?: "pending" | "claimed" | "fired" | string
	/** 重复规则使用的时区 */
	timezone?: string
	/** JSON 格式的重复规则 */
	recurrenceJson?: string | null
	/** 推迟到期时间 (Unix 毫秒) */
	snoozedUntil?: number | null
}

/** Bridge reminder 命令返回的完整记录状态。 */
export type ReminderStatus = "pending" | "claimed" | "fired" | "completed" | "cancelled"

/** Bridge reminder 命令返回的完整提醒记录 (对应 ReminderItem)。 */
export interface ReminderItemDto {
	id: string
	content: string
	triggerAt: number
	repeatDaily: boolean
	createdAt: string
	status: ReminderStatus
	timezone: string
	recurrenceJson?: string | null
	snoozedUntil?: number | null
	claimedAt?: string | null
	firedAt?: string | null
	updatedAt: string
}

/** reminder_update 的参数；triggerTime/triggerAt 与 delayMinutes 互斥。 */
export interface ReminderUpdateArgs {
	id: string
	content?: string
	triggerTime?: number
	triggerAt?: number
	delayMinutes?: number
	repeatDaily?: boolean
	timezone?: string
	recurrenceJson?: string | null
}

/** reminder_snooze 的参数；支持相对分钟和绝对 Unix 毫秒时间。 */
export interface ReminderSnoozeArgs {
	id: string
	delayMinutes?: number
	snoozedUntil?: number
	snoozeUntil?: number
}

/** 主动交互状态 */
export interface ProactiveState {
	idleEnabled: boolean
	idleMinutes: number
	dailyGreeting: boolean
	reminders: ReminderDto[]
}

/** 技能条目 (指令正文按需 skills_export 获取) */
export interface SkillDto {
	id: string
	name: string
	description: string
	author: string
	version: string
	icon: string
	tags: string[]
	category: string
	instructions: string
	enabled: boolean
	source: "builtin" | "market" | "custom" | "url"
}

/** skills_marketplace 返回的市场目录条目 (不含 installedAt/enabled)。 */
export interface SkillMarketplaceDto extends Omit<SkillDto, "enabled"> {
	tools?: string[] | null
}

/** 保存或导入技能时宿主接受的技能数据。 */
export interface SkillRecordInput {
	id: string
	name?: string
	description?: string
	author?: string
	version?: string
	icon?: string
	tags?: string[]
	category?: string
	instructions?: string
	tools?: string[] | null
	enabled?: boolean
	source?: string
	installedAt?: number
	url?: string | null
}

/** 保存或导入技能命令返回的完整记录。 */
export interface SkillRecordDto extends SkillDto {
	tools?: string[] | null
	installedAt: number
	url?: string | null
}

/** 工具条目 */
export interface ToolDto {
	name: string
	description: string
	permissionLevel: "safe" | "confirm" | "dangerous"
	category: "builtin" | "mcp" | "custom"
	enabled: boolean
}

/** 自动化单项能力状态 */
export interface AutomationCapabilityDto {
	/** 能力标识 (如 desktop / browser / vision) */
	id: string
	/** 能力名称 */
	name: string
	/** 是否可用 */
	available: boolean
	/** 不可用原因 (未接入/缺失依赖/权限未开启) */
	unavailableReason?: string | null
}

/** 浏览器结构化动作条目 (脱敏与受控定义) */
export interface BrowserActionDto {
	type: string
	description?: string
	targetKind?: string
	[key: string]: unknown
}

/** 浏览器任务受限结果 (脱敏, 不含截图、完整页面源码或敏感输入) */
export interface BrowserTaskResultDto {
	taskId: string
	success: boolean
	summary?: string
	data?: unknown
	error?: string | null
	finishedAt?: string
}

/** 浏览器会话生命周期状态 (对应 AutomationBrowserStatusSnapshot) */
export type AutomationBrowserState = "stopped" | "starting" | "running" | "failed"

/** 浏览器生命周期命令返回值 (包含计算属性 running) */
export interface AutomationBrowserStatusDto {
	state: AutomationBrowserState
	enabled: boolean
	available: boolean
	unavailableReason?: string | null
	running: boolean
}

/** 自动化任务生命周期状态 (对应后端 AutomationTaskState 枚举) */
export type AutomationTaskLifecycleState = "queued" | "running" | "paused" | "completed" | "cancelled" | "failed"

/** 自动化审批动作名称 (对应后端 AutomationActionKind 枚举) */
export type AutomationActionKindDto = "click" | "typeText" | "keyPress" | "scroll"

/** 浏览器或桌面任务启动后返回的脱敏状态 (对应 AutomationTaskStatusSnapshot) */
export interface AutomationTaskStatusDto {
	id: string
	state: AutomationTaskLifecycleState
	step: number
	progressCategory: string
	errorCategory?: string | null
	taskKind: "browser" | "desktop"
	pauseReason?: string | null
	currentStep: number
	totalSteps?: number | null
	hasResult: boolean
	resultSummary?: string | null
	actionKinds: AutomationActionKindDto[]
	approvalRequestId?: string | null
}

/** 浏览器结构化任务启动结果 (对应 AutomationBrowserTaskStartSnapshot) */
export interface AutomationBrowserTaskStartDto {
	taskId: string
	state: AutomationTaskLifecycleState
}

/** 桌面视觉可选窗口的脱敏信息 (对应 AutomationDesktopWindowSnapshot) */
export interface AutomationDesktopWindowDto {
	token: string
	width: number
	height: number
	isForeground: boolean
}

/** 桌面视觉任务参数；task/targetToken 是当前字段，另两组是兼容别名。 */
export type AutomationDesktopTaskArgs =
	| {task: string; targetToken: string}
	| {task: string; windowToken: string}
	| {goal: string; targetToken: string}
	| {goal: string; windowToken: string}

/** 桌面视觉任务启动结果 (对应 AutomationDesktopTaskStartSnapshot) */
export interface AutomationDesktopTaskStartDto {
	taskId: string
	status: AutomationTaskStatusDto
}

/** 自动化设置命令返回值 (对应 AutomationSettingsSnapshot) */
export interface AutomationSettingsDto {
	enabled: boolean
	allowPointer: boolean
	allowKeyboard: boolean
	allowScroll: boolean
	browserEnabled: boolean
}

/** 自动化审计日志条目 (脱敏, 仅包含时间、类型、动作分类与结果) */
export interface AutomationAuditRecordDto {
	id: string
	taskId?: string
	timestamp: string
	taskKind: "browser" | "desktop" | string
	actionCategory: string
	outcome: "succeeded" | "failed" | "cancelled" | "rejected" | string
	failureReason?: string | null
}

/** 自动化任务生命周期状态 */
export type AutomationTaskStatus =
	| "queued"
	| "running"
	| "awaiting_approval"
	| "paused"
	| "succeeded"
	| "completed"
	| "failed"
	| "cancelled"

/** 自动化任务只读状态快照 (脱敏，不含截图、提示词、URL、窗口名或输入参数) */
export interface AutomationTaskDto {
	/** 任务标识 */
	id: string
	/** 脱敏短标题 */
	title?: string
	/** 任务类型 (如 browser / desktop / custom) */
	taskKind?: "browser" | "desktop" | string
	/** 生命周期状态 */
	state: AutomationTaskStatus | string
	/** 暂停原因 (如 safe_page / sensitive_action / user_paused) */
	pauseReason?: "safe_page" | "sensitive_action" | "user_paused" | string | null
	/** 创建时间 (ISO 字符串或格式化时间) */
	createdAt?: string
	/** 开始执行时间 */
	startedAt?: string | null
	/** 结束时间 */
	finishedAt?: string | null
	/** 稳定错误分类标识 (如 timeout / permission_denied) */
	failureCode?: string | null
	/** 当前步骤序号 (从 1 起) */
	currentStep?: number
	/** 总步骤数 */
	totalSteps?: number
	/** 进度百分比 (0-100 或 0-1) */
	progress?: number
	/** 待审批动作类型列表 */
	actionKinds?: string[]
	/** 关联的审批请求标识 (若处于待审批) */
	approvalRequestId?: string
	/** 是否有受限结果可获取 */
	hasResult?: boolean
	/** 简明脱敏结果摘要 */
	resultSummary?: string | null
	/** 受限结果对象 (可选) */
	result?: BrowserTaskResultDto | unknown
}

/** 脱敏审批请求 */
export interface AutomationApprovalDto {
	/** 审批请求标识 */
	requestId: string
	/** 关联任务标识 */
	taskId: string
	/** 涉及的动作分类列表 */
	actionKinds: string[]
	/** 请求时间 */
	requestedAt: string
}

/** 视觉能力检测结果 (对应 AutomationVisionProbeSnapshot) */
export interface VisionProbeResult {
	available: boolean
	reason?: string | null
}

/** 自动化状态快照 */
export interface AutomationState {
	/** 自动化总开关 */
	enabled: boolean
	/** 桌面自动化操作 (鼠标/键盘/窗口交互) */
	desktopEnabled: boolean
	/** 浏览器自动化操作 (DOM/页面交互) */
	browserEnabled: boolean
	/** 视觉能力是否就绪 */
	visionReady: boolean
	/** 各项能力就绪状态列表 */
	capabilities?: AutomationCapabilityDto[]
	/** 不可用原因 (若整个自动化不可用) */
	unavailableReason?: string | null
	/** 当前正在执行或处于活动态的任务 */
	activeTask?: AutomationTaskDto | null
	/** 任务列表 (可选，包含历史或活动任务) */
	tasks?: AutomationTaskDto[]
	/** 排队中任务数量 */
	queuedCount?: number
	/** 待处理的审批请求 (可选) */
	pendingApproval?: AutomationApprovalDto | null
	/** 待处理的审批请求列表 (可选) */
	pendingApprovals?: AutomationApprovalDto[]
	/** 整体执行能力是否就绪 */
	available?: boolean
}

/** 情绪状态 */
export interface EmotionDto {
	type: string
}

/** UI 状态快照 */
export interface UiSnapshot {
	version: number
	app: AppInfo
	general: GeneralState
	telemetry: TelemetryState
	secretIssues: SecretIssueDto[]
	ai: AiState
	models: ModelsState
	pet: PetState
	platform: PlatformState
	behaviors: BehaviorsState
	voice: VoiceState
	embedding: EmbeddingState
	memory: MemoryState
	proactive: ProactiveState
	skills: SkillDto[]
	enabledSkillsCount: number
	tools: ToolDto[]
	mcpServersCount?: number
	emotion: EmotionDto
	/** 自动化能力快照 (可选, 后端未就绪时为 undefined) */
	automation?: AutomationState
	/** 自动更新状态快照 (可选) */
	updater?: UpdaterStatusDto | null
}

// ===================================================================
// Agent 事件
// ===================================================================

/** Agent 运行状态 (与后端 AgentRunState 对齐) */
export type AgentState =
	| "idle"
	| "thinking"
	| "streaming"
	| "tool_executing"
	| "waiting_approval"
	| "speaking"
	| "error"

/** LLM 用量指标 */
export interface UsageMetrics {
	promptTokens: number
	completionTokens: number
	totalTokens: number
	cachedTokens: number
	cacheHitRate: number
	durationMs: number
	model?: string
}

/** 工具授权请求 */
export interface ApprovalRequestDto {
	type: "approval-request"
	sessionId: string | null
	requestId: string
	toolName: string
	arguments?: Record<string, unknown>
	description?: string
	permissionLevel: "confirm" | "dangerous"
	category?: string
}

/** Agent 事件联合载荷 */
export type AgentEventPayload =
	| {type: "state"; sessionId?: string | null; state: AgentState}
	| {type: "chunk"; sessionId: string; chunk: string}
	| {type: "tool-executing"; sessionId: string; toolName: string; arguments?: Record<string, unknown>}
	| {type: "tool-executed"; sessionId: string; toolName: string; result?: unknown; success: boolean; error?: string}
	| ({type: "usage"; sessionId: string} & UsageMetrics)
	| {type: "complete"; sessionId: string; message: {text: string; emotion?: string; expression?: string; action?: string}}
	| {type: "cancelled"; sessionId: string}
	| {type: "error"; sessionId: string; error: string}
	| ApprovalRequestDto
	| {type: "approval-result"; sessionId: string; requestId: string; approved: boolean; reason: string}

// ===================================================================
// 聊天历史 (服务端已规范化)
// ===================================================================

/** 聊天历史消息 */
export interface HistoryMessage {
	id: number
	role: "user" | "assistant"
	content: string
	createdAt: string
}

// ===================================================================
// 领域对象
// ===================================================================

/** memory_add 的参数。 */
export interface MemoryAddArgs {
	content: string
	type?: string
	importance?: number
	tags?: string
	kind?: string
}

/** memory_update 的参数。 */
export interface MemoryUpdateArgs {
	id: number
	content: string
	importance?: number
	tags?: string
	kind?: string
	canonicalSummary?: string
	personaSummary?: string
	confidence?: number
}

/** memory_atom_list 的参数。 */
export interface MemoryAtomListArgs {
	memoryId?: number
	status?: string
	limit?: number
	offset?: number
}

/** 记忆条目 */
export interface MemoryItem {
	id: number
	type: string
	content: string
	importance: number
	source: string
	tags?: string
	embedding?: string
	createdAt: string
	updatedAt: string
	kind?: string
	canonicalSummary?: string
	personaSummary?: string
	confidence?: number
	status?: string
	accessCount?: number
	reinforcementCount?: number
	lastAccessedAt?: string
	lastReinforcedAt?: string
	ttlDays?: number
	expiresAt?: string
	supersededBy?: number
	embeddingFingerprint?: string
}

/** 记忆总览 */
export interface MemoryOverview {
	activeMemories: number
	atomCount: number
	archivedMemories: number
	totalMemories: number
	knowledgeChunks: number
	reflectionCursor?: string
	lastReflection?: string
	lastMaintenance?: string
	index: MemoryIndexStatus
	settings: MemorySettings
}

/** 分页记忆列表 */
export interface MemoryListPage {
	items: MemoryItem[]
	total: number
}

/** Recall Debugger 结果 */
export interface MemoryRecallDebug {
	trace?: {
		query: string
		expandedQuery: string
		keywordHits: {memoryId: number; score: number; rank: number}[]
		vectorHits: {memoryId: number; score: number; rank: number}[]
		atomHits: {memoryId: number; score: number; rank: number}[]
		rrfHits: {memoryId: number; score: number; rank: number}[]
		filteredIds: number[]
		injectedIds: number[]
	}
	personal: MemoryItem[]
	atoms: MemoryAtom[]
	knowledge: {id: number; heading: string; subheading?: string; content: string; awareness: string; knowledgeType?: string; score: number}[]
	echoes: {content: string; score: number}[]
}

/** 记忆导出结果 (脱敏, 不包含原始向量/聊天上下文/工具参数) */
export interface MemoryExportResult {
	fileName?: string
	version?: string | number
	totalCount: number
	activeCount?: number
	archivedCount?: number
	atomCount?: number
	sanitizedFields: string[]
	exportedAt?: string
	content?: string
}

/** 导入预览条目 (仅摘要与属性, 不展示或保存内部原始向量/对话/工具) */
export interface MemoryImportPreviewItem {
	id?: number
	contentSummary: string
	kind?: string
	importance?: number
	confidence?: number
	status?: string
	tags?: string
	conflictType?: "none" | "duplicate" | "conflict"
	conflictReason?: string
}

/** 导入预览结果 */
export interface MemoryImportPreviewResult {
	valid: boolean
	totalCount: number
	newCount: number
	duplicateCount: number
	conflictCount: number
	errorCount: number
	errors?: string[]
	items?: MemoryImportPreviewItem[]
	previewToken?: string
	sanitizedNotice?: string
}

/** 导入提交条目 */
export interface MemoryImportCommitItem {
	id?: number
	content: string
	kind?: string
	importance?: number
	confidence?: number
	tags?: string
	action?: "create" | "update" | "skip"
}

/** 导入冲突解决策略 */
export type MemoryImportConflictStrategy = "skip" | "overwrite" | "create_copy"

/** 导入提交结果 */
export interface MemoryImportCommitResult {
	success: boolean
	importedCount: number
	updatedCount: number
	skippedCount: number
	errorCount?: number
	message?: string
}

/** MCP 服务器状态 */
export interface McpServerStatusInfo {
	serverId: string
	name: string
	status: "disconnected" | "connecting" | "connected" | "error"
	errorMessage?: string
	hasEnvironment?: boolean
	secretIssue?: string
	tools: {
		name: string
		description?: string
		inputSchema?: Record<string, unknown>
	}[]
	resources?: unknown[]
}

/** MCP 服务器保存与测试命令的参数。 */
export interface McpServerConfigArgs {
	id: string
	name: string
	transport?: "stdio" | "sse"
	command?: string | null
	args?: string[] | null
	env?: Record<string, string> | null
	url?: string | null
	enabled?: boolean
	autoConnect?: boolean
}

/** MCP 工具列表命令返回的带命名空间条目。 */
export interface McpToolListItem {
	serverId: string
	serverName: string
	toolName: string
	fullName: string
	description: string
	inputSchema: Record<string, unknown>
}

/** MCP 工具执行结果内容项。 */
export interface McpContentItemDto {
	type: string
	text?: string | null
	data?: string | null
	mimeType?: string | null
}

/** MCP 工具调用命令返回值。 */
export interface McpToolResultDto {
	content: McpContentItemDto[]
	isError: boolean
}

/** 交互反应模式: 本地动作 / AI 大脑响应 */
export type InteractionReactionMode = "local" | "ai"

/** 交互动作触发模式: 无 / 随机 / 指定 */
export type InteractionActionMode = "none" | "random" | "selected"

/** 交互动作定义 (动作或表情) */
export interface InteractionAction {
	mode: InteractionActionMode
	group?: string
	name?: string
}

/** 归一化矩形区域 (0~1, y 向下) */
export interface InteractionRect {
	x: number
	y: number
	width: number
	height: number
}

/** 自定义矩形交互区域 */
export interface InteractionRegion {
	id: string
	name: string
	reactionMode: InteractionReactionMode
	rect: InteractionRect
	motion: InteractionAction
	expression: InteractionAction
}

/** 交互区域配置 */
export interface InteractionConfig {
	version: 1
	regions: InteractionRegion[]
}

/** 模型元数据 (model_get_meta) */
export interface ModelMeta {
	modelId: string
	scale: number
	opacity: number
	shadow: boolean
	renderScale: number
	qualityMode: "adaptive" | "quality" | "eco"
	maxFps: number
	expressions: string[]
	motions: {group: string; names: string[]}[]
	interactions: InteractionConfig
}
