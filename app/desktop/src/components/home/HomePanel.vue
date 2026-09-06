<script setup lang="ts">
/**
 * 主页
 *
 * 三段式: 角色舞台 (谁在陪你) → 运行概况 (状态是否正常) → 快速前往 (去哪儿)。
 * 版本 / 协议 / 渲染引擎这类静态信息只在「关于 Nori」里出现一次, 这里不再重复。
 */
import {computed, onBeforeUnmount, onMounted, ref} from "vue"
import useLanguages from "../../services/i18n/useLanguages.ts"
import Icon from "../Icon.vue"
import AppChip from "../ui/AppChip.vue"
import AppButton from "../ui/AppButton.vue"
import AppStatTile from "../ui/AppStatTile.vue"
import type {IconMode, IconName} from "../../services/icon"
import {MODEL_LIST} from "../../services/live2d/models"
import {RUNTIME} from "../../services/runtime"
import {feedback} from "../../services/feedback"

const props = defineProps<{
	petVisible: boolean
}>()

const emit = defineEmits<{
	"toggle-pet": []
	navigate: [tab: "talk" | "model" | "settings"]
}>()

const I18N = computed(() => useLanguages().views.main.home)

// ---- 状态全部来自后端快照 ----
const SNAPSHOT = computed(() => RUNTIME.snapshot.value)
const selectedModelId = computed(() => SNAPSHOT.value?.models.selected ?? "arg-nori")
const currentModel = computed(() => MODEL_LIST.find(model => model.id === selectedModelId.value) ?? MODEL_LIST[0])
const modelInstalled = computed(() =>
	(SNAPSHOT.value?.models.items.some(item => item.id === selectedModelId.value && item.installed) ?? false)
	&& !SNAPSHOT.value?.models.loadError)
const aiConfigured = computed(() => SNAPSHOT.value?.ai.configured ?? false)
const aiProvider = computed(() => SNAPSHOT.value?.ai.provider ?? "")
const aiModel = computed(() => SNAPSHOT.value?.ai.model ?? "")
const enabledSkillsCount = computed(() => SNAPSHOT.value?.enabledSkillsCount ?? 0)
const enabledToolsCount = computed(() => (SNAPSHOT.value?.tools ?? []).filter(tool => tool.enabled).length)
const mcpServersCount = computed(() => SNAPSHOT.value?.mcpServersCount ?? 0)
const SAFE_MODE = computed(() => SNAPSHOT.value?.app.safeMode ?? false)

// ---- 快捷动作提示反馈 ----
const motionFeedback = ref(false)
let feedbackTimer: ReturnType<typeof setTimeout> | null = null

// 复制 QQ 提示
const qqCopied = ref(false)
let qqTimer: ReturnType<typeof setTimeout> | null = null

onBeforeUnmount(() => {
	if (feedbackTimer) clearTimeout(feedbackTimer)
	if (qqTimer) clearTimeout(qqTimer)
})

// 运行概况指标 (纯展示, 导航统一交给下方磁贴, 避免四块都跳同一个页面)
interface StatItem {
	key: string
	icon: IconName
	label: string
	value: string
	hint: string
	tone: "neutral" | "teal" | "warning"
}

const STATS = computed<StatItem[]>(() => [
	{
		key: "ai",
		icon: "cpu",
		label: I18N.value.stats.ai,
		value: aiConfigured.value ? I18N.value.stats.aiReady : I18N.value.stats.aiMissing,
		hint: aiModel.value || aiProvider.value || I18N.value.stats.aiProviderNone,
		tone: aiConfigured.value ? "teal" : "warning",
	},
	{
		key: "skills",
		icon: "sparkles",
		label: I18N.value.stats.skills,
		value: String(enabledSkillsCount.value),
		hint: I18N.value.stats.skillsHint,
		tone: enabledSkillsCount.value > 0 ? "teal" : "neutral",
	},
	{
		key: "tools",
		icon: "tool",
		label: I18N.value.stats.tools,
		value: String(enabledToolsCount.value),
		hint: I18N.value.stats.toolsHint,
		tone: enabledToolsCount.value > 0 ? "teal" : "neutral",
	},
	{
		key: "mcp",
		icon: "server",
		label: I18N.value.stats.mcp,
		value: String(mcpServersCount.value),
		hint: I18N.value.stats.mcpHint,
		tone: "neutral",
	},
])

