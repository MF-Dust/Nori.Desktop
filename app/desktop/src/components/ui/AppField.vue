<script setup lang="ts">
/**
 * 表单字段
 *
 * 标签 + 控件 + 提示/错误。saved 用于债务防抖保存后的「已保存」反馈。
 */
import useLanguages from "../../services/i18n/useLanguages"
import {computed} from "vue"
import Icon from "../Icon.vue"

const props = withDefaults(defineProps<{
	label: string
	hint?: string
	error?: string
	/** 保存状态 */
	state?: "idle" | "saving" | "saved" | "error"
}>(), {
	state: "idle",
})

const I18N = computed(() => useLanguages().components.ui.field)
const STATE_TEXT = computed(() => {
	if (props.state === "saving") return I18N.value.saving
	if (props.state === "saved") return I18N.value.saved
	return ""
})
</script>

<template>
	<label class="field">
		<span class="flex items-center gap-2">
			<span class="field-label">{{ label }}</span>
			<span
				v-if="STATE_TEXT"
				class="inline-flex items-center gap-1 text-xs"
				:class="state === 'saved' ? 'text-success' : 'text-text-faint'"
				aria-live="polite"
			>
				<Icon :name="state === 'saving' ? 'loading' : 'check'" :class="{spin: state === 'saving'}" :size="11"/>
				<span>{{ STATE_TEXT }}</span>
			</span>
		</span>

		<slot/>

		<span v-if="error" class="text-xs text-danger-text" role="alert">{{ error }}</span>
		<span v-else-if="hint" class="text-hint">{{ hint }}</span>
	</label>
</template>
