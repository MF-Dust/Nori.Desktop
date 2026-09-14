<script setup lang="ts">
import {computed, defineAsyncComponent, h, onBeforeUnmount, onErrorCaptured, onMounted, ref, watch} from "vue"
import {useWindowFocus} from "@vueuse/core"
import useLanguages from "../services/i18n/useLanguages.ts"
import {RUNTIME} from "../services/runtime"
import TitleBar from "../components/TitleBar.vue"
import Icon from "../components/Icon.vue"
import OperationDrawer from "../components/automation/OperationDrawer.vue"
import AppChip from "../components/ui/AppChip.vue"
import AppButton from "../components/ui/AppButton.vue"
import AppSkeleton from "../components/ui/AppSkeleton.vue"
import type {IconName} from "../services/icon"
import {showWindow, hideWindow} from "../services/window"
import {MODEL_LIST} from "../services/live2d/models"
import {feedback} from "../services/feedback"
import {installAudioHost, uninstallAudioHost} from "../services/audio"

const I18N = computed(() => useLanguages().views.main)
const UI_I18N = computed(() => useLanguages().components.ui.state)
const PLATFORM_I18N = computed(() => useLanguages().views.main.platform)
const isWindowFocused = useWindowFocus()

// 平台能力: 托盘不可用时要在主窗内补一个常驻入口 (部分 Linux 桌面环境没有 StatusNotifier)
const PLATFORM = computed(() => RUNTIME.platform())
const DEGRADED_HINTS = computed(() => {
	const HINTS: string[] = []
	if (!PLATFORM.value.supportsTray) HINTS.push(PLATFORM_I18N.value.trayUnavailable)
	if (!PLATFORM.value.supportsHitThrough) HINTS.push(PLATFORM_I18N.value.hitThroughDegraded)
	if (!PLATFORM.value.supportsGlobalCursor) HINTS.push(PLATFORM_I18N.value.cursorDegraded)
	return HINTS
})

// 异步面板: 卡住/加载失败时给出可见状态, 而不是一片空白
const PANEL_LOADING = () => h(AppSkeleton, {rows: 4})
const PANEL_ERROR = () => h("div", {class: "flex flex-1 items-center justify-center gap-2 text-base text-danger-text"}, [
	h(Icon, {name: "info", size: 18}),
	h("span", UI_I18N.value.loadFailed),
])

const PANEL_OPTIONS = {
	loadingComponent: PANEL_LOADING,
	errorComponent: PANEL_ERROR,
	delay: 0,
	timeout: 15_000,
} as const

const HomePanel = defineAsyncComponent({loader: () => import("../components/home/HomePanel.vue"), ...PANEL_OPTIONS})

// ---- 侧边导航 (「记忆」已从设置二级提升为一级页, 「关于」仍在设置的二级列表里) ----
type NavKey = "home" | "talk" | "model" | "memory" | "settings"

const aiConfigured = computed(() => RUNTIME.snapshot.value?.ai.configured ?? false)

/**
 * 侧边栏分两类，因为这两类**行为根本不一样**。
 *
 * 改动前五项画得一模一样：一样的选中样式、一样的左侧高亮条、一样的
 * `aria-current="page"`。但只有「主页」是页，其余四项点下去是**另开一个窗口**，
 * 而且 activeNav 压根不会变 —— 于是窗口开在旁边了，侧边栏还钉在「主页」上，
 * 像是什么都没发生。看起来是标签页，用起来是启动器，选中态还在撒谎。
 *
 * 现在：主页是页，其余四项是启动器，各自画各自的。
 */
const HOME_ITEM = computed(() => ({key: "home" as const, label: I18N.value.nav.home, icon: "noriOS" as IconName}))

/** 点下去会另开一个窗口的四项。`window` 是它在快照里对应的那个键。 */
const LAUNCHERS = computed<{
	key: Exclude<NavKey, "home">
	window: keyof NonNullable<typeof RUNTIME.snapshot.value>["windows"]
	label: string
	icon: IconName
	badge?: boolean
}[]>(() => [
	{key: "talk", window: "chat", label: I18N.value.nav.talk, icon: "bot"},
	{key: "model", window: "models", label: I18N.value.nav.model, icon: "package"},
	{key: "memory", window: "memory", label: I18N.value.nav.memory, icon: "memory"},
	{key: "settings", window: "settings", label: I18N.value.nav.settings, icon: "settings", badge: !aiConfigured.value},
])

