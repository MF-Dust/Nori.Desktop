/**
 * Live2D 控制器 (pixi-live2d-display)
 *
 * 替换原有的 live2d-easy-control 方案，使用 PixiJS + pixi-live2d-display 渲染。
 * 保留兼容 API，新增插件系统与点击交互。
 *
 * 参考 AIRI: packages/stage-ui-live2d/src/components/scenes/live2d/Canvas.vue
 * + Model.vue
 */
import {Application} from "@pixi/app"
import {extensions} from "@pixi/extensions"
import {Ticker, TickerPlugin} from "@pixi/ticker"
import {DropShadowFilter} from "pixi-filters"
import {Live2DModel, Live2DFactory, MotionPriority} from "pixi-live2d-display/cubism4"
import type {Cubism4InternalModel} from "pixi-live2d-display/cubism4"
// CubismModel 类型未导出, 使用 any
// eslint-disable-next-line @typescript-eslint/no-explicit-any
type CubismModel = any
import {ref, type Ref} from "vue"

import {assetUrl} from "./config"
import {selectInteractionMotionGroups} from "./motions"
import {calcFitModel, calculateSafeBaseSize} from "./composables/fit-model"
import {useMotionManagerUpdate} from "./plugins"
import {useAutoBlinkPlugin} from "./plugins/auto-blink"
import {useIdleEyeFocusPlugin} from "./plugins/eye-focus"
import {useExpressionController} from "./plugins/expression"
import {useLipSyncPlugin} from "./plugins/lip-sync"
import {createBeatSyncController, useBeatSyncPlugin, type BeatSyncController} from "./plugins/beat-sync"
import {useIdleDisablePlugin} from "./plugins/idle-disable"
import {expressionStore} from "./stores/expression-store"
import {modelParameters} from "./stores/model-parameters"

// ---- 全局 ticker 注册 (只需一次) ----
let tickerRegistered = false
const ensureTicker = () => {
	if (tickerRegistered) return
	tickerRegistered = true
	Live2DModel.registerTicker(Ticker)
	extensions.add(TickerPlugin)
}
ensureTicker()

// ===================================================================
// 类型
// ===================================================================

export interface Live2DModelSpec {
	directory: string
	fileBase: string
}

export interface MotionGroup {
	group: string
	names: string[]
}

export const MOTION_PRIORITY = {
	none: MotionPriority.NONE,
	idle: MotionPriority.IDLE,
	normal: MotionPriority.NORMAL,
	force: MotionPriority.FORCE,
} as const

export interface Live2DMountOptions {
	/**
	 * 画布 CSS 宽度 (px), 不传则铺满窗口
	 */
	canvasWidth?: number
	/**
	 * 画布 CSS 高度 (px), 不传则铺满窗口
	 */
	canvasHeight?: number
	/**
	 * 挂载目标容器 (预览模式), 不传则挂到 body
	 */
	container?: HTMLElement
	/**
	 * 兼容字段: 预览与伴侣视窗都统一按 userScale 缩放, 这里保留给旧调用.
	 */
	fitModelToContainer?: boolean
}

export interface Live2DInteractionMask {
	width: number
	height: number
	data: string
}

export interface Live2DConfigPatch {
	autoBlink?: boolean
	eyeTracking?: boolean
	idleEyeAnimation?: boolean
	idleAnimation?: boolean
	expressionEnabled?: boolean
	shadowEnabled?: boolean
	lipSyncEnabled?: boolean
	beatSyncEnabled?: boolean
	clickInteraction?: boolean
	renderScale?: number
	maxFps?: number
	userScale?: number
}

