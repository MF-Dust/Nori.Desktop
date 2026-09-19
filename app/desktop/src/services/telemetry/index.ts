import type {ErrorEvent, Event, SpanJSON, TransactionEvent} from "@sentry/core"
import type {App} from "vue"
import type {Router} from "vue-router"
import {host} from "../host"
import type {TelemetryState, UiSnapshot} from "../runtime"
import {NormalizeOperation, REPLAY_OPTIONS, ShouldEnableTelemetry, TELEMETRY_SAMPLING} from "./policy"

const WEB_DSN = import.meta.env.VITE_SENTRY_DSN_WEB ?? ""
const RELEASE = import.meta.env.VITE_SENTRY_RELEASE ?? ""
const ENVIRONMENT = import.meta.env.VITE_SENTRY_ENVIRONMENT || "production"
const REPLAY_INTEGRATIONS = new Set(["GlobalHandlers", "BrowserApiErrors", "Breadcrumbs", "HttpContext", "BrowserTracing"])
const RECENT_ERRORS = new Map<string, number>()
const SEEN_ERROR_OBJECTS = new WeakSet<object>()
const MAX_RECENT_ERRORS = 128
const RECENT_ERROR_TTL_MS = 1000
const SCRUBBED_URL = "app://webview"

type SentryModule = typeof import("@sentry/vue")

// 绝不能静态 import。只有 consent=granted 且存在 DSN 时才加载 Web Sentry chunk。
let SENTRY: SentryModule | null = null
let active = false
let initialized = false
let consentEnabled = false
let activeKey = ""
let currentWindow = "unknown"
let currentPlatform = "unknown"
let replay: ReplayController | undefined
let syncQueue = Promise.resolve()

type ReplayController = {
	startBuffering: () => void
	stop: (options?: {flush?: boolean}) => Promise<void>
}

type RecordValue = Record<string, unknown>

const isRecord = (value: unknown): value is RecordValue => typeof value === "object" && value !== null

/**
 * 当前 WebView 的安全标签。
 *
 * label 只来自宿主注入的固定窗口名, 不把 URL、查询参数或业务数据带进标签。
 */
const safeReplayId = (value: unknown): string | undefined =>
	typeof value === "string" && /^[A-Za-z0-9_-]{1,64}$/.test(value) ? value : undefined

const safeTags = (replayId?: unknown) => ({
	runtime: "webview",
	window: currentWindow,
	platform: currentPlatform,
	...(safeReplayId(replayId) ? {replayId: safeReplayId(replayId)} : {}),
})

const SAFE_WINDOWS = new Set(["main", "first-run", "init", "pet"])

