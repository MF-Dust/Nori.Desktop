<script setup lang="ts">
import {computed, onMounted, ref} from "vue"
import {RUNTIME} from "../../services/runtime"
import {feedback} from "../../services/feedback"
import useLanguages from "../../services/i18n/useLanguages.ts"
import logo from "../../assets/images/logo.png"
import Icon from "../Icon.vue"
import AppSwitchRow from "../ui/AppSwitchRow.vue"
import type {IconName} from "../../services/icon"

const I18N = computed(() => useLanguages().components.firstRun.ready)

// 向导的 AI 步可跳过, 摘要照实说 (填过 = 已接入, 跳过 = 之后再补)
const props = withDefaults(defineProps<{aiConfigured?: boolean}>(), {aiConfigured: false})

const emit = defineEmits<{
	telemetryChanged: [enabled: boolean]
}>()

const telemetryEnabled = ref(true)
const telemetryConsent = ref<"unset" | "granted" | "denied">("unset")
const telemetryAvailable = ref(false)
const TELEMETRY_DESC = computed(() => {
	if (!telemetryAvailable.value) return I18N.value.telemetry.unavailable
	if (telemetryConsent.value === "unset") return I18N.value.telemetry.pending
	return telemetryEnabled.value ? I18N.value.telemetry.enabled : I18N.value.telemetry.disabled
})

onMounted(async () => {
	try {
		await RUNTIME.init()
		const SNAPSHOT = RUNTIME.snapshot.value
		if (!SNAPSHOT) return
		telemetryConsent.value = SNAPSHOT.telemetry.consent
		telemetryEnabled.value = SNAPSHOT.telemetry.consent !== "denied"
		telemetryAvailable.value = SNAPSHOT.telemetry.available
		emit("telemetryChanged", telemetryEnabled.value)
	} catch (error) {
		feedback.error(I18N.value.telemetry.saveFailed, error)
	}
})

const onTelemetryChange = (value: boolean) => {
	telemetryEnabled.value = value
	telemetryConsent.value = value ? "granted" : "denied"
	emit("telemetryChanged", value)
}

// 就绪摘要 (语言/形象/AI 三项)
const SUMMARY = computed<{icon: IconName; label: string; value: string}[]>(() => [
	{icon: "noriOS", label: I18N.value.summary.language, value: I18N.value.summary.languageValue},
	{icon: "package", label: I18N.value.summary.model, value: I18N.value.summary.modelValue},
	{icon: "cpu", label: I18N.value.summary.ai, value: props.aiConfigured ? I18N.value.summary.aiReady : I18N.value.summary.aiValue},
])
</script>

<template>
	<section key="ready" data-first-run-step="ready" class="w-full flex flex-col items-center gap-2 px-7 py-1.5 my-auto text-center">
		<div class="relative w-[6.5rem] h-[6.5rem] flex items-center justify-center">
			<img class="relative w-[5.5rem] h-[5.5rem] object-contain animate-breathe" :src="logo" alt="Nori"/>
		</div>

		<div class="flex flex-col items-center gap-0.5">
			<span class="chip-teal text-xs">
				<Icon name="sparkles" :size="11"/>
				<span>All Set &amp; Ready</span>
			</span>
			<h2 class="text-2xl font-700 glow-teal">{{ I18N.title }}</h2>
			<p class="text-xs text-text-body leading-relaxed max-w-[36rem]">{{ I18N.desc }}</p>
		</div>

		<div class="w-full max-w-[42rem] flex items-center justify-around gap-2 px-3 py-1.5 surface-card">
			<template v-for="(item, index) in SUMMARY" :key="item.label">
				<span v-if="index > 0" class="w-[0.1rem] h-5 bg-line-subtle shrink-0"/>
				<div class="flex items-center gap-2 text-left">
					<span class="w-6 h-6 shrink-0 rounded-sm flex items-center justify-center bg-nori-teal-bright/8 border border-line-subtle text-nori-teal-bright">
						<Icon :name="item.icon" :size="13"/>
					</span>
					<span class="flex flex-col gap-0.2">
						<span class="text-xs text-text-faint">{{ item.label }}</span>
						<span class="text-xs text-text-primary font-500">{{ item.value }}</span>
					</span>
				</div>
			</template>
		</div>

		<div class="w-full max-w-[42rem] px-3.5 py-1.5 surface-card text-left">
			<AppSwitchRow
				:title="I18N.telemetry.title"
				:desc="TELEMETRY_DESC"
				:model-value="telemetryEnabled"
				@update:model-value="onTelemetryChange"
			/>
		</div>

		<span class="chip text-xs py-0.8">
			<Icon name="info" :size="12" class="text-nori-teal-soft"/>
			<span>{{ I18N.initDesc }}</span>
		</span>
	</section>
</template>