interface Live2DInternal {
	app: Application
	container: HTMLDivElement
	model: Live2DModel<Cubism4InternalModel>
	baseWidth: number
	baseHeight: number
	initialModelWidth: number
	initialModelHeight: number
	normalizedScale: number
	expressionController: ReturnType<typeof useExpressionController>
	beatSync: BeatSyncController
	pluginSystem: ReturnType<typeof useMotionManagerUpdate>
	savedEyeBlink: Cubism4InternalModel["eyeBlink"] | null
	savedExpressionManager: unknown
	expressionRefs: {Name: string; File: string}[]
	readExpFile: (path: string) => Promise<string>
	// 配置 refs (插件读取)
	autoBlinkRef: Ref<boolean>
	eyeTrackingRef: Ref<boolean>
	eyeFocusSourceActiveRef: Ref<boolean>
	idleAnimationRef: Ref<boolean>
	forceIdleEyeAnimationRef: Ref<boolean>
	autoBlinkEnabledRef: Ref<boolean>
	forceAutoBlinkEnabledRef: Ref<boolean>
	beatSyncEnabledRef: Ref<boolean>
	lastUpdateTimeRef: Ref<number>
	// 配置值
	expressionEnabled: boolean
	shadowEnabled: boolean
	lipSyncEnabled: boolean
	beatSyncEnabled: boolean
	clickInteraction: boolean
	renderScale: number
	maxFps: number
	userScale: number
	modelName: string
	fitModelToContainer: boolean
}

interface InteractionMaskCache {
	width: number
	height: number
	viewWidth: number
	viewHeight: number
	bits: Uint8Array
}

// ===================================================================
// 控制器工厂
// ===================================================================

export type Live2DController = ReturnType<typeof createLive2D>

const clamp = (value: number, min: number, max: number): number => Math.min(max, Math.max(min, value))

