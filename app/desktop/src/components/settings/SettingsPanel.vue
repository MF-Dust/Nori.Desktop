<script setup lang="ts">
/**
 * 设置面板
 *
 * 信息构架: 左侧二级列表 (分组) + 顶部面包屑 + 搜索过滤。
 * 搜索索引由语言包生成 (services/settings/searchIndex), 不再手写关键词, 命中的小节会直接列在列表项下。
 * 键盘: `/` 聚焦搜索, ↑/↓ 在可见项间移动, Enter 打开。
 * 「长期记忆」已提升为主窗一级页, 不在这份列表里。
 */
import {computed, ref, watch} from "vue"
import useLanguages from "../../services/i18n/useLanguages.ts"
import Icon from "../Icon.vue"
import AppSearchField from "../ui/AppSearchField.vue"
import type {IconName} from "../../services/icon"
import {buildSettingsSearchIndex, matchSettingsEntry, settingsMessageRoot} from "../../services/settings/searchIndex"
import AiSettings from "./AiSettings.vue"
import VoiceSettings from "./VoiceSettings.vue"
import ProactiveSettings from "./ProactiveSettings.vue"
import SkillsSettings from "./SkillsSettings.vue"
import McpSettings from "./McpSettings.vue"
import AutomationSettings from "./AutomationSettings.vue"
import PluginsSettings from "./PluginsSettings.vue"
import GeneralSettings from "./GeneralSettings.vue"
import DebugSettings from "./DebugSettings.vue"
import AboutSettings from "./AboutSettings.vue"

/** 二级页 key, 同时是它在语言包 `views.main` 下的子树名 (搜索索引靠这个对应) */
type SettingsTabKey = "ai" | "voice" | "proactive" | "skills" | "mcp" | "automation" | "plugins" | "general" | "updates" | "debug" | "about"

const props = withDefaults(defineProps<{
	/** 打开时直达的子页 (主页磁贴跳转用) */
	initialTab?: string
	/** 同一目标重复跳转的信号: 只看 initialTab 的话第二次点同一张磁贴不会有反应 */
	openSeq?: number
}>(), {
	initialTab: "",
	openSeq: 0,
})

const I18N = computed(() => useLanguages().views.main.settingsTabs)
const GROUP_I18N = computed(() => useLanguages().views.main.settingsGroups)
const SEARCH_I18N = computed(() => useLanguages().views.main.settingsSearch)

interface TabItem {
	key: SettingsTabKey
	label: string
	icon: IconName
}

interface TabGroup {
	title: string
	tabs: TabItem[]
}

/** 搜索命中后带上命中的小节标题 */
interface VisibleTab extends TabItem {
	matches: string[]
}

interface VisibleGroup {
	title: string
	tabs: VisibleTab[]
}

const TAB_GROUPS = computed<TabGroup[]>(() => [
	{
		title: GROUP_I18N.value.core,
		tabs: [
			{key: "ai", label: I18N.value.ai, icon: "cpu"},
		],
	},
	{
		title: GROUP_I18N.value.perception,
		tabs: [
			{key: "voice", label: I18N.value.voice, icon: "volume"},
			{key: "proactive", label: I18N.value.proactive, icon: "sparkles"},
		],
	},
	{
		title: GROUP_I18N.value.extend,
		tabs: [
			{key: "plugins", label: I18N.value.plugins, icon: "plug"},
			{key: "skills", label: I18N.value.skills, icon: "sparkles"},
			{key: "mcp", label: I18N.value.mcp, icon: "plug"},
			{key: "automation", label: I18N.value.automation, icon: "bot"},
		],
	},
	{
		title: GROUP_I18N.value.system,
		tabs: [
			{key: "general", label: I18N.value.general, icon: "settings"},
			{key: "updates", label: I18N.value.updates, icon: "refresh"},
			{key: "debug", label: I18N.value.debug, icon: "terminal"},
			{key: "about", label: I18N.value.about, icon: "info"},
		],
	},
])

const ALL_TABS = computed<TabItem[]>(() => TAB_GROUPS.value.flatMap(group => group.tabs))

const currentTab = ref<SettingsTabKey>("ai")
const keyword = ref("")

// 检索索引: 每页的真实文案 + 英文键名, 语言切换会自动重建
const SEARCH_INDEX = computed(() => {
	const ROOT = settingsMessageRoot()
	return buildSettingsSearchIndex(ALL_TABS.value.map(tab => ({
		key: tab.key,
		label: tab.label,
		page: tab.key === "updates" && ROOT?.general && typeof ROOT.general !== "string"
			? ROOT.general.updates
			: ROOT?.[tab.key],
	})))
})

// 过滤后的分组 (空搜索时全展示)
const VISIBLE_GROUPS = computed<VisibleGroup[]>(() => {
	const NEEDLE = keyword.value.trim()
	if (!NEEDLE) return TAB_GROUPS.value.map(group => ({title: group.title, tabs: group.tabs.map(tab => ({...tab, matches: []}))}))
	return TAB_GROUPS.value
		.map(group => ({
			title: group.title,
			tabs: group.tabs.flatMap(tab => {
				const MATCHES = matchSettingsEntry(SEARCH_INDEX.value.get(tab.key), NEEDLE)
				return MATCHES ? [{...tab, matches: MATCHES.slice(0, 3)}] : []
			}),
		}))
		.filter(group => group.tabs.length > 0)
})

const VISIBLE_TABS = computed<VisibleTab[]>(() => VISIBLE_GROUPS.value.flatMap(group => group.tabs))

// 当前项所属分组 (面包屑)
const currentGroupTitle = computed(() =>
	TAB_GROUPS.value.find(group => group.tabs.some(tab => tab.key === currentTab.value))?.title ?? "")
