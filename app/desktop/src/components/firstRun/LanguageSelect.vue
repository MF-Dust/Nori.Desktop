<script setup lang="ts">
import {computed, ref, onMounted} from "vue"
import useLanguage from "../../services/i18n"
import useLanguages from "../../services/i18n/useLanguages.ts"
import type {LanguageType} from "../../services/i18n"
import {RUNTIME} from "../../services/runtime"
import {feedback} from "../../services/feedback"
import zhCn from "../../assets/images/flags/cn.png"
import enGb from "../../assets/images/flags/gb.png"
import enUs from "../../assets/images/flags/us.png"
import Icon from "../../components/Icon.vue"

const language = useLanguage

const I18N = computed(() => useLanguages().components.firstRun.languageSelect)

// 语言 code → 本地国旗图片 (来自 flagcdn 下载, 存于 src/assets/images/flags)
const FLAG_MAP = new Map<string, string>([
	["zh-CN", zhCn],
	["zh", zhCn],
	["en", enGb],
	["en-US", enUs],
])

// 语言 code → 显示名称 (fallback 用 Intl.DisplayNames)
const NAME_MAP = new Map<string, {name: string; sub: string}>([
	["zh-CN", {name: "简体中文", sub: "Chinese (Simplified)"}],
	["zh", {name: "简体中文", sub: "Chinese (Simplified)"}],
	["en", {name: "English", sub: "English (UK)"}],
	["en-US", {name: "English (US)", sub: "American English"}],
])

const flagOf = (code: string): string => FLAG_MAP.get(code) ?? FLAG_MAP.get(code.split("-")[0]) ?? ""

const nameInfoOf = (code: string): {name: string; sub: string} => {
	const NAME = NAME_MAP.get(code)
	if (NAME) return NAME
	const autoName = new Intl.DisplayNames([code], {type: "language"}).of(code.split("-")[0]) || code
	return {name: autoName, sub: code}
}

// 可用语言列表
const languages = ref<string[]>([])

// 当前语言
const current = ref<LanguageType>("zh-CN")

// 加载语言列表和当前语言
onMounted(async () => {
	try {
		await RUNTIME.init()
		languages.value = language.getLanguages()
		current.value = RUNTIME.snapshot.value?.general.language ?? "zh-CN"
	} catch (error) {
		feedback.error(I18N.value.switchFailed, error)
	}
})

// 切换语言
const select = async (code: string) => {
	const PREVIOUS = current.value
	current.value = code
	try {
		await language.setLanguage(code)
		await RUNTIME.updateGeneral({language: code})
	} catch (error) {
		current.value = PREVIOUS
		feedback.error(I18N.value.switchFailed, error)
	}
}
</script>

<template>
	<section key="language-select" data-first-run-step="language" class="w-full flex flex-col items-center gap-4 px-7 py-2.5 my-auto">
		<div class="flex flex-col items-center gap-1 text-center">
			<span class="chip-teal">
				<Icon name="noriOS" :size="12"/>
				<span>{{ I18N.badge }}</span>
			</span>
			<h2 class="text-2xl font-700 glow-teal">{{ I18N.title }}</h2>
			<p class="text-xs text-sub">{{ I18N.subtitle }}</p>
		</div>

		<div class="w-full max-w-[44rem] grid grid-cols-2 gap-3">
			<button
				v-for="code in languages"
				:key="code"
				type="button"
				class="group relative flex items-center gap-2.5 px-3.5 py-3 rounded-md text-left cursor-pointer overflow-hidden
					border-2 border-line-subtle bg-overlay-4 text-text-primary transition-all duration-200 focus-ring
					hover:(bg-nori-teal-bright/8 border-nori-teal-soft shadow-elev-1)"
				:class="current === code ? 'border-nori-teal bg-nori-teal-bright/12' : ''"
				:aria-pressed="current === code"
				@click="select(code)"
			>
				<span class="w-[3.4rem] h-[2.4rem] shrink-0 rounded-xs overflow-hidden border border-overlay-12 shadow-elev-1">
					<img v-if="flagOf(code)" class="w-full h-full object-cover block" :src="flagOf(code)" :alt="nameInfoOf(code).name"/>
					<span v-else class="block w-full h-full bg-overlay-12"/>
				</span>

				<span class="flex-1 min-w-0 flex flex-col gap-0.5">
					<span
						class="text-base font-500 whitespace-nowrap"
						:class="current === code ? 'text-nori-teal-bright font-600' : 'text-text-primary'"
					>{{ nameInfoOf(code).name }}</span>
					<span class="text-xs text-text-faint whitespace-nowrap">{{ nameInfoOf(code).sub }}</span>
				</span>

				<span
					class="w-5 h-5 shrink-0 rounded-full flex items-center justify-center transition-all duration-200"
					:class="current === code ? 'bg-nori-teal text-on-teal scale-100 opacity-100' : 'scale-60 opacity-0'"
				>
					<Icon name="check" :size="12"/>
				</span>
			</button>

			<p v-if="languages.length === 0" class="col-span-full text-center text-sub py-5">{{ I18N.langEmpty }}</p>
		</div>
	</section>
</template>
