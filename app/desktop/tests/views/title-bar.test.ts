import {afterEach, beforeEach, describe, expect, it, vi} from "vitest"
import {createApp, h, nextTick, type App} from "vue"
import TitleBar from "../../src/components/TitleBar.vue"
import {RUNTIME} from "../../src/services/runtime"
import {i18n} from "../../src/services/i18n"
import ZH from "../../src/services/i18n/locales/zh-CN"
import {feedback} from "../../src/services/feedback"

vi.mock("../../src/services/runtime", () => ({RUNTIME: {
	platform: () => ({supportsWindowDrag: true}),
	onWindowState: vi.fn(), windowState: vi.fn(), minimizeWindow: vi.fn(), toggleWindowMaximized: vi.fn(),
}}))
vi.mock("../../src/services/feedback", () => ({feedback: {error: vi.fn()}}))
const DRAG = vi.hoisted(() => vi.fn().mockResolvedValue(undefined))
vi.mock("../../src/services/host/window", () => ({getCurrentWindow: () => ({startDragging: DRAG})}))

describe("红黄绿窗口控件", () => {
	let app: App | undefined
	let container: HTMLDivElement
	let handler: (state: {maximized: boolean; canResize: boolean}) => void
	const STOP = vi.fn()
	const CLOSE = vi.fn()
	const settle = async () => { await Promise.resolve(); await Promise.resolve(); await nextTick() }
	const mount = async () => {
		container = document.createElement("div")
		document.body.appendChild(container)
		app = createApp({render: () => h(TitleBar, {showClose: true, onClose: CLOSE})})
		app.use(i18n)
		app.mount(container)
		await settle()
		return [...container.querySelectorAll("button")]
	}

	beforeEach(() => {
		vi.clearAllMocks()
		i18n.global.setLocaleMessage("zh-CN", ZH)
		i18n.global.locale.value = "zh-CN"
		vi.mocked(RUNTIME.onWindowState).mockImplementation(async callback => { handler = callback; return STOP })
		vi.mocked(RUNTIME.windowState).mockResolvedValue({canResize: true, maximized: false})
		vi.mocked(RUNTIME.minimizeWindow).mockResolvedValue(undefined)
		vi.mocked(RUNTIME.toggleWindowMaximized).mockResolvedValue({canResize: true, maximized: true})
	})
	afterEach(() => { app?.unmount(); app = undefined; container?.remove() })

	it("三个按钮按关闭、最小化、最大化排列并调用各自操作", async () => {
		const BUTTONS = await mount()
		expect(BUTTONS).toHaveLength(3)
		for (const button of BUTTONS) button.click()
		await settle()
		expect(CLOSE).toHaveBeenCalledOnce()
		expect(RUNTIME.minimizeWindow).toHaveBeenCalledOnce()
		expect(RUNTIME.toggleWindowMaximized).toHaveBeenCalledOnce()
		expect(BUTTONS[2].getAttribute("aria-label")).toMatch(/还原|Restore/)
	})

	it("固定尺寸窗口禁用绿色按钮，系统状态事件更新还原标签", async () => {
		vi.mocked(RUNTIME.windowState).mockResolvedValue({canResize: false, maximized: false})
		const BUTTONS = await mount()
		expect(BUTTONS[2].disabled).toBe(true)
		BUTTONS[2].click()
		expect(RUNTIME.toggleWindowMaximized).not.toHaveBeenCalled()
		handler({canResize: true, maximized: true})
		await settle()
		expect(BUTTONS[2].disabled).toBe(false)
		expect(BUTTONS[2].getAttribute("aria-label")).toMatch(/还原|Restore/)
	})

	it("空白处支持拖动和双击，控制按钮不触发窗口拖动", async () => {
		const BUTTONS = await mount()
		const HEADER = container.firstElementChild!
		BUTTONS[0].dispatchEvent(new MouseEvent("mousedown", {bubbles: true, button: 0}))
		BUTTONS[0].dispatchEvent(new MouseEvent("dblclick", {bubbles: true, button: 0}))
		expect(DRAG).not.toHaveBeenCalled()
		expect(RUNTIME.toggleWindowMaximized).not.toHaveBeenCalled()
		HEADER.dispatchEvent(new MouseEvent("mousedown", {bubbles: true, button: 0}))
		HEADER.dispatchEvent(new MouseEvent("dblclick", {bubbles: true, button: 0}))
		await settle()
		expect(DRAG).toHaveBeenCalledOnce()
		expect(RUNTIME.toggleWindowMaximized).toHaveBeenCalledOnce()
	})

	it("操作失败可见，卸载后解除状态订阅", async () => {
		vi.mocked(RUNTIME.minimizeWindow).mockRejectedValue(new Error("测试失败"))
		const BUTTONS = await mount()
		BUTTONS[1].click()
		await settle()
		expect(feedback.error).toHaveBeenCalledOnce()
		app!.unmount(); app = undefined
		expect(STOP).toHaveBeenCalledOnce()
	})
})
