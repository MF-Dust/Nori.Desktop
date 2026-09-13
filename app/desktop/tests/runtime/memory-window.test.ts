import {beforeEach, describe, expect, it, vi} from "vitest"

const HOST_INVOKE = vi.hoisted(() => vi.fn())
vi.mock("../../src/services/host/invoke", () => ({invoke: HOST_INVOKE}))

import {RUNTIME} from "../../src/services/runtime"
import type {MemoryPage} from "../../src/services/runtime/types"

describe("原生记忆窗口入口", () => {
	beforeEach(() => {
		HOST_INVOKE.mockReset()
	})

	it("未指定页面时保留宿主当前页面", async () => {
		await RUNTIME.openMemory()
		expect(HOST_INVOKE).toHaveBeenCalledWith("window_open_memory", undefined)
	})

	it.each<MemoryPage>(["overview", "memories", "atoms", "knowledge", "archive", "transfer", "debugger", "advanced"])("支持直达 %s", async page => {
		await RUNTIME.openMemory(page)
		expect(HOST_INVOKE).toHaveBeenCalledWith("window_open_memory", {page})
	})

	it("宿主失败传递给调用方显示反馈", async () => {
		HOST_INVOKE.mockRejectedValue(new Error("应用窗口尚未就绪"))
		await expect(RUNTIME.openMemory()).rejects.toThrow("应用窗口尚未就绪")
	})
})
