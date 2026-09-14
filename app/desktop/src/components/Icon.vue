<script setup lang="ts">
import {computed} from "vue"
import {icon, resolveIconName, type IconName, type IconMode, type IconData} from "../services/icon"

const props = withDefaults(defineProps<{
	name: IconName | string
	mode?: IconMode
	size?: number | string
	strokeWidth?: number | string
}>(), {
	mode: "stroke",
	size: 24,
	strokeWidth: 1.75
})

// 当前图标名 (未知名称兑底, 不让一个脏数据拖崩整个页面)
const iconName = computed<IconName>(() => resolveIconName(props.name))

// 当前图标数据
const iconData = computed<IconData>(() => icon[iconName.value])

// 当前实际使用的模式
const renderMode = computed<IconMode>(() => {
	const DATA = iconData.value
	if (DATA[props.mode]?.length) return props.mode
	if (DATA.stroke) return "stroke"
	if (DATA.fill) return "fill"
	if (DATA.duotone) return "duotone"
	console.error(`图标 ${iconName.value} 不支持 ${props.mode} 模式`)
	return "stroke"
})

/**
 * 色块层 (只有 duotone 用到)。
 *
 * duotone 是**叠在描边下面的一层**, 不是另画一套图形: 轮廓只写一次, duotone 里
 * 只放需要填色的那几块。这样两种模式不会走形, 也不必维护两份几何。
 *
 * 取自 Nori 立绘本身的画法 —— 黑色块面压着白底, 薄荷色只在一处点出来。
 *
 * 改动前 duotone 是**画不出东西的**: 模板按模式给整个 svg 设 fill / stroke,
 * 而 duotone 两样都给了 none, 一旦有图标声明 duotone 就会渲染成一片空白,
 * 而且不报错。当时没有图标用它, 所以一直没人发现。
 */
const solidPaths = computed((): string[] =>
	renderMode.value === "duotone" ? iconData.value.duotone ?? [] : [])

/** 线条层。duotone 复用 stroke 的轮廓; 没有 stroke 时退回 fill。 */
const linePaths = computed((): string[] => {
	const DATA = iconData.value
	if (renderMode.value === "fill") return DATA.fill ?? []
	return DATA.stroke ?? DATA.fill ?? []
})

// fill 模式整块填色; 其余模式线条不填充
const lineFill = computed(() => renderMode.value === "fill" ? "currentColor" : "none")

const lineStroke = computed(() => renderMode.value === "fill" ? "none" : "currentColor")

const lineStrokeWidth = computed(() => renderMode.value === "fill" ? 0 : props.strokeWidth)

// 是否为加载状态
const isLoading = computed(() => {
	return iconName.value === "loading"
})
</script>

<template>
	<svg
		class="block shrink-0"
		:class="{spin: isLoading}"
		:width="size"
		:height="size"
		viewBox="0 0 24 24"
		fill="none"
		stroke="none"
		stroke-linecap="round"
		stroke-linejoin="round"
		aria-hidden="true"
		focusable="false"
	>
		<path
			v-for="(d, i) in solidPaths"
			:key="`solid-${i}`"
			:d="d"
			fill="currentColor"
			opacity="0.22"
		/>
		<path
			v-for="(d, i) in linePaths"
			:key="`line-${i}`"
			:d="d"
			:fill="lineFill"
			:stroke="lineStroke"
			:stroke-width="lineStrokeWidth"
		/>
	</svg>
</template>
