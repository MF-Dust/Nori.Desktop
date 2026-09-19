/**
 * 全局错误捕获
 *
 * 参考 ClassIsland 的全局兜底思路: Vue errorHandler + window error/unhandledrejection,
 * 统一转发到宿主 write_log 落盘, 让前端错误在 DevTools 之外也可追溯。
 * 复用现有 write_log 桥接命令, 不改桥接协议。
 */
import type {App} from "vue"
import {invoke} from "../host/invoke"
import {CaptureError} from "../telemetry"

/** 同一首行消息最多转发的次数, 防止渲染循环类错误刷爆日志文件 */
const MAX_REPEAT = 5

const REPEAT_COUNTS = new Map<string, number>()

/**
 * 中央错误脱敏边界。
 *
 * 错误消息和堆栈可能包含聊天内容、请求正文、令牌或本机路径; 日志只记录类型,
 * 具体错误仍通过界面反馈给用户, 不把原文送入宿主日志。
 */
export const RedactErrorText = (value: string): string => value
	// Codacy 误报：字符类扫描是线性的，结果随后截断至1000字符。
	// eslint-disable-next-line -- Codacy误报：字符类匹配无灾难性回溯
	.replace(/https?:\/\/[^\s/@:]+(?::[^\s/@]*)?@/gi, "https://[redacted]@")
	.replace(/([?&](?:api[_-]?key|authorization|bearer|token|password|secret|cookie)=)[^&#\s]*/gi, "$1[redacted]")
	.replace(/((?:api[_-]?key|authorization|bearer|token|password|secret|cookie)\s*[:=]\s*)[^\s,;]+/gi, "$1[redacted]")
	.replace(/(?:[A-Za-z]:\\|\/Users\/|\/home\/|\/tmp\/|\/var\/folders\/)[^\r\n\s]+/gi, "[path]")
	.slice(0, 1000)

/** 只返回稳定的错误类型, 不保留异常正文或堆栈。 */
const formatError = (error: unknown): string => {
	if (error instanceof Error) return `${error.name}: [redacted]`
	return "UnknownError: [redacted]"
}

/**
 * 构造只有稳定 name 的合成 Error (不含原始正文)。
 *
 * window.onerror 收到的非 Error 值 (字符串 throw、宿主包装对象) 借此获得可分组
 * 的错误名, 不把原始值上传遥测。
 */
const createSyntheticError = (name: string): Error => {
	const SAFE_NAME = /^[A-Za-z][A-Za-z0-9]{0,63}$/.test(name) ? name : "WindowError"
	const error = new Error("window error")
	error.name = SAFE_NAME
	return error
}

/**
 * 转发到宿主日志; 同一首行消息限流, 超出后静默丢弃
 */
const forward = async (message: string): Promise<void> => {
	const SAFE_MESSAGE = RedactErrorText(message)
	const KEY = SAFE_MESSAGE.split("\n")[0] ?? SAFE_MESSAGE
	const COUNT = (REPEAT_COUNTS.get(KEY) ?? 0) + 1
	REPEAT_COUNTS.set(KEY, COUNT)
	if (COUNT === MAX_REPEAT + 1) console.warn(`[error] 相同错误已达上限, 后续不再转发: ${RedactErrorText(KEY)}`)
	if (REPEAT_COUNTS.size > 256) REPEAT_COUNTS.delete(REPEAT_COUNTS.keys().next().value as string)
	if (COUNT > MAX_REPEAT) return
	try {
		await invoke("write_log", {level: "error", message: SAFE_MESSAGE})
	} catch (failure) {
		// 上报自身失败只走控制台, 绝不递归触发捕获
		console.error("[error] 错误上报失败:", RedactErrorText(formatError(failure)))
		console.error(SAFE_MESSAGE)
	}
}

/**
 * 安装全局错误捕获, 在 APP.mount 之前调用
 */
export const installErrorHandlers = (app: App): void => {
	// Vue 组件内的渲染/生命周期/事件处理器异常
	app.config.errorHandler = (error, _instance, info) => {
		CaptureError(error, "vue.error")
		void forward(`[vue:${info}] ${formatError(error)}`)
	}
	// errorHandler 覆盖不到的同步脚本错误与资源加载失败
	window.addEventListener("error", (event) => {
		const TARGET = event.target
		// Codacy 误报：RESOURCE是元素类型判断结果，不是HTML字符串。
		// eslint-disable-next-line -- Codacy误报：仅保存HTML元素类型判断布尔值
		const RESOURCE = TARGET instanceof HTMLScriptElement || TARGET instanceof HTMLLinkElement || TARGET instanceof HTMLImageElement
		if (RESOURCE) {
			void forward(`[resource.error:${TARGET.tagName.toLowerCase()}] ${TARGET instanceof HTMLLinkElement ? "link" : TARGET instanceof HTMLImageElement ? "img" : "script"}`)
			return
		}
		// 保留稳定的 error.name 供遥测分组, 避免所有非 Error 值都归成同一类 "Error"。
		const ERROR_NAME = event.error instanceof Error && event.error.name ? event.error.name : "WindowError"
		const ERROR = event.error instanceof Error ? event.error : createSyntheticError(ERROR_NAME)
		CaptureError(ERROR, "window.error")
		const DETAIL = event.error instanceof Error ? `\n${formatError(event.error)}` : ""
		void forward(`[window.onerror:${ERROR_NAME}] ${RedactErrorText(event.message || "window error")}${DETAIL}`)
	})
	// 未处理的 Promise 拒绝
	window.addEventListener("unhandledrejection", (event) => {
		CaptureError(event.reason, "promise.rejection")
		void forward(`[unhandledrejection] ${formatError(event.reason)}`)
	})
}
