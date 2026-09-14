<script setup lang="ts">
/**
 * 首次运行 · 接入模型服务
 *
 * 原来的向导从「选形象」直接跳到「就绪」, 用户进主界面第一次说话才发现没配模型服务,
 * 得自己翻到设置页去填 —— 这一步把配置提到向导里, 同时明确它可以跳过。
 *
 * 授权面限制: first-run 窗口能调 `llm_fetch_models` 与 `settings_update_ai_providers`,
 * 但 `ai_test_connection` 只允许 main。所以这里用「获取模型」验证地址与密钥,
 * 拉到列表就说明这套凭据是通的。
 * 草稿本身不落盘, 由 FirstRunView 在离开这一步时统一保存。
 */
import {computed, ref} from "vue"
import useLanguages from "../../services/i18n/useLanguages.ts"
import {RUNTIME} from "../../services/runtime"
import {feedback} from "../../services/feedback"
import Icon from "../Icon.vue"
import AppButton from "../ui/AppButton.vue"
import AppField from "../ui/AppField.vue"
import {AI_DEFAULT_BASE_URLS, AI_PROVIDER_OPTIONS, effectiveBaseUrl, emptyAiDraft} from "../../services/firstRun/aiDraft"
import type {AiDraft, AiProviderKey} from "../../services/firstRun/aiDraft"

const I18N = computed(() => useLanguages().components.firstRun.aiSetup)
const AI_I18N = computed(() => useLanguages().views.main.ai)

const emit = defineEmits<{
	draft: [value: AiDraft]
	skip: []
}>()

const draft = ref<AiDraft>(emptyAiDraft())
const models = ref<string[]>([])
const fetching = ref(false)
const fetchResult = ref("")
const fetchSuccess = ref(false)

const PROVIDER_OPTIONS = computed(() => AI_PROVIDER_OPTIONS.map(key => ({label: AI_I18N.value.providers[key], value: key})))
const MODEL_OPTIONS = computed(() => {
	const LIST = [...models.value]
	if (draft.value.model && !LIST.includes(draft.value.model)) LIST.unshift(draft.value.model)
	return LIST.map(item => ({label: item, value: item}))
})

const BASE_URL_PLACEHOLDER = computed(() => AI_DEFAULT_BASE_URLS[draft.value.provider])
const API_KEY_PLACEHOLDER = computed(() => {
	switch (draft.value.provider) {
		case "anthropic": return "sk-ant-..."
		case "google": return "AIza..."
		default: return "sk-..."
	}
})

const canFetch = computed(() => !fetching.value && draft.value.apiKey.trim().length > 0)

/** 每次改动都把草稿抬给 FirstRunView (它负责在离开这一步时保存) */
const push = () => emit("draft", {...draft.value})

const onProviderChange = (value: AiProviderKey) => {
	draft.value.provider = value
	// 换协议后旧模型列表不再适用
	models.value = []
	fetchResult.value = ""
	push()
}

const onSelectModel = (value: string) => {
	draft.value.model = value
	push()
}

const fetchModels = async () => {
	if (!canFetch.value) return
	fetching.value = true
	fetchResult.value = ""
	try {
		const LIST = await RUNTIME.fetchModels(draft.value.provider, effectiveBaseUrl(draft.value), draft.value.apiKey.trim())
		models.value = LIST
		fetchSuccess.value = LIST.length > 0
		fetchResult.value = LIST.length > 0 ? `${I18N.value.verified} (${LIST.length})` : AI_I18N.value.modelEmpty
		if (LIST.length > 0 && !draft.value.model) {
			draft.value.model = LIST[0]
			push()
		}
	} catch (error) {
		fetchSuccess.value = false
		fetchResult.value = AI_I18N.value.fetchFailed
		feedback.error(AI_I18N.value.fetchFailed, error)
	} finally {
		fetching.value = false
	}
}

const skip = () => {
	draft.value = emptyAiDraft()
	models.value = []
	fetchResult.value = ""
	push()
	emit("skip")
}
</script>

<template>
	<section key="ai-setup" data-first-run-step="ai" class="w-full flex flex-col items-center gap-2 px-7 py-1.5 my-auto">
		<div class="flex flex-col items-center gap-1 text-center">
			<span class="chip-teal text-xs">
				<Icon name="cpu" :size="11"/>
				<span>{{ I18N.badge }}</span>
			</span>
			<h2 class="text-xl font-700 glow-teal">{{ I18N.title }}</h2>
			<p class="text-xs text-sub max-w-[42rem]">{{ I18N.hint }}</p>
		</div>

		<div class="w-full max-w-[48rem] flex flex-col gap-2.5 p-3.5 surface-card">
			<!-- 第一行: 服务商 + API 地址 -->
			<div class="grid grid-cols-2 gap-3">
				<div class="field min-w-0">
					<span class="field-label font-500 text-xs">{{ AI_I18N.provider }}</span>
					<n-select
						:value="draft.provider"
						:options="PROVIDER_OPTIONS"
						@update:value="onProviderChange"
					/>
				</div>
				<AppField class="min-w-0" :label="AI_I18N.apiBaseUrl" :hint="I18N.baseUrlHint">
					<input
						v-model="draft.baseUrl"
						class="input-base"
						type="text"
						spellcheck="false"
						:placeholder="BASE_URL_PLACEHOLDER"
						@input="push"
					/>
				</AppField>
			</div>

			<!-- 第二行: API 密钥 + 模型选择与拉取 -->
			<div class="grid grid-cols-2 gap-3">
				<AppField class="min-w-0" :label="AI_I18N.apiKey" :hint="I18N.apiKeyHint">
					<input
						v-model="draft.apiKey"
						class="input-base"
						type="password"
						spellcheck="false"
						autocomplete="off"
						:placeholder="API_KEY_PLACEHOLDER"
						@input="push"
					/>
				</AppField>

				<div class="field min-w-0">
					<div class="flex items-center justify-between">
						<span class="field-label font-500 text-xs">{{ AI_I18N.model }}</span>
						<span
							v-if="fetchResult"
							class="inline-flex items-center gap-1 text-xs"
							:class="fetchSuccess ? 'text-success' : 'text-danger-text'"
							aria-live="polite"
						>
							<Icon :name="fetchSuccess ? 'check' : 'alert'" :size="11"/>
							<span>{{ fetchResult }}</span>
						</span>
					</div>
					<div class="flex items-center gap-2">
						<n-select
							:value="draft.model"
							:options="MODEL_OPTIONS"
							:disabled="MODEL_OPTIONS.length === 0"
							:placeholder="MODEL_OPTIONS.length === 0 ? AI_I18N.modelEmpty : AI_I18N.modelPlaceholder"
							class="flex-1 min-w-0"
							@update:value="onSelectModel"
						/>
						<AppButton
							variant="primary"
							size="sm"
							:loading="fetching"
							:disabled="!canFetch"
							class="shrink-0 whitespace-nowrap"
							@click="fetchModels"
						>{{ fetching ? AI_I18N.getting : AI_I18N.getModel }}</AppButton>
					</div>
				</div>
			</div>
		</div>

		<!-- 底部跳过引导 -->
		<div class="flex items-center justify-center gap-2 text-xs text-text-faint">
			<span class="chip text-xs">{{ I18N.optional }}</span>
			<span>{{ I18N.later }}</span>
			<AppButton variant="ghost" size="sm" icon="arrow-right" @click="skip">{{ I18N.skip }}</AppButton>
		</div>
	</section>
</template>
