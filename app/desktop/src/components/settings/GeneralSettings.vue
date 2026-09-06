<script setup lang="ts">
import {computed, onMounted, ref} from "vue"
import {RUNTIME} from "../../services/runtime"
import {useSnapshotSave} from "../../composables/useSnapshotSave"
import {useSnapshotField} from "../../composables/useSnapshotField"
import useLanguage from "../../services/i18n"
import useLanguages from "../../services/i18n/useLanguages"
import {feedback} from "../../services/feedback"
import {openUrl} from "../../services/host/shell"
import AppButton from "../ui/AppButton.vue"
import AppCard from "../ui/AppCard.vue"
import AppSectionHeader from "../ui/AppSectionHeader.vue"
import AppSwitchRow from "../ui/AppSwitchRow.vue"
import Icon from "../Icon.vue"
import zhCn from "../../assets/images/flags/cn.png"
import enUs from "../../assets/images/flags/us.png"

const PROPS = withDefaults(defineProps<{
	updatesOnly?: boolean
}>(), {
	updatesOnly: false,
})

const TEXT = computed(() => useLanguages().views.main.general)
const PAGE_TITLE = computed(() => PROPS.updatesOnly ? TEXT.value.updates.title : TEXT.value.title)
const PAGE_SUBTITLE = computed(() => PROPS.updatesOnly ? TEXT.value.updates.subtitle : TEXT.value.subtitle)

const SAVE_MGR = useSnapshotSave({
	onError: (key, error) => {
		const message = key === "clickThrough"
			? TEXT.value.startup.clickThroughSaveFailed
			: key === "autoCheckUpdates"
				? TEXT.value.updates.saveFailed
				: TEXT.value.telemetry.saveFailed
		feedback.error(message, error)
	},
})
const {defineField, saveNow} = SAVE_MGR

const currentLangField = useSnapshotField(snapshot => snapshot.general.language, "zh-CN")
const currentLang = currentLangField.value

const autoSummonField = defineField(
	"petAutoSummon",
	snapshot => snapshot.general.petAutoSummon,
	true,
	val => RUNTIME.updateGeneral({petAutoSummon: val}),
)
const autoSummon = autoSummonField.value

const telemetryEnabledField = defineField(
	"telemetryEnabled",
	snapshot => snapshot.telemetry.enabled,
	false,
	val => RUNTIME.updateGeneral({telemetryEnabled: val}),
)
const telemetryEnabled = telemetryEnabledField.value

const clickThroughField = defineField(
	"clickThrough",
	snapshot => snapshot.behaviors.clickThrough ?? false,
	false,
	val => RUNTIME.setModelBehavior({clickThrough: val}),
)
const clickThrough = clickThroughField.value
const clickThroughSupported = computed(() => RUNTIME.platform().supportsHitThrough)

const autoCheckUpdatesField = defineField(
	"autoCheckUpdates",
	snapshot => snapshot.general.autoCheckUpdates ?? true,
	true,
	val => RUNTIME.updateGeneral({autoCheckUpdates: val}),
)
const autoCheckUpdates = autoCheckUpdatesField.value

const currentProductVersion = computed(() => RUNTIME.snapshot.value?.app.productVersion ?? RUNTIME.snapshot.value?.app.appVersion ?? "Dev")
const updater = computed(() => RUNTIME.snapshot.value?.updater)

const checkPending = ref(false)
const installPending = ref(false)
const cancelRequested = ref(false)
const UNAVAILABLE = computed(() => updater.value?.unavailableReason)
const checking = computed(() => checkPending.value || updater.value?.state === "checking")
const inProgress = computed(() => installPending.value || updater.value?.state === "downloading" || updater.value?.state === "verifying" || updater.value?.state === "installing")
const PROGRESS = computed(() => Math.max(0, Math.min(100, Math.round((updater.value?.progress ?? 0) * 100))))
const DOWNLOAD_SIZE = computed(() => `${((updater.value?.downloadedBytes ?? 0) / 1048576).toFixed(1)} / ${((updater.value?.totalBytes ?? 0) / 1048576).toFixed(1)} MiB`)
const isReadyToRestart = computed(() => updater.value?.state === "readytorestart")
const isAvailable = computed(() => updater.value?.state === "available")
const isUpToDate = computed(() => updater.value?.state === "uptodate")
const isError = computed(() => updater.value?.state === "error")
const isCancelled = computed(() => updater.value?.state === "cancelled")
const restarting = ref(false)

