<script setup lang="ts">
import {computed, onBeforeUnmount, onMounted, ref} from "vue"
import {useWindowFocus} from "@vueuse/core"
import {RUNTIME} from "../services/runtime"
import {listen, type UnlistenFn} from "../services/host/event"
import {getCurrentWindow} from "../services/host/window"
import useLanguages from "../services/i18n/useLanguages.ts"
import TitleBar from "../components/TitleBar.vue"
import Icon from "../components/Icon.vue"
import AppButton from "../components/ui/AppButton.vue"
import logo from "../assets/images/logo.png"

const I18N = computed(() => useLanguages().views.init)
const isWindowFocused = useWindowFocus()

/** 等待宿主初始化信号的上限, 超时后给用户一条自救路径 */
const INIT_TIMEOUT_MS = 10_000

// 状态文本
const statusText = ref(I18N.value.title)

// 超时兜底面板与重试错误
const timedOut = ref(false)
const retryError = ref("")
const entering = ref(false)

// 关闭窗口
const closeApp = () => {
	void RUNTIME.exitApp()
}

let unlistenInitStart: UnlistenFn | null = null
let timeoutTimer: ReturnType<typeof setTimeout> | null = null
let started = false

const clearWatchdog = () => {
	if (timeoutTimer) clearTimeout(timeoutTimer)
	timeoutTimer = null
}

onBeforeUnmount(() => {
	if (unlistenInitStart) unlistenInitStart()
	unlistenInitStart = null
	clearWatchdog()
})

// 初始化流程: 宿主统一负责 main/pet/init 的显隐, 页面只发起一次进入请求。
const startInitFlow = async (): Promise<void> => {
	if (started) return
	started = true
	clearWatchdog()
	timedOut.value = false
	try {
		await RUNTIME.init()
		await RUNTIME.initEnterMain()
	} catch (error) {
		// 主窗口没能打开时不能静默: 退回超时面板让用户重试
		started = false
		timedOut.value = true
		retryError.value = I18N.value.enterFailed
		console.error("初始化流程失败:", error)
	}
}

// 超时面板上的手动重试
const retryEnterMain = async (): Promise<void> => {
	if (entering.value) return
	entering.value = true
	retryError.value = ""
	try {
		await startInitFlow()
	} finally {
		entering.value = false
	}
}

// 当前窗口是否可见 (宿主不可用时视为可见, 保持原行为)
const isWindowVisible = async (): Promise<boolean> => {
	try {
		return await getCurrentWindow().isVisible()
	} catch {
		return true
	}
}

onMounted(async () => {
	statusText.value = I18N.value.live2d
	await RUNTIME.init()

	// 首次运行路径下 init 窗口隐藏启动: 直接执行会在引导页旁弹出主窗口,
	// 因此要等宿主在向导完成后广播 nori:init-start。
	// 顺序很关键: 先订阅, 再握手 —— 反过来会漏掉订阅前到达的广播。
	unlistenInitStart = await listen("nori:init-start", () => {
		void startInitFlow()
	})

	let pending = false
	try {
		pending = (await RUNTIME.initReady()).initStartPending
	} catch {
		// 宿主不可用 (纯 vite 调试) 时按可见性判断
	}

	if (pending || await isWindowVisible()) {
		await startInitFlow()
		return
	}

	// 既没可见也没拿到信号: 留一条 10s 兜底出口, 不让用户永远看着转圈
	timeoutTimer = setTimeout(() => {
		if (!started) timedOut.value = true
	}, INIT_TIMEOUT_MS)
})
</script>

<template>
	<div
		class="window-root window-surface-boot"
		:class="isWindowFocused ? 'window-chrome-focused' : ''"
	>
		<TitleBar
			show-close
			:close-label="I18N.close"
			@close="closeApp"
		/>

		<div class="flex-1 min-h-0 scroll-area">
			<div class="min-h-full flex flex-col items-center justify-center gap-7 pb-6 relative">
				<!-- 品牌标识保持安静，加载状态由下方进度图标表达。 -->
				<div class="relative w-[15rem] h-[15rem] flex items-center justify-center">
					<img class="relative w-[8.2rem] h-[8.2rem] object-contain animate-breathe" :src="logo" alt="Nori"/>
				</div>

				<!-- 状态胶囊 -->
				<div
					v-if="!timedOut"
					class="inline-flex items-center gap-2.5 px-4.5 py-2 rounded-pill bg-bg-abyss/70 border border-nori-teal-bright/30 shadow-elev-1"
					role="status"
					aria-live="polite"
				>
					<Icon name="loading" class="spin text-nori-teal-bright" :size="15"/>
					<span class="text-base text-text-primary font-600 tracking-[0.03rem]">{{ statusText }}</span>
				</div>

				<!-- 宿主信号迟迟不来时的自救出口 -->
				<div v-else class="flex flex-col items-center gap-3 px-6 py-5 surface-card text-center shadow-elev-1 border-line-strong" role="alert">
					<span class="title-sm text-text-primary">{{ I18N.timeout }}</span>
					<span class="text-sub max-w-[32rem] leading-relaxed">{{ I18N.timeoutDesc }}</span>
					<AppButton variant="primary" class="mt-1.5 px-5" icon="arrow-right" :loading="entering" @click="retryEnterMain">
						{{ I18N.enterMain }}
					</AppButton>
					<span v-if="retryError" class="text-sm text-danger-text font-500">{{ retryError }}</span>
				</div>
			</div>
		</div>
	</div>
</template>
