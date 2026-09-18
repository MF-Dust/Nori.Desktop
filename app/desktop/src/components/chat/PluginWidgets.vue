<script setup lang="ts">
/**
 * 插件聊天卡片槽 (通用机制, 不含任何具体插件逻辑)
 *
 * 约定: 活跃插件包内存在 web/card.html 时, 宿主在此以独立 sandbox iframe 挂载,
 * 卡片通过 window.postMessage 请求宿主代理调用插件动作 (plugin_action):
 *   卡片 → 宿主: {source: "nori-plugin-widget", requestId, pluginId, actionId, args}
 *   宿主 → 卡片: {source: "nori-plugin-widget-host", requestId, result, error}
 */
import {onBeforeUnmount, onMounted, ref, type ComponentPublicInstance} from "vue"
import {feedback} from "../../services/feedback"
import {ListPluginWidgets, PluginWidgetBridge, type PluginWidgetInfo} from "../../services/runtime/pluginWidgets"
import Icon from "../Icon.vue"

const widgets = ref<PluginWidgetInfo[]>([])
const BRIDGE = new PluginWidgetBridge((message, error) => feedback.error(message, error))
let disposed = false
let refreshing = false
let refreshFailed = false
const expanded = ref(new Set<string>())
const knownIds = new Set<string>()

async function refresh(): Promise<void> {
	if (disposed || refreshing) return
	refreshing = true
	try {
		const list = await ListPluginWidgets()
		if (disposed) return
		refreshFailed = false
		BRIDGE.Retain(list)
		widgets.value = list
		// 新出现的卡片默认展开: 扫码登录这类流程不应要求用户先找到折叠入口
		let expandedChanged = false
		const next = new Set(expanded.value)
		for (const widget of list) {
			if (!knownIds.has(widget.pluginId)) {
				knownIds.add(widget.pluginId)
				next.add(widget.pluginId)
				expandedChanged = true
			}
		}
		if (expandedChanged) expanded.value = next
	} catch (error) {
		if (disposed) return
		BRIDGE.Clear()
		widgets.value = []
		if (!refreshFailed) feedback.error("加载插件卡片失败", error)
		refreshFailed = true
	} finally {
		refreshing = false
	}
}

function toggle(pluginId: string): void {
	const next = new Set(expanded.value)
	if (next.has(pluginId)) {
		next.delete(pluginId)
		const widget = widgets.value.find(item => item.pluginId === pluginId)
		if (widget) BRIDGE.Bind(widget, null)
	}
	else next.add(pluginId)
	expanded.value = next
}

function bindFrame(widget: PluginWidgetInfo, element: Element | ComponentPublicInstance | null): void {
	BRIDGE.Bind(widget, element instanceof HTMLIFrameElement ? element : null)
}

function proxyCall(event: MessageEvent): void {
	void BRIDGE.Handle(event)
}

let refreshTimer: ReturnType<typeof setInterval> | null = null

onMounted(() => {
	window.addEventListener("message", proxyCall)
	void refresh()
	// 插件激活可能晚于聊天挂载 (宿主启动后异步激活), 轮询保证卡片能及时出现
	refreshTimer = setInterval(() => {
		if (document.hidden) return
		void refresh()
	}, 10_000)
})

onBeforeUnmount(() => {
	disposed = true
	BRIDGE.Clear()
	if (refreshTimer) clearInterval(refreshTimer)
	window.removeEventListener("message", proxyCall)
})
</script>

<template>
	<div v-if="widgets.length" class="flex flex-col gap-2">
		<div
			v-for="widget in widgets"
			:key="JSON.stringify([widget.pluginId, widget.entry])"
			class="mx-4.5 mt-3 rounded-xl border border-line-subtle bg-bg-deep/60 backdrop-blur-[1rem] text-sm overflow-hidden"
		>
			<button
				type="button"
				:aria-expanded="expanded.has(widget.pluginId)"
				class="w-full flex items-center gap-2 px-3.5 py-2 text-left hover:bg-bg-hover/40"
				@click="toggle(widget.pluginId)"
			>
				<span class="text-nori-teal-bright">◆</span>
				<span class="font-medium">{{ widget.title }}</span>
				<span class="flex-1" />
				<Icon :name="expanded.has(widget.pluginId) ? 'arrow-up' : 'arrow-down'" class="w-4 h-4 text-text-muted" />
			</button>
			<iframe
				v-if="expanded.has(widget.pluginId)"
				:ref="element => bindFrame(widget, element)"
				:src="widget.entry"
				sandbox="allow-scripts"
				class="w-full border-0 bg-transparent"
				style="height: 22rem"
				:title="widget.title"
			/>
		</div>
	</div>
</template>
