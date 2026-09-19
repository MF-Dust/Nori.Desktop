<script setup lang="ts">
import {computed, onBeforeUnmount, onMounted, ref} from "vue"
import {useRouter} from "vue-router"
import {getCurrentWindowLabel, navigateToOwnWindow} from "./services/window"
import {RUNTIME} from "./services/runtime"
import useLanguage from "./services/i18n"
import {naiveDarkTheme, naiveThemeOverrides} from "./assets/style/naiveTheme"
import FeedbackHost from "./components/ui/FeedbackHost.vue"
import AppButton from "./components/ui/AppButton.vue"
import AppModal from "./components/ui/AppModal.vue"
import Icon from "./components/Icon.vue"
import useLanguages from "./services/i18n/useLanguages.ts"
import {errorText, feedback} from "./services/feedback"
import {BindWindowBackdrop} from "./services/theme/windowBackdrop"

const ROUTER = useRouter()
const I18N = computed(() => useLanguages().components.ui.state)
const retryingBootstrap = ref(false)
const currentLabel = ref("")
const decidingTelemetry = ref(false)
let stopBackdropSync: (() => void) | undefined
const telemetryConsentRequired = computed(() =>
	currentLabel.value === "main" && RUNTIME.snapshot.value?.telemetry.consent === "unset")
const bootstrapErrorText = computed(() => {
	const ERROR = RUNTIME.bootstrapError.value
	return ERROR ? errorText(ERROR) : ""
})

const retryBootstrap = async (): Promise<void> => {
	if (retryingBootstrap.value) return
	retryingBootstrap.value = true
	try {
		await RUNTIME.retryInit()
	} catch {
		// 错误原因已写入 RUNTIME.bootstrapError, 由兜底页继续展示。
	} finally {
		retryingBootstrap.value = false
	}
}

const decideTelemetry = async (enabled: boolean): Promise<void> => {
	if (decidingTelemetry.value) return
	decidingTelemetry.value = true
	try {
		await RUNTIME.updateGeneral({telemetryEnabled: enabled})
	} catch (error) {
		feedback.error(I18N.value.telemetryConsentSaveFailed, error)
	} finally {
		decidingTelemetry.value = false
	}
}

onMounted(async () => {
	stopBackdropSync = BindWindowBackdrop(document.documentElement.style)
	// 窗口导航: 按当前窗口 label 跳转到对应页面 (纯浏览器调试时跳过)
	try {
		const LABEL = await getCurrentWindowLabel()
		currentLabel.value = LABEL ?? ""
		const TARGET = await navigateToOwnWindow(ROUTER)
		await RUNTIME.writeLog("info", `窗口 ${LABEL} 已挂载, 跳转到 ${TARGET}`)
	} catch {
		// 非宿主环境(纯 vite 调试)忽略
	}
})

// 语言在任何窗口被改都会广播 state-changed, 这里跟随快照重放, 避免多窗口语言不一致。
// RUNTIME 是模块级单例，App 卸载时必须显式移除 handler，避免窗口重挂载/HMR 后累积旧闭包。
const stopLanguageSync = RUNTIME.onLanguageChanged((language) => {
	void useLanguage.setLanguage(language)
})

onBeforeUnmount(() => {
	stopLanguageSync()
	stopBackdropSync?.()
})
</script>

<template>
	<NConfigProvider :theme="naiveDarkTheme" :theme-overrides="naiveThemeOverrides">
		<NMessageProvider>
			<NDialogProvider>
				<NNotificationProvider>
					<main
						v-if="RUNTIME.bootstrapError.value"
						class="w-100vw h-100vh scroll-area bg-bg-abyss p-6"
						role="alert"
						aria-live="assertive"
					>
						<div class="min-h-full flex items-center justify-center">
							<section class="w-full max-w-[48rem] flex flex-col items-center gap-4 rounded-lg border border-danger/35 bg-bg-card/90 p-6 text-center shadow-elev-2">
								<span class="flex h-12 w-12 items-center justify-center rounded-full bg-danger/12 text-danger-text">
									<Icon name="alert" :size="24"/>
								</span>
								<h1 class="m-0 text-xl font-700 text-text-primary">{{ I18N.bootstrapTitle }}</h1>
								<p class="m-0 text-base text-text-body">{{ I18N.bootstrapDesc }}</p>
								<p v-if="bootstrapErrorText" class="m-0 max-w-full break-words text-sm text-danger-text" role="status">{{ bootstrapErrorText }}</p>
								<AppButton variant="primary" icon="refresh" :loading="retryingBootstrap" @click="retryBootstrap">
									{{ retryingBootstrap ? I18N.bootstrapRetrying : I18N.retry }}
								</AppButton>
							</section>
						</div>
					</main>
					<template v-else>
						<FeedbackHost/>
						<RouterView/>
						<AppModal
							:show="telemetryConsentRequired"
							:title="I18N.telemetryConsentTitle"
							:close-label="I18N.telemetryConsentDeny"
							:mask-closable="false"
							@update:show="value => { if (!value) void decideTelemetry(false) }"
						>
							<p class="m-0 text-base leading-relaxed text-text-body">{{ I18N.telemetryConsentDesc }}</p>
							<template #footer>
								<AppButton :disabled="decidingTelemetry" @click="decideTelemetry(false)">{{ I18N.telemetryConsentDeny }}</AppButton>
								<AppButton variant="primary" :loading="decidingTelemetry" @click="decideTelemetry(true)">{{ I18N.telemetryConsentAllow }}</AppButton>
							</template>
						</AppModal>
					</template>
				</NNotificationProvider>
			</NDialogProvider>
		</NMessageProvider>
	</NConfigProvider>
</template>
