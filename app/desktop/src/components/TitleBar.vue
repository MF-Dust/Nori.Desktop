<script setup lang="ts">
// 窗口通用标题栏: 左侧红绿灯 + 标题 + 拖拽区, 右侧内容通过 slot 注入
import {computed, onBeforeUnmount, onMounted, ref} from "vue"
import {getCurrentWindow} from "../services/host/window"
import {RUNTIME} from "../services/runtime"
import useLanguages from "../services/i18n/useLanguages.ts"
import Icon from "./Icon.vue"
import {feedback} from "../services/feedback"

const props = withDefaults(defineProps<{
	showClose?: boolean
	showMinimize?: boolean
	closeLabel?: string
	minimizeLabel?: string
}>(), {
	showClose: false,
	showMinimize: true,
	closeLabel: "",
	minimizeLabel: "",
})

const emit = defineEmits<{
	close: []
}>()

defineSlots<{
	default?: () => unknown
}>()

const PLATFORM_I18N = computed(() => useLanguages().views.main.platform)
const UI_I18N = computed(() => useLanguages().components.ui.state)
const MAIN_I18N = computed(() => useLanguages().views.main)

const computedCloseLabel = computed(() => props.closeLabel || UI_I18N.value.close)
const computedMinimizeLabel = computed(() => props.minimizeLabel || MAIN_I18N.value.footer.minimize)
const maximized = ref(false)
const canResize = ref(false)
const changingState = ref(false)
const zoomLabel = computed(() => maximized.value ? UI_I18N.value.restoreWindow : UI_I18N.value.maximize)
let disposed = false
let receivedState = false
let unlisten: (() => void) | undefined

onMounted(async () => {
	try {
		const STOP = await RUNTIME.onWindowState(state => {
			receivedState = true
			if (!disposed) {
				maximized.value = state.maximized
				canResize.value = state.canResize
			}
		})
		if (disposed) { STOP(); return }
		unlisten = STOP
		const STATE = await RUNTIME.windowState()
		if (!disposed && !receivedState && STATE) {
			maximized.value = STATE.maximized
			canResize.value = STATE.canResize
		}
	} catch {
		// 浏览器预览无法改变宿主尺寸，最大化保持禁用。
	}
})

onBeforeUnmount(() => { disposed = true; unlisten?.() })

const minimize = async () => {
	try { await RUNTIME.minimizeWindow() }
	catch (error) { feedback.error(UI_I18N.value.windowActionFailed, error) }
}

const toggleMaximized = async () => {
	if (!canResize.value || changingState.value) return
	changingState.value = true
	try {
		const STATE = await RUNTIME.toggleWindowMaximized()
		if (!disposed) {
			maximized.value = STATE.maximized
			canResize.value = STATE.canResize
		}
	} catch (error) { feedback.error(UI_I18N.value.windowActionFailed, error) }
	finally { changingState.value = false }
}

const isControl = (event: MouseEvent) => (event.target as HTMLElement).closest("button, input, a, select, textarea")
const doubleClickTitle = (event: MouseEvent) => {
	if (event.button === 0 && !isControl(event)) void toggleMaximized()
}

// 宿主能不能接管原生拖动 (Wayland 之类拿不到 → 显示拖动手柄而不是假装能拖)
const canDrag = computed(() => RUNTIME.platform().supportsWindowDrag)

// WebView 会吞掉指针事件, 拿不到原来 data-tauri-drag-region 的效果,
// 改为按下时回调宿主, 由系统接管窗口拖动
const startDrag = (event: MouseEvent) => {
	if (!canDrag.value) return
	// 只响应标题栏空白处的左键, 按钮等交互元素不触发
	if (event.button !== 0 || event.detail > 1) return
	if (isControl(event)) return
	void getCurrentWindow().startDragging().catch(() => {
		/* 非宿主环境忽略 */
	})
}
</script>

<template>
	<div
		class="relative h-[4.4rem] shrink-0 flex items-center justify-between gap-3 pl-3.5 pr-3.5 select-none
			border-b border-line-subtle bg-bg-abyss/60"
		@mousedown="startDrag"
		@dblclick="doubleClickTitle"
	>
		<!-- 顶部极细高光线 -->

		<div class="flex items-center gap-2.5 shrink-0">
			<!-- MacOS 红绿灯风格控制按钮 (左上角) -->
			<div v-if="props.showClose || props.showMinimize" class="flex items-center gap-0 mr-1 group/traffic">
				<button
					v-if="props.showClose"
					type="button"
					class="btn-traffic group/close"
					:title="computedCloseLabel"
					:aria-label="computedCloseLabel"
					@click="emit('close')"
				>
					<span class="traffic-dot bg-traffic-close group-hover/close:bg-traffic-close-hover"><Icon name="close" :size="8" class="opacity-0 group-hover/traffic:opacity-80 group-focus-visible/close:opacity-80"/></span>
				</button>
				<button
					v-if="props.showMinimize"
					type="button"
					class="btn-traffic group/minimize"
					:title="computedMinimizeLabel"
					:aria-label="computedMinimizeLabel"
					@click="minimize"
				>
					<span class="traffic-dot bg-traffic-minimize group-hover/minimize:bg-traffic-minimize-hover"><Icon name="minus" :size="8" class="opacity-0 group-hover/traffic:opacity-80 group-focus-visible/minimize:opacity-80"/></span>
				</button>
				<button type="button" class="btn-traffic group/zoom" :title="zoomLabel" :aria-label="zoomLabel" :disabled="!canResize || changingState" @click="toggleMaximized">
					<span class="traffic-dot" :class="canResize ? 'bg-traffic-zoom group-hover/zoom:bg-traffic-zoom-hover' : 'bg-overlay-20'">
						<Icon name="plus" :size="8" class="opacity-0 group-hover/traffic:opacity-80 group-focus-visible/zoom:opacity-80"/>
					</span>
				</button>
			</div>

			<span class="inline-flex items-center justify-center w-5 h-5 rounded-full bg-nori-teal-bright/10 border border-nori-teal-bright/25 text-nori-teal-bright">
				<Icon name="sparkles" :size="11"/>
			</span>
			<!-- 渐变裁剪文字在 WebView2 合成异常时会整行不可见, 这里用实色 + 轻光晕 -->
			<span class="text-md font-700 tracking-[0.06rem] text-text-primary">Nori</span>
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
