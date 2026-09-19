<script setup lang="ts">
/**
 * 空状态 / 未配置提示
 */
import Icon from "../Icon.vue"
import type {IconName} from "../../services/icon"

withDefaults(defineProps<{
	icon?: IconName | string
	title: string
	desc?: string
	/** 大号: 用于整页空状态 */
	large?: boolean
}>(), {
	icon: "sparkles",
	large: false,
})
</script>

<template>
	<div class="flex flex-1 flex-col items-center justify-center gap-3 p-5 text-center">
		<div class="relative flex items-center justify-center" :class="large ? 'w-20 h-20' : 'w-12 h-12'">
			<Icon :name="icon" :size="large ? 36 : 22" class="relative text-nori-teal-bright"/>
		</div>
		<h3 :class="large ? 'text-2xl font-700 glow-teal' : 'title-md'">{{ title }}</h3>
		<p v-if="desc" class="text-sub max-w-[38rem] leading-relaxed">{{ desc }}</p>
		<div class="mt-1 flex items-center gap-2">
			<slot/>
		</div>
	</div>
</template>