export const createLive2D = () => {
	let internal: Live2DInternal | null = null
	let interactionMaskCache: InteractionMaskCache | null = null
	let lastTapAt = 0

	const autoBlinkPlugin = useAutoBlinkPlugin()
	const idleEyeFocusPlugin = useIdleEyeFocusPlugin()
	const idleDisablePlugin = useIdleDisablePlugin()

	const mouthOpenSize = ref(0)
	const nowSpeaking = ref(false)
	const lipSyncPlugin = useLipSyncPlugin(mouthOpenSize, nowSpeaking)

	// ---- 布局与渲染 ----

	const applyRendererSize = () => {
		const inner = internal
		if (!inner) return
		const rect = inner.container.getBoundingClientRect()
		const dpr = typeof window !== "undefined" ? window.devicePixelRatio || 1 : 1
		const totalScale = inner.renderScale * dpr
		inner.baseWidth = Math.max(1, rect.width)
		inner.baseHeight = Math.max(1, rect.height)
		inner.app.renderer.resize(
			Math.max(1, Math.round(inner.baseWidth * totalScale)),
			Math.max(1, Math.round(inner.baseHeight * totalScale)),
		)
		inner.app.stage.scale.set(totalScale)
	}

	const applyLayout = () => {
		const inner = internal
		if (!inner?.model) return
		const rect = inner.container.getBoundingClientRect()
		const normalized = calcFitModel(
			{width: Math.max(1, rect.width), height: Math.max(1, rect.height)},
			{width: inner.initialModelWidth, height: inner.initialModelHeight},
		)
		inner.normalizedScale = normalized.scale
		const finalScale = normalized.scale * inner.userScale
		inner.model.scale.set(finalScale, finalScale)
		inner.model.x = normalized.x
		inner.model.y = normalized.y
	}

	const applyShadow = () => {
		const inner = internal
		if (!inner?.model) return
		if (inner.shadowEnabled) {
			inner.model.filters = [new DropShadowFilter({
				alpha: 0.2,
				blur: 0,
				distance: 20,
				rotation: 45,
			})]
		} else {
			inner.model.filters = []
		}
	}

	const applyMaxFps = () => {
		const inner = internal
		if (!inner) return
		inner.app.ticker.maxFPS = inner.maxFps > 0 ? Math.max(1, Math.round(inner.maxFps)) : 0
	}

	const hookPlugins = () => {
		const inner = internal
		if (!inner) return
		const {pluginSystem, beatSync} = inner
		pluginSystem.register(idleDisablePlugin, "pre")
		pluginSystem.register(useBeatSyncPlugin(beatSync), "pre")
		pluginSystem.register(idleEyeFocusPlugin, "post")
		pluginSystem.register(inner.expressionController.applyExpressions, "final")
		pluginSystem.register(autoBlinkPlugin, "final")
		pluginSystem.register(lipSyncPlugin, "final")

		const motionManager = inner.model.internalModel.motionManager
		const originalUpdate = motionManager.update as (model: CubismModel, now: number) => boolean
		motionManager.update = function (model: CubismModel, now: number) {
			return pluginSystem.hookUpdate(model, now, originalUpdate)
		}
	}

	const initExpressionSystem = async () => {
		const inner = internal
		if (!inner) return
		if (!inner.expressionEnabled) return
		if (inner.expressionRefs.length === 0) return
		try {
			await inner.expressionController.initialise(inner.expressionRefs, inner.readExpFile)
		} catch (error) {
			console.warn("[Live2D] 表情初始化失败:", error)
		}
	}

	const handleHit = (hitAreas: string[]) => {
		const inner = internal
		if (!inner?.clickInteraction) return
		const now = performance.now()
		if (now - lastTapAt < 1000) return
		lastTapAt = now

		const AREAS = hitAreas.map((area) => area.toLowerCase())
		if (AREAS.includes("head") && inner.expressionEnabled) {
			const names = expressionStore.allGroupNames()
			const EXPRESSIONS = names.length > 0 ? names : expressionStore.allNames()
			if (EXPRESSIONS.length > 0) {
				const random = EXPRESSIONS[Math.floor(Math.random() * EXPRESSIONS.length)]
				if (expressionStore.toggle(random)) return
			}
		}

		void playInteractionMotion(inner)
	}

	const modelWorldPoint = (clientX: number, clientY: number): {x: number; y: number; rect: DOMRect} | null => {
		const inner = internal
		if (!inner?.model) return null
		const rect = inner.app.view.getBoundingClientRect()
		if (rect.width <= 0 || rect.height <= 0) return null
		const view = inner.app.view
		return {
			x: (clientX - rect.left) * view.width / rect.width,
			y: (clientY - rect.top) * view.height / rect.height,
			rect,
		}
	}

	const readAlpha = (x: number, y: number): number | null => {
		const inner = internal
		if (!inner?.model) return null
		try {
			const renderer = inner.app.renderer as unknown as {gl?: WebGLRenderingContext | null}
			const gl = renderer.gl
			if (!gl) return null
			const view = inner.app.view
			const pixelX = Math.floor(x)
			const pixelY = view.height - 1 - Math.floor(y)
			if (pixelX < 0 || pixelX >= view.width || pixelY < 0 || pixelY >= view.height) return 0
			const pixel = new Uint8Array(4)
			gl.readPixels(pixelX, pixelY, 1, 1, gl.RGBA, gl.UNSIGNED_BYTE, pixel)
			return pixel[3]
		} catch {
			return null
		}
	}

	const isRenderedPixel = (clientX: number, clientY: number): boolean => {
		const point = modelWorldPoint(clientX, clientY)
		if (!point) return false
		const alpha = readAlpha(point.x, point.y)
		return alpha != null && alpha > 16
	}

	// ===================================================================
	// 挂载 / 卸载
	// ===================================================================

	const mount = async (spec: Live2DModelSpec, options: Live2DMountOptions = {}): Promise<void> => {
		await destroy()
		interactionMaskCache = null

		const mountTarget = options.container ?? document.body
		const fixedSize = options.canvasWidth != null && options.canvasHeight != null

		const container = document.createElement("div")
		const fitModelToContainer = options.fitModelToContainer ?? true
		container.style.zIndex = "1"
		container.style.pointerEvents = "none"
		container.style.overflow = "hidden"
		if (options.container) {
			container.style.position = "absolute"
			container.style.inset = "0"
		} else {
			container.style.position = "fixed"
			container.style.inset = "0"
			container.style.width = "100vw"
			container.style.height = "100vh"
		}
		if (fixedSize) {
			container.style.width = `${options.canvasWidth}px`
			container.style.height = `${options.canvasHeight}px`
			container.style.left = "0"
			container.style.top = "0"
		}
		mountTarget.appendChild(container)

		const rect = container.getBoundingClientRect()
		const app = new Application({
			width: Math.max(1, Math.round(rect.width)),
			height: Math.max(1, Math.round(rect.height)),
			backgroundAlpha: 0,
			preserveDrawingBuffer: true,
			autoDensity: false,
			resolution: 1,
		})
		app.view.style.width = "100%"
		app.view.style.height = "100%"
		app.view.style.display = "block"
		app.view.style.pointerEvents = "auto"
		container.appendChild(app.view)

		const modelName = spec.directory

		const autoBlinkRef = ref(true)
		const eyeTrackingRef = ref(true)
		const eyeFocusSourceActiveRef = ref(false)
		const idleAnimationRef = ref(true)
		const forceIdleEyeAnimationRef = ref(true)
		const autoBlinkEnabledRef = ref(true)
		const forceAutoBlinkEnabledRef = ref(true)
		const beatSyncEnabledRef = ref(false)
		const lastUpdateTimeRef = ref(0)

		// 占位 internalModel (插件系统在模型加载后重建)
		const placeholder: Live2DInternal = {
			app,
			container,
			model: null!,
			baseWidth: rect.width,
			baseHeight: rect.height,
			initialModelWidth: 400,
			initialModelHeight: 520,
			normalizedScale: 1,
			expressionController: null as unknown as ReturnType<typeof useExpressionController>,
			beatSync: null as unknown as BeatSyncController,
			pluginSystem: null as unknown as ReturnType<typeof useMotionManagerUpdate>,
			savedEyeBlink: null,
			savedExpressionManager: null,
			expressionRefs: [],
			readExpFile: async () => "",
			autoBlinkRef,
			eyeTrackingRef,
			eyeFocusSourceActiveRef,
			idleAnimationRef,
			forceIdleEyeAnimationRef,
			autoBlinkEnabledRef,
			forceAutoBlinkEnabledRef,
			beatSyncEnabledRef,
			lastUpdateTimeRef,
			expressionEnabled: true,
			shadowEnabled: true,
			lipSyncEnabled: true,
			beatSyncEnabled: false,
			clickInteraction: true,
			renderScale: 2,
			maxFps: 0,
			userScale: 1,
			modelName,
			fitModelToContainer,
		}
		internal = placeholder

		try {
			const model = new Live2DModel<Cubism4InternalModel>()
			const url = `${assetUrl(`live2d/${spec.directory}`)}/${spec.fileBase}.model3.json`
			await Live2DFactory.setupLive2DModel(model, url, {autoInteract: false})

			// 开启纹理 Mipmap 生成与 2048 蒙版缓冲（修复设置页预览颗粒感与边缘锯齿）
			const textures = (model as unknown as {textures?: {baseTexture?: {mipmap?: number; update?: () => void}}[]}).textures
			if (textures) {
				for (const tex of textures) {
					if (tex?.baseTexture) {
						tex.baseTexture.mipmap = 1
						tex.baseTexture.update?.()
					}
				}
			}
			// 裁剪蒙版缓冲: pixi-live2d-display 默认 256, 眼/口/发的蒙版边缘会有明显阶梯。
			// 渲染器挂在 internalModel.renderer 上 (不是 coreModel.renderer), 且只接受一个参数。
			const CUBISM_RENDERER = (model.internalModel as unknown as {
				renderer?: {setClippingMaskBufferSize?: (size: number) => void}
			}).renderer
			CUBISM_RENDERER?.setClippingMaskBufferSize?.(2048)

			app.stage.addChild(model)
			model.anchor.set(0.5, 0.5)

			const expressionController = useExpressionController({internalModel: model.internalModel, modelId: modelName})
			const beatSync = createBeatSyncController()
			const pluginSystem = useMotionManagerUpdate({
				internalModel: model.internalModel,
				modelParameters: modelParameters,
				live2dEyeTrackingEnabled: eyeTrackingRef,
				live2dEyeFocusSourceActive: eyeFocusSourceActiveRef,
				live2dIdleAnimationEnabled: idleAnimationRef,
				live2dForceIdleEyeAnimation: forceIdleEyeAnimationRef,
				live2dAutoBlinkEnabled: autoBlinkEnabledRef,
				live2dForceAutoBlinkEnabled: forceAutoBlinkEnabledRef,
				live2dBeatSyncEnabled: beatSyncEnabledRef,
				lastUpdateTime: lastUpdateTimeRef,
			})

			// 读取表情引用
			const settings = model.internalModel.settings as unknown as {
				expressions?: {Name: string; File: string}[]
				resolveURL?: (path: string) => string
			}
			const expressionRefs = settings.expressions ?? []
			const readExpFile = async (filePath: string): Promise<string> => {
				const resolvedUrl = settings.resolveURL?.(filePath) ?? filePath
				const response = await fetch(resolvedUrl)
				if (!response.ok) throw new Error(`Failed to fetch exp3: ${filePath} (${response.status})`)
				return response.text()
			}

			const rawModelWidth = model.internalModel.width || model.internalModel.originalWidth || 400
			const rawModelHeight = model.internalModel.height || model.internalModel.originalHeight || 520

			internal = {
				...placeholder,
				model,
				initialModelWidth: rawModelWidth,
				initialModelHeight: rawModelHeight,
				expressionController,
				beatSync,
				pluginSystem,
				savedEyeBlink: model.internalModel.eyeBlink,
				savedExpressionManager: model.internalModel.motionManager.expressionManager,
				expressionRefs,
				readExpFile,
			}

			// 我们的插件负责眨眼与表情, 屏蔽 SDK 的 expressionManager / eyeBlink
			;(model.internalModel as unknown as {eyeBlink: unknown}).eyeBlink = null
			;(model.internalModel.motionManager as unknown as {expressionManager: unknown}).expressionManager = null

			applyRendererSize()
			applyLayout()
			applyShadow()
			applyMaxFps()
			hookPlugins()
			await initExpressionSystem()
		} catch (error) {
			console.error("[Live2D] 模型加载失败:", error)
			try {
				app.destroy(true, {children: true, texture: false, baseTexture: false})
			} catch {
				/* ignore */
			}
			try {
				container.remove()
			} catch {
				/* ignore */
			}
			internal = null
			throw error
		}
	}

	const destroy = async (): Promise<void> => {
		const inner = internal
		if (!inner) return
		internal = null
		interactionMaskCache = null
		try {
			inner.expressionController.dispose()
		} catch {
			/* ignore */
		}
		try {
			inner.app.destroy(true, {children: true, texture: false, baseTexture: false})
		} catch {
			/* ignore */
		}
		try {
			inner.container.remove()
		} catch {
			/* ignore */
		}
	}

	// ===================================================================
	// 基础 API
	// ===================================================================

	const canvas = (): HTMLCanvasElement | null => internal?.app.view ?? null

	const isPointOnModel = (clientX: number, clientY: number): boolean => {
		const inner = internal
		if (!inner?.model) return false
		const point = modelWorldPoint(clientX, clientY)
		if (!point) return false
		const cache = interactionMaskCache
		const view = inner.app.view
		if (cache && cache.viewWidth === view.width && cache.viewHeight === view.height) {
			const column = Math.floor((clientX - point.rect.left) / point.rect.width * cache.width)
			const row = Math.floor((clientY - point.rect.top) / point.rect.height * cache.height)
			if (column < 0 || row < 0 || column >= cache.width || row >= cache.height) return false
			const index = row * cache.width + column
			return (cache.bits[index >> 3] & (1 << (index & 7))) !== 0
		}

		return isRenderedPixel(clientX, clientY)
	}

	const tapAt = (clientX: number, clientY: number): void => {
		const inner = internal
		if (!inner?.model) return
		const point = modelWorldPoint(clientX, clientY)
		if (!point) return

		if (!isRenderedPixel(clientX, clientY)) return
		try {
			const hitAreas = inner.model.hitTest(point.x, point.y)
			handleHit(hitAreas.length > 0 ? hitAreas : ["body"])
		} catch {
			/* 命中区定义异常时忽略本次点击 */
		}
	}

	const getInteractionMask = (width = 96, height = 128): Live2DInteractionMask | null => {
		const inner = internal
		interactionMaskCache = null
		if (!inner?.model || width <= 0 || height <= 0) return null
		const view = inner.app.view
		const rect = view.getBoundingClientRect()
		if (rect.width <= 0 || rect.height <= 0) return null
		const renderer = inner.app.renderer as unknown as {gl?: WebGLRenderingContext | null}
		const gl = renderer.gl
		if (!gl) return null
		const filters = inner.model.filters
		const pixels = new Uint8Array(view.width * view.height * 4)
		try {
			// 阴影是视觉效果，不应把透明桌面区域扩成可交互区域。
			inner.model.filters = []
			inner.app.renderer.render(inner.app.stage)
			gl.readPixels(0, 0, view.width, view.height, gl.RGBA, gl.UNSIGNED_BYTE, pixels)
		} catch {
			return null
		} finally {
			inner.model.filters = filters
			try {
				inner.app.renderer.render(inner.app.stage)
			} catch {
				/* 恢复视觉帧失败时由 ticker 下一帧重绘 */
			}
		}
		const bits = new Uint8Array(Math.ceil(width * height / 8))
		const sample = (clientX: number, clientY: number): boolean => {
			const point = modelWorldPoint(clientX, clientY)
			if (!point) return false
			const x = Math.floor(point.x)
			const y = view.height - 1 - Math.floor(point.y)
			if (x < 0 || x >= view.width || y < 0 || y >= view.height) return false
			return pixels[(y * view.width + x) * 4 + 3] > 16
		}
		for (let row = 0; row < height; row++) {
			for (let column = 0; column < width; column++) {
				const left = rect.left + column * rect.width / width
				const top = rect.top + row * rect.height / height
				const cellWidth = rect.width / width
				const cellHeight = rect.height / height
				const hit = sample(left + cellWidth * 0.5, top + cellHeight * 0.5)
					|| sample(left + cellWidth * 0.2, top + cellHeight * 0.2)
					|| sample(left + cellWidth * 0.8, top + cellHeight * 0.2)
					|| sample(left + cellWidth * 0.2, top + cellHeight * 0.8)
					|| sample(left + cellWidth * 0.8, top + cellHeight * 0.8)
				if (hit) {
					const index = row * width + column
					bits[index >> 3] |= 1 << (index & 7)
				}
			}
		}
		let binary = ""
		for (const byte of bits) binary += String.fromCharCode(byte)
		interactionMaskCache = {width, height, viewWidth: view.width, viewHeight: view.height, bits}
		return {width, height, data: btoa(binary)}
	}

	const getMotions = async (): Promise<MotionGroup[] | null> => {
		const inner = internal
		if (!inner?.model) return null
		const motionManager = inner.model.internalModel.motionManager
		const groups: MotionGroup[] = []
		for (const [group, motions] of Object.entries(motionManager.definitions)) {
			if (!Array.isArray(motions)) continue
			const names = motions
				.map((motion) => (motion as {File?: string}).File ?? "")
				.map((file) => file.replace(/\.motion3\.json$/i, "").replace(/^.*\//, ""))
				.filter((name) => name !== "")
			if (names.length === 0) continue
			groups.push({group, names})
		}
		return groups.length > 0 ? groups : null
	}

	const playMotionByIndex = async (group: string, no: number, priority = MOTION_PRIORITY.force): Promise<boolean> => {
		const inner = internal
		if (!inner?.model) return false
		try {
			return await inner.model.motion(group, no, priority)
		} catch {
			return false
		}
	}

	async function playInteractionMotion(inner: Live2DInternal): Promise<boolean> {
		if (!inner.model) return false
		try {
			const groups = selectInteractionMotionGroups((await getMotions()) ?? [])
			for (const group of groups) {
				const start = Math.floor(Math.random() * group.names.length)
				for (let offset = 0; offset < group.names.length; offset++) {
					const index = (start + offset) % group.names.length
					if (await playMotionByIndex(group.group, index)) return true
				}
			}
		} catch {
			/* 模型动作定义异常时保持点击事件安全 */
		}
		return false
	}

	const playMotionByName = async (name: string, priority = MOTION_PRIORITY.force): Promise<boolean> => {
		const motions = await getMotions()
		if (!motions) return false
		for (const group of motions) {
			const index = group.names.findIndex((n) => n === name)
			if (index >= 0) return playMotionByIndex(group.group, index, priority)
		}
		return false
	}

	const playExpression = async (name: string): Promise<void> => {
		if (!internal) return
		expressionStore.play(name)
	}

	const stopExpression = async (): Promise<void> => {
		if (!internal) return
		expressionStore.stop()
	}

	const lookAt = (clientX: number, clientY: number): void => {
		const inner = internal
		if (!inner?.model || !inner.eyeTrackingRef.value) return
		const point = modelWorldPoint(clientX, clientY)
		if (!point) return
		inner.model.focus(point.x, point.y)
	}

	const focusAt = (x: number, y: number): void => {
		const inner = internal
		if (!inner?.model) return
		inner.model.focus(x, y, false)
	}

	const triggerBeat = (timestamp?: number | null): void => {
		const inner = internal
		if (!inner?.beatSyncEnabled) return
		inner.beatSync.triggerBeat(timestamp ?? null)
	}

	const getBaseSize = (): {width: number; height: number} => {
		const inner = internal
		if (!inner) return {width: 400, height: 520}
		return calculateSafeBaseSize(inner.initialModelWidth, inner.initialModelHeight)
	}

	const resize = (width?: number, height?: number): void => {
		const inner = internal
		if (!inner) return
		if (width != null && height != null) {
			inner.container.style.width = `${width}px`
			inner.container.style.height = `${height}px`
		}
		applyRendererSize()
		applyLayout()
	}

	// ===================================================================
	// 配置
	// ===================================================================

	const setAutoBlink = (enabled: boolean): void => {
		if (internal) {
			internal.autoBlinkRef.value = enabled
			internal.autoBlinkEnabledRef.value = enabled
			internal.forceAutoBlinkEnabledRef.value = enabled
		}
	}

	const setEyeTracking = (enabled: boolean): void => {
		if (internal) internal.eyeTrackingRef.value = enabled
	}

	const setIdleEyeAnimation = (enabled: boolean): void => {
		if (internal) internal.forceIdleEyeAnimationRef.value = enabled
	}

	const setIdleAnimation = (enabled: boolean): void => {
		if (internal) internal.idleAnimationRef.value = enabled
	}

	const setExpressionEnabled = (enabled: boolean): void => {
		const inner = internal
		if (!inner) return
		inner.expressionEnabled = enabled
		if (enabled) {
			// 重新屏蔽 SDK 管理器
			if (inner.model.internalModel.motionManager.expressionManager != null) {
				inner.savedExpressionManager = inner.model.internalModel.motionManager.expressionManager
				;(inner.model.internalModel.motionManager as unknown as {expressionManager: unknown}).expressionManager = null
			}
			if (inner.model.internalModel.eyeBlink != null) {
				inner.savedEyeBlink = inner.model.internalModel.eyeBlink
				;(inner.model.internalModel as unknown as {eyeBlink: unknown}).eyeBlink = null
			}
			void initExpressionSystem()
		} else {
			expressionStore.dispose()
		}
	}

	const setShadowEnabled = (enabled: boolean): void => {
		const inner = internal
		if (!inner) return
		inner.shadowEnabled = enabled
		applyShadow()
	}

	const setRenderScale = (scale: number): void => {
		const inner = internal
		if (!inner) return
		inner.renderScale = clamp(scale, 0.5, 2)
		applyRendererSize()
		applyLayout()
	}

	const setMaxFps = (fps: number): void => {
		const inner = internal
		if (!inner) return
		inner.maxFps = fps
		applyMaxFps()
	}

	const setLipSyncEnabled = (enabled: boolean): void => {
		if (internal) internal.lipSyncEnabled = enabled
	}

	const setBeatSyncEnabled = (enabled: boolean): void => {
		const inner = internal
		if (!inner) return
		inner.beatSyncEnabled = enabled
		inner.beatSyncEnabledRef.value = enabled
	}

	const setClickInteraction = (enabled: boolean): void => {
		if (internal) internal.clickInteraction = enabled
	}

	const setUserScale = (scale: number): void => {
		const inner = internal
		if (!inner) return
		inner.userScale = clamp(scale, 0.1, 4)
		applyLayout()
	}

	const setMouthOpen = (value: number): void => {
		mouthOpenSize.value = clamp(value, 0, 1)
	}

	const setNowSpeaking = (speaking: boolean): void => {
		nowSpeaking.value = speaking
	}

	const applyConfig = (config: Live2DConfigPatch): void => {
		if (config.autoBlink !== undefined) setAutoBlink(config.autoBlink)
		if (config.eyeTracking !== undefined) setEyeTracking(config.eyeTracking)
		if (config.idleEyeAnimation !== undefined) setIdleEyeAnimation(config.idleEyeAnimation)
		if (config.idleAnimation !== undefined) setIdleAnimation(config.idleAnimation)
		if (config.expressionEnabled !== undefined) setExpressionEnabled(config.expressionEnabled)
		if (config.shadowEnabled !== undefined) setShadowEnabled(config.shadowEnabled)
		if (config.lipSyncEnabled !== undefined) setLipSyncEnabled(config.lipSyncEnabled)
		if (config.beatSyncEnabled !== undefined) setBeatSyncEnabled(config.beatSyncEnabled)
		if (config.clickInteraction !== undefined) setClickInteraction(config.clickInteraction)
		if (config.renderScale !== undefined) setRenderScale(config.renderScale)
		if (config.maxFps !== undefined) setMaxFps(config.maxFps)
		if (config.userScale !== undefined) setUserScale(config.userScale)
	}

	const getState = () => {
		const inner = internal
		if (!inner) return null
		return {
			modelName: inner.modelName ?? "",
			baseWidth: inner.baseWidth,
			baseHeight: inner.baseHeight,
			initialModelWidth: inner.initialModelWidth,
			initialModelHeight: inner.initialModelHeight,
			normalizedScale: inner.normalizedScale,
			userScale: inner.userScale,
			renderScale: inner.renderScale,
			fitModelToContainer: inner.fitModelToContainer,
		}
	}

	return {
		mount,
		destroy,
		canvas,
		isPointOnModel,
		tapAt,
		getMotions,
		playMotionByIndex,
		playMotionByName,
		playExpression,
		stopExpression,
		lookAt,
		focusAt,
		triggerBeat,
		getBaseSize,
		resize,
		setAutoBlink,
		setEyeTracking,
		setIdleEyeAnimation,
		setIdleAnimation,
		setExpressionEnabled,
		setShadowEnabled,
		setRenderScale,
		setMaxFps,
		setLipSyncEnabled,
		setBeatSyncEnabled,
		setClickInteraction,
		setUserScale,
		setMouthOpen,
		setNowSpeaking,
		applyConfig,
		getState,
		getInteractionMask,
	}
}
