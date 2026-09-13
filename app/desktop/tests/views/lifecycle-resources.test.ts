import {readFileSync} from "node:fs"
import {join} from "node:path"
import {describe, expect, it} from "vitest"

const ROOT = process.cwd()
const SRC = join(ROOT, "src")

const normalizeEol = (content: string): string => content.replace(/\r\n?/g, "\n")
const read = (path: string): string => normalizeEol(readFileSync(join(SRC, path), "utf8"))
const readProject = (path: string): string => normalizeEol(readFileSync(join(ROOT, path), "utf8"))

describe("页面与宿主资源生命周期", () => {
	it("主工作区不再内置对话页并持有原生入口", () => {
		const MAIN = read("views/Main.vue")

		expect(MAIN).not.toContain("<ChatView")
		expect(MAIN).not.toContain("<KeepAlive>")
		expect(MAIN).toContain("RUNTIME.openChat")
		expect(MAIN).toContain("RUNTIME.openModels")
		expect(MAIN).toContain("RUNTIME.openSettings")
		expect(MAIN).toContain("RUNTIME.openMemory")
		expect(MAIN).not.toContain("<ModelManagement")
		expect(MAIN).not.toContain("<MemoryPanel")
		expect(MAIN).not.toContain("<SettingsPanel")
	})

	it("应用根组件卸载时退订模块级语言监听", () => {
		const APP = read("App.vue")

		expect(APP).toContain("const stopLanguageSync = RUNTIME.onLanguageChanged")
		expect(APP).toMatch(/onBeforeUnmount\(\(\) => \{\s+stopLanguageSync\(\)/)
	})

	it("音频宿主卸载会关闭 WebAudio 并取消过期的麦克风启动", () => {
		const AUDIO = read("services/audio/index.ts")

		expect(AUDIO).toContain("CONTEXT.close()")
		expect(AUDIO).toContain("cancelRecording()")
		expect(AUDIO).toContain("releaseAudioGraph()")
		expect(AUDIO).toContain("GENERATION !== recordingGeneration")
		expect(AUDIO).toContain("stopStream(acquiredStream)")
	})

	it("WebView 不再装载浏览器 Live2D 引擎，资产前缀仍保持相对路径", () => {
		const HTML = readProject("index.html")
		const PACKAGE = JSON.parse(readProject("package.json"))
		expect(HTML).not.toContain("live2dcubismcore")
		expect(PACKAGE.dependencies).not.toHaveProperty("pixi-live2d-display")
		expect(Object.keys(PACKAGE.dependencies).filter(name => name.startsWith("@pixi/") || name === "pixi-filters")).toEqual([])
		expect(readProject("vite.config.ts")).toContain("base: \"./\"")
	})

	it("真正关闭伴侣窗口时断开运行时事件与 WindowManager 强引用", () => {
		const PET_WINDOW = readProject("Nori.Desktop/Windows/PetWindow.cs")
		const WINDOW_MANAGER = readProject("Nori.Desktop/Windows/WindowManager.cs")

		expect(PET_WINDOW).toContain("_runtime.ModelChanged -= OnRuntimeModelChanged")
		expect(PET_WINDOW).toContain("_runtime.LayoutChanged -= OnRuntimeLayoutChanged")
		expect(PET_WINDOW).toContain("_cursorTrackingTimer.Tick -= OnCursorTrackingTick")
		expect(PET_WINDOW).toContain("_hitShapeTimer.Tick -= OnHitShapeTick")
		expect(WINDOW_MANAGER).toContain("if (ReferenceEquals(_petWindow, pw)) _petWindow = null;")
	})
})
