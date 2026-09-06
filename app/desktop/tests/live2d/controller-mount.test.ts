import {beforeEach, describe, expect, it, vi} from "vitest"

// 控制器挂载回归测试:
// pixi-live2d-display 的 urlToJSON 中间件只接受字符串 URL,
// 传 {url, id} 对象会被当成 settings JSON 解析并抛出
// "Unknown settings format", 导致模型完全不渲染。
// 这里验证 mount() 必须以字符串 URL 调用 setupLive2DModel。

const setupLive2DModel = vi.fn(async () => {})

let currentModel: ReturnType<typeof modelMock>

const appMock = {
	view: {style: {}, width: 1, height: 1, getContext: () => null},
	stage: {scale: {set: vi.fn()}, addChild: vi.fn()},
	renderer: {resize: vi.fn(), resolution: 1},
	ticker: {maxFPS: 0},
	destroy: vi.fn(),
}

const modelMock = () => ({
	anchor: {set: vi.fn()},
	scale: {set: vi.fn()},
	x: 0,
	y: 0,
	filters: [],
	on: vi.fn(),
	internalModel: {
		width: 400,
		originalWidth: 400,
		height: 520,
		originalHeight: 520,
		settings: {expressions: []},
		motionManager: {expressionManager: null, update: vi.fn(), definitions: {}},
		eyeBlink: null,
		hitAreas: {},
	},
})

vi.mock("@pixi/app", () => ({
	Application: class {
		constructor() {
			return appMock
		}
	},
}))

vi.mock("@pixi/extensions", () => ({
	extensions: {add: vi.fn()},
}))

vi.mock("@pixi/ticker", () => ({
	Ticker: {shared: {add: vi.fn(), remove: vi.fn()}},
	TickerPlugin: {},
}))

vi.mock("pixi-filters", () => ({
	DropShadowFilter: class DropShadowFilter {
		constructor() { /* mock */ }
	},
}))

vi.mock("pixi-live2d-display/cubism4", () => ({
	Live2DModel: class Live2DModel {
		static registerTicker() { /* mock */ }
		constructor() {
			return currentModel
		}
	},
	Live2DFactory: {
		setupLive2DModel,
	},
	MotionPriority: {NONE: 0, IDLE: 1, NORMAL: 2, FORCE: 3},
}))

const makeContainer = () => {
	const container = {
		style: {},
		appendChild: vi.fn(),
		getBoundingClientRect: () => ({width: 400, height: 520, left: 0, top: 0}),
	}
	return container
}

describe("Live2D 控制器挂载 source 格式", () => {
	beforeEach(() => {
		setupLive2DModel.mockClear()
		appMock.renderer.resize.mockClear()
		appMock.stage.addChild.mockClear()
		appMock.stage.scale.set.mockClear()
		appMock.destroy.mockClear()
		currentModel = modelMock()
	})

	it("mount 以字符串 URL 调用 setupLive2DModel", async () => {
		const CONTAINER = makeContainer()
		const documentMock = {
			body: {appendChild: vi.fn()},
			createElement: vi.fn(() => CONTAINER),
		}
		;(globalThis as Record<string, unknown>).document = documentMock

		const {createLive2D} = await import("../../src/services/live2d/index")
		const CONTROLLER = createLive2D()

		await CONTROLLER.mount({directory: "arg-nori", fileBase: "ARGNori"})

		expect(setupLive2DModel).toHaveBeenCalledTimes(1)
		const [MODEL, SOURCE, OPTIONS] = setupLive2DModel.mock.calls[0]
		expect(typeof SOURCE).toBe("string")
		expect(SOURCE).toContain("live2d/arg-nori/ARGNori.model3.json")
		expect(OPTIONS).toEqual({autoInteract: false})
		expect(MODEL).toBeDefined()

		await CONTROLLER.destroy()
	})

	it("setUserScale 会直接缩放模型且保留更小的最小值", async () => {
		const CONTAINER = makeContainer()
		const documentMock = {
			body: {appendChild: vi.fn()},
			createElement: vi.fn(() => CONTAINER),
		}
		;(globalThis as Record<string, unknown>).document = documentMock

		const {createLive2D} = await import("../../src/services/live2d/index")
		const CONTROLLER = createLive2D()

		await CONTROLLER.mount({directory: "arg-nori", fileBase: "ARGNori"})
		currentModel.scale.set.mockClear()

		CONTROLLER.setUserScale(0.05)

		expect(currentModel.scale.set).toHaveBeenLastCalledWith(0.1, 0.1)

		currentModel.scale.set.mockClear()
		CONTROLLER.setUserScale(5)
		expect(currentModel.scale.set).toHaveBeenLastCalledWith(4, 4)

		await CONTROLLER.destroy()
	})
})