// 社区外链列表
interface CommunityLink {
	key: string
	label: string
	icon: IconName
	mode?: IconMode
	url?: string
	qq?: string
}

const communityLinks = computed<CommunityLink[]>(() => [
	{
		key: "steam",
		label: I18N.value.links.steam,
		icon: "steam",
		mode: "fill",
		url: "https://store.steampowered.com/app/4996280/I_NORI/",
	},
	{
		key: "noriOS",
		label: I18N.value.links.noriOS,
		icon: "noriOS",
		mode: "stroke",
		url: "https://os.inori.ai/landing",
	},
	{
		key: "qq",
		label: qqCopied.value ? I18N.value.links.copied : I18N.value.links.qq,
		icon: "qq",
		mode: "fill",
		qq: "1041616195",
	},
	{
		key: "bilibili",
		label: I18N.value.links.bilibili,
		icon: "bilibili",
		mode: "fill",
		url: "https://space.bilibili.com/326505494",
	},
	{
		key: "github",
		label: I18N.value.links.github,
		icon: "github",
		mode: "fill",
		url: "https://github.com/MF-Dust/Nori-Desktop-Pet",
	},
])

// 导航磁贴 (状态一律由上方运行概况承担, 这里只留去处)
interface NavCard {
	key: "talk" | "model" | "settings"
	icon: IconName
	title: string
	desc: string
	action: string
}

const NAV_CARDS = computed<NavCard[]>(() => [
	{
		key: "talk",
		icon: "send",
		title: I18N.value.cards.chat.title,
		desc: I18N.value.cards.chat.desc,
		action: I18N.value.cards.chat.action,
	},
	{
		key: "model",
		icon: "package",
		title: I18N.value.cards.model.title,
		desc: I18N.value.cards.model.desc,
		action: I18N.value.cards.model.action,
	},
	{
		key: "settings",
		icon: "cpu",
		title: I18N.value.cards.ai.title,
		desc: I18N.value.cards.ai.desc,
		action: I18N.value.cards.ai.action,
	},
])

// 快速动作: 触发打招呼/随机动作
const triggerQuickMotion = async () => {
	if (!modelInstalled.value) {
		emit("navigate", "model")
		return
	}
	try {
		const PLAYED = await RUNTIME.petPlayMotion()
		if (!PLAYED) return
		motionFeedback.value = true
		if (feedbackTimer) clearTimeout(feedbackTimer)
		feedbackTimer = setTimeout(() => {
			motionFeedback.value = false
		}, 1500)
	} catch (error) {
		feedback.error(I18N.value.motionFailed, error)
	}
}

// 处理社区外链点击
const handleCommunityClick = async (link: CommunityLink) => {
	if (link.qq) {
		try {
			await RUNTIME.copyText(link.qq)
			qqCopied.value = true
			if (qqTimer) clearTimeout(qqTimer)
			qqTimer = setTimeout(() => {
				qqCopied.value = false
			}, 2000)
			await RUNTIME.writeLog("info", `已复制 QQ 交流群号: ${link.qq}`)
		} catch (error) {
			feedback.error(I18N.value.copyFailed, error)
		}
		return
	}
	if (link.url) {
		try {
			await RUNTIME.openUrl(link.url)
		} catch (error) {
			feedback.error(I18N.value.openFailed, error)
		}
	}
}

onMounted(() => {
	void RUNTIME.init()
})
</script>