/** 那个窗口此刻开着没有。开着就点亮，而不是画一个假的「已选中」。 */
const isWindowOpen = (key: "chat" | "models" | "memory" | "settings") =>
	RUNTIME.snapshot.value?.windows?.[key] ?? false

// 常驻快照通知不会丢失后台检查结果，也不会强制弹窗或抢焦点。
const UPDATE = computed(() => RUNTIME.snapshot.value?.updater)
const UPDATE_NOTICE = computed(() => UPDATE.value?.state === "available" || UPDATE.value?.state === "readytorestart")
const showUpdate = () => {
	void openNativeSettings("updates")
}

/**
 * 主窗口只有一页。
 *
 * 这里原来还有一整套「来路记录 + 返回按钮」：navOrigin / ORIGIN_LABEL / goBack，
 * 外加 activeNav 这个可变状态。但四个启动器全都在 goNav 之前 return 掉了，
 * goNav 只会被 `goNav(key)` 这一种形式调到（origin 恒为 null）—— 也就是说
 * navOrigin 永远是 null，那个返回按钮**一次都没渲染过**，activeNav 也永远是 home。
 *
 * 一并删掉。留着它会让人以为这里还有别的页可以去。
 */

const openNativeSettings = async (page?: string) => {
	try {
		await RUNTIME.openSettings(page)
	} catch (error) {
		feedback.error(UI_I18N.value.saveFailed, error)
	}
}

const openNativeModels = () => {
	void RUNTIME.openModels().catch(error => feedback.error(UI_I18N.value.loadFailed, error))
}

const openNativeChat = () => {
	void RUNTIME.openChat().catch(error => feedback.error(UI_I18N.value.loadFailed, error))
}

/** 打开对应的窗口。已经开着的会被带到前面来，这是各个 open* 自己的行为。 */
const openWindow = (key: Exclude<NavKey, "home">) => {
	if (key === "talk") return openNativeChat()
	if (key === "model") return openNativeModels()
	if (key === "memory") {
		void RUNTIME.openMemory().catch(error => feedback.error(UI_I18N.value.loadFailed, error))
		return
	}
	void openNativeSettings()
}

// ---- 侧边栏折叠 (状态持久化在 general.sidebarCollapsed) ----
const collapsed = ref(false)
watch(() => RUNTIME.snapshot.value?.general.sidebarCollapsed, (value) => {
	if (typeof value === "boolean") collapsed.value = value
}, {immediate: true})

const toggleSidebar = async () => {
	collapsed.value = !collapsed.value
	try {
		await RUNTIME.updateGeneral({sidebarCollapsed: collapsed.value})
	} catch (error) {
		feedback.error(UI_I18N.value.saveFailed, error)
	}
}

// 窗口操作: 最小化主窗口 / 退出应用
const minimizeMain = async () => {
	await hideWindow("main")
}

const exitApp = () => {
	void RUNTIME.exitApp()
}

// 伴侣当前是否显示: 宿主快照是唯一真相 (托盘切换后会广播 state-changed, 不会陈旧)
const petVisible = computed(() => RUNTIME.snapshot.value?.pet.visible ?? false)
const selectedModelName = computed(() => {
	const MODEL_ID = RUNTIME.snapshot.value?.models.selected ?? "nori"
	return MODEL_LIST.find(model => model.id === MODEL_ID)?.name ?? MODEL_ID
})

// 唤出 / 收起 Nori
const togglePet = async () => {
	try {
		if (petVisible.value) {
			await hideWindow("pet")
			await RUNTIME.writeLog("info", "主窗口收起 Nori")
		} else {
			await showWindow("pet")
			await RUNTIME.writeLog("info", "主窗口唤出 Nori")
		}
	} catch (error) {
		feedback.error(UI_I18N.value.saveFailed, error)
	} finally {
		await RUNTIME.refresh()
	}
}

// 主页磁贴跳转。三张磁贴各自开一个窗口, 和侧边栏那四项是同一批动作。
const navigate = (tab: "talk" | "model" | "settings") => {
	if (tab === "talk") {
		openNativeChat()
		return
	}
	if (tab === "model") {
		openNativeModels()
		return
	}
	if (tab === "settings") {
		void openNativeSettings("ai")
		return
	}
}

