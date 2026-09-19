import {describe, expect, it, beforeEach} from "vitest"
import {createApp, h, nextTick} from "vue"
import useLanguage, {i18n} from "../../src/services/i18n"
import {RUNTIME} from "../../src/services/runtime"
import FirstRunView from "../../src/views/FirstRunView.vue"

/**
 * 首次运行向导挂载回归
 *
 * 向导是唯一一条「用户必经且只走一次」的路径, 出错没有第二次机会, 所以这里
 * 把五步全走一遍: 每步都要渲染出内容、步进器要跟着走, AI 步既要能验证凭据
 * 落盘, 也要能跳过而不打后端。
 */

/** 记录本次挂载里打过的后端命令 */
interface InvokeLog {
	cmd: string
	args: Record<string, unknown> | undefined
}

const SNAPSHOT = {
	version: 1,
	app: {appVersion: "0.0.0-test", productVersion: "v0.0.0-test", platform: "windows", debugCrashTestsAvailable: false},
	general: {language: "zh-CN", petAutoSummon: true, backgroundBlurEnabled: true, sidebarCollapsed: false},
	telemetry: {consent: "unset", enabled: false, available: true},
	secretIssues: [],
	ai: {configured: false, provider: "openai", baseUrl: "", model: "", persona: "nori", hasApiKey: false},
	models: {
		selected: "arg-nori",
		items: [{id: "arg-nori", installed: true}, {id: "nori", installed: true}],
		loadError: null,
		scale: 1,
		expressions: [],
	},
	pet: {visible: false, renderMetrics: null},
	platform: {
		os: "windows",
		sessionType: "x11",
		supportsGlobalCursor: true,
		supportsWindowDrag: true,
		supportsHitThrough: true,
		supportsTopmost: true,
		supportsTray: true,
	},
	behaviors: {},
	memory: {},
	voice: {},
	embedding: {},
	proactive: {idleEnabled: false, idleMinutes: 15, dailyGreeting: false, reminders: []},
	skills: [],
	enabledSkillsCount: 0,
	tools: [],
	mcpServersCount: 0,
	emotion: {type: "neutral"},
}

