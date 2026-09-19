import {invoke} from "../host/invoke"

export type LogLevel = "trace" | "debug" | "info" | "warn" | "error" | "fatal"
export type LogEventId = "vue.error" | "window.error" | "promise.rejection" | "resource.error" | "feedback.error" | "audio.error" | "app.initialization_failed" | "logging.suppressed" | "app.mounted" | "app.initialized" | "pet.shown" | "pet.hidden" | "clipboard.copied" | "diagnostics.test" | "client.event"
export interface RuntimeLogEntry {
	time: string
	level: LogLevel
	source: "backend" | "frontend"
	message: string
	timestamp: string
	sessionId: string
	sequence: number
	category: string | null
	eventId: string | null
	windowLabel: string | null
	operationId: string | null
	exceptionType: string | null
	exceptionSite: string | null
}
export interface LoggingStatus {
	minimumLevel: LogLevel
	droppedCount: number
	writeFailureCount: number
	lastError: string | null
	stopped: boolean
}

/** 日志只传递稳定事件与已知错误类型，不接受异常正文或任意对象。 */
export const WriteLogEvent = (level: LogLevel, eventId: LogEventId, errorType?: string, suppressedCount?: number): Promise<void> =>
	invoke("write_log", {level, eventId, message: "", errorType, ...(suppressedCount === undefined ? {} : {suppressedCount})})

export const GetLoggingStatus = (): Promise<LoggingStatus> => invoke("get_logging_status")
export const SetLoggingLevel = (level: LogLevel): Promise<void> => invoke("set_logging_level", {level})

const COUNTS = new Map<string, {start: number; count: number}>()
const ERROR_NAMES = new Set(["Error", "TypeError", "RangeError", "ReferenceError", "SyntaxError", "URIError", "EvalError", "AggregateError"])

/** 每分钟最多转发五次同类错误，额外记录一次限流事件；上报失败不递归。 */
export const ReportLogError = async (eventId: LogEventId, error?: unknown): Promise<void> => {
	const TYPE = error instanceof Error && ERROR_NAMES.has(error.name) ? error.name : "Error"
	const KEY = `${eventId}:${TYPE}`
	const NOW = Date.now()
	let state = COUNTS.get(KEY)
	const PREVIOUS_SUPPRESSED = state && NOW - state.start >= 60_000 ? Math.max(0, state.count - 5) : 0
	if (!state || NOW - state.start >= 60_000) {
		state = {start: NOW, count: 0}
		if (COUNTS.size >= 256) COUNTS.delete(COUNTS.keys().next().value as string)
		COUNTS.set(KEY, state)
	}
	state.count++
	if (state.count > 6) return
	try {
		if (PREVIOUS_SUPPRESSED > 0) await WriteLogEvent("warn", "logging.suppressed", TYPE, PREVIOUS_SUPPRESSED)
		await WriteLogEvent(state.count === 6 ? "warn" : "error", state.count === 6 ? "logging.suppressed" : eventId, TYPE, state.count === 6 ? 1 : undefined)
	} catch {
		console.error("日志转发失败", eventId, TYPE)
	}
}