<template>
	<div class="w-full h-full flex flex-col gap-4 px-0.5 py-0.5 scroll-area">
		<!-- 顶部告警: 模型缺失与安全模式, 无异常时整段不占位 -->
		<section
			v-if="!modelInstalled"
			class="flex items-center justify-between gap-3 rounded-md border border-warning/35 bg-warning/8 px-4 py-3"
			role="alert"
		>
			<div class="flex min-w-0 items-center gap-2.5">
				<Icon name="alert" :size="17" class="shrink-0 text-warning"/>
				<div class="flex min-w-0 flex-col gap-0.5">
					<span class="text-sm font-600 text-text-primary">{{ I18N.modelMissingTitle }}</span>
					<span class="text-xs text-text-muted">{{ SNAPSHOT?.models.loadError || I18N.modelMissingDesc }}</span>
				</div>
			</div>
			<AppButton icon="package" @click="emit('navigate', 'model')">{{ I18N.importModel }}</AppButton>
		</section>

		<section
			v-if="SAFE_MODE"
			class="flex min-w-0 items-center gap-2.5 rounded-md border border-warning/35 bg-warning/8 px-4 py-3"
			role="status"
		>
			<Icon name="alert" :size="17" class="shrink-0 text-warning"/>
			<div class="flex min-w-0 flex-col gap-0.5">
				<span class="text-sm font-600 text-text-primary">{{ I18N.safeModeTitle }}</span>
				<span class="text-xs text-text-muted">{{ I18N.safeModeDesc }}</span>
			</div>
		</section>

		<!-- 角色舞台 -->
		<section
			class="relative overflow-hidden flex flex-wrap items-center justify-between gap-4 p-5 rounded-lg
				bg-gradient-to-r from-bg-card/90 via-bg-card to-bg-panel/40 border border-line-strong backdrop-blur-[1.6rem]
				shadow-[0_0.8rem_3.2rem_rgba(0,0,0,0.45),inset_0_0_0_0.1rem_var(--line-subtle)]"
		>
			<!-- 深海径向光晕背景 -->
			<span class="absolute -top-1/2 -left-[15%] w-[42rem] h-[24rem] opacity-35 pointer-events-none bg-[radial-gradient(circle,var(--glow-teal)_0%,transparent_68%)]"/>
			<span class="absolute top-0 inset-x-0 h-[0.1rem] bg-gradient-to-r from-transparent via-nori-teal-bright/30 to-transparent pointer-events-none"/>

			<div class="relative flex items-center gap-5 min-w-0">
				<!-- 头像与在线呼吸环 -->
				<div class="relative w-[5.8rem] h-[5.8rem] shrink-0 rounded-full flex items-center justify-center bg-bg-deep/90 border-2 border-nori-teal-bright/35 overflow-hidden shadow-[0_0_2rem_var(--glow-teal-soft)]">
					<img :src="currentModel.thumb" :alt="currentModel.name" class="w-full h-full object-cover object-top transition-transform duration-300 hover:scale-110"/>
					<span
						class="absolute bottom-0 right-0 w-[1.3rem] h-[1.3rem] rounded-full border-2 border-bg-abyss"
						:class="props.petVisible ? 'bg-success shadow-[0_0_0.8rem_var(--success)] animate-pulse' : 'bg-text-faint'"
					/>
				</div>

				<div class="flex flex-col gap-1.5 min-w-0">
					<div class="flex items-center gap-2.5 flex-wrap">
						<h2 class="title-lg tracking-[0.02rem] text-text-primary [text-shadow:0_0_1.4rem_var(--glow-teal-soft)]">{{ currentModel.name }}</h2>
						<AppChip :tone="props.petVisible ? 'success' : 'neutral'" dot>
							{{ props.petVisible ? I18N.petStatusOnline : I18N.petStatusOffline }}
						</AppChip>
					</div>
					<p class="text-xs text-text-muted leading-relaxed">
						{{ props.petVisible ? I18N.petStatusDescOnline : I18N.petStatusDescOffline }}
					</p>
				</div>
			</div>

			<div class="relative flex items-center gap-2.5 shrink-0">
				<AppButton
					:variant="props.petVisible ? 'ghost' : 'primary'"
					:icon="modelInstalled ? (props.petVisible ? 'close' : 'sparkles') : 'package'"
					class="px-4 py-2"
					@click="modelInstalled ? emit('toggle-pet') : emit('navigate', 'model')"
				>
					{{ modelInstalled ? (props.petVisible ? I18N.hidePet : I18N.summonPet) : I18N.importModel }}
				</AppButton>

				<AppButton
					v-if="props.petVisible && modelInstalled"
					icon="sparkles"
					:disabled="motionFeedback"
					class="px-3.5 py-2"
					@click="triggerQuickMotion"
				>
					{{ motionFeedback ? I18N.quickMotionDone : I18N.quickMotion }}
				</AppButton>
			</div>
		</section>

		<!-- 运行概况 -->
		<section class="flex flex-col gap-2">
			<h3 class="text-hint uppercase font-600 tracking-[0.06rem]">{{ I18N.stats.title }}</h3>
			<div class="grid gap-2.5 grid-cols-2 md:grid-cols-4">
				<AppStatTile
					v-for="item in STATS"
					:key="item.key"
					:icon="item.icon"
					:label="item.label"
					:value="item.value"
					:hint="item.hint"
					:tone="item.tone"
				/>
			</div>
		</section>

		<!-- 快速前往 -->
		<section class="flex flex-col gap-2">
			<h3 class="text-hint uppercase font-600 tracking-[0.06rem]">{{ I18N.cards.title }}</h3>
			<div class="grid gap-2.5 grid-cols-1 md:grid-cols-3">
				<button
					v-for="card in NAV_CARDS"
					:key="card.key"
					type="button"
					class="group relative overflow-hidden flex flex-col gap-2 p-4 text-left
						surface-card cursor-pointer transition-all duration-200 focus-ring
						hover:(border-line-glow bg-bg-card-hover -translate-y-[0.2rem] shadow-[0_0.8rem_2.4rem_rgba(0,0,0,0.5),0_0_1.6rem_var(--glow-teal-soft)])"
					@click="emit('navigate', card.key)"
				>
					<span class="absolute top-0 inset-x-0 h-[0.1rem] bg-gradient-to-r from-transparent via-nori-teal-bright/0 to-transparent transition-all duration-300 group-hover:via-nori-teal-bright/40"/>

					<div class="flex items-center gap-2.5 min-w-0">
						<span class="w-8 h-8 shrink-0 rounded-sm flex items-center justify-center border border-line-subtle text-nori-teal-bright bg-nori-teal-bright/10 transition-all duration-200 group-hover:(border-nori-teal-bright/40 shadow-[0_0_1.2rem_var(--glow-teal-soft)])">
							<Icon :name="card.icon" :size="16"/>
						</span>
						<span class="title-sm truncate text-text-primary transition-colors group-hover:text-nori-teal-bright">{{ card.title }}</span>
					</div>

					<span class="text-xs text-text-muted leading-relaxed">{{ card.desc }}</span>

					<span class="mt-auto pt-2 flex items-center justify-between border-t border-line-subtle text-xs text-text-muted transition-colors group-hover:text-nori-teal-bright">
						<span class="font-500">{{ card.action }}</span>
						<Icon name="arrow-right" :size="14" class="transition-transform duration-200 group-hover:translate-x-1"/>
					</span>
				</button>
			</div>
		</section>

		<!-- 生态社区 -->
		<section class="flex flex-col gap-2 pt-2 border-t border-line-subtle">
			<h3 class="text-hint uppercase font-600 tracking-[0.06rem]">{{ I18N.links.title }}</h3>
			<div class="flex flex-wrap gap-2.5">
				<button
					v-for="item in communityLinks"
					:key="item.key"
					type="button"
					class="btn-ghost px-3.5 py-1.8"
					:class="item.key === 'qq' && qqCopied ? 'bg-success/15 border-success/40 text-success' : ''"
					@click="handleCommunityClick(item)"
				>
					<Icon :name="item.icon" :mode="item.mode" :size="14"/>
					<span class="font-500">{{ item.label }}</span>
				</button>
			</div>
		</section>
	</div>
</template>
