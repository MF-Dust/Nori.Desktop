import {beforeEach, describe, expect, it, vi} from "vitest"

const HOST_INVOKE = vi.hoisted(() => vi.fn())

vi.mock("../../src/services/host/invoke", () => ({invoke: HOST_INVOKE}))

import {RUNTIME} from "../../src/services/runtime"

describe("自动更新运行时接口", () => {
	beforeEach(() => {
		HOST_INVOKE.mockReset()
	})

	it("checkUpdate 调用 updater_check 桥接命令", async () => {
		const mockResult = {
			available: true,
			currentVersion: "1.0.0",
			latestVersion: "1.1.0",
			releaseTag: "v1.1.0",
			releaseNotes: "更新说明",
			publishedAt: "2025-05-18T12:00:00Z",
		}
		HOST_INVOKE.mockResolvedValue(mockResult)

		const result = await RUNTIME.checkUpdate()

		expect(HOST_INVOKE).toHaveBeenCalledWith("updater_check")
		expect(result).toEqual(mockResult)
	})

	it("installUpdate 调用 updater_install 桥接命令", async () => {
		const mockResult = {
			success: true,
			slotName: "app-1.1.0-0",
			productVersion: "1.1.0",
		}
		HOST_INVOKE.mockResolvedValue(mockResult)

		const result = await RUNTIME.installUpdate()

		expect(HOST_INVOKE).toHaveBeenCalledWith("updater_install")
		expect(result).toEqual(mockResult)
	})

	it("cancelUpdate 调用 updater_cancel 桥接命令", async () => {
		HOST_INVOKE.mockResolvedValue(true)

		const result = await RUNTIME.cancelUpdate()

		expect(HOST_INVOKE).toHaveBeenCalledWith("updater_cancel")
		expect(result).toBe(true)
	})

	it("restartApp 调用 updater_restart 桥接命令", async () => {
		HOST_INVOKE.mockResolvedValue(undefined)

		await RUNTIME.restartApp()

		expect(HOST_INVOKE).toHaveBeenCalledWith("updater_restart")
	})

	it("updateGeneral 包含 autoCheckUpdates 参数", async () => {
		HOST_INVOKE.mockResolvedValue(undefined)

		await RUNTIME.updateGeneral({autoCheckUpdates: false})

		expect(HOST_INVOKE).toHaveBeenCalledWith("settings_update_general", {autoCheckUpdates: false})
	})

	it("openSettings 调用原生桥接窗口命令", async () => {
		HOST_INVOKE.mockResolvedValue(undefined)

		await RUNTIME.openSettings("plugins")

		expect(HOST_INVOKE).toHaveBeenCalledWith("window_open_settings", {page: "plugins"})
	})

	it("openSettings 未指定页面时传递 undefined", async () => {
		HOST_INVOKE.mockResolvedValue(undefined)

		await RUNTIME.openSettings()

		expect(HOST_INVOKE).toHaveBeenCalledWith("window_open_settings", undefined)
	})
})
