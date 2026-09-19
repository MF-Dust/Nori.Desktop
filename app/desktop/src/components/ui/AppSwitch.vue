<script setup lang="ts">
/**
 * 开关 (替代 n-switch)
 *
 * 语义上是 role="switch" 的按钮: 键盘可达、屏幕阅读器可读, 外观全部由原子类描述,
 * 不依赖 naive 的运行时样式注入。受控组件, 只认 v-model。
 */
const props = withDefaults(defineProps<{
	modelValue: boolean
	/** 无障碍名称: 外层没有 <label> 关联时必须给 */
	label?: string
	disabled?: boolean
	loading?: boolean
}>(), {
	disabled: false,
	loading: false,
})

const EMIT = defineEmits<{(event: "update:modelValue", value: boolean): void}>()

const toggle = () => {
	if (props.disabled || props.loading) return
	EMIT("update:modelValue", !props.modelValue)
}
</script>

<template>
	<button
		type="button"
		role="switch"
		class="btn-base relative w-[4.4rem] h-[2.4rem] shrink-0 rounded-pill border transition-all duration-200"
		:class="modelValue
			? 'bg-nori-teal border-nori-teal'
			: 'bg-overlay-8 border-line-subtle hover:not-disabled:border-line-strong'"
		:aria-checked="modelValue"
		:aria-label="label"
		:aria-busy="loading"
		:disabled="disabled || loading"
		@click="toggle"
	>
		<span
			class="absolute top-[0.3rem] w-[1.8rem] h-[1.8rem] rounded-full transition-all duration-200"
			:class="modelValue ? 'left-[2.3rem] bg-bg-abyss' : 'left-[0.3rem] bg-text-muted'"
		/>
	</button>
</template>
