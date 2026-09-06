import {beforeEach, describe, expect, it, vi} from "vitest"

const CURRENT = vi.hoisted(() => ({label: "main"}))
const SHOW = vi.hoisted(() => vi.fn())
const FOCUS = vi.hoisted(() => vi.fn())
const HIDE = vi.hoisted(() => vi.fn())
const CLOSE = vi.hoisted(() => vi.fn())

vi.mock("../../src/services/host/window", () => ({
	getCurrentWindow: () => ({label: CURRENT.label, close: CLOSE}),
	getWindowByLabel: (label: string) => ({label, show: SHOW, setFocus: FOCUS, hide: HIDE, close: CLOSE}),
}))

import {showWindow} from "../../src/services/window"

describe("窗口调度", () => {
	beforeEach(() => {
		CURRENT.label = "main"
		SHOW.mockReset().mockResolvedValue(undefined)
		FOCUS.mockReset().mockResolvedValue(undefined)
		HIDE.mockReset().mockResolvedValue(undefined)
		CLOSE.mockReset().mockResolvedValue(undefined)
	})

	it("跨窗口显示伴侣时不请求未授权的焦点操作", async () => {
		await showWindow("pet")

		expect(SHOW).toHaveBeenCalledOnce()
		expect(FOCUS).not.toHaveBeenCalled()
	})

	it("显示当前窗口时仍会恢复焦点", async () => {
		await showWindow("main")

		expect(SHOW).toHaveBeenCalledOnce()
		expect(FOCUS).toHaveBeenCalledOnce()
	})
})
