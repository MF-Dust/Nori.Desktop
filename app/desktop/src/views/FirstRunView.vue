<script setup lang="ts">
import {computed, ref, onMounted} from "vue"
import {useWindowFocus} from "@vueuse/core"
import useLanguages from "../services/i18n/useLanguages.ts"
import {RUNTIME} from "../services/runtime"
import Icon from "../components/Icon.vue"
import TitleBar from "../components/TitleBar.vue"
import AppButton from "../components/ui/AppButton.vue"
import Welcome from "../components/firstRun/Welcome.vue"
import LanguageSelect from "../components/firstRun/LanguageSelect.vue"
import ModelSelect from "../components/firstRun/ModelSelect.vue"
import AiSetup from "../components/firstRun/AiSetup.vue"
import Ready from "../components/firstRun/Ready.vue"
import {createWizard, WIZARD_STEPS} from "../services/firstRun/wizard"
import {buildAiChatPatch, emptyAiDraft} from "../services/firstRun/aiDraft"
import type {AiDraft} from "../services/firstRun/aiDraft"
import {feedback} from "../services/feedback"
import {APP_VERSION} from "../services/version"

const I18N = computed(() => useLanguages().views.firstRun)
const isWindowFocused = useWindowFocus()

// 首次运行期间仅保存用于展示/日志的快照信息
const appVersion = ref(APP_VERSION)
const selectedModel = ref("")
const telemetryEnabled = ref(true)
// AI 步草稿: 这一步可跳过, 所以只在离开它时落盘一次
const aiDraft = ref<AiDraft>(emptyAiDraft())
const savingAi = ref(false)
// 就绪页要据此换掉「可在主界面扩展」那句摘要
const aiSaved = ref(false)

// 步骤名称 (i18n)
const STEP_LABELS = computed(() => [
	I18N.value.steps.welcome,
	I18N.value.steps.language,
	I18N.value.steps.model,
	I18N.value.steps.ai,
	I18N.value.steps.ready,
])
const STEPS_COUNT = WIZARD_STEPS.length

// 组件挂载后拉取后端快照
onMounted(async () => {
	await RUNTIME.init()
	appVersion.value = RUNTIME.snapshot.value?.app.appVersion ?? appVersion.value
	const SNAPSHOT = RUNTIME.snapshot.value
	const SELECTED = SNAPSHOT?.models.selected ?? ""
	selectedModel.value = SNAPSHOT?.models.items.some(item => item.id === SELECTED && item.installed) ? SELECTED : ""
})

// 向导状态机 (步进、守卫与提交状态全在 services/firstRun/wizard.ts)
const WIZARD = createWizard(async () => {
	await RUNTIME.completeFirstRun(selectedModel.value, telemetryEnabled.value)
	await RUNTIME.writeLog("info", "", "app.initialized")
})

const state = ref(WIZARD.snapshot())
const sync = () => {
	state.value = WIZARD.snapshot()
}

const currentStep = computed(() => state.value.index)
const direction = computed(() => state.value.direction)
const isFirst = computed(() => state.value.isFirst)
const isLast = computed(() => state.value.isLast)
const submitting = computed(() => state.value.finishState === "submitting")
const stepError = computed(() => state.value.stepError)
const finishError = computed(() => state.value.finishError)

// 下一步 / 上一步
const next = async () => {
	// 离开 AI 步: 填了内容就先落盘再前进, 失败停在原地并把错误摆到底部
	if (state.value.step === "ai") {
		const PATCH = buildAiChatPatch(aiDraft.value)
		// 每次离开都重算: 用户可能回头把填过的内容清空
		aiSaved.value = false
		if (PATCH) {
			savingAi.value = true
			try {
				await RUNTIME.updateAiProviders({chat: PATCH})
				aiSaved.value = true
			} catch (error) {
				feedback.error(I18N.value.error.saveAi, error)
				WIZARD.blockStep(I18N.value.error.saveAi)
				sync()
				return
			} finally {
				savingAi.value = false
			}
		}
	}
	WIZARD.next()
	sync()
}

const prev = () => {
	WIZARD.prev()
	sync()
}

// 模型选择失败: 阻止前进并把错误摆到底部
const onModelError = (message: string) => {
	if (message) WIZARD.blockStep(message)
	else WIZARD.clearStep()
	sync()
}

const onModelSelected = (modelId: string) => {
	selectedModel.value = modelId
}

// AI 步: 草稿变化即解除上一次保存失败造成的阻断, 免得用户被卡住
const onAiDraft = (draft: AiDraft) => {
	aiDraft.value = draft
	if (state.value.stepError) {
		WIZARD.clearStep()
		sync()
	}
}

