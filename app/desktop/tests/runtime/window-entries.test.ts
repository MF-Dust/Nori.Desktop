import {beforeEach, describe, expect, it, vi} from "vitest"
import type {MemoryPage} from "../../src/services/runtime/types"

const HOST_INVOKE = vi.hoisted(() => vi.fn())
vi.mock("../../src/services/host/invoke", () => ({invoke: HOST_INVOKE}))

import {RUNTIME} from "../../src/services/runtime"

describe("原生窗口运行时入口", () => {
	beforeEach(() => {
		HOST_INVOKE.mockReset()
	})

	it("打开模型和对话窗口时调用对应宿主命令", async () => {
		await RUNTIME.openModels()
		await RUNTIME.openChat()

		expect(HOST_INVOKE).toHaveBeenNthCalledWith(1, "window_open_models")
		expect(HOST_INVOKE).toHaveBeenNthCalledWith(2, "window_open_chat")
	})

	it.each<MemoryPage | undefined>([undefined, "overview", "memories", "atoms", "knowledge", "archive", "transfer", "debugger", "advanced"])("记忆窗口支持直达 %s", async page => {
		await RUNTIME.openMemory(page)
		expect(HOST_INVOKE).toHaveBeenCalledWith("window_open_memory", page === undefined ? undefined : {page})
	})

	it("对话窗口保留授权期限与清空结果", async () => {
		const DEADLINE = {deadlineUtc: "2026-09-13T00:01:00Z"}
		HOST_INVOKE.mockResolvedValueOnce(DEADLINE)
		await expect(RUNTIME.extendApproval("request-1")).resolves.toEqual(DEADLINE)
		expect(HOST_INVOKE).toHaveBeenLastCalledWith("approval_extend", {requestId: "request-1"})

		const CLEAR_RESULT = {remoteReset: false, note: null}
		HOST_INVOKE.mockResolvedValueOnce(CLEAR_RESULT)
		await expect(RUNTIME.clearChat()).resolves.toEqual(CLEAR_RESULT)
		expect(HOST_INVOKE).toHaveBeenLastCalledWith("chat_clear")
	})

	it("宿主失败传递给调用方", async () => {
		HOST_INVOKE.mockRejectedValue(new Error("应用窗口尚未就绪"))
		await expect(RUNTIME.openModels()).rejects.toThrow("应用窗口尚未就绪")
	})
})
