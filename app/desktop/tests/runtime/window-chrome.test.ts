import {beforeEach, describe, expect, it, vi} from "vitest"
import type {WindowChromeState} from "../../src/services/runtime/types"

const HOST = vi.hoisted(() => ({invoke: vi.fn(), listen: vi.fn()}))
vi.mock("../../src/services/host/invoke", () => ({invoke: HOST.invoke}))
vi.mock("../../src/services/host/event", () => ({listen: HOST.listen}))

import {RUNTIME} from "../../src/services/runtime"

describe("当前窗口标题栏命令", () => {
	beforeEach(() => {
		HOST.invoke.mockReset()
		HOST.listen.mockReset()
	})

	it("最小化不传入窗口标签", async () => {
		HOST.invoke.mockResolvedValue(undefined)
		await RUNTIME.minimizeWindow()
		expect(HOST.invoke).toHaveBeenCalledWith("window_minimize")
	})

	it("查询与最大化返回宿主实际状态", async () => {
		const STATE: WindowChromeState = {maximized: true, canResize: true}
		HOST.invoke.mockResolvedValue(STATE)
		expect(await RUNTIME.windowState()).toEqual(STATE)
		expect(HOST.invoke).toHaveBeenLastCalledWith("window_get_state")
		expect(await RUNTIME.toggleWindowMaximized()).toEqual(STATE)
		expect(HOST.invoke).toHaveBeenLastCalledWith("window_toggle_maximized")
	})

	it("状态事件回传宿主payload并提供退订函数", async () => {
		const STATE: WindowChromeState = {maximized: false, canResize: false}
		const STOP = vi.fn()
		const HANDLER = vi.fn()
		HOST.listen.mockImplementation(async (_event, callback) => {
			callback({payload: STATE})
			return STOP
		})
		const UNLISTEN = await RUNTIME.onWindowState(HANDLER)
		expect(HOST.listen).toHaveBeenCalledWith("nori:window-state", expect.any(Function))
		expect(HANDLER).toHaveBeenCalledWith(STATE)
		UNLISTEN()
		expect(STOP).toHaveBeenCalledOnce()
	})
})