// AI 步: 显式跳过 (草稿已被子组件清空, 这里直接前进)
const onAiSkip = () => {
	aiDraft.value = emptyAiDraft()
	aiSaved.value = false
	WIZARD.clearStep()
	WIZARD.next()
	sync()
}

const onTelemetryChanged = (enabled: boolean) => {
	telemetryEnabled.value = enabled
}

// 关闭窗口
const closeApp = () => {
	void RUNTIME.exitApp()
}

// 完成初始化 (失败保留在末步可重试)
const finish = async () => {
	await WIZARD.finish()
	sync()
}
</script>

<template>
	<div
		class="window-root window-surface transition-[background,box-shadow] duration-200"
		:class="isWindowFocused ? 'window-chrome-focused' : ''"
	>
		<TitleBar
			show-close
			:close-label="I18N.close"
			@close="closeApp"
		>
			<div class="flex items-center justify-center">
				<div class="flex items-center gap-2 px-3 py-1 rounded-pill bg-bg-abyss/80 border border-line-strong shadow-elev-1">
					<div
						v-for="(label, idx) in STEP_LABELS"
						:key="idx"
						class="flex items-center gap-1.5 text-xs transition-all duration-200"
						:class="idx === currentStep
							? 'text-nori-teal-bright font-600'
							: (idx < currentStep ? 'text-nori-teal-soft' : 'text-text-faint/60')"
					>
						<span
							class="rounded-full transition-all duration-200"
							:class="idx === currentStep
								? 'w-2 h-2 bg-nori-teal-bright'
								: (idx < currentStep ? 'w-1.5 h-1.5 bg-nori-teal' : 'w-1.5 h-1.5 bg-overlay-20')"
						/>
						<span v-if="idx === currentStep" class="whitespace-nowrap">{{ label }}</span>
					</div>
				</div>
			</div>

			<div class="flex items-center gap-2.5">
				<div class="flex items-center gap-2 px-2 py-0.8 rounded-sm bg-overlay-4 border border-line-subtle">
					<div class="flex gap-1">
						<span
							v-for="i in STEPS_COUNT"
							:key="i"
							class="w-[1.2rem] h-[0.35rem] rounded-pill transition-all duration-200"
							:class="i <= currentStep + 1
								? 'bg-nori-teal'
								: 'bg-overlay-12'"
						/>
					</div>
					<span class="text-xs text-text-faint mono font-500">{{ currentStep + 1 }} / {{ STEPS_COUNT }}</span>
				</div>
			</div>
		</TitleBar>

		<!-- 舞台: 720×480 固定窗口下内容自适应 stage 居中, 极端情况提供平滑滚动 -->
		<div class="relative flex-1 w-full min-h-0 scroll-area flex flex-col">
			<Transition :name="direction > 0 ? 'page-next' : 'page-prev'" mode="out-in">
				<Welcome v-if="currentStep === 0"/>
				<LanguageSelect v-else-if="currentStep === 1"/>
				<ModelSelect
					v-else-if="currentStep === 2"
					@error="onModelError"
					@selected="onModelSelected"
				/>
				<AiSetup
					v-else-if="currentStep === 3"
					@draft="onAiDraft"
					@skip="onAiSkip"
				/>
				<Ready v-else :ai-configured="aiSaved" @telemetry-changed="onTelemetryChanged"/>
			</Transition>
		</div>

		<!-- 底部导航 -->
		<div class="relative z-2 h-14 shrink-0 flex items-center justify-between gap-3 px-6 bg-bg-abyss/85 border-t border-line-subtle">

			<AppButton v-if="!isFirst" icon="arrow-left" :disabled="submitting" @click="prev">
				{{ I18N.back }}
			</AppButton>
			<span v-else class="w-2.5"/>

			<p v-if="stepError || finishError" class="flex-1 inline-flex items-center justify-center gap-1.5 m-0 text-sm text-danger-text text-center" role="alert">
				<Icon name="info" :size="13"/>
				<span>{{ stepError || finishError }}</span>
			</p>

			<AppButton v-if="!isLast" variant="primary" :loading="savingAi" :disabled="!state.canNext || savingAi" @click="next">
				<span class="inline-flex items-center gap-2">
					<span>{{ I18N.next }}</span>
					<Icon name="arrow-right" :size="15"/>
				</span>
			</AppButton>
			<AppButton
				v-else
				variant="primary"
				icon="sparkles"
				class="px-7 text-md"
				:loading="submitting"
				@click="finish"
			>
				{{ submitting ? I18N.starting : (finishError ? I18N.retry : I18N.start) }}
			</AppButton>
		</div>
	</div>
</template>