const currentTabLabel = computed(() =>
	ALL_TABS.value.find(tab => tab.key === currentTab.value)?.label ?? "")

// 搜索把当前项过滤掉时, 自动跳到第一个可见项
watch(VISIBLE_TABS, (tabs) => {
	if (tabs.length === 0) return
	if (!tabs.some(tab => tab.key === currentTab.value)) currentTab.value = tabs[0].key
})

// 主页磁贴要求直达某个子页 (openSeq 变化即重新定位, 哪怕目标没变)
watch(() => [props.initialTab, props.openSeq] as const, ([tab]) => {
	if (tab && ALL_TABS.value.some(item => item.key === tab)) {
		currentTab.value = tab as SettingsTabKey
		keyword.value = ""
	}
}, {immediate: true})

// 上下键在可见项之间移动
const moveSelection = (delta: number) => {
	const TABS = VISIBLE_TABS.value
	if (TABS.length === 0) return
	const INDEX = TABS.findIndex(tab => tab.key === currentTab.value)
	const NEXT = (INDEX + delta + TABS.length) % TABS.length
	currentTab.value = TABS[NEXT].key
}

const onListKeydown = (event: KeyboardEvent) => {
	if (event.key === "ArrowDown") {
		event.preventDefault()
		moveSelection(1)
	} else if (event.key === "ArrowUp") {
		event.preventDefault()
		moveSelection(-1)
	}
}
</script>

<template>
	<div class="w-full h-full min-h-0 flex overflow-hidden glass-panel rounded-lg shadow-[0_0.8rem_3.2rem_rgba(0,0,0,0.45)]">
		<!-- 左侧二级列表导航 -->
		<nav
			class="w-[19rem] shrink-0 flex flex-col min-h-0 border-r border-line-subtle bg-bg-abyss/60 backdrop-blur-[1.4rem]"
			:aria-label="currentTabLabel"
			@keydown="onListKeydown"
		>
			<!-- 顶部快捷搜索框 -->
			<div class="p-3.5 border-b border-line-subtle bg-bg-deep/40">
				<AppSearchField
					v-model="keyword"
					:placeholder="SEARCH_I18N.placeholder"
					:clear-label="SEARCH_I18N.clear"
					shortcut-key="/"
				/>
			</div>

			<!-- 分组列表项 -->
			<div class="flex-1 scroll-area p-3 flex flex-col gap-4">
				<div v-for="group in VISIBLE_GROUPS" :key="group.title" class="flex flex-col gap-1.2">
					<span class="px-2 text-hint font-600 uppercase tracking-[0.08rem] text-text-faint/90">{{ group.title }}</span>
					<button
						v-for="tab in group.tabs"
						:key="tab.key"
						type="button"
						class="nav-item group py-2.2 px-3"
						:class="currentTab === tab.key ? 'nav-item-active' : ''"
						:aria-current="currentTab === tab.key ? 'page' : undefined"
						@click="currentTab = tab.key"
					>
						<Icon :name="tab.icon" :size="15" class="shrink-0 transition-transform duration-200 group-hover:scale-110"/>
						<span class="min-w-0 flex flex-col items-start gap-0.5">
							<span class="max-w-full truncate font-500">{{ tab.label }}</span>
							<!-- 搜索命中的小节: 直接告诉用户命中在哪一段, 而不是只把整页留在列表里 -->
							<span
								v-for="match in tab.matches"
								:key="match"
								class="max-w-full truncate text-xs font-400 text-text-faint"
							>{{ match }}</span>
						</span>
					</button>
				</div>

				<p v-if="VISIBLE_GROUPS.length === 0" class="px-2 py-6 text-sub text-center leading-relaxed">{{ SEARCH_I18N.empty }}</p>
			</div>
		</nav>

		<!-- 右侧内容面板 -->
		<div class="flex-1 min-w-0 flex flex-col min-h-0 bg-bg-deep/30">
			<!-- 顶部面包屑导航条 -->
			<div class="relative shrink-0 flex items-center gap-2.5 px-6 py-3.5 border-b border-line-subtle bg-bg-deep/75 backdrop-blur-[1.2rem]">
				<span class="absolute top-0 inset-x-0 h-[0.1rem] bg-gradient-to-r from-transparent via-nori-teal-bright/20 to-transparent pointer-events-none"/>
				<span class="text-sm text-text-faint font-500">{{ currentGroupTitle }}</span>
				<Icon name="arrow-right" :size="11" class="text-text-faint"/>
				<span class="text-sm font-600 tracking-[0.02rem] text-nori-teal-bright [text-shadow:0_0_1rem_var(--glow-teal-soft)]">{{ currentTabLabel }}</span>
			</div>

			<!-- 内容滚动区 (KeepAlive: 来回切子页不重新拉一遍后端快照, 也不丢滚动位置与半填的表单) -->
			<div
				class="flex-1 min-h-0 flex flex-col scroll-area p-5"
				:data-settings-panel="currentTab"
			>
				<KeepAlive>
					<AiSettings v-if="currentTab === 'ai'"/>
					<VoiceSettings v-else-if="currentTab === 'voice'"/>
					<ProactiveSettings v-else-if="currentTab === 'proactive'"/>
					<SkillsSettings v-else-if="currentTab === 'skills'"/>
					<McpSettings v-else-if="currentTab === 'mcp'"/>
					<AutomationSettings v-else-if="currentTab === 'automation'"/>
					<PluginsSettings v-else-if="currentTab === 'plugins'"/>
					<GeneralSettings v-else-if="currentTab === 'general'"/>
					<GeneralSettings v-else-if="currentTab === 'updates'" updates-only/>
					<DebugSettings v-else-if="currentTab === 'debug'"/>
					<AboutSettings v-else/>
				</KeepAlive>
			</div>
		</div>
	</div>
</template>