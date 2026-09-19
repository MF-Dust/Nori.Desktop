<script setup lang="ts">
/**
 * 设置卡片
 *
 * 设置页统一容器: 图标 + 标题 (+ 说明) + 内容插槽。
 */
import Icon from "../Icon.vue"
import type {IconName} from "../../services/icon"

defineProps<{
	title?: string
	icon?: IconName | string
	desc?: string
}>()
</script>

<template>
	<section class="surface-card relative overflow-hidden flex shrink-0 flex-col gap-3.5 p-4">

		<header v-if="title || $slots.header" class="flex items-center gap-2.5 min-h-[2.8rem]">
			<slot name="header">
				<span v-if="icon" class="w-7 h-7 rounded-sm flex items-center justify-center bg-nori-teal-bright/8 border border-nori-teal-bright/18 text-nori-teal-bright shrink-0">
					<Icon :name="icon" :size="15"/>
				</span>
				<div class="flex flex-col gap-0.5 min-w-0">
					<span class="title-sm">{{ title }}</span>
					<span v-if="desc" class="text-hint leading-relaxed">{{ desc }}</span>
				</div>
			</slot>
			<div class="ml-auto flex items-center gap-2 shrink-0">
				<slot name="actions"/>
			</div>
		</header>

		<div class="flex flex-col gap-3">
			<slot/>
		</div>
	</section>
</template>
