import {beforeEach, describe, expect, it, vi} from "vitest"

const HOST_INVOKE = vi.hoisted(() => vi.fn())
vi.mock("../../src/services/host/invoke", () => ({invoke: HOST_INVOKE}))

import {RUNTIME} from "../../src/services/runtime"

describe("原生对话窗口入口", () => {
	beforeEach(() => {
		HOST_INVOKE.mockReset()
	})

	it("请求宿主打开对话窗口", async () => {
		await RUNTIME.openChat()
		expect(HOST_INVOKE).toHaveBeenCalledWith("window_open_chat")
	})

	it("保留宿主的实际授权期限与清空结果", async () => {
		const DEADLINE = {deadlineUtc: "2026-09-13T00:01:00Z"}
		HOST_INVOKE.mockResolvedValueOnce(DEADLINE)
		await expect(RUNTIME.extendApproval("request-1")).resolves.toEqual(DEADLINE)
		expect(HOST_INVOKE).toHaveBeenLastCalledWith("approval_extend", {requestId: "request-1"})
		const CLEAR_RESULT = {remoteReset: false, note: null}
		HOST_INVOKE.mockResolvedValueOnce(CLEAR_RESULT)
		await expect(RUNTIME.clearChat()).resolves.toEqual(CLEAR_RESULT)
		expect(HOST_INVOKE).toHaveBeenLastCalledWith("chat_clear")
	})

	it("宿主失败传递给调用方显示反馈", async () => {
		HOST_INVOKE.mockRejectedValue(new Error("应用窗口尚未就绪"))
		await expect(RUNTIME.openChat()).rejects.toThrow("应用窗口尚未就绪")
	})
})