const telemetryAvailable = computed(() => RUNTIME.snapshot.value?.telemetry.available ?? false)
const telemetryConsent = computed(() => RUNTIME.snapshot.value?.telemetry.consent ?? "unset")
const TELEMETRY_DESC = computed(() => {
	if (!telemetryAvailable.value) return TEXT.value.telemetry.unavailable
	if (telemetryConsent.value === "unset") return TEXT.value.telemetry.pending
	return telemetryEnabled.value ? TEXT.value.telemetry.enabledDesc : TEXT.value.telemetry.disabled
})
const TELEMETRY_STATUS = computed(() => {
	if (!telemetryAvailable.value) return TEXT.value.telemetry.unavailable
	if (telemetryConsent.value === "unset") return TEXT.value.telemetry.statusPending
	return telemetryEnabled.value ? TEXT.value.telemetry.statusEnabled : TEXT.value.telemetry.statusDisabled
})

// 切换语言: 本地立即生效, 失败时回滚到快照语言。
const onLanguageChange = (lang: string) => {
	currentLang.value = lang
	currentLangField.touch()
	void saveNow("language", async () => {
		try {
			await useLanguage.setLanguage(lang)
			await RUNTIME.updateGeneral({language: lang})
			currentLangField.commit()
		} catch (error) {
			currentLangField.reset()
			await useLanguage.setLanguage(currentLang.value)
			throw error
		}
	})
}

const onAutoSummonChange = (val: boolean) => {
	autoSummon.value = val
	void autoSummonField.saveNow()
}

const onTelemetryChange = (val: boolean) => {
	telemetryEnabled.value = val
	void telemetryEnabledField.saveNow()
}

const onClickThroughChange = (val: boolean) => {
	if (!clickThroughSupported.value) return
	clickThrough.value = val
	void clickThroughField.saveNow()
}

const onAutoCheckChange = (val: boolean) => {
	autoCheckUpdates.value = val
	void autoCheckUpdatesField.saveNow()
}

const onCheckUpdates = async () => {
	if (checking.value || inProgress.value || isReadyToRestart.value || UNAVAILABLE.value) return
	checkPending.value = true
	try {
		await RUNTIME.checkUpdate()
	} catch (error) {
		if (!cancelRequested.value) feedback.error(TEXT.value.updates.checkFailed, error)
	} finally {
		checkPending.value = false
		cancelRequested.value = false
		await RUNTIME.refresh().catch(error => feedback.error(TEXT.value.updates.checkFailed, error))
	}
}

const onDownloadAndInstall = async () => {
	if (inProgress.value || !isAvailable.value || UNAVAILABLE.value) return
	installPending.value = true
	try {
		await RUNTIME.installUpdate()
	} catch (error) {
		if (!cancelRequested.value) feedback.error(TEXT.value.updates.installFailed, error)
	} finally {
		installPending.value = false
		cancelRequested.value = false
		await RUNTIME.refresh().catch(error => feedback.error(TEXT.value.updates.installFailed, error))
	}
}

const onCancelUpdate = async () => {
	cancelRequested.value = true
	try {
		await RUNTIME.cancelUpdate()
	} catch (error) {
		cancelRequested.value = false
		feedback.error(TEXT.value.updates.cancelFailed, error)
	}
}

const onManualDownload = async () => {
	const URL = updater.value?.manualDownloadUrl
	if (!URL) return
	try { await openUrl(URL) } catch (error) { feedback.error(TEXT.value.updates.openFailed, error) }
}

const onRestartApp = async () => {
	if (restarting.value) return
	restarting.value = true
	try {
		await RUNTIME.restartApp()
	} catch (error) {
		feedback.error(TEXT.value.updates.restartFailed, error)
		restarting.value = false
	}
}

onMounted(() => {
	void RUNTIME.init().catch(error => feedback.error(TEXT.value.telemetry.saveFailed, error))
})
</script>

