import {beforeEach, afterEach, describe, expect, it, vi} from "vitest"

const INVOKE = vi.hoisted(() => vi.fn().mockResolvedValue(undefined))
vi.mock("../../src/services/host/invoke", () => ({invoke: INVOKE}))

describe("结构化前端日志", () => {
	beforeEach(() => { vi.resetModules(); INVOKE.mockReset().mockResolvedValue(undefined); vi.useFakeTimers() })
	afterEach(() => { vi.useRealTimers(); vi.restoreAllMocks() })

	it("异常正文和自定义错误名不进入桥接参数", async () => {
		const {ReportLogError} = await import("../../src/services/runtime/logging")
		const ERROR = new Error("聊天正文 api_key=secret /home/private")
		ERROR.name = "user_content"
		await ReportLogError("window.error", ERROR)
		expect(INVOKE).toHaveBeenCalledWith("write_log", {level: "error", eventId: "window.error", message: "", errorType: "Error"})
		expect(JSON.stringify(INVOKE.mock.calls)).not.toContain("secret")
	})

	it("限流有界且一分钟后恢复", async () => {
		const {ReportLogError} = await import("../../src/services/runtime/logging")
		for (let index = 0; index < 50; index++) await ReportLogError("vue.error", new TypeError("不记录"))
		expect(INVOKE).toHaveBeenCalledTimes(6)
		expect(INVOKE.mock.calls[5]?.[1].eventId).toBe("logging.suppressed")
		vi.advanceTimersByTime(60_000)
		await ReportLogError("vue.error", new TypeError("不记录"))
		expect(INVOKE).toHaveBeenCalledTimes(8)
		expect(INVOKE.mock.calls[6]?.[1].suppressedCount).toBe(45)
		expect(INVOKE.mock.calls[7]?.[1].eventId).toBe("vue.error")
	})

	it("转发失败不会递归或在控制台泄露原文", async () => {
		INVOKE.mockRejectedValue(new Error("服务器原文"))
		const CONSOLE = vi.spyOn(console, "error").mockImplementation(() => {})
		const {ReportLogError} = await import("../../src/services/runtime/logging")
		await ReportLogError("feedback.error", new Error("用户正文"))
		expect(INVOKE).toHaveBeenCalledTimes(1)
		expect(CONSOLE).toHaveBeenCalledWith("日志转发失败", "feedback.error", "Error")
	})
})