const scrubStackPath = (value: unknown): string => {
	if (typeof value !== "string") return SCRUBBED_URL
	const PATH = value.trim()
	if (!PATH) return PATH
	if (/^(?:file|blob|data|https?|webpack|vite|capacitor):/i.test(PATH) || /^[A-Za-z]:[\\/]/.test(PATH) || /^\\\\|^\//.test(PATH)) {
		return SCRUBBED_URL
	}
	return PATH.replace(/[?#].*$/, "").slice(0, 256)
}

const scrubFrame = (frame: {filename?: string; abs_path?: string; module?: string; vars?: unknown; pre_context?: unknown; post_context?: unknown}): void => {
	if (frame.filename !== undefined) frame.filename = scrubStackPath(frame.filename)
	if (frame.abs_path !== undefined) frame.abs_path = scrubStackPath(frame.abs_path)
	if (frame.module?.includes("://") || /^[A-Za-z]:[\\/]/.test(frame.module ?? "")) frame.module = SCRUBBED_URL
	delete frame.vars
	delete frame.pre_context
	delete frame.post_context
}

const REPLAY_URL_KEY = /^(?:url|urls|uri|href|src|initialurl|initial_url|abs_path|filename|code_file)$/i
const REPLAY_URL_VALUE = /^(?:[A-Za-z][A-Za-z\d+.-]*:\/\/|(?:file|blob|data):)/i

const scrubReplayValue = (value: unknown, key = ""): unknown => {
	if (Array.isArray(value)) {
		if (REPLAY_URL_KEY.test(key)) return value.map(() => SCRUBBED_URL)
		return value.map(item => scrubReplayValue(item, key))
	}
	if (!isRecord(value)) {
		if (typeof value === "string" && (REPLAY_URL_VALUE.test(value) || (/[?#]/.test(value) && /^(?:name|description|previous|from|to)$/i.test(key)))) {
			return SCRUBBED_URL
		}
		return value
	}
	return Object.fromEntries(Object.entries(value).map(([CHILD_KEY, CHILD_VALUE]) => [
		CHILD_KEY,
		REPLAY_URL_KEY.test(CHILD_KEY)
			? Array.isArray(CHILD_VALUE) ? CHILD_VALUE.map(() => SCRUBBED_URL) : SCRUBBED_URL
			: scrubReplayValue(CHILD_VALUE, CHILD_KEY),
	])) as RecordValue
}

/** Replay event processor: URLs and paths are fixed, replay IDs stay linkable. */
export const scrubReplayEvent = (event: Event): Event => {
	if (event.type !== "replay_event") return event
	const RESULT = scrubReplayValue(event) as Event & RecordValue
	const REPLAY_ID = safeReplayId(RESULT.replay_id)
	if (REPLAY_ID) RESULT.replay_id = REPLAY_ID
	return RESULT
}

/** Replay custom recording events can contain navigation/network span URLs. */
export const scrubReplayRecordingEvent = (event: unknown): unknown => scrubReplayValue(event)

/** 把错误压缩成稳定 key, 只用于本地去重, 不会发送给 Sentry。 */
const errorKey = (error: unknown): string => {
	const KEY = error instanceof Error ? error.name : typeof error
	return KEY.slice(0, 80)
}

/** 前端事件最后一道脱敏边界。 */
export const scrubEvent = (event: ErrorEvent): ErrorEvent | null => {
	if (!consentEnabled) return null
	event.user = undefined
	event.request = undefined
	event.extra = undefined
	event.logentry = undefined
	event.breadcrumbs = undefined
	event.contexts = undefined
	const replayId = event.tags?.replayId
	event.tags = replayId ? {...safeTags(), replayId} : safeTags()
	if (event.exception?.values) {
		for (const exception of event.exception.values) {
			exception.type = "Error"
			exception.value = "Error"
			for (const frame of exception.stacktrace?.frames ?? []) scrubFrame(frame)
		}
	}
	const images = (event.debug_meta as {images?: unknown} | undefined)?.images
	if (Array.isArray(images)) {
		for (const image of images) {
			if (isRecord(image) && image.code_file !== undefined) image.code_file = scrubStackPath(image.code_file)
		}
	}
	if (event.message) event.message = event.exception ? undefined : "web_error"
	if (event.transaction) event.transaction = NormalizeOperation(event.transaction)
	if (event.spans) {
		event.spans = event.spans.map((span: SpanJSON) => ({
			...span,
			description: "web.span",
			data: {},
		}))
	}
	return event
}

/** 事务只允许携带路由/固定操作名, 不记录请求 URL 或参数。 */
export const scrubTransaction = (event: TransactionEvent): TransactionEvent | null => {
	if (!consentEnabled) return null
	event.user = undefined
	event.request = undefined
	event.extra = undefined
	event.logentry = undefined
	event.breadcrumbs = undefined
	event.contexts = undefined
	event.tags = safeTags(event.tags?.replayId)
	event.transaction = NormalizeOperation(event.transaction ?? "web_transaction")
	if (event.spans) {
		event.spans = event.spans.map((span: SpanJSON) => ({
			...span,
			description: "web.span",
			data: {},
		}))
	}
	return event
}

/** 初始化 Web SDK; Replay 作为可选集成单独控制。 */
const InitializeWebSentry = (SDK: SentryModule, app: App, router: Router, includeReplay: boolean): ReplayController | undefined => {
	SDK.init({
		app,
		enabled: false,
		attachErrorHandler: false,
		attachProps: false,
		dsn: WEB_DSN,
		release: RELEASE || undefined,
		environment: ENVIRONMENT,
		integrations: integrations => {
			const RESULT = integrations
				.filter(integration => !REPLAY_INTEGRATIONS.has(integration.name))
				.concat(SDK.browserTracingIntegration({
					router,
					traceFetch: false,
					traceXHR: false,
					instrumentPageLoad: true,
					instrumentNavigation: true,
				}))
			if (includeReplay) {
				RESULT.push(SDK.replayIntegration({
					...REPLAY_OPTIONS,
					networkCaptureBodies: false,
					beforeAddRecordingEvent: event => scrubReplayRecordingEvent(event) as typeof event,
				}))
			}
			return RESULT
		},
		tracesSampleRate: TELEMETRY_SAMPLING.traces,
		replaysSessionSampleRate: TELEMETRY_SAMPLING.replaysSession,
		replaysOnErrorSampleRate: TELEMETRY_SAMPLING.replaysOnError,
		tracePropagationTargets: [],
		beforeSend: scrubEvent,
		beforeSendTransaction: scrubTransaction,
		beforeBreadcrumb: () => null,
	})
	const CLIENT = SDK.getClient()
	CLIENT?.addEventProcessor(scrubReplayEvent)
	return includeReplay ? CLIENT?.getIntegrationByName("Replay") as unknown as ReplayController | undefined : undefined
}

const loadWebSentry = async (): Promise<SentryModule> => {
	if (!SENTRY) SENTRY = await import("@sentry/vue")
	return SENTRY
}

const disableWebTelemetry = async (): Promise<void> => {
	consentEnabled = false
	try {
		await replay?.stop({flush: false})
	} catch {
		// Replay cleanup is best effort; the transport is disabled below regardless.
	}
	const CLIENT = SENTRY?.getClient()
	if (CLIENT) CLIENT.getOptions().enabled = false
	active = false
	activeKey = ""
}

const enableWebTelemetry = (): boolean => {
	const CLIENT = SENTRY?.getClient()
	if (!CLIENT) {
		active = false
		activeKey = ""
		return false
	}
	CLIENT.getOptions().enabled = true
	try {
		// Sampled sessions remain active; stopped/unsampled sessions resume in buffer mode.
		replay?.startBuffering()
	} catch {
		replay = undefined
	}
	consentEnabled = true
	active = true
	return true
}

const applyWebTelemetry = async (app: App, router: Router, snapshot: UiSnapshot | null): Promise<void> => {
	const telemetry: TelemetryState | undefined = snapshot?.telemetry
	const shouldEnable = ShouldEnableTelemetry(WEB_DSN, telemetry)
	const RAW_WINDOW = host()?.label || "unknown"
	const NEXT_WINDOW = SAFE_WINDOWS.has(RAW_WINDOW) ? RAW_WINDOW : "unknown"
	const RAW_PLATFORM = snapshot?.app.platform || "unknown"
	const NEXT_PLATFORM = RAW_PLATFORM === "windows" || RAW_PLATFORM === "macos" || RAW_PLATFORM === "linux" ? RAW_PLATFORM : "unknown"
	const NEXT_KEY = `${NEXT_WINDOW}:${NEXT_PLATFORM}:${RELEASE}:${ENVIRONMENT}`
	currentWindow = NEXT_WINDOW
	currentPlatform = NEXT_PLATFORM

	if (!shouldEnable) {
		if (initialized) await disableWebTelemetry()
		return
	}
	if (initialized) {
		if (!active) enableWebTelemetry()
		if (!active || !SENTRY) return
		if (activeKey !== NEXT_KEY) {
			SENTRY.setTags(safeTags())
			activeKey = NEXT_KEY
		}
		return
	}

	try {
		const SDK = await loadWebSentry()
		replay = InitializeWebSentry(SDK, app, router, NEXT_WINDOW === "main")
	} catch {
		// Keep error/performance telemetry if Replay cannot initialize in a WebView.
		try {
			const SDK = await loadWebSentry()
			replay = InitializeWebSentry(SDK, app, router, false)
		} catch {
			replay = undefined
		}
	}
	initialized = Boolean(SENTRY?.getClient())
	if (!initialized || !enableWebTelemetry() || !SENTRY) {
		consentEnabled = false
		return
	}
	consentEnabled = true
	SENTRY.setTags(safeTags())
	activeKey = NEXT_KEY
}

/**
 * 根据宿主快照启停 Web SDK。
 *
 * 没有快照、没有 DSN 或 consent 未明确 granted 时都不加载 transport。
 */
export const SyncWebTelemetry = async (app: App, router: Router, snapshot: UiSnapshot | null): Promise<void> => {
	const RUN = syncQueue.then(() => applyWebTelemetry(app, router, snapshot))
	syncQueue = RUN.catch(() => undefined)
	await RUN
}

/** 手动捕获前端异常; 全局错误处理器和 Vue handler 共用此入口。 */
export const CaptureError = (error: unknown, operation: string): void => {
	if (!active || !consentEnabled || !SENTRY) return
	if (typeof error === "object" && error !== null) {
		if (SEEN_ERROR_OBJECTS.has(error)) return
		SEEN_ERROR_OBJECTS.add(error)
	}
	const KEY = errorKey(error)
	const NOW = Date.now()
	for (const [key, timestamp] of RECENT_ERRORS) {
		if (NOW - timestamp >= RECENT_ERROR_TTL_MS) RECENT_ERRORS.delete(key)
	}
	const LAST = RECENT_ERRORS.get(KEY) ?? 0
	if (NOW - LAST < RECENT_ERROR_TTL_MS) return
	while (RECENT_ERRORS.size >= MAX_RECENT_ERRORS) {
		const FIRST = RECENT_ERRORS.keys().next().value
		if (typeof FIRST !== "string") break
		RECENT_ERRORS.delete(FIRST)
	}
	RECENT_ERRORS.set(KEY, NOW)
	try {
		SENTRY.withScope(scope => {
			scope.setTags(safeTags())
			scope.setTag("operation", NormalizeOperation(operation))
			SENTRY?.captureException(error)
		})
	} catch {
		// 错误上报自身失败不能触发第二条错误链路。
	}
}

/** 供测试和调试页确认当前 Web SDK 是否已启用。 */
export const IsWebTelemetryEnabled = (): boolean => active
