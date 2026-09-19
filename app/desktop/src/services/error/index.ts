import type {App} from "vue"
import {ReportLogError} from "../runtime/logging"
import {CaptureError} from "../telemetry"

const INSTALLED = new WeakSet<App>()
let windowInstalled = false

/** 安装全局兜底；本地日志只记录固定事件，遥测继续走独立授权与脱敏边界。 */
export const installErrorHandlers = (app: App): void => {
	if (INSTALLED.has(app)) return
	INSTALLED.add(app)
	app.config.errorHandler = (error) => {
		CaptureError(error, "vue.error")
		void ReportLogError("vue.error", error)
	}
	if (windowInstalled) return
	windowInstalled = true
	window.addEventListener("error", (event) => {
		const TARGET = event.target
		if (TARGET instanceof HTMLScriptElement || TARGET instanceof HTMLLinkElement || TARGET instanceof HTMLImageElement) {
			void ReportLogError("resource.error")
			return
		}
		const ERROR = event.error instanceof Error ? event.error : new Error("window error")
		CaptureError(ERROR, "window.error")
		void ReportLogError("window.error", ERROR)
	}, true)
	window.addEventListener("unhandledrejection", (event) => {
		CaptureError(event.reason, "promise.rejection")
		void ReportLogError("promise.rejection", event.reason)
	})
}
