<script setup lang="ts">
import {computed, onMounted, ref, watch} from "vue"
import useLanguages from "../../services/i18n/useLanguages.ts"
import {useSnapshotSave} from "../../composables/useSnapshotSave"
import {feedback} from "../../services/feedback"
import {RUNTIME} from "../../services/runtime"
import AppSwitchRow from "../ui/AppSwitchRow.vue"

const props = withDefaults(defineProps<{
	modelId?: string
}>(), {
	modelId: "",
})

const I18N = computed(() => useLanguages().views.main.model.behavior)

// ---- 状态来自后端快照 ----
const autoBlink = ref(true)
const eyeTracking = ref(true)
const idleEyeAnimation = ref(true)
const idleAnimation = ref(true)
const clickInteraction = ref(true)
const clickThrough = ref(false)
const expressionEnabled = ref(true)
const lipSync = ref(true)
const shadow = ref(true)
const beatSync = ref(false)
const aiInteraction = ref(false)
const renderScale = ref(2)
const maxFps = ref(0)

// 是否已配置 AI (未配置时禁用 AI 互动开关)
const aiConfigured = computed(() => Boolean(RUNTIME.snapshot.value?.ai.configured))
const clickThroughSupported = computed(() => RUNTIME.platform().supportsHitThrough)

// 保存辅助: 每个字段独立防抖 timer + 卸载 flush (规范要求); 设置页统一走 useSnapshotSave
const SAVE = useSnapshotSave({
	onError: (_key, error) => feedback.error(I18N.value.saveFailed, error),
})

const saveBehavior = (key: string, value: boolean | number) => {
	void SAVE.saveNow(key, () => RUNTIME.setModelBehavior({[key]: value}))
}

const makeToggle = (key: string, state: {value: boolean}) => computed({
	get: () => state.value,
	set: (value: boolean) => {
		state.value = value
		saveBehavior(key, value)
	},
})

const autoBlinkToggle = makeToggle("autoBlink", autoBlink)
const eyeTrackingToggle = makeToggle("eyeTracking", eyeTracking)
const idleEyeToggle = makeToggle("idleEyeAnimation", idleEyeAnimation)
const idleAnimToggle = makeToggle("idleAnimation", idleAnimation)
const clickInteractionToggle = makeToggle("clickInteraction", clickInteraction)
const clickThroughToggle = makeToggle("clickThrough", clickThrough)
const expressionEnabledToggle = makeToggle("expressionEnabled", expressionEnabled)
const lipSyncToggle = makeToggle("lipSync", lipSync)
const shadowToggle = computed({
	get: () => shadow.value,
	set: (value: boolean) => {
		if (!props.modelId) return
		shadow.value = value
		void SAVE.saveNow("shadow", () => RUNTIME.setModelDisplay(props.modelId, {shadow: value}))
	},
})
const beatSyncToggle = makeToggle("beatSync", beatSync)
const aiInteractionToggle = computed({
	get: () => aiInteraction.value && aiConfigured.value,
	set: (value: boolean) => {
		aiInteraction.value = value
		saveBehavior("aiInteraction", value)
	},
})

onMounted(async () => {
	await RUNTIME.init()
	const BEHAVIOR = RUNTIME.snapshot.value?.behaviors
	if (BEHAVIOR) {
		autoBlink.value = BEHAVIOR.autoBlink
		eyeTracking.value = BEHAVIOR.eyeTracking
		idleEyeAnimation.value = BEHAVIOR.idleEyeAnimation
		idleAnimation.value = BEHAVIOR.idleAnimation
		clickInteraction.value = BEHAVIOR.clickInteraction
		clickThrough.value = BEHAVIOR.clickThrough ?? false
		expressionEnabled.value = BEHAVIOR.expressionEnabled
		lipSync.value = BEHAVIOR.lipSync
		shadow.value = BEHAVIOR.shadow
		beatSync.value = BEHAVIOR.beatSync
		aiInteraction.value = BEHAVIOR.aiInteraction ?? false
		renderScale.value = BEHAVIOR.renderScale
		maxFps.value = BEHAVIOR.maxFps
	}
	if (props.modelId) await loadModelDisplay(props.modelId)
})

const loadModelDisplay = async (modelId: string): Promise<void> => {
	try {
		const META = await RUNTIME.modelMeta(modelId)
		shadow.value = META.shadow
		renderScale.value = META.renderScale
		maxFps.value = META.maxFps
	} catch (error) {
		feedback.error(I18N.value.scaleLoadFailed, error)
	}
}