<template>
	<div class="w-full h-full flex flex-col gap-4 px-6 py-4 scroll-area">
		<AppSectionHeader :title="PAGE_TITLE" :subtitle="PAGE_SUBTITLE"/>

		<div class="flex flex-col gap-3.5 pb-5">
		<template v-if="!PROPS.updatesOnly">
			<!-- 1. 界面语言 -->
			<AppCard :title="TEXT.language.title" icon="noriOS">
				<div class="flex flex-wrap gap-2.5">
					<!-- 单选按钮本体用 sr-only 隐藏而非 display:none, 保留键盘可达与读屏语义 -->
					<label
						class="pill-choice focus-ring-within gap-2 px-4 py-2 text-sm"
						:class="currentLang === 'zh-CN' ? 'pill-choice-on' : 'pill-choice-off'"
					>
						<input
							v-model="currentLang"
							type="radio"
							value="zh-CN"
							class="sr-only"
							@change="onLanguageChange('zh-CN')"
						/>
						<span class="w-[2rem] h-[1.4rem] shrink-0 rounded-[0.2rem] overflow-hidden border border-overlay-12">
							<img :src="zhCn" alt="CN" class="w-full h-full object-cover block"/>
						</span>
						<span>{{ TEXT.language.chinese }}</span>
						<Icon v-if="currentLang === 'zh-CN'" name="check" :size="13" class="text-nori-teal-bright ml-0.5"/>
					</label>
					<label
						class="pill-choice focus-ring-within gap-2 px-4 py-2 text-sm"
						:class="currentLang === 'en-US' ? 'pill-choice-on' : 'pill-choice-off'"
					>
						<input
							v-model="currentLang"
							type="radio"
							value="en-US"
							class="sr-only"
							@change="onLanguageChange('en-US')"
						/>
						<span class="w-[2rem] h-[1.4rem] shrink-0 rounded-[0.2rem] overflow-hidden border border-overlay-12">
							<img :src="enUs" alt="US" class="w-full h-full object-cover block"/>
						</span>
						<span>{{ TEXT.language.english }}</span>
						<Icon v-if="currentLang === 'en-US'" name="check" :size="13" class="text-nori-teal-bright ml-0.5"/>
					</label>
				</div>
			</AppCard>

			<!-- 2. 启动与窗口行为 -->
			<AppCard :title="TEXT.startup.title" icon="settings">
				<AppSwitchRow
					:title="TEXT.startup.autoSummon"
					:desc="TEXT.startup.autoSummonDesc"
					:model-value="autoSummon"
					@update:model-value="onAutoSummonChange"
				/>
				<AppSwitchRow
					:title="TEXT.startup.clickThrough"
					:desc="clickThroughSupported ? TEXT.startup.clickThroughDesc : TEXT.startup.clickThroughUnsupportedDesc"
					:model-value="clickThrough"
					:disabled="!clickThroughSupported"
					@update:model-value="onClickThroughChange"
				/>
			</AppCard>

			<!-- 3. 错误遥测与隐私 -->
			<AppCard :title="TEXT.telemetry.title" icon="info">
				<AppSwitchRow
					:title="TEXT.telemetry.enabled"
					:desc="TELEMETRY_DESC"
					:model-value="telemetryEnabled"
					@update:model-value="onTelemetryChange"
				/>
				<span class="text-hint">{{ TELEMETRY_STATUS }}</span>
			</AppCard>
		</template>

		<!-- 4. 软件更新 -->
		<AppCard :title="TEXT.updates.title" icon="sparkle">
				<template #actions>
					<AppButton
						variant="ghost"
						size="sm"
						icon="refresh"
						:loading="checking"
						:disabled="inProgress || isReadyToRestart || !!UNAVAILABLE || !updater"
						@click="onCheckUpdates"
					>
						{{ checking ? TEXT.updates.checking : TEXT.updates.checkNow }}
					</AppButton>
				</template>

				<div class="flex items-center justify-between py-1 text-sm">
					<span class="text-text-muted">{{ TEXT.updates.currentVersion }}</span>
					<span class="font-mono text-text-primary">{{ currentProductVersion }}</span>
				</div>

				<div v-if="updater?.lastCheckedAt" class="flex items-center justify-between py-0.5 text-xs text-text-muted">
					<span>{{ TEXT.updates.lastChecked }}</span>
					<span class="font-mono">{{ updater.lastCheckedAt }}</span>
				</div>

				<AppSwitchRow
					:title="TEXT.updates.autoCheck"
					:desc="TEXT.updates.autoCheckDesc"
					:model-value="autoCheckUpdates"
					:disabled="!!UNAVAILABLE || !updater"
					@update:model-value="onAutoCheckChange"
				/>

				<p v-if="UNAVAILABLE" class="text-sm text-text-muted" role="status">{{ UNAVAILABLE }}</p>

				<div v-if="checking" class="flex flex-wrap items-center justify-between gap-2" role="status">
					<span class="text-sm text-text-muted">{{ TEXT.updates.checking }}</span>
					<AppButton size="sm" :disabled="cancelRequested" @click="onCancelUpdate">
						{{ cancelRequested ? TEXT.updates.cancelling : TEXT.updates.cancel }}
					</AppButton>
				</div>

				<!-- 状态 1: 就绪待重启 -->
				<div v-else-if="isReadyToRestart" class="flex flex-wrap items-center justify-between gap-3 p-3 rounded-sm bg-nori-teal-bright/10 border border-nori-teal-bright/30" role="status">
					<div class="flex flex-col gap-1">
						<span class="font-medium text-sm text-text-primary">{{ TEXT.updates.readyToRestart }}</span>
						<span v-if="updater?.availableVersion" class="text-xs text-text-muted">
							{{ updater.availableVersion }}
						</span>
					</div>
					<AppButton
						variant="primary"
						size="sm"
						:loading="restarting"
						@click="onRestartApp"
					>
						{{ TEXT.updates.restartNow }}
					</AppButton>
				</div>

				<!-- 状态 2: 正在下载 / 正在校验 / 正在安装 (带进度条与取消) -->
				<div v-else-if="inProgress" class="flex flex-col gap-2 p-3 rounded-sm bg-bg-card border border-line-subtle">
					<div class="flex items-center justify-between">
						<span class="text-sm font-medium text-text-primary">
							{{ updater?.state === "downloading" ? TEXT.updates.downloading : updater?.state === "verifying" ? TEXT.updates.verifying : TEXT.updates.installing }}
						</span>
						<div class="flex items-center gap-2">
							<span v-if="updater?.state === 'downloading'" class="font-mono text-xs text-text-muted">
								{{ PROGRESS }}%
							</span>
							<AppButton
								variant="ghost"
								size="sm"
								:disabled="cancelRequested"
								@click="onCancelUpdate"
							>
								{{ cancelRequested ? TEXT.updates.cancelling : TEXT.updates.cancel }}
							</AppButton>
						</div>
					</div>
					<div v-if="updater?.state === 'downloading'" class="flex flex-col gap-2">
						<div class="w-full h-[0.4rem] bg-bg-surface rounded overflow-hidden" role="progressbar" :aria-label="TEXT.updates.downloading" :aria-valuenow="PROGRESS" :aria-valuemin="0" :aria-valuemax="100">
							<div class="h-full bg-nori-teal-bright" :style="{width: `${PROGRESS}%`}"/>
						</div>
						<span class="text-xs text-text-muted font-mono">{{ DOWNLOAD_SIZE }}</span>
					</div>
					<p v-if="updater?.state === 'installing'" class="text-xs text-text-muted">{{ TEXT.updates.commitNotice }}</p>
				</div>

				<!-- 状态 3: 发现新版本 -->
				<div v-else-if="isAvailable" class="flex flex-col gap-2.5 p-3 rounded-sm bg-bg-card border border-line-subtle">
					<div class="flex flex-wrap items-center justify-between gap-3">
						<div class="flex items-center gap-2 min-w-0 break-all">
							<span class="font-medium text-sm text-nori-teal-bright">{{ TEXT.updates.available }}: {{ updater?.availableVersion }}</span>
						</div>
						<AppButton
							variant="primary"
							size="sm"
							@click="onDownloadAndInstall"
						>
							{{ TEXT.updates.downloadAndInstall }}
						</AppButton>
					</div>
					<p class="text-xs text-text-muted">{{ TEXT.updates.installNotice }}</p>
					<div v-if="updater?.releaseNotes" class="text-xs text-text-muted max-h-[10rem] overflow-y-auto whitespace-pre-wrap break-words leading-relaxed p-2 rounded bg-bg-surface/50 border border-line-subtle">
						{{ updater.releaseNotes }}
					</div>
				</div>

				<div v-else-if="updater?.manualDownloadUrl" class="flex flex-wrap items-center justify-between gap-3" role="status">
					<p class="text-sm text-text-muted">{{ TEXT.updates.manualRequired }}</p>
					<AppButton size="sm" @click="onManualDownload">{{ TEXT.updates.manualDownload }}</AppButton>
				</div>

				<!-- 状态 4: 已是最新版本 -->
				<div v-else-if="isUpToDate" class="text-hint">
					{{ TEXT.updates.upToDate }}
				</div>

				<!-- 状态 5: 取消 -->
				<div v-else-if="isCancelled" class="text-hint text-text-muted">
					{{ TEXT.updates.cancelled }}
				</div>

				<!-- 状态 6: 错误 -->
				<div v-else-if="isError" class="text-hint text-danger-text" role="alert">
					{{ updater?.message }}
				</div>
			</AppCard>
		</div>
	</div>
</template>
