import {beforeEach, describe, expect, it, vi} from "vitest"

const HOST_INVOKE = vi.hoisted(() => vi.fn())
vi.mock("../../src/services/host/invoke", () => ({invoke: HOST_INVOKE}))

import {RUNTIME} from "../../src/services/runtime"

describe("原生模型窗口入口", () => {
	beforeEach(() => {
		HOST_INVOKE.mockReset()
	})

	it("由宿主打开或恢复独立模型窗口", async () => {
		await RUNTIME.openModels()
		expect(HOST_INVOKE).toHaveBeenCalledWith("window_open_models")
	})

	it("宿主失败传递给调用方显示反馈", async () => {
		HOST_INVOKE.mockRejectedValue(new Error("应用窗口尚未就绪"))
		await expect(RUNTIME.openModels()).rejects.toThrow("应用窗口尚未就绪")
	})
})
