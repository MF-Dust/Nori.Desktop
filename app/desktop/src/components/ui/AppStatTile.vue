<script setup lang="ts">
/**
 * 指标磁贴 (数值 + 标签 + 状态)
 *
 * 主页导航磁贴与对话页的指标条原本各写一套, 这里统一成一种读法:
 * 大号数值在上, 标签在下, 右上角放状态。可点击时整块是一个按钮。
 */
import {computed} from "vue"
import Icon from "../Icon.vue"
import type {IconName} from "../../services/icon"

const props = withDefaults(defineProps<{
	label: string
	value: string
	icon?: IconName
	/** 数值下方的补充说明 */
	hint?: string
	tone?: "neutral" | "teal" | "success" | "warning" | "danger"
	/** 给出后整块可点击 (渲染成 button 并用它作无障碍名称) */
	actionLabel?: string
	disabled?: boolean
}>(), {
	tone: "neutral",
	disabled: false,
})

const EMIT = defineEmits<{(event: "click"): void}>()

const VALUE_CLASS = computed(() => ({
	neutral: "text-text-primary",
	teal: "text-nori-teal-bright",
	success: "text-success",
	warning: "text-warning",
	danger: "text-danger-text",
}[props.tone]))

const onClick = () => {
	if (!props.disabled && props.actionLabel) EMIT("click")
}
</script>

<template>
	<component
		:is="actionLabel ? 'button' : 'div'"
		:type="actionLabel ? 'button' : undefined"
		class="surface-card min-w-0 flex flex-col gap-1.5 px-3.5 py-3 text-left"
		:class="actionLabel ? 'focus-ring cursor-pointer hover:-translate-y-[0.1rem] disabled:(opacity-50 cursor-not-allowed)' : ''"
		:aria-label="actionLabel"
		:disabled="actionLabel ? disabled : undefined"
		@click="onClick"
	>
		<span class="flex items-center gap-1.5 text-hint">
			<Icon v-if="icon" :name="icon" :size="15"/>
			<span class="truncate">{{ label }}</span>
		</span>
		<!-- 数值可能是模型名这类长串, 统一截断以免撑破栅格列 -->
		<span class="mono text-xl font-600 leading-tight truncate" :class="VALUE_CLASS">{{ value }}</span>
		<span v-if="hint" class="text-hint truncate">{{ hint }}</span>
	</component>
</template>
