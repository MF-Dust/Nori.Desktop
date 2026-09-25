import {afterEach, beforeEach, describe, expect, it, vi} from "vitest"

const STATE = vi.hoisted(() => ({
	label: "audio-host" as string | null,
	install: vi.fn(),
	uninstall: vi.fn(),
}))
vi.mock("../../src/services/host", () => ({host: () => ({label: STATE.label})}))
vi.mock("../../src/services/audio", () => ({installAudioHost: STATE.install, uninstallAudioHost: STATE.uninstall}))

beforeEach(() => {
	vi.resetModules()
	vi.clearAllMocks()
	STATE.install.mockResolvedValue(undefined)
})
afterEach(() => window.dispatchEvent(new Event("pagehide")))

describe("专用音频宿主引导", () => {
	it("仅装配音频协议", async () => {
		STATE.label = "audio-host"
		await import("../../src/bootstrap")
		expect(STATE.install).toHaveBeenCalledOnce()
		window.dispatchEvent(new Event("pagehide"))
		expect(STATE.uninstall).toHaveBeenCalledOnce()
	})

	it("其它页面不装配音频协议", async () => {
		STATE.label = "main"
		await import("../../src/bootstrap")
		expect(STATE.install).not.toHaveBeenCalled()
	})
})
