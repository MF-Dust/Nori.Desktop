<script setup lang="ts" generic="K extends string">
/**
 * 分段控件 (统一三级导航)
 *
 * McpSettings / SkillsSettings / MemorySettings 各自手抄过一套 pill tab,
 * 三份样式串已经开始漂移, 这里收敛成一个组件。
 * 遵循 tablist 语义并支持左右方向键在段之间移动。
 * 键类型是泛型的, 这样调用方的联合类型 (如 `"servers" | "builtin"`) 能透过 v-model 保留。
 */
import {computed, ref} from "vue"
import Icon from "../Icon.vue"
import type {IconName} from "../../services/icon"

export interface SegmentItem<K extends string = string> {
	key: K
	label: string
	icon?: IconName
	/** 右上角计数徽标 (0 或空表示不显示) */
	count?: number
}

const props = withDefaults(defineProps<{
	modelValue: K
	items: SegmentItem<K>[]
	/** 整组的无障碍名称 */
	label: string
	size?: "sm" | "md"
}>(), {
	size: "md",
})

const EMIT = defineEmits<{(event: "update:modelValue", value: K): void}>()

const BUTTONS = ref<HTMLButtonElement[]>([])

const SIZE_CLASS = computed(() => props.size === "sm" ? "px-3 py-1 text-xs" : "px-3.5 py-1.5 text-sm")

const select = (key: K) => {
	if (key !== props.modelValue) EMIT("update:modelValue", key)
}

// 方向键在分段之间循环, 与侧边导航的键盘行为保持一致
const onKeydown = (event: KeyboardEvent, index: number) => {
	const STEP = event.key === "ArrowRight" ? 1 : (event.key === "ArrowLeft" ? -1 : 0)
	if (STEP === 0) return
	event.preventDefault()
	const NEXT = (index + STEP + props.items.length) % props.items.length
	// eslint-disable-next-line -- Codacy误报：NEXT已按items.length取模
	select(props.items[NEXT].key)
	// eslint-disable-next-line -- Codacy误报：NEXT已按items.length取模
	BUTTONS.value[NEXT]?.focus()
}
</script>

<template>
	<div
		class="inline-flex flex-wrap items-center gap-1 p-1 rounded-pill bg-overlay-4 border border-line-subtle"
		role="tablist"
		:aria-label="label"
	>
		<button
			v-for="(item, index) in items"
			:key="item.key"
			:ref="(el) => { if (el) BUTTONS[index] = el as HTMLButtonElement }"
			type="button"
			role="tab"
			class="btn-base rounded-pill font-500 transition-all duration-150"
			:class="[
				SIZE_CLASS,
				item.key === modelValue
					? 'bg-nori-teal-bright/15 text-nori-teal-bright font-600'
					: 'text-text-muted hover:(bg-overlay-6 text-text-primary)',
			]"
			:aria-selected="item.key === modelValue"
			:tabindex="item.key === modelValue ? 0 : -1"
			@click="select(item.key)"
			@keydown="onKeydown($event, index)"
		>
			<Icon v-if="item.icon" :name="item.icon" :size="13"/>
			<span>{{ item.label }}</span>
			<span v-if="item.count" class="mono opacity-70">{{ item.count }}</span>
		</button>
	</div>
</template>