describe("首次运行向导挂载", () => {
	let calls: InvokeLog[] = []

	// 语言包是懒加载的, 先等它落位, 断言才能按中文文案来
	beforeEach(async () => {
		await useLanguage.setLanguage("zh-CN")
		calls = []
		;(window as never as {__nori: unknown}).__nori = {
			assetBase: "/nori-assets/",
			label: "first-run",
			invoke: async (cmd: string, args: Record<string, unknown> | undefined) => {
				calls.push({cmd, args})
				if (cmd === "ui_get_snapshot") return SNAPSHOT
				if (cmd === "llm_fetch_models") return ["gpt-4o-mini", "gpt-4o"]
				return null
			},
			emit: () => {},
			listen: () => () => {},
			dispatch: () => {},
		}
		RUNTIME.snapshot.value = SNAPSHOT as never
	})

	const mountWizard = () => {
		const CONTAINER = document.createElement("div")
		document.body.appendChild(CONTAINER)
		const APP = createApp({render: () => h(FirstRunView)})
		APP.use(i18n)
		APP.mount(CONTAINER)
		return {app: APP, container: CONTAINER}
	}

	/**
	 * 等 Vue 与步骤切换动画都落定
	 *
	 * 舞台用的是 `<Transition mode="out-in">`: 旧步骤的离开阶段跑在
	 * requestAnimationFrame 里 (jsdom 走真实 16ms 计时), 离开没结束前新步骤
	 * 根本不会挂上去 —— 只 await nextTick 的话断言会一直读到上一步。
	 * 所以这里交替让出微任务与宏任务, 给动画留出真实时间。
	 */
	const settle = async (): Promise<void> => {
		for (let round = 0; round < 6; round += 1) {
			await nextTick()
			await new Promise<void>(resolve => setTimeout(resolve, 20))
		}
		await nextTick()
	}

	/** 当前渲染到哪一步 (Transition 的 out-in 期间可能同时不存在, settle 之后必然只剩一个) */
	const stepOf = (container: Element): string | null =>
		container.querySelector("[data-first-run-step]")?.getAttribute("data-first-run-step") ?? null

	/** 底部导航按钮 (第一个是上一步/占位, 最后一个是下一步/开始) */
	const buttonByText = (container: Element, needle: string): HTMLButtonElement | null =>
		Array.from(container.querySelectorAll("button")).find(
			button => (button.textContent ?? "").includes(needle),
		) ?? null

	const click = async (element: Element | null): Promise<void> => {
		expect(element).toBeTruthy()
		element?.dispatchEvent(new MouseEvent("click", {bubbles: true}))
		await settle()
	}

	const NEXT = "下一步"

	it("五步依次渲染, 步进器跟着走", async () => {
		const MOUNT = mountWizard()
		try {
			await settle()
			expect(stepOf(MOUNT.container)).toBe("welcome")
			// 步进器: 一共 5 段
			expect(MOUNT.container.textContent).toContain("1 / 5")

			for (const [INDEX, STEP] of ["language", "model", "ai", "ready"].entries()) {
				await click(buttonByText(MOUNT.container, NEXT))
				expect(stepOf(MOUNT.container), STEP).toBe(STEP)
				expect(MOUNT.container.querySelector("[data-first-run-step]")?.textContent?.trim()).not.toBe("")
				expect(MOUNT.container.textContent).toContain(`${INDEX + 2} / 5`)
			}

			// 末步换成「开始」, 不再有下一步
			expect(buttonByText(MOUNT.container, NEXT)).toBeNull()
			expect(buttonByText(MOUNT.container, "开始")).toBeTruthy()
		} finally {
			MOUNT.app.unmount()
			MOUNT.container.remove()
		}
	})

	it("AI 步填了密钥: 能拉模型, 离开时落盘并在就绪页标记已接入", async () => {
		const MOUNT = mountWizard()
		try {
			await settle()
			await click(buttonByText(MOUNT.container, NEXT))
			await click(buttonByText(MOUNT.container, NEXT))
			await click(buttonByText(MOUNT.container, NEXT))
			expect(stepOf(MOUNT.container)).toBe("ai")

			const KEY_INPUT = MOUNT.container.querySelector("input[type=password]") as HTMLInputElement | null
			expect(KEY_INPUT).toBeTruthy()
			if (!KEY_INPUT) return
			KEY_INPUT.value = "sk-test-key"
			KEY_INPUT.dispatchEvent(new Event("input", {bubbles: true}))
			await settle()

			await click(buttonByText(MOUNT.container, "获取模型"))
			const FETCH = calls.filter(item => item.cmd === "llm_fetch_models")
			expect(FETCH).toHaveLength(1)
			expect(FETCH[0].args).toEqual({
				provider: "openai",
				baseUrl: "https://api.openai.com/v1",
				apiKey: "sk-test-key",
			})
			// 拉到列表就算验证通过, 并自动选上第一个模型
			expect(MOUNT.container.textContent).toContain("凭据可用")

			await click(buttonByText(MOUNT.container, NEXT))
			expect(stepOf(MOUNT.container)).toBe("ready")
			const SAVED = calls.filter(item => item.cmd === "settings_update_ai_providers")
			expect(SAVED).toHaveLength(1)
			expect(SAVED[0].args).toEqual({
				chat: {
					provider: "openai",
					baseUrl: "https://api.openai.com/v1",
					apiKey: "sk-test-key",
					model: "gpt-4o-mini",
				},
			})
			// 就绪页摘要照实反映这一步的结果
			expect(MOUNT.container.textContent).toContain("已接入")
		} finally {
			MOUNT.app.unmount()
			MOUNT.container.remove()
		}
	})

	it("AI 步跳过: 不打后端, 就绪页仍提示之后可补", async () => {
		const MOUNT = mountWizard()
		try {
			await settle()
			await click(buttonByText(MOUNT.container, NEXT))
			await click(buttonByText(MOUNT.container, NEXT))
			await click(buttonByText(MOUNT.container, NEXT))
			expect(stepOf(MOUNT.container)).toBe("ai")

			await click(buttonByText(MOUNT.container, "跳过这一步"))
			expect(stepOf(MOUNT.container)).toBe("ready")
			expect(calls.some(item => item.cmd === "settings_update_ai_providers")).toBe(false)
			expect(MOUNT.container.textContent).toContain("可在主界面扩展")
		} finally {
			MOUNT.app.unmount()
			MOUNT.container.remove()
		}
	})
})
