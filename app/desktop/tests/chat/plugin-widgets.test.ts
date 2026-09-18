import {afterEach, beforeEach, describe, expect, it, vi} from "vitest"
import {createApp, h, nextTick} from "vue"
import PluginWidgets from "../../src/components/chat/PluginWidgets.vue"

const {invokeMock, errorMock} = vi.hoisted(() => ({
	invokeMock: vi.fn(),
	errorMock: vi.fn(),
}))

vi.mock("../../src/services/host/invoke", () => ({
	invoke: invokeMock,
}))

vi.mock("../../src/services/feedback", () => ({
	feedback: {error: errorMock},
}))

describe("PluginWidgets.vue", () => {
	const mounts: Array<{app: ReturnType<typeof createApp>; container: HTMLDivElement}> = []

	beforeEach(() => {
		vi.useFakeTimers()
		invokeMock.mockReset()
		errorMock.mockReset()
	})

	afterEach(() => {
		for (const mount of mounts) {
			mount.app.unmount()
			mount.container.remove()
		}
		mounts.length = 0
		vi.useRealTimers()
	})

	const WIDGETS = [
		{pluginId: "plugin-a", title: "卡片 A", entry: "http://localhost:5173/a/card.html"},
		{pluginId: "plugin-b", title: "卡片 B", entry: "http://localhost:5173/b/card.html"},
	]

	async function mountWidgets(): Promise<HTMLDivElement> {
		invokeMock.mockResolvedValueOnce({widgets: WIDGETS})
		const container = document.createElement("div")
		document.body.appendChild(container)
		const app = createApp({render: () => h(PluginWidgets)})
		app.mount(container)
		mounts.push({app, container})
		await nextTick()
		await Promise.resolve()
		await nextTick()
		return container
	}

	async function send(source: Window | null, data: unknown, origin = "null"): Promise<void> {
		window.dispatchEvent(new MessageEvent("message", {source, origin, data}))
		await Promise.resolve()
		await Promise.resolve()
	}

	function action(extra: Record<string, unknown> = {}): Record<string, unknown> {
		return {source: "nori-plugin-widget", requestId: 1, actionId: "refresh", args: {}, ...extra}
	}

	it("隔离 iframe 并使用实际窗口所属身份处理 SDK 动作", async () => {
		const container = await mountWidgets()
		const frames = container.querySelectorAll("iframe")
		expect(frames[0].getAttribute("sandbox")).toBe("allow-scripts")
		const reply = vi.spyOn(frames[0].contentWindow!, "postMessage")
		const otherReply = vi.spyOn(frames[1].contentWindow!, "postMessage")
		invokeMock.mockResolvedValueOnce({ok: true})
		await send(frames[0].contentWindow, action({pluginId: "plugin-a"}))
		expect(invokeMock).toHaveBeenLastCalledWith("plugin_action", {pluginId: "plugin-a", actionId: "refresh", args: {}})
		expect(reply).toHaveBeenCalledWith({source: "nori-plugin-widget-host", requestId: 1, result: {ok: true}, error: undefined}, "*")
		expect(otherReply).not.toHaveBeenCalled()
		invokeMock.mockResolvedValueOnce({ok: true})
		await send(frames[1].contentWindow, action({args: null}))
		expect(invokeMock).toHaveBeenLastCalledWith("plugin_action", {pluginId: "plugin-b", actionId: "refresh", args: undefined})
	})

	it("拒绝未登记窗口、父窗口、空来源和非 opaque origin", async () => {
		const container = await mountWidgets()
		const frame = container.querySelector("iframe")!
		const outsider = document.createElement("iframe")
		container.appendChild(outsider)
		const reply = vi.spyOn(frame.contentWindow!, "postMessage")
		await send(outsider.contentWindow, action({pluginId: "plugin-a"}))
		await send(window, action({pluginId: "plugin-a"}))
		await send(null, action())
		await send(frame.contentWindow, action(), "http://localhost:5173")
		expect(invokeMock).toHaveBeenCalledTimes(1)
		expect(reply).not.toHaveBeenCalled()
	})

	it("拒绝跨插件冒用并提供可读错误", async () => {
		const container = await mountWidgets()
		const frame = container.querySelector("iframe")!
		const reply = vi.spyOn(frame.contentWindow!, "postMessage")
		await send(frame.contentWindow, action({pluginId: "plugin-b"}))
		expect(invokeMock).toHaveBeenCalledTimes(1)
		expect(reply).toHaveBeenCalledWith(expect.objectContaining({error: "插件卡片不能调用其他插件的动作。"}), "*")
		expect(errorMock).toHaveBeenCalled()
	})

	it.each([
		{cmd: "get_config"}, {kind: "emit"}, {command: "window_show"}, {event: "nori:test"},
		{actionId: ""}, {actionId: "   "}, {actionId: "x".repeat(257)}, {args: []}, {args: "invalid"},
	])("拒绝非动作消息和畸形动作参数 %j", async extra => {
		const container = await mountWidgets()
		await send(container.querySelector("iframe")!.contentWindow, action(extra))
		expect(invokeMock).toHaveBeenCalledTimes(1)
		expect(errorMock).toHaveBeenCalled()
	})

	it.each([NaN, Infinity, -1, 1.5, Number.MAX_SAFE_INTEGER + 1, "1"])("忽略无效请求编号 %s", async requestId => {
		const container = await mountWidgets()
		await send(container.querySelector("iframe")!.contentWindow, action({requestId}))
		expect(invokeMock).toHaveBeenCalledTimes(1)
	})

	it("权限撤销时将宿主拒绝同时反馈给卡片和用户", async () => {
		const container = await mountWidgets()
		const frame = container.querySelector("iframe")!
		const reply = vi.spyOn(frame.contentWindow!, "postMessage")
		const error = new Error("插件权限已被撤销")
		invokeMock.mockRejectedValueOnce(error)
		await send(frame.contentWindow, action())
		expect(reply).toHaveBeenCalledWith(expect.objectContaining({result: null, error: error.message}), "*")
		expect(errorMock).toHaveBeenCalledWith("插件卡片动作执行失败", error)
	})

	it("折叠后拒绝旧窗口且丢弃旧动作回包", async () => {
		const container = await mountWidgets()
		const source = container.querySelector("iframe")!.contentWindow!
		const reply = vi.spyOn(source, "postMessage")
		let complete!: (value: Record<string, unknown>) => void
		invokeMock.mockReturnValueOnce(new Promise(resolve => { complete = resolve }))
		await send(source, action())
		container.querySelector("button")!.click()
		await nextTick()
		await send(source, action({requestId: 2}))
		complete({secret: "old-result"})
		await Promise.resolve()
		await Promise.resolve()
		expect(invokeMock).toHaveBeenCalledTimes(2)
		expect(reply).not.toHaveBeenCalled()
	})

	it("插件列表撤销与入口替换后旧窗口无权调用", async () => {
		const container = await mountWidgets()
		const oldSources = [...container.querySelectorAll("iframe")].map(frame => frame.contentWindow)
		invokeMock.mockResolvedValueOnce({widgets: [{...WIDGETS[0], entry: "http://localhost:5173/a/new.html"}]})
		await vi.advanceTimersByTimeAsync(10_000)
		await nextTick()
		for (const source of oldSources) await send(source, action())
		expect(invokeMock).toHaveBeenCalledTimes(2)
		expect(container.querySelectorAll("iframe")).toHaveLength(1)
		expect(container.querySelector("iframe")!.getAttribute("src")).toContain("new.html")
	})

	it("卸载期间完成的首次刷新不会重新注册监听或计时器", async () => {
		let complete!: (value: unknown) => void
		invokeMock.mockReturnValueOnce(new Promise(resolve => { complete = resolve }))
		const container = document.createElement("div")
		document.body.appendChild(container)
		const app = createApp({render: () => h(PluginWidgets)})
		app.mount(container)
		app.unmount()
		container.remove()
		complete({widgets: WIDGETS})
		await Promise.resolve()
		await nextTick()
		await vi.advanceTimersByTimeAsync(30_000)
		await send(window, action())
		expect(vi.getTimerCount()).toBe(0)
		expect(invokeMock).toHaveBeenCalledTimes(1)
	})

	it("组件卸载后丢弃正在执行的动作结果且移除消息监听", async () => {
		const container = await mountWidgets()
		const source = container.querySelector("iframe")!.contentWindow!
		const reply = vi.spyOn(source, "postMessage")
		let complete!: (value: Record<string, unknown>) => void
		invokeMock.mockReturnValueOnce(new Promise(resolve => { complete = resolve }))
		await send(source, action())
		const mounted = mounts.pop()!
		mounted.app.unmount()
		mounted.container.remove()
		complete({ok: true})
		await Promise.resolve()
		await Promise.resolve()
		await send(source, action({requestId: 2}))
		expect(reply).not.toHaveBeenCalled()
		expect(invokeMock).toHaveBeenCalledTimes(2)
		expect(vi.getTimerCount()).toBe(0)
	})

	it("starts with no widgets, polls every 10s, and defaults newly appeared widgets to expanded", async () => {
		// 初始调用: 无部件
		invokeMock.mockResolvedValueOnce({widgets: []})

		const container = document.createElement("div")
		document.body.appendChild(container)
		const app = createApp({
			render: () => h(PluginWidgets),
		})
		app.mount(container)
		mounts.push({app, container})

		// 等待首次 mount 和 refresh 执行
		await nextTick()
		await Promise.resolve()
		await nextTick()

		expect(invokeMock).toHaveBeenCalledWith("plugin_widgets")
		expect(container.querySelector("iframe")).toBeNull()
		expect(container.textContent?.trim()).toBe("")

		// 10秒后轮询返回新插件卡片
		invokeMock.mockResolvedValueOnce({
			widgets: [
				{
					pluginId: "nori.plugin.cloudmusic",
					title: "网易云音乐",
					entry: "http://localhost:5173/card.html",
				},
			],
		})

		await vi.advanceTimersByTimeAsync(10_000)
		await nextTick()

		// 验证卡片已渲染，且默认展开 (iframe 存在并包含正确属性)
		expect(container.textContent).toContain("网易云音乐")
		const iframe = container.querySelector("iframe")
		expect(iframe).not.toBeNull()
		expect(iframe?.getAttribute("src")).toBe("http://localhost:5173/card.html")
		expect(iframe?.style.height).toBe("22rem")

		// 测试点击折叠/展开
		const toggleBtn = container.querySelector("button")
		expect(toggleBtn).not.toBeNull()

		toggleBtn?.click()
		await nextTick()
		expect(container.querySelector("iframe")).toBeNull()

		toggleBtn?.click()
		await nextTick()
		expect(container.querySelector("iframe")).not.toBeNull()
	})
})
