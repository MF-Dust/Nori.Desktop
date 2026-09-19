<script setup lang="ts">
/**
 * 状态药丸
 *
 * tone 决定语义配色, dot 显示状态圆点 (在线/离线一类的状态标记)。
 */
import {computed} from "vue"
import Icon from "../Icon.vue"
import type {IconName, IconMode} from "../../services/icon"

const props = withDefaults(defineProps<{
	tone?: "neutral" | "teal" | "success" | "warning" | "danger"
	icon?: IconName | string
	iconMode?: IconMode
	dot?: boolean
}>(), {
	tone: "neutral",
	dot: false,
})

const TONE_CLASS = computed(() => ({
	neutral: "chip",
	teal: "chip-teal",
	success: "chip-success",
	warning: "chip-warning",
	danger: "chip-danger",
}[props.tone]))

const DOT_CLASS = computed(() => ({
	neutral: "bg-text-faint",
	teal: "bg-nori-teal-bright",
	success: "bg-success",
	warning: "bg-warning",
	danger: "bg-danger-text",
}[props.tone]))
</script>

<template>
	<span :class="TONE_CLASS">
		<span v-if="dot" class="w-1.5 h-1.5 rounded-full shrink-0 transition-colors duration-200" :class="DOT_CLASS"/>
		<Icon v-if="icon" :name="icon" :mode="iconMode" :size="12" class="shrink-0"/>
		<slot/>
	</span>
</template>
