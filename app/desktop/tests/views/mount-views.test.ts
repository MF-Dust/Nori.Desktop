import {describe, expect, it, beforeEach} from "vitest"
import {createApp, h, nextTick} from "vue"
import {i18n} from "../../src/services/i18n"
import ZH from "../../src/services/i18n/locales/zh-CN"
import {mergePluginMessages} from "../../src/services/i18n/pluginMessages"
import {RUNTIME} from "../../src/services/runtime"
import Main from "../../src/views/Main.vue"
import HomePanel from "../../src/components/home/HomePanel.vue"

describe("Views and Panels Mounting", () => {
	let INVOKED: string[] = []
	const mockSnapshot = {
		version: 1,
		app: {
			appVersion: "0.1.0",
			platform: "windows",
			debugCrashTestsAvailable: false,
		},
		general: {
			language: "zh-CN",
			petAutoSummon: true,
			sidebarCollapsed: false,
		},
		telemetry: {
			consent: "granted",
			enabled: true,
			available: true,
		},
		secretIssues: [],
		ai: {
			configured: true,
			provider: "openai",
			baseUrl: "https://api.openai.com/v1",
			model: "gpt-4o",
			persona: "nori",
			hasApiKey: true,
		},
		models: {
			selected: "arg-nori",
			items: [
				{id: "arg-nori", installed: true},
				{id: "nori", installed: true},
			],
			loadError: null,
			scale: 1.0,
			expressions: [],
		},
		pet: {
			visible: true,
			renderMetrics: null,
		},
		windows: {chat: false, models: false, memory: false, settings: false},
		platform: {
			os: "windows",
			sessionType: "x11",
			supportsGlobalCursor: true,
			supportsWindowDrag: true,
			supportsHitThrough: true,
			supportsTopmost: true,
			supportsTray: true,
		},
		behaviors: {
			clickInteraction: true,
			autoBlink: true,
			eyeTracking: true,
			idleEyeAnimation: true,
			idleAnimation: true,
			expressionEnabled: true,
			lipSync: true,
			shadow: true,
			beatSync: false,
			aiInteraction: false,
			opacity: 1.0,
			renderScale: 2.0,
			qualityMode: "adaptive",
			maxFps: 0,
		},
		memory: {
			enabled: true,
			reflectionEnabled: true,
			decayEnabled: true,
			archiveEnabled: true,
			active: 0,
			atoms: 0,
			archived: 0,
			total: 0,
			knowledgePath: "",
			knowledgeChunks: 0,
			indexState: "idle",
			indexProcessed: 0,
			indexTotal: 0,
			lastError: null,
			lastReflection: null,
			lastMaintenance: null,
			ftsAvailable: true,
			reflectionRounds: 8,
			reflectionMinChars: 2500,
			recallTopK: 6,
			keywordTopK: 20,
			vectorTopK: 20,
			rrfK: 60,
			minSimilarity: 0.25,
			sourceRetentionThreshold: 0.75,
			archiveThreshold: 0.15,
			knowledgeEnabled: true,
			knowledgeWatch: true,
			debugRetrieval: false,
		},
		voice: {
			volume: 1.0,
			ttsProvider: "openai",
			ttsBaseUrl: "",
			hasTtsApiKey: false,
			ttsVoice: "nova",
			ttsSpeed: 1.0,
			ttsAutoPlay: true,
			gptsovitsBaseUrl: "http://127.0.0.1:9880",
			gptsovitsRefAudio: "",
			gptsovitsPromptText: "",
			gptsovitsPromptLang: "zh",
			sttProvider: "whisper",
			sttBaseUrl: "",
			hasSttApiKey: false,
			noticePending: false,
			speaking: false,
		},
		embedding: {
			model: "BAAI/bge-m3",
			baseUrl: "",
			dimensions: "",
			hasApiKey: false,
		},
		proactive: {
			idleEnabled: true,
			idleMinutes: 15,
			dailyGreeting: true,
			reminders: [],
		},
		skills: [],
		enabledSkillsCount: 0,
		tools: [],
		mcpServersCount: 0,
		emotion: {type: "neutral"},
	}

	beforeEach(() => {
		INVOKED = [];
		(window as any).__nori = {
			assetBase: "/nori-assets/",
			label: "main",
			invoke: async (cmd: string, _args: any) => {
				INVOKED.push(cmd)
				if (cmd === "ui_get_snapshot") return mockSnapshot
				if (cmd === "model_get_meta") return {modelId: "arg-nori", scale: 1, expressions: [], motions: [], interactions: {version: 1, regions: []}, opacity: 1, shadow: true, renderScale: 2, qualityMode: "adaptive", maxFps: 0}
				if (cmd === "audio_host_ready") return null
				if (cmd === "mcp_get_servers") return []
				return null
			},
			emit: () => {},
			listen: () => () => {},
			dispatch: () => {},
		}
		RUNTIME.snapshot.value = mockSnapshot as any
		i18n.global.setLocaleMessage("zh-CN", mergePluginMessages("zh-CN", ZH))
		i18n.global.locale.value = "zh-CN"
	})

	const mountComponent = (component: any, props: any = {}) => {
		const CONTAINER = document.createElement("div")
		document.body.appendChild(CONTAINER)
		const APP = createApp({
			render: () => h(component, props),
		})
		APP.use(i18n)
		APP.mount(CONTAINER)
		return {app: APP, container: CONTAINER}
	}

	const settleView = async (): Promise<void> => {
		for (let index = 0; index < 4; index += 1) await nextTick()
		await new Promise<void>(resolve => setTimeout(resolve, 0))
		await nextTick()
	}

	const click = (element: Element): void => {
		element.dispatchEvent(new MouseEvent("click", {bubbles: true}))
	}

	it("侧边栏把「页」和「启动器」分开：只有主页是页，其余四项各开一个窗口", async () => {
		const MOUNT = mountComponent(Main)
		try {
			await settleView()
			expect(MOUNT.container.innerHTML).toBeTruthy()

			// 主页不是按钮：它是「你在这里」，点它没有任何事情要发生。
			const CURRENT = MOUNT.container.querySelectorAll("aside nav [aria-current='page']")
			expect(CURRENT).toHaveLength(1)
			expect(CURRENT[0].tagName).not.toBe("BUTTON")

			// 其余四项是启动器。
			const LAUNCHERS = Array.from(MOUNT.container.querySelectorAll("aside nav button"))
			expect(LAUNCHERS).toHaveLength(4)

			/*
			 * 启动器**不许**带 aria-current。
			 *
			 * 改动前五项都带，而 activeNav 永远是 home —— 也就是说那四项的选中态是一个
			 * 恒为假的承诺：点了「对话」，窗口开在旁边，侧边栏却一动不动。这条用例就是
			 * 钉住那个谎不要再回来。
			 */
			for (const launcher of LAUNCHERS) {
				expect(launcher.getAttribute("aria-current")).toBeNull()
			}

			const PANELS = MOUNT.container.querySelectorAll("[data-main-panel]")
			expect(PANELS).toHaveLength(1)
			expect(PANELS[0].getAttribute("data-main-panel")).toBe("home")

			for (const launcher of LAUNCHERS) {
				click(launcher)
				await settleView()
				// 主面板始终是主页：这四项从来就不切页。
				expect(PANELS[0].getAttribute("data-main-panel")).toBe("home")
			}

			expect(INVOKED).toContain("window_open_chat")
			expect(INVOKED).toContain("window_open_models")
			expect(INVOKED).toContain("window_open_settings")
			expect(INVOKED).toContain("window_open_memory")
			// 开窗口是宿主的事，主界面不该顺手去拉那两份数据。
			expect(INVOKED).not.toContain("model_get_meta")
			expect(INVOKED).not.toContain("memory_list_page")
		} finally {
			MOUNT.app.unmount()
			MOUNT.container.remove()
		}
	})

	it("窗口开着时，侧边栏对应那一项亮一颗点", async () => {
		const SNAPSHOT = RUNTIME.snapshot.value as any
		SNAPSHOT.windows = {chat: true, models: false, memory: false, settings: false}
		const MOUNT = mountComponent(Main)
		try {
			await settleView()
			const LAUNCHERS = Array.from(MOUNT.container.querySelectorAll("aside nav button"))
			// 只有「对话」那一项该有点：这颗点说的是那个窗口开着，不是"选中了它"。
			const LIT = LAUNCHERS.filter(b => b.querySelector(".animate-pulse-soft"))
			expect(LIT).toHaveLength(1)
			expect(LIT[0].textContent).toContain(ZH.views.main.nav.talk)
		} finally {
			SNAPSHOT.windows = {chat: false, models: false, memory: false, settings: false}
			MOUNT.app.unmount()
			MOUNT.container.remove()
		}
	})

	it("主页快速卡片打开原生模型窗口且不替换主面板", async () => {
		const MOUNT = mountComponent(Main)
		try {
			await settleView()
			const BUTTON = Array.from(MOUNT.container.querySelectorAll("main button"))
				.find(button => button.textContent?.includes(ZH.views.main.home.cards.model.title))
			expect(BUTTON).toBeDefined()
			click(BUTTON!)
			await settleView()
			expect(INVOKED).toContain("window_open_models")
			expect(INVOKED).not.toContain("model_get_meta")
			expect(MOUNT.container.querySelector("[data-main-panel]")?.getAttribute("data-main-panel")).toBe("home")
		} finally {
			MOUNT.app.unmount()
			MOUNT.container.remove()
		}
	})

	it("主页对话卡片打开原生对话窗口且不替换主面板", async () => {
		const MOUNT = mountComponent(Main)
		try {
			await settleView()
			const BUTTON = Array.from(MOUNT.container.querySelectorAll("main button"))
				.find(button => button.textContent?.includes(ZH.views.main.home.cards.chat.title))
			expect(BUTTON).toBeDefined()
			click(BUTTON!)
			await settleView()
			expect(INVOKED).toContain("window_open_chat")
			expect(INVOKED).not.toContain("model_get_meta")
			expect(MOUNT.container.querySelector("[data-main-panel]")?.getAttribute("data-main-panel")).toBe("home")
		} finally {
			MOUNT.app.unmount()
			MOUNT.container.remove()
		}
	})

	it("handles empty / null snapshot gracefully in all panels", () => {
		RUNTIME.snapshot.value = null
		const MOUNT = mountComponent(HomePanel)
		try {
			expect(MOUNT.container.innerHTML).toBeTruthy()
		} finally {
			MOUNT.app.unmount()
			MOUNT.container.remove()
		}
	})
})
