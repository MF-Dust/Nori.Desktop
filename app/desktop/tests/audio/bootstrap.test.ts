import {afterEach, beforeEach, describe, expect, it, vi} from "vitest"

const STATE = vi.hoisted(() => ({
	label: "audio-host" as string | null,
	install: vi.fn(),
	uninstall: vi.fn(),
	main: vi.fn(),
}))
vi.mock("../../src/services/host", () => ({host: () => ({label: STATE.label})}))
vi.mock("../../src/services/audio", () => ({installAudioHost: STATE.install, uninstallAudioHost: STATE.uninstall}))
vi.mock("../../src/main", () => { STATE.main(); return {} })

beforeEach(() => {
	vi.resetModules()
	vi.clearAllMocks()
	STATE.install.mockResolvedValue(undefined)
})
afterEach(() => window.dispatchEvent(new Event("pagehide")))

describe("专用音频宿主引导", () => {
	it("仅装配音频协议，不加载 Vue 主界面", async () => {
		STATE.label = "audio-host"
		await import("../../src/bootstrap")
		expect(STATE.install).toHaveBeenCalledOnce()
		expect(STATE.main).not.toHaveBeenCalled()
		window.dispatchEvent(new Event("pagehide"))
		expect(STATE.uninstall).toHaveBeenCalledOnce()
	})

	it("普通页面仍使用原有 Vue 入口", async () => {
		STATE.label = "main"
		await import("../../src/bootstrap")
		expect(STATE.main).toHaveBeenCalledOnce()
		expect(STATE.install).not.toHaveBeenCalled()
	})
})