// 面板内未捕获异常不能拖崩整个主窗口
const panelError = ref("")
onErrorCaptured((error) => {
	panelError.value = error instanceof Error ? error.message : String(error)
	console.error("面板异常:", error)
	return false
})

onMounted(async () => {
	await RUNTIME.init()
	// 主窗口兼任音频宿主: TTS 播放与麦克风录音都在这里 (关窗只隐藏, 所以一直在线)
	await installAudioHost()
	void RUNTIME.writeLog("info", "主窗口 Main 挂载完成")
})

onBeforeUnmount(() => {
	uninstallAudioHost()
})
</script>

<template>
	<div
		class="window-root window-surface"
		:class="isWindowFocused ? 'window-chrome-focused' : ''"
	>
		<TitleBar
			show-close
			show-minimize
			:close-label="I18N.footer.exit"
			:minimize-label="I18N.footer.minimize"
			@close="exitApp"
			@minimize="minimizeMain"
		>
			<!-- 这里原来挂着一枚「当前页」面包屑。主窗口只有一页, 它恒等于「主页」—— 是装饰, 撤掉。 -->
			<div class="flex items-center gap-2.5">
				<OperationDrawer/>
			</div>
		</TitleBar>

		<div class="flex-1 flex min-h-0 relative">
			<!-- 侧边导航控制台 -->
			<aside
				class="shrink-0 flex flex-col justify-between gap-3 py-3 px-2.5 border-r border-line-subtle
					bg-bg-abyss/55 backdrop-blur-[1.4rem] transition-[width] duration-250 z-2"
				:class="collapsed ? 'w-[5.6rem]' : 'w-[15.5rem]'"
			>
				<nav class="flex flex-col gap-1.5" :aria-label="I18N.nav.home">
					<!-- 你在这里。主窗口只有这一页, 所以左边那道高亮条只属于它。 -->
					<div
						class="nav-item nav-item-active"
						:class="collapsed ? 'justify-center px-0' : ''"
						:title="collapsed ? HOME_ITEM.label : undefined"
						aria-current="page"
					>
						<span class="absolute left-0 top-1.5 bottom-1.5 w-[0.35rem] rounded-pill bg-gradient-to-b from-nori-teal-bright to-nori-teal shadow-[0_0_1rem_var(--glow-teal)]"/>
						<span class="flex items-center justify-center shrink-0 text-nori-teal-bright">
							<Icon :name="HOME_ITEM.icon" :size="17"/>
						</span>
						<span v-if="!collapsed" class="truncate font-500">{{ HOME_ITEM.label }}</span>
					</div>

					<!--
						下面四项是启动器, 不是标签页 —— 点下去各自开一个窗口。
						所以它们没有高亮条、没有 aria-current: 那两样是「你在这一页」的意思。
						右边那颗点说的是**那个窗口此刻开着没有** —— 真状态, 换掉了原先那个
						永远不会亮的假选中态。
					-->
					<p v-if="!collapsed" class="mt-2 mb-0.5 px-3 text-xs text-text-faint">{{ I18N.nav.openGroup }}</p>
					<span v-else class="mt-2 mb-0.5 mx-3 h-px bg-line-subtle" aria-hidden="true"/>

					<button
						v-for="item in LAUNCHERS"
						:key="item.key"
						type="button"
						class="nav-item group"
						:class="collapsed ? 'justify-center px-0' : ''"
						:title="item.label + I18N.nav.opensWindow"
						:aria-label="item.label + I18N.nav.opensWindow"
						@click="openWindow(item.key)"
					>
						<span class="flex items-center justify-center shrink-0 transition-transform duration-200 group-hover:scale-110">
							<Icon :name="item.icon" :size="17"/>
						</span>
						<span v-if="!collapsed" class="truncate font-500">{{ item.label }}</span>

						<!--
							开着才画那颗点, 没开就什么都不画。
							第一版给"没开"也画了一圈淡描边 —— line-strong 是 28% 的青色, 描在
							这么小的一个圆上, 实机根本看不见。一个看不见的提示不是提示, 只是每一行都挂着的
							一点噪声。去掉之后这颗点只剩一个意思: 那个窗口正开着。
						-->
						<span
							v-if="isWindowOpen(item.window)"
							class="w-1.6 h-1.6 rounded-full bg-nori-teal-bright animate-pulse-soft"
							:class="collapsed ? 'absolute bottom-1.5 right-1.5' : 'absolute right-3'"
							:style="{'--pulse-tint': 'var(--glow-teal)'}"
						/>
						<span
							v-if="item.badge"
							class="w-1.8 h-1.8 rounded-full bg-warning shadow-[0_0_0.8rem_var(--warning)]"
							:class="collapsed ? 'absolute top-1.5 right-1.5' : 'absolute right-7'"
						/>
					</button>
				</nav>

				<button
					type="button"
					class="nav-item justify-center text-text-faint hover:text-nori-teal-bright hover:bg-overlay-6"
					:title="collapsed ? I18N.sidebar.expand : I18N.sidebar.collapse"
					:aria-label="collapsed ? I18N.sidebar.expand : I18N.sidebar.collapse"
					@click="toggleSidebar"
				>
					<Icon :name="collapsed ? 'arrow-right' : 'arrow-left'" :size="14"/>
					<span v-if="!collapsed" class="text-xs font-500">{{ I18N.sidebar.collapse }}</span>
				</button>
			</aside>

			<!-- 主工作区 -->
			<main
				class="flex-1 min-h-0 flex flex-col items-stretch overflow-hidden px-5 py-4 relative"
				data-main-panel="home"
			>
				<div v-if="UPDATE_NOTICE" class="shrink-0 flex flex-wrap items-center justify-between gap-2 mb-2.5 px-3 py-2 border-b border-line-subtle" role="status" aria-live="polite">
					<span class="text-sm text-text-primary break-all">
						{{ UPDATE?.state === "readytorestart" ? I18N.general.updates.readyToRestart : I18N.general.updates.available }} · {{ UPDATE?.availableVersion }}
					</span>
					<AppButton size="sm" @click="showUpdate">{{ I18N.general.updates.viewUpdate }}</AppButton>
				</div>
				<p
					v-if="panelError"
					class="shrink-0 mb-2.5 px-3 py-1.5 rounded-sm text-sm text-danger-text bg-danger/12 border border-danger/28"
					role="alert"
				>{{ panelError }}</p>

				<!-- 主工作区保留主页：对话窗口交给宿主原生窗口打开。 -->
				<div class="flex-1 min-h-0 flex flex-col scroll-area">
					<HomePanel
						:pet-visible="petVisible"
						@toggle-pet="togglePet"
						@navigate="navigate"
					/>
				</div>
			</main>
		</div>

		<!-- 底部状态与操作胶囊栏 -->
		<div class="relative shrink-0 flex flex-col gap-1.5 px-5 py-2.5 border-t border-line-subtle bg-bg-abyss/75 backdrop-blur-[1.4rem]">
			<span class="absolute top-0 inset-x-0 h-[0.1rem] bg-gradient-to-r from-transparent via-nori-teal-bright/20 to-transparent pointer-events-none"/>

			<!-- 平台降级提示: 没有托盘/穿透/全局光标时明确告知, 而不是静默失效 -->
			<p
				v-for="hint in DEGRADED_HINTS"
				:key="hint"
				class="m-0 inline-flex items-center gap-1.5 text-xs text-warning"
				role="note"
			>
				<Icon name="info" :size="12"/>
				<span>{{ hint }}</span>
			</p>

			<div class="flex items-center justify-between gap-3">
				<!-- 伴侣实时连接胶囊 -->
				<div class="flex items-center gap-2">
					<AppChip :tone="petVisible ? 'success' : 'neutral'" dot>
						<span>{{ I18N.footer.petLabel }}: {{ petVisible ? I18N.footer.petOnline : I18N.footer.petOffline }}</span>
						<span class="mono opacity-80">({{ selectedModelName }})</span>
					</AppChip>
				</div>

				<div class="flex items-center gap-2.5">
					<!-- 托盘不可用时的内建退出入口 -->
					<AppButton v-if="!PLATFORM.supportsTray" icon="power" @click="exitApp">
						{{ I18N.footer.exit }}
					</AppButton>
					<AppButton
						variant="primary"
						:icon="petVisible ? 'close' : 'sparkles'"
						class="shadow-[0_0.2rem_1.4rem_var(--glow-teal-soft)] hover:shadow-[0_0.4rem_2rem_var(--glow-teal)]"
						@click="togglePet"
					>
						{{ petVisible ? I18N.hidePet : I18N.summonPet }}
					</AppButton>
				</div>
			</div>
		</div>
	</div>
</template>
