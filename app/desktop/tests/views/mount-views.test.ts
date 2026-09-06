import {describe, expect, it, beforeEach} from "vitest"
import {createApp, h, nextTick} from "vue"
import {i18n} from "../../src/services/i18n"
import ZH from "../../src/services/i18n/locales/zh-CN"
import {mergePluginMessages} from "../../src/services/i18n/pluginMessages"
import {RUNTIME} from "../../src/services/runtime"
import Main from "../../src/views/Main.vue"
import HomePanel from "../../src/components/home/HomePanel.vue"
import ChatView from "../../src/components/ChatView.vue"
import ModelManagement from "../../src/components/settings/ModelManagement.vue"
import SettingsPanel from "../../src/components/settings/SettingsPanel.vue"
import AiSettings from "../../src/components/settings/AiSettings.vue"
import MemorySettings from "../../src/components/settings/MemorySettings.vue"
import VoiceSettings from "../../src/components/settings/VoiceSettings.vue"
import ProactiveSettings from "../../src/components/settings/ProactiveSettings.vue"
import SkillsSettings from "../../src/components/settings/SkillsSettings.vue"
import McpSettings from "../../src/components/settings/McpSettings.vue"
import AutomationSettings from "../../src/components/settings/AutomationSettings.vue"
import GeneralSettings from "../../src/components/settings/GeneralSettings.vue"
import DebugSettings from "../../src/components/settings/DebugSettings.vue"
import AboutSettings from "../../src/components/settings/AboutSettings.vue"

describe("Views and Panels Mounting", () => {
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
		(window as any).__nori = {
			assetBase: "/nori-assets/",
			label: "main",
			invoke: async (cmd: string, _args: any) => {
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

	it("switches Main tabs without leaving a blank panel", async () => {
		const MOUNT = mountComponent(Main)
		// 「记忆」已从设置二级页提升为一级页, 侧栏共 5 项
		const MAIN_TABS = ["home", "talk", "model", "memory", "settings"]
		try {
			await settleView()
			expect(MOUNT.container.innerHTML).toBeTruthy()

			const NAV_BUTTONS = Array.from(MOUNT.container.querySelectorAll("aside nav button"))
			expect(NAV_BUTTONS).toHaveLength(MAIN_TABS.length)
			for (const [INDEX, TAB] of MAIN_TABS.entries()) {
				click(NAV_BUTTONS[INDEX])
				await settleView()
				const PANELS = MOUNT.container.querySelectorAll("[data-main-panel]")
				expect(PANELS).toHaveLength(1)
				expect(PANELS[0].getAttribute("data-main-panel")).toBe(TAB)
				expect(PANELS[0].textContent?.trim()).not.toBe("")
			}

			click(NAV_BUTTONS[1])
			click(NAV_BUTTONS[2])
			click(NAV_BUTTONS[4])
			await settleView()
			const PANELS = MOUNT.container.querySelectorAll("[data-main-panel]")
			expect(PANELS).toHaveLength(1)
			expect(PANELS[0].getAttribute("data-main-panel")).toBe("settings")
		} finally {
			MOUNT.app.unmount()
			MOUNT.container.remove()
		}
	})

	it("switches all SettingsPanel tabs without leaving a blank panel", async () => {
		const MOUNT = mountComponent(SettingsPanel)
		// 设置面板包含 11 个子页
		const SETTINGS_TABS = ["ai", "voice", "proactive", "plugins", "skills", "mcp", "automation", "general", "updates", "debug", "about"]
		try {
			await settleView()
			const NAV_BUTTONS = Array.from(MOUNT.container.querySelectorAll("nav button"))
			expect(NAV_BUTTONS).toHaveLength(SETTINGS_TABS.length)
			expect(NAV_BUTTONS.some(button => button.textContent?.includes("插件"))).toBe(true)
			for (const [INDEX, TAB] of SETTINGS_TABS.entries()) {
				click(NAV_BUTTONS[INDEX])
				await settleView()
				const PANELS = MOUNT.container.querySelectorAll("[data-settings-panel]")
				expect(PANELS).toHaveLength(1)
				expect(PANELS[0].getAttribute("data-settings-panel")).toBe(TAB)
				expect(PANELS[0].textContent?.trim()).not.toBe("")
			}

			click(NAV_BUTTONS[8])
			click(NAV_BUTTONS[3])
			click(NAV_BUTTONS[0])
			await settleView()
			const PANELS = MOUNT.container.querySelectorAll("[data-settings-panel]")
			expect(PANELS).toHaveLength(1)
			expect(PANELS[0].getAttribute("data-settings-panel")).toBe("ai")

			click(NAV_BUTTONS[7])
			await settleView()
			const GENERAL_PANEL = MOUNT.container.querySelector("[data-settings-panel='general']")
			expect(GENERAL_PANEL?.textContent).toContain("鼠标穿透")
		} finally {
			MOUNT.app.unmount()
			MOUNT.container.remove()
		}
	})

	it("handles empty / null snapshot gracefully in all panels", () => {
		RUNTIME.snapshot.value = null
		const panels = [
			HomePanel,
			ChatView,
			ModelManagement,
			SettingsPanel,
			AiSettings,
			MemorySettings,
			VoiceSettings,
			ProactiveSettings,
			SkillsSettings,
			McpSettings,
			AutomationSettings,
			GeneralSettings,
			DebugSettings,
			AboutSettings,
		]
		for (const comp of panels) {
			const MOUNT = mountComponent(comp)
			try {
				expect(MOUNT.container.innerHTML).toBeTruthy()
			} finally {
				MOUNT.app.unmount()
				MOUNT.container.remove()
			}
		}
	})
})
