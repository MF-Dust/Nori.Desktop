<script setup lang="ts">
import {computed, onMounted, ref, watch} from "vue"
import useLanguages from "../../services/i18n/useLanguages.ts"
import {RUNTIME} from "../../services/runtime"
import {feedback} from "../../services/feedback"
import Icon from "../Icon.vue"

const props = withDefaults(defineProps<{
	// 模型 id (资源名)
	modelId: string
	// 展示名
	modelName?: string
	// 是否可调整表情 (未启用/未安装的模型不可播放)
	expressionEnabled?: boolean
	// 初始缩放
	initialScale?: number
	// 初始表情选择
	initialExpressions?: string[]
}>(), {
	modelName: "",
	expressionEnabled: true,
	initialScale: 1,
	initialExpressions: () => [],
})

const emit = defineEmits<{
	scale: [value: number]
	expressions: [list: string[]]
}>()

const I18N = computed(() => useLanguages().views.main.model)
const UI_TEXT = computed(() => useLanguages().components.ui.state)

// ---- 大小 ----
const scale = ref(props.initialScale)
const scalePercent = computed(() => Math.round(scale.value * 100))

const onScaleInput = () => {
	emit("scale", scale.value)
}

watch(() => props.initialScale, (val) => {
	if (typeof val === "number") scale.value = val
})

// ---- 表情 ----
interface ExpressionItem {
	name: string
}

const expressions = ref<ExpressionItem[]>([])
const selected = ref<string[]>([...props.initialExpressions])
const loading = ref(true)

// 表情展示名: 优先取已知映射, 否则原样显示
const expressionLabel = (name: string): string =>
	I18N.value.expressionNames[name as keyof typeof I18N.value.expressionNames] ?? name

const selectedCountText = computed(() =>
	selected.value.length > 0
		? `${I18N.value.expressionSelected}${selected.value.length}${I18N.value.expressionCount}`
		: ""
)

// 选择表情: 单选 (再点一次取消)
const toggleExpression = (name: string) => {
	selected.value = selected.value.includes(name) ? [] : [name]
	emit("expressions", [...selected.value])
}

// 清空表情
const clearExpressions = () => {
	selected.value = []
	emit("expressions", [])
}

// 模型元数据由后端读取, 前端只渲染名称
const loadExpressions = async () => {
	if (!props.expressionEnabled || !props.modelId) {
		loading.value = false
		return
	}
	loading.value = true
	try {
		await RUNTIME.init()
		const META = await RUNTIME.modelMeta(props.modelId)
		expressions.value = (META.expressions ?? []).map(name => ({name}))
	} catch (error) {
		feedback.error(I18N.value.expressionLoadFailed, error)
		expressions.value = []
	} finally {
		loading.value = false
	}
}

watch(() => props.modelId, async (newId) => {
	if (newId) await loadExpressions()
})

watch(() => props.initialExpressions, (list) => {
	selected.value = [...(list ?? [])]
})

watch(() => props.expressionEnabled, (enabled) => {
	if (enabled) void loadExpressions()
})

onMounted(() => {
	void loadExpressions()
})
</script>

<template>
	<!-- 根容器保持 width:100% + 纵向流, 供 ModelManagement 的调整面板量取宽度 -->
	<div class="w-full flex flex-col gap-[1.8rem]">
		<h3 class="m-0 text-lg font-700 text-text-primary text-left">
			{{ I18N.adjustTitle }}
			<span class="ml-2 text-xs font-500 text-nori-teal-bright">{{ modelName || modelId }}</span>
		</h3>

		<div class="flex flex-col items-start gap-[0.9rem]">
			<span class="text-sm font-500 text-text-body">{{ I18N.scale }}</span>
			<div class="w-full flex items-center gap-3">
				<n-slider
					v-model:value="scale"
					:min="0.5"
					:max="4"
					:step="0.05"
					:format-tooltip="(v: number) => `${Math.round(v * 100)}%`"
					class="flex-1 min-w-0"
					@update:value="onScaleInput"
				/>
				<span class="w-[4.8rem] shrink-0 text-sm font-600 text-right text-nori-teal-bright mono">{{ scalePercent }}%</span>
			</div>
		</div>

		<div class="flex flex-col items-start gap-[0.9rem] w-full min-w-0">
			<div class="flex items-center justify-between w-full">
				<span class="text-sm font-500 text-text-body">{{ I18N.expression }}</span>
				<span v-if="expressionEnabled && selected.length > 0" class="text-xs font-600 text-nori-teal-bright">{{ selectedCountText }}</span>
			</div>

			<div v-if="expressionEnabled" class="w-full flex flex-col gap-2">
				<div v-if="loading" class="flex items-center gap-2 py-2 text-hint text-xs">
					<Icon name="loading" :size="13" class="spin text-nori-teal-bright"/>
					<span>{{ UI_TEXT.loading }}</span>
				</div>
				<div
					v-else-if="expressions.length > 0"
					class="w-full max-h-[16rem] overflow-y-auto scroll-area flex flex-wrap gap-2 pr-1"
				>
					<button
						type="button"
						class="pill-choice focus-ring px-3.5 py-[0.55rem] text-xs hover:-translate-y-[0.1rem]"
						:class="selected.length === 0 ? 'pill-choice-on' : 'pill-choice-off'"
						:aria-pressed="selected.length === 0"
						@click="clearExpressions"
					>
						{{ I18N.expressionNone }}
					</button>
					<button
						v-for="item in expressions"
						:key="item.name"
						type="button"
						class="pill-choice focus-ring px-3.5 py-[0.55rem] text-xs hover:-translate-y-[0.1rem]"
						:class="selected.includes(item.name) ? 'pill-choice-on' : 'pill-choice-off'"
						:aria-pressed="selected.includes(item.name)"
						@click="toggleExpression(item.name)"
					>
						{{ expressionLabel(item.name) }}
					</button>
				</div>
				<p v-else class="m-0 text-hint">{{ I18N.expressionNone }}</p>

				<p class="m-0 text-hint">{{ I18N.expressionSelectHint }}</p>
			</div>
			<div v-else>
				<p class="m-0 text-hint">{{ I18N.expressionHint }}</p>
			</div>
		</div>
	</div>
</template>
