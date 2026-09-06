<script setup lang="ts">
// 窗口通用标题栏: 左侧红绿灯 + 标题 + 拖拽区, 右侧内容通过 slot 注入
import {computed} from "vue"
import {getCurrentWindow} from "../services/host/window"
import {RUNTIME} from "../services/runtime"
import useLanguages from "../services/i18n/useLanguages.ts"
import Icon from "./Icon.vue"

const props = withDefaults(defineProps<{
	showClose?: boolean
	showMinimize?: boolean
	closeLabel?: string
	minimizeLabel?: string
}>(), {
	showClose: false,
	showMinimize: false,
	closeLabel: "",
	minimizeLabel: "",
})

const emit = defineEmits<{
	close: []
	minimize: []
}>()

defineSlots<{
	default?: () => unknown
}>()

const PLATFORM_I18N = computed(() => useLanguages().views.main.platform)
const UI_I18N = computed(() => useLanguages().components.ui.state)
const MAIN_I18N = computed(() => useLanguages().views.main)

const computedCloseLabel = computed(() => props.closeLabel || UI_I18N.value.close)
const computedMinimizeLabel = computed(() => props.minimizeLabel || MAIN_I18N.value.footer.minimize)

// 宿主能不能接管原生拖动 (Wayland 之类拿不到 → 显示拖动手柄而不是假装能拖)
const canDrag = computed(() => RUNTIME.platform().supportsWindowDrag)

// WebView 会吞掉指针事件, 拿不到原来 data-tauri-drag-region 的效果,
// 改为按下时回调宿主, 由系统接管窗口拖动
const startDrag = (event: MouseEvent) => {
	if (!canDrag.value) return
	// 只响应标题栏空白处的左键, 按钮等交互元素不触发
	if (event.button !== 0) return
	if ((event.target as HTMLElement).closest("button, input, a, select, textarea")) return
	void getCurrentWindow().startDragging().catch(() => {
		/* 非宿主环境忽略 */
	})
}
</script>

<template>
	<div
		class="relative h-[4.4rem] shrink-0 flex items-center justify-between gap-3 pl-3.5 pr-3.5 select-none
			border-b border-line-subtle bg-bg-abyss/60 backdrop-blur-[1rem]"
		@mousedown="startDrag"
	>
		<!-- 顶部极细高光线 -->
		<span class="absolute top-0 inset-x-0 h-[0.1rem] bg-gradient-to-r from-transparent via-nori-teal-bright/20 to-transparent pointer-events-none"/>

		<div class="flex items-center gap-2.5 shrink-0">
			<!-- MacOS 红绿灯风格控制按钮 (左上角) -->
			<div v-if="props.showClose || props.showMinimize" class="flex items-center gap-1.8 mr-1 group/traffic">
				<button
					v-if="props.showClose"
					type="button"
					class="btn-traffic-close"
					:title="computedCloseLabel"
					:aria-label="computedCloseLabel"
					@click="emit('close')"
				>
					<Icon name="close" :size="7"/>
				</button>
				<button
					v-if="props.showMinimize"
					type="button"
					class="btn-traffic-min"
					:title="computedMinimizeLabel"
					:aria-label="computedMinimizeLabel"
					@click="emit('minimize')"
				>
					<Icon name="minus" :size="7"/>
				</button>
			</div>

			<span class="inline-flex items-center justify-center w-5 h-5 rounded-full bg-nori-teal-bright/10 border border-nori-teal-bright/25 text-nori-teal-bright shadow-[0_0_0.8rem_var(--glow-teal-soft)]">
				<Icon name="sparkles" :size="11"/>
			</span>
			<!-- 渐变裁剪文字在 WebView2 合成异常时会整行不可见, 这里用实色 + 轻光晕 -->
			<span class="text-md font-700 tracking-[0.06rem] text-text-primary [text-shadow:0_0_1.2rem_var(--glow-teal-soft)]">Nori</span>
			<span class="px-1.5 py-0.2 rounded-pill text-xs font-500 bg-overlay-4 border border-line-subtle text-text-faint mono">Nori OS</span>
			<!-- 拿不到原生拖动时给一个明确的提示图标, 不让用户以为标题栏坏了 -->
			<span
				v-if="!canDrag"
				class="ml-1 inline-flex items-center text-text-faint cursor-default"
				:title="PLATFORM_I18N.dragHandle"
				:aria-label="PLATFORM_I18N.dragHandle"
			>
				<Icon name="arrow-up" :size="11"/>
			</span>
		</div>
		<slot/>
	</div>
</template>
