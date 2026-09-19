import {beforeEach, describe, expect, it, vi} from "vitest"
import {BindWindowBackdrop} from "../../src/services/theme/windowBackdrop"
import {RUNTIME} from "../../src/services/runtime"

vi.mock("../../src/services/runtime", () => ({RUNTIME: {
	onWindowBackdrop: vi.fn(),
	windowBackdropState: vi.fn(),
}}))

const flush = async () => {
	await Promise.resolve()
	await Promise.resolve()
}

describe("窗口材质同步", () => {
	let handler: (state: {active: boolean}) => void
	let style: CSSStyleDeclaration
	const STOP = vi.fn()

	beforeEach(() => {
		vi.clearAllMocks()
		style = document.createElement("div").style
		vi.mocked(RUNTIME.onWindowBackdrop).mockImplementation(async (callback) => {
			handler = callback
			return STOP
		})
		vi.mocked(RUNTIME.windowBackdropState).mockResolvedValue({active: false})
	})

	it("默认实底，只有实际模糊状态才能启用半透明", async () => {
		const DISPOSE = BindWindowBackdrop(style)
		expect(style.getPropertyValue("--window-background")).toBe("var(--bg-base)")
		await flush()
		handler({active: true})
		expect(style.getPropertyValue("--window-background")).toBe("var(--bg-glass)")
		handler({active: false})
		expect(style.getPropertyValue("--window-background")).toBe("var(--bg-base)")
		DISPOSE()
		expect(STOP).toHaveBeenCalledOnce()
		expect(style.getPropertyValue("--window-background")).toBe("")
	})

	it("初始查询不能覆盖已到达的更新事件", async () => {
		let resolveState!: (state: {active: boolean}) => void
		vi.mocked(RUNTIME.windowBackdropState).mockReturnValue(new Promise(resolve => { resolveState = resolve }))
		const DISPOSE = BindWindowBackdrop(style)
		await flush()
		handler({active: false})
		resolveState({active: true})
		await flush()
		expect(style.getPropertyValue("--window-background")).toBe("var(--bg-base)")
		DISPOSE()
	})

	it("查询失败维持实底，后续通知仍可恢复", async () => {
		vi.mocked(RUNTIME.windowBackdropState).mockRejectedValue(new Error("宿主尚未就绪"))
		const DISPOSE = BindWindowBackdrop(style)
		await flush()
		expect(style.getPropertyValue("--window-background")).toBe("var(--bg-base)")
		handler({active: true})
		expect(style.getPropertyValue("--window-background")).toBe("var(--bg-glass)")
		DISPOSE()
	})

	it("异步订阅完成前卸载会释放监听且不再写入样式", async () => {
		let resolveListener!: (stop: () => void) => void
		vi.mocked(RUNTIME.onWindowBackdrop).mockReturnValue(new Promise(resolve => { resolveListener = resolve }))
		const DISPOSE = BindWindowBackdrop(style)
		DISPOSE()
		resolveListener(STOP)
		await flush()
		expect(STOP).toHaveBeenCalledOnce()
		expect(RUNTIME.windowBackdropState).not.toHaveBeenCalled()
		expect(style.getPropertyValue("--window-background")).toBe("")
	})
})
