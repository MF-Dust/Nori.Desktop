import {describe, expect, it, beforeEach, vi} from "vitest"
import {createApp, h, nextTick} from "vue"
import useLanguage, {i18n} from "../../src/services/i18n"
import {RUNTIME} from "../../src/services/runtime"
import {feedback} from "../../src/services/feedback"
import ProactiveSettings from "../../src/components/settings/ProactiveSettings.vue"

describe("ProactiveSettings.vue", () => {
	const mountComponent = () => {
		const CONTAINER = document.createElement("div")
		document.body.appendChild(CONTAINER)
		const APP = createApp({
			render: () => h(ProactiveSettings),
		})
		APP.use(i18n)
		APP.mount(CONTAINER)
		return {app: APP, container: CONTAINER}
	}

	const settleView = async (): Promise<void> => {
		for (let index = 0; index < 6; index += 1) {
			await nextTick()
			await new Promise<void>(resolve => setTimeout(resolve, 10))
		}
		await nextTick()
	}

	const createMockSnapshot = (overrides?: any) => ({
		version: 1,
		app: {appVersion: "0.1.0", platform: "windows", debugCrashTestsAvailable: false, safeMode: false},
		general: {language: "zh-CN", petAutoSummon: true, sidebarCollapsed: false},
		telemetry: {consent: "granted", enabled: true, available: true},
		secretIssues: [],
		ai: {configured: true, provider: "openai", baseUrl: "", model: "gpt-4o", persona: "nori", hasApiKey: true},
		models: {selected: "arg-nori", items: [], scale: 1.0, expressions: []},
		pet: {visible: true},
		platform: {
			os: "windows",
			sessionType: "windows",
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
			renderScale: 1.0,
			maxFps: 60,
			aiInteraction: false,
		},
		voice: {
			volume: 1.0,
			ttsProvider: "openai",
			ttsBaseUrl: "",
			hasTtsApiKey: false,
			ttsVoice: "nova",
			ttsSpeed: 1.0,
			ttsAutoPlay: true,
			gptsovitsBaseUrl: "",
			gptsovitsRefAudio: "",
			gptsovitsPromptText: "",
			gptsovitsPromptLang: "zh",
			sttProvider: "whisper",
			sttBaseUrl: "",
			hasSttApiKey: false,
			noticePending: false,
			speaking: false,
		},
		embedding: {configured: false, model: "", baseUrl: "", dimensions: "", hasApiKey: false},
		memory: {
			enabled: false,
			reflectionEnabled: false,
			decayEnabled: false,
			archiveEnabled: false,
			active: 0,
			atoms: 0,
			archived: 0,
			total: 0,
			knowledgePath: "",
			knowledgeChunks: 0,
			indexState: "ready",
			indexProcessed: 0,
			indexTotal: 0,
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
			knowledgeEnabled: false,
			knowledgeWatch: false,
			debugRetrieval: false,
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
		emotion: {type: "neutral"},
		...overrides,
	})

	beforeEach(async () => {
		document.body.innerHTML = ""
		await useLanguage.setLanguage("zh-CN")
		vi.restoreAllMocks()
		;(window as any).__nori = {
			assetBase: "/nori-assets/",
			label: "main",
			invoke: async (cmd: string, _args: any) => {
				if (cmd === "ui_get_snapshot") return RUNTIME.snapshot.value ?? createMockSnapshot()
				return null
			},
			emit: () => {},
			listen: () => () => {},
			dispatch: () => {},
		}
	})

	it("renders loading skeleton when snapshot is null", async () => {
		RUNTIME.snapshot.value = null

		const MOUNT = mountComponent()
		try {
			await settleView()
			const SKELETON = MOUNT.container.querySelector("[aria-hidden='true']")
			expect(SKELETON).not.toBeNull()
		} finally {
			MOUNT.app.unmount()
			MOUNT.container.remove()
		}
	})

	it("renders empty state when reminders list is empty", async () => {
		RUNTIME.snapshot.value = createMockSnapshot({
			proactive: {
				idleEnabled: true,
				idleMinutes: 15,
				dailyGreeting: true,
				reminders: [],
			},
		}) as any

		const MOUNT = mountComponent()
		try {
			await settleView()
			expect(MOUNT.container.textContent).toContain("暂无排队提醒")
		} finally {
			MOUNT.app.unmount()
			MOUNT.container.remove()
		}
	})

	it("renders reminder items with localized absolute time, relative time, and recurrence badges", async () => {
		const FUTURE_MS = Date.now() + 15 * 60 * 1000
		RUNTIME.snapshot.value = createMockSnapshot({
			proactive: {
				idleEnabled: true,
				idleMinutes: 15,
				dailyGreeting: true,
				reminders: [
					{id: "rem-1", content: "喝一杯温水", triggerTime: FUTURE_MS},
					{id: "rem-2", content: "每日站立拉伸", triggerTime: FUTURE_MS, repeatDaily: true},
				],
			},
		}) as any

		const MOUNT = mountComponent()
		try {
			await settleView()
			expect(MOUNT.container.textContent).toContain("喝一杯温水")
			expect(MOUNT.container.textContent).toContain("单次提醒")
			expect(MOUNT.container.textContent).toContain("每日站立拉伸")
			expect(MOUNT.container.textContent).toContain("每日重复")
			expect(MOUNT.container.textContent).toContain("预计触发")
		} finally {
			MOUNT.app.unmount()
			MOUNT.container.remove()
		}
	})

	it("validates empty content on reminder addition and rejects it", async () => {
		const INVOKED: {cmd: string; args: any}[] = []
		;(window as any).__nori.invoke = async (cmd: string, args: any) => {
			if (cmd === "ui_get_snapshot") return RUNTIME.snapshot.value ?? createMockSnapshot()
			INVOKED.push({cmd, args})
			return null
		}

		RUNTIME.snapshot.value = createMockSnapshot() as any

		const MOUNT = mountComponent()
		try {
			await settleView()
			const INPUT = MOUNT.container.querySelector("input[maxlength='200']") as HTMLInputElement
			expect(INPUT).not.toBeNull()

			// Pressing Enter when input is empty
			INPUT.dispatchEvent(new KeyboardEvent("keydown", {key: "Enter"}))
			await settleView()

			expect(INVOKED.some(c => c.cmd === "reminder_add")).toBe(false)
			expect(MOUNT.container.textContent).toContain("请输入提醒内容")
		} finally {
			MOUNT.app.unmount()
			MOUNT.container.remove()
		}
	})

	it("adds a reminder and handles backend success", async () => {
		const INVOKED: {cmd: string; args: any}[] = []
		;(window as any).__nori.invoke = async (cmd: string, args: any) => {
			if (cmd === "ui_get_snapshot") return RUNTIME.snapshot.value ?? createMockSnapshot()
			INVOKED.push({cmd, args})
			if (cmd === "reminder_add") {
				return {id: "rem-new", content: args.content, triggerTime: Date.now() + args.delayMinutes * 60000}
			}
			return null
		}

		RUNTIME.snapshot.value = createMockSnapshot() as any

		const MOUNT = mountComponent()
		try {
			await settleView()
			const INPUT = MOUNT.container.querySelector("input[maxlength='200']") as HTMLInputElement
			expect(INPUT).not.toBeNull()
			INPUT.value = "开会准备"
			INPUT.dispatchEvent(new Event("input"))
			await settleView()

			const BUTTONS = Array.from(MOUNT.container.querySelectorAll("button"))
			const ADD_BTN = BUTTONS.find(b => b.textContent?.includes("添加提醒"))
			expect(ADD_BTN).toBeDefined()
			ADD_BTN?.click()
			await settleView()

			expect(INVOKED.some(c => c.cmd === "reminder_add" && c.args.content === "开会准备")).toBe(true)
		} finally {
			MOUNT.app.unmount()
			MOUNT.container.remove()
		}
	})


	it("creates a daily recurring reminder through the existing update API", async () => {
		const INVOKED: {cmd: string; args: any}[] = []
		;(window as any).__nori.invoke = async (cmd: string, args: any) => {
			if (cmd === "ui_get_snapshot") return RUNTIME.snapshot.value ?? createMockSnapshot()
			INVOKED.push({cmd, args})
			if (cmd === "reminder_add") {
				return {id: "rem-daily", content: args.content, triggerTime: Date.now() + args.delayMinutes * 60000}
			}
			if (cmd === "reminder_update") {
				return {id: args.id, content: "每日复习", triggerTime: Date.now() + 900000, repeatDaily: true}
			}
			return null
		}

		RUNTIME.snapshot.value = createMockSnapshot() as any

		const MOUNT = mountComponent()
		try {
			await settleView()
			const REPEAT_SWITCH = MOUNT.container.querySelector("button[role='switch'][aria-label='每日重复']") as HTMLButtonElement
			expect(REPEAT_SWITCH).not.toBeNull()
			REPEAT_SWITCH.click()
			await settleView()

			const INPUT = MOUNT.container.querySelector("input[maxlength='200']") as HTMLInputElement
			INPUT.value = "每日复习"
			INPUT.dispatchEvent(new Event("input"))
			await settleView()

			const ADD_BTN = Array.from(MOUNT.container.querySelectorAll("button"))
				.find(button => button.textContent?.includes("添加提醒"))
			ADD_BTN?.click()
			await settleView()

			expect(INVOKED.some(call => call.cmd === "reminder_add" && call.args.content === "每日复习")).toBe(true)
			expect(INVOKED.some(call => call.cmd === "reminder_update"
				&& call.args.id === "rem-daily"
				&& call.args.repeatDaily === true)).toBe(true)
		} finally {
			MOUNT.app.unmount()
			MOUNT.container.remove()
		}
	})

	it("handles reminder addition failure via feedback.error", async () => {
		const FEEDBACK_ERROR_SPY = vi.spyOn(feedback, "error").mockImplementation(() => {})
		;(window as any).__nori.invoke = async (cmd: string) => {
			if (cmd === "ui_get_snapshot") return RUNTIME.snapshot.value ?? createMockSnapshot()
			if (cmd === "reminder_add") {
				throw new Error("Sqlite locked")
			}
			return null
		}

		RUNTIME.snapshot.value = createMockSnapshot() as any

		const MOUNT = mountComponent()
		try {
			await settleView()
			const INPUT = MOUNT.container.querySelector("input[maxlength='200']") as HTMLInputElement
			INPUT.value = "测试提醒"
			INPUT.dispatchEvent(new Event("input"))
			await settleView()

			const BUTTONS = Array.from(MOUNT.container.querySelectorAll("button"))
			const ADD_BTN = BUTTONS.find(b => b.textContent?.includes("添加提醒"))
			ADD_BTN?.click()
			await settleView()

			expect(FEEDBACK_ERROR_SPY).toHaveBeenCalled()
		} finally {
			MOUNT.app.unmount()
			MOUNT.container.remove()
		}
	})

	it("opens cancel confirmation modal and performs cancellation", async () => {
		const INVOKED: {cmd: string; args: any}[] = []
		;(window as any).__nori.invoke = async (cmd: string, args: any) => {
			if (cmd === "ui_get_snapshot") return RUNTIME.snapshot.value ?? createMockSnapshot()
			INVOKED.push({cmd, args})
			if (cmd === "reminder_cancel") {
				return true
			}
			return null
		}

		RUNTIME.snapshot.value = createMockSnapshot({
			proactive: {
				idleEnabled: true,
				idleMinutes: 15,
				dailyGreeting: true,
				reminders: [{id: "rem-target", content: "待取消的提醒", triggerTime: Date.now() + 60000}],
			},
		}) as any

		const MOUNT = mountComponent()
		try {
			await settleView()
			// Click the cancel button on the reminder row
			const CANCEL_BTN = MOUNT.container.querySelector("button[aria-label='取消此提醒']") as HTMLButtonElement
			expect(CANCEL_BTN).not.toBeNull()
			CANCEL_BTN.click()
			await settleView()

			// Modal should be open
			expect(document.body.textContent).toContain("取消提醒确认")

			// Click confirm in the modal
			const CONFIRM_BTNS = Array.from(document.body.querySelectorAll("button")).filter(b => b.textContent?.includes("确认取消"))
			expect(CONFIRM_BTNS.length).toBeGreaterThan(0)
			CONFIRM_BTNS[0].click()
			await settleView()

			expect(INVOKED.some(c => c.cmd === "reminder_cancel" && c.args.id === "rem-target")).toBe(true)
		} finally {
			MOUNT.app.unmount()
			MOUNT.container.remove()
		}
	})

	it("handles cancellation failure via feedback.error", async () => {
		const FEEDBACK_ERROR_SPY = vi.spyOn(feedback, "error").mockImplementation(() => {})
		;(window as any).__nori.invoke = async (cmd: string) => {
			if (cmd === "ui_get_snapshot") return RUNTIME.snapshot.value ?? createMockSnapshot()
			if (cmd === "reminder_cancel") {
				throw new Error("Reminder not found")
			}
			return null
		}

		RUNTIME.snapshot.value = createMockSnapshot({
			proactive: {
				idleEnabled: true,
				idleMinutes: 15,
				dailyGreeting: true,
				reminders: [{id: "rem-fail", content: "测试失败提醒", triggerTime: Date.now() + 60000}],
			},
		}) as any

		const MOUNT = mountComponent()
		try {
			await settleView()
			const CANCEL_BTN = MOUNT.container.querySelector("button[aria-label='取消此提醒']") as HTMLButtonElement
			CANCEL_BTN.click()
			await settleView()

			const CONFIRM_BTNS = Array.from(document.body.querySelectorAll("button")).filter(b => b.textContent?.includes("确认取消"))
			CONFIRM_BTNS[0].click()
			await settleView()

			expect(FEEDBACK_ERROR_SPY).toHaveBeenCalled()
		} finally {
			MOUNT.app.unmount()
			MOUNT.container.remove()
		}
	})

	it("reacts to locale change and renders localized text in en-US", async () => {
		await useLanguage.setLanguage("en-US")

		RUNTIME.snapshot.value = createMockSnapshot({
			proactive: {
				idleEnabled: true,
				idleMinutes: 15,
				dailyGreeting: true,
				reminders: [{id: "rem-en", content: "English Reminder", triggerTime: Date.now() + 30 * 60000}],
			},
		}) as any

		const MOUNT = mountComponent()
		try {
			await settleView()
			expect(MOUNT.container.textContent).toContain("Proactive Interaction & Daily Care")
			expect(MOUNT.container.textContent).toContain("Idle Care")
			expect(MOUNT.container.textContent).toContain("Daily Greetings")
			expect(MOUNT.container.textContent).toContain("Scheduled Reminders")
			expect(MOUNT.container.textContent).toContain("One-time")
		} finally {
			MOUNT.app.unmount()
			MOUNT.container.remove()
		}
	})

	it("disables interactive controls and shows warning banner in Safe Mode", async () => {
		RUNTIME.snapshot.value = createMockSnapshot({
			app: {appVersion: "0.1.0", platform: "windows", debugCrashTestsAvailable: false, safeMode: true},
			proactive: {
				idleEnabled: false,
				idleMinutes: 15,
				dailyGreeting: false,
				reminders: [],
			},
		}) as any

		const MOUNT = mountComponent()
		try {
			await settleView()
			expect(MOUNT.container.textContent).toContain("安全模式下已自动禁用所有主动交互与定时提醒")

			// Check switch buttons are disabled
			const SWITCHES = MOUNT.container.querySelectorAll("button[role='switch']")
			for (const SW of Array.from(SWITCHES)) {
				expect((SW as HTMLButtonElement).disabled).toBe(true)
			}
		} finally {
			MOUNT.app.unmount()
			MOUNT.container.remove()
		}
	})

	it("handles refresh action and invokes RUNTIME.refresh", async () => {
		const REFRESH_SPY = vi.spyOn(RUNTIME, "refresh").mockImplementation(async () => {})

		RUNTIME.snapshot.value = createMockSnapshot() as any

		const MOUNT = mountComponent()
		try {
			await settleView()
			const REFRESH_BTN = Array.from(MOUNT.container.querySelectorAll("button")).find(b => b.textContent?.includes("刷新状态"))
			expect(REFRESH_BTN).toBeDefined()
			REFRESH_BTN?.click()
			await settleView()

			expect(REFRESH_SPY).toHaveBeenCalled()
		} finally {
			MOUNT.app.unmount()
			MOUNT.container.remove()
		}
	})

	it("renders due now for reminders whose trigger time has passed", async () => {
		const PAST_MS = Date.now() - 5000
		RUNTIME.snapshot.value = createMockSnapshot({
			proactive: {
				idleEnabled: true,
				idleMinutes: 15,
				dailyGreeting: true,
				reminders: [{id: "rem-past", content: "已过期的提醒", triggerTime: PAST_MS}],
			},
		}) as any

		const MOUNT = mountComponent()
		try {
			await settleView()
			expect(MOUNT.container.textContent).toContain("已过期的提醒")
			expect(MOUNT.container.textContent).toContain("即将或已到触发时间")
		} finally {
			MOUNT.app.unmount()
			MOUNT.container.remove()
		}
	})

	it("dispatches settings update when toggling idle care and interval", async () => {
		const INVOKED: {cmd: string; args: any}[] = []
		;(window as any).__nori.invoke = async (cmd: string, args: any) => {
			if (cmd === "ui_get_snapshot") return RUNTIME.snapshot.value ?? createMockSnapshot()
			INVOKED.push({cmd, args})
			return null
		}

		RUNTIME.snapshot.value = createMockSnapshot({
			proactive: {
				idleEnabled: true,
				idleMinutes: 15,
				dailyGreeting: true,
				reminders: [],
			},
		}) as any

		const MOUNT = mountComponent()
		try {
			await settleView()

			// Change radio interval while enabled
			const RADIOS = MOUNT.container.querySelectorAll("input[type='radio']")
			expect(RADIOS.length).toBeGreaterThanOrEqual(4)
			const RADIO_30 = RADIOS[2] as HTMLInputElement
			RADIO_30.click()
			RADIO_30.dispatchEvent(new Event("change"))
			await settleView()

			expect(INVOKED.some(c => c.cmd === "settings_update_proactive")).toBe(true)

			// Toggle idle switch
			const SWITCHES = MOUNT.container.querySelectorAll("button[role='switch']")
			expect(SWITCHES.length).toBeGreaterThanOrEqual(2)
			const IDLE_SWITCH = SWITCHES[0] as HTMLButtonElement
			IDLE_SWITCH.click()
			await settleView()

			expect(INVOKED.some(c => c.cmd === "settings_update_proactive")).toBe(true)
		} finally {
			MOUNT.app.unmount()
			MOUNT.container.remove()
		}
	})

	it("dispatches settings update when toggling daily greeting", async () => {
		const INVOKED: {cmd: string; args: any}[] = []
		;(window as any).__nori.invoke = async (cmd: string, args: any) => {
			if (cmd === "ui_get_snapshot") return RUNTIME.snapshot.value ?? createMockSnapshot()
			INVOKED.push({cmd, args})
			return null
		}

		RUNTIME.snapshot.value = createMockSnapshot({
			proactive: {
				idleEnabled: true,
				idleMinutes: 15,
				dailyGreeting: true,
				reminders: [],
			},
		}) as any

		const MOUNT = mountComponent()
		try {
			await settleView()
			const SWITCHES = MOUNT.container.querySelectorAll("button[role='switch']")
			const DAILY_SWITCH = SWITCHES[1] as HTMLButtonElement
			DAILY_SWITCH.click()
			await settleView()

			expect(INVOKED.some(c => c.cmd === "settings_update_proactive")).toBe(true)
		} finally {
			MOUNT.app.unmount()
			MOUNT.container.remove()
		}
	})
})