watch(() => props.modelId, async modelId => {
	if (modelId) await loadModelDisplay(modelId)
})

const onRenderScaleUpdate = (value: number) => {
	if (!props.modelId) return
	renderScale.value = value
	SAVE.save("renderScale", () => RUNTIME.setModelDisplay(props.modelId, {renderScale: value}))
}

const onMaxFpsUpdate = (value: number) => {
	if (!props.modelId) return
	maxFps.value = value
	void SAVE.saveNow("maxFps", () => RUNTIME.setModelDisplay(props.modelId, {maxFps: value}))
}

const fpsOptions = computed(() => [
	{label: I18N.value.maxFpsNone, value: 0},
	{label: I18N.value.maxFps30, value: 30},
	{label: I18N.value.maxFps60, value: 60},
])
</script>

<template>
	<div class="w-full flex flex-col gap-3.5">
		<h3 class="m-0 text-lg font-700 text-text-primary">{{ I18N.title }}</h3>

		<div class="flex flex-col gap-1.5">
			<AppSwitchRow
				boxed
				:title="I18N.clickInteraction"
				:desc="I18N.clickInteractionDesc"
				v-model="clickInteractionToggle"
			/>

			<AppSwitchRow
				boxed
				:title="I18N.clickThrough"
				:desc="clickThroughSupported ? I18N.clickThroughDesc : I18N.clickThroughUnsupportedDesc"
				:disabled="!clickThroughSupported"
				v-model="clickThroughToggle"
			/>

			<AppSwitchRow
				boxed
				:title="I18N.aiInteraction"
				:desc="aiConfigured ? I18N.aiInteractionDesc : I18N.aiInteractionDisabledDesc"
				:disabled="!aiConfigured"
				v-model="aiInteractionToggle"
			/>

			<AppSwitchRow
				boxed
				:title="I18N.autoBlink"
				:desc="I18N.autoBlinkDesc"
				v-model="autoBlinkToggle"
			/>

			<AppSwitchRow
				boxed
				:title="I18N.eyeTracking"
				:desc="I18N.eyeTrackingDesc"
				v-model="eyeTrackingToggle"
			/>

			<AppSwitchRow
				boxed
				:title="I18N.idleEyeAnimation"
				:desc="I18N.idleEyeAnimationDesc"
				v-model="idleEyeToggle"
			/>

			<AppSwitchRow
				boxed
				:title="I18N.idleAnimation"
				:desc="I18N.idleAnimationDesc"
				v-model="idleAnimToggle"
			/>

			<AppSwitchRow
				boxed
				:title="I18N.expressionEnabled"
				:desc="I18N.expressionEnabledDesc"
				v-model="expressionEnabledToggle"
			/>

			<AppSwitchRow
				boxed
				:title="I18N.lipSync"
				:desc="I18N.lipSyncDesc"
				v-model="lipSyncToggle"
			/>

			<AppSwitchRow
				boxed
				:title="I18N.shadow"
				:desc="I18N.shadowDesc"
				v-model="shadowToggle"
			/>

			<AppSwitchRow
				boxed
				:title="I18N.beatSync"
				:desc="I18N.beatSyncDesc"
				v-model="beatSyncToggle"
			/>
		</div>

		<div class="surface-inset flex flex-col gap-1.5 px-[1.1rem] py-2">
			<span class="text-base font-500 text-text-primary">{{ I18N.renderScale }}</span>
			<span class="text-hint">{{ I18N.renderScaleDesc }}</span>
			<div class="flex items-center gap-2.5">
				<n-slider
					:value="renderScale"
					:min="0.5"
					:max="4"
					:step="0.25"
					:format-tooltip="(v: number) => `${v.toFixed(2)}x`"
					class="flex-1 min-w-0"
					@update:value="onRenderScaleUpdate"
				/>
				<span class="w-[5rem] shrink-0 text-sm font-600 text-right text-nori-teal-bright mono">{{ renderScale.toFixed(2) }}x</span>
			</div>
		</div>

		<div class="surface-inset flex flex-col gap-1.5 px-[1.1rem] py-2">
			<span class="text-base font-500 text-text-primary">{{ I18N.maxFps }}</span>
			<div class="w-[14rem]">
				<n-select
					:value="maxFps"
					:options="fpsOptions"
					@update:value="onMaxFpsUpdate"
				/>
			</div>
		</div>
	</div>
</template>
