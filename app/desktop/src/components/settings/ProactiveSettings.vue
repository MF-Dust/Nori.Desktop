<script setup lang="ts">
import {computed, onBeforeUnmount, onMounted, ref} from "vue"
import {RUNTIME, type ReminderDto} from "../../services/runtime"
import {useSnapshotSave} from "../../composables/useSnapshotSave"
import {feedback} from "../../services/feedback"
import {i18n} from "../../services/i18n"
import useLanguages from "../../services/i18n/useLanguages"
import Icon from "../Icon.vue"
import AppCard from "../ui/AppCard.vue"
import AppSectionHeader from "../ui/AppSectionHeader.vue"
import AppSwitchRow from "../ui/AppSwitchRow.vue"
import AppButton from "../ui/AppButton.vue"
import AppChip from "../ui/AppChip.vue"
import AppEmpty from "../ui/AppEmpty.vue"
import AppConfirm from "../ui/AppConfirm.vue"
import AppSkeleton from "../ui/AppSkeleton.vue"

const TEXT = computed(() => useLanguages().views.main.proactive)
const UI_I18N = computed(() => useLanguages().components.ui.state)

const isSafeMode = computed(() => RUNTIME.snapshot.value?.app.safeMode ?? false)
const isRefreshing = ref(false)
const isAdding = ref(false)
const isCancelling = ref(false)
const validationError = ref("")

// 刷新状态
const refreshSnapshot = async () => {
	isRefreshing.value = true
	try {
		await RUNTIME.refresh()
	} catch (error) {
		feedback.error(TEXT.value.refreshFailed, error)
	} finally {
		isRefreshing.value = false
	}
}

// 自动保存与字段定义
const SAVE_MGR = useSnapshotSave({
	onError: (_key, error) => feedback.error(TEXT.value.reminders.addFailed, error),
})
const {defineField} = SAVE_MGR

const idleEnabledField = defineField(
	"idleEnabled",
	snapshot => snapshot.proactive.idleEnabled,
	true,
	val => RUNTIME.updateProactive({idleEnabled: val}),
)
const idleEnabled = idleEnabledField.value

const dailyGreetingField = defineField(
	"dailyGreeting",
	snapshot => snapshot.proactive.dailyGreeting,
	true,
	val => RUNTIME.updateProactive({dailyGreeting: val}),
)
const dailyGreeting = dailyGreetingField.value

const idleMinutesField = defineField(
	"idleMinutes",
	snapshot => snapshot.proactive.idleMinutes,
	15,
	val => RUNTIME.updateProactive({idleMinutes: val}),
)
const idleMinutes = idleMinutesField.value

// 提醒列表 (防御性提取)
interface ReminderViewModel {
	id: string
	content: string
	triggerTime: number
	repeatDaily?: boolean
	status?: string
}

const reminders = computed<ReminderViewModel[]>(() => {
	const rawList: ReminderDto[] = RUNTIME.snapshot.value?.proactive.reminders ?? []
	return rawList.map(item => ({
		id: item.id,
		content: item.content,
		triggerTime: item.triggerTime,
		repeatDaily: item.repeatDaily,
		status: item.status,
	}))
})

// 时间响应式驱动 (每 30 秒递进一次)
const nowTick = ref(Date.now())
let tickTimer: ReturnType<typeof setInterval> | null = null

onMounted(async () => {
	await RUNTIME.init()
	tickTimer = setInterval(() => {
		nowTick.value = Date.now()
	}, 30_000)
})

onBeforeUnmount(() => {
	if (tickTimer) {
		clearInterval(tickTimer)
		tickTimer = null
	}
})

// 绝对时间格式化
const formatAbsoluteTime = (timestamp: number): string => {
	try {
		const date = new Date(timestamp)
		if (isNaN(date.getTime())) return ""
		const locale = i18n.global.locale.value === "en-US" ? "en-US" : "zh-CN"
		return new Intl.DateTimeFormat(locale, {
			month: "numeric",
			day: "numeric",
			hour: "2-digit",
			minute: "2-digit",
			hour12: false,
		}).format(date)
	} catch {
		return new Date(timestamp).toLocaleTimeString()
	}
}

// 相对时间格式化
const formatRelativeTime = (timestamp: number): string => {
	const now = nowTick.value
	const diffMs = timestamp - now
	const diffSec = Math.round(diffMs / 1000)
	const locale = i18n.global.locale.value === "en-US" ? "en-US" : "zh-CN"

	if (diffMs <= 0) {
		return TEXT.value.reminders.dueNow
	}

	try {
		const rtf = new Intl.RelativeTimeFormat(locale, {numeric: "always"})
		const diffMin = Math.round(diffSec / 60)
		const diffHour = Math.round(diffSec / 3600)
		const diffDay = Math.round(diffSec / 86400)

		if (diffMin < 60) {
			return rtf.format(Math.max(1, diffMin), "minute")
		}
		if (diffHour < 24) {
			return rtf.format(diffHour, "hour")
		}
		return rtf.format(diffDay, "day")
	} catch {
		const minutes = Math.max(1, Math.round(diffMs / 60000))
		return `${minutes} ${TEXT.value.minutesLater}`
	}
}

// 新建提醒输入与预设
const newReminderText = ref("")
const selectedPreset = ref<number>(15)
const customMinutesInput = ref<number | null>(null)
const repeatDaily = ref(false)

const isCustomMode = computed(() => selectedPreset.value === -1)

const effectiveDelayMinutes = computed<number>(() => {
	if (isCustomMode.value) {
		return customMinutesInput.value ?? 0
	}
	return selectedPreset.value
})

const REMINDER_PRESETS = computed(() => [
	{label: `5 ${TEXT.value.minutesLater}`, value: 5},
	{label: `15 ${TEXT.value.minutesLater}`, value: 15},
	{label: `30 ${TEXT.value.minutesLater}`, value: 30},
	{label: `1 ${TEXT.value.hourLater}`, value: 60},
	{label: `2 ${TEXT.value.hoursLater}`, value: 120},
	{label: `4 ${TEXT.value.hoursLater}`, value: 240},
	{label: `1 ${TEXT.value.dayLater}`, value: 1440},
	{label: TEXT.value.customDelay, value: -1},
])

// 校验与提交
const validateForm = (): boolean => {
	const content = newReminderText.value.trim()
	if (!content) {
		validationError.value = TEXT.value.reminders.contentRequired
		return false
	}
	if (content.length > 200) {
		validationError.value = TEXT.value.reminders.contentTooLong
		return false
	}
	const delay = effectiveDelayMinutes.value
	if (!delay || delay <= 0 || delay > 43200) {
		validationError.value = TEXT.value.reminders.delayRequired
		return false
	}
	validationError.value = ""
	return true
}

const onInputChange = () => {
	if (validationError.value) {
		validateForm()
	}
}

const onPresetChange = (val: number) => {
	selectedPreset.value = val
	if (val !== -1) {
		customMinutesInput.value = null
	}
	if (validationError.value) {
		validateForm()
	}
}

const onIdleEnabledChange = (value: boolean) => {
	if (isSafeMode.value) return
	idleEnabled.value = value
	void idleEnabledField.saveNow()
}

const onDailyGreetingChange = (value: boolean) => {
	if (isSafeMode.value) return
	dailyGreeting.value = value
	void dailyGreetingField.saveNow()
}

const onIdleMinutesChange = (value: number) => {
	if (isSafeMode.value) return
	idleMinutes.value = value
	void idleMinutesField.saveNow()
}

// 手动创建提醒
const createReminder = async () => {
	if (isAdding.value) return
	if (!validateForm()) return

	isAdding.value = true
	try {
		const CREATED = await RUNTIME.reminderAdd(newReminderText.value.trim(), effectiveDelayMinutes.value)
		if (repeatDaily.value) {
			try {
				await RUNTIME.reminderUpdate({id: CREATED.id, repeatDaily: true})
			} catch (error) {
				try { await RUNTIME.reminderCancel(CREATED.id) } catch { /* 保持原始更新异常 */ }
				throw error
			}
		}
		await RUNTIME.refresh()
		newReminderText.value = ""
		selectedPreset.value = 15
		customMinutesInput.value = null
		repeatDaily.value = false
		validationError.value = ""
	} catch (error) {
		feedback.error(TEXT.value.reminders.addFailed, error)
	} finally {
		isAdding.value = false
	}
}

// 取消确认弹窗状态
const isCancelConfirmOpen = ref(false)
const pendingCancelItem = ref<ReminderViewModel | null>(null)

const promptCancelReminder = (item: ReminderViewModel) => {
	pendingCancelItem.value = item
	isCancelConfirmOpen.value = true
}

const confirmCancelReminder = async () => {
	if (!pendingCancelItem.value || isCancelling.value) return
	const item = pendingCancelItem.value
	isCancelling.value = true
	try {
		await RUNTIME.reminderCancel(item.id)
		await RUNTIME.refresh()
		isCancelConfirmOpen.value = false
		pendingCancelItem.value = null
	} catch (error) {
		feedback.error(TEXT.value.reminders.cancelFailed, error)
	} finally {
		isCancelling.value = false
	}
}
</script>

<template>
	<div class="w-full h-full flex flex-col gap-4 px-6 py-4 scroll-area">
		<!-- 页面标题与刷新操作 -->
		<AppSectionHeader :title="TEXT.title" :subtitle="TEXT.subtitle">
			<template #actions>
				<AppButton
					variant="ghost"
					size="sm"
					icon="refresh"
					:loading="isRefreshing"
					@click="refreshSnapshot"
				>
					{{ TEXT.refresh }}
				</AppButton>
			</template>
		</AppSectionHeader>

		<!-- 安全模式提示 -->
		<div
			v-if="isSafeMode"
			class="flex items-center gap-2.5 px-4 py-2.5 rounded-sm bg-warning/10 border border-warning/35 text-warning text-xs"
			role="status"
		>
			<Icon name="alert" :size="14" class="shrink-0"/>
			<span>{{ TEXT.safeModeWarning }}</span>
		</div>

		<!-- 骨架占位 (无快照时优雅兜底) -->
		<AppSkeleton v-if="!RUNTIME.snapshot.value" :rows="4"/>

		<div v-else class="flex flex-col gap-3.5 pb-5">
			<!-- 1. 挂机主动关怀 -->
			<AppCard :title="TEXT.idle.title" icon="sparkles">
				<AppSwitchRow
					:title="TEXT.idle.enabled"
					:desc="TEXT.idle.enabledDesc"
					:model-value="idleEnabled"
					:disabled="isSafeMode"
					@update:model-value="onIdleEnabledChange"
				/>

				<div v-if="idleEnabled" class="field flex flex-col gap-2 pt-1 border-t border-line-subtle">
					<div class="flex items-center justify-between">
						<span class="field-label">{{ TEXT.idle.interval }}</span>
						<span class="text-hint">{{ TEXT.idle.hint }}</span>
					</div>

					<div class="flex flex-wrap gap-2">
						<!-- 单选按钮本体用 sr-only 隐藏而非 display:none, 保留键盘可达与读屏语义 -->
						<label
							v-for="min in [5, 15, 30, 60]"
							:key="min"
							class="pill-choice focus-ring-within gap-1.5 px-3.5 py-1.5 text-xs"
							:class="[
								idleMinutes === min ? 'pill-choice-on' : 'pill-choice-off',
								isSafeMode ? 'opacity-50 cursor-not-allowed pointer-events-none' : ''
							]"
						>
							<input
								v-model="idleMinutes"
								type="radio"
								:value="min"
								:disabled="isSafeMode"
								class="sr-only"
								@change="onIdleMinutesChange(min)"
							/>
							<span class="mono">{{ min }}</span>
							<span>{{ TEXT.minutes }}</span>
						</label>
					</div>
				</div>
			</AppCard>

			<!-- 2. 日常早晚安日程 -->
			<AppCard :title="TEXT.daily.title" icon="clock">
				<AppSwitchRow
					:title="TEXT.daily.enabled"
					:desc="TEXT.daily.enabledDesc"
					:model-value="dailyGreeting"
					:disabled="isSafeMode"
					@update:model-value="onDailyGreetingChange"
				/>

				<!-- 生活时刻预览卡片组 -->
				<div class="flex flex-col gap-2 pt-1 border-t border-line-subtle">
					<span class="field-label">{{ TEXT.daily.scheduleTitle }}</span>
					<div class="grid grid-cols-1 md:grid-cols-3 gap-2.5">
						<!-- 晨间时刻 -->
						<div class="surface-inset p-3 flex flex-col gap-1">
							<div class="flex items-center justify-between">
								<span class="text-xs font-600 text-text-primary">{{ TEXT.daily.morningTitle }}</span>
								<span class="mono text-xs text-nori-teal-bright">{{ TEXT.daily.morningTime }}</span>
							</div>
							<span class="text-hint leading-relaxed">{{ TEXT.daily.morningDesc }}</span>
						</div>

						<!-- 午餐时刻 -->
						<div class="surface-inset p-3 flex flex-col gap-1">
							<div class="flex items-center justify-between">
								<span class="text-xs font-600 text-text-primary">{{ TEXT.daily.lunchTitle }}</span>
								<span class="mono text-xs text-nori-teal-bright">{{ TEXT.daily.lunchTime }}</span>
							</div>
							<span class="text-hint leading-relaxed">{{ TEXT.daily.lunchDesc }}</span>
						</div>

						<!-- 晚间时刻 -->
						<div class="surface-inset p-3 flex flex-col gap-1">
							<div class="flex items-center justify-between">
								<span class="text-xs font-600 text-text-primary">{{ TEXT.daily.nightTitle }}</span>
								<span class="mono text-xs text-nori-teal-bright">{{ TEXT.daily.nightTime }}</span>
							</div>
							<span class="text-hint leading-relaxed">{{ TEXT.daily.nightDesc }}</span>
						</div>
					</div>
				</div>
			</AppCard>

			<!-- 3. 定时提醒管理 (持久化于后端 SQLite, 重启自动恢复) -->
			<AppCard :title="TEXT.reminders.title" icon="bell" :desc="TEXT.reminders.desc">
				<template #actions>
					<AppChip v-if="reminders.length > 0" tone="teal">
						<span class="mono">{{ reminders.length }}</span>
						<span>{{ TEXT.reminders.count }}</span>
					</AppChip>
				</template>

				<!-- 添加提醒表单区 -->
				<div class="surface-inset p-3 flex flex-col gap-2.5">
					<div class="flex flex-col md:flex-row gap-2">
						<input
							v-model="newReminderText"
							class="input-base flex-1"
							:placeholder="TEXT.reminders.placeholder"
							maxlength="200"
							@input="onInputChange"
							@keydown.enter="createReminder"
						/>

						<div class="flex items-center gap-2">
							<n-select
								:value="selectedPreset"
								:options="REMINDER_PRESETS"
								class="w-[14rem]"
								@update:value="onPresetChange"
							/>

							<input
								v-if="isCustomMode"
								v-model.number="customMinutesInput"
								type="number"
								min="1"
								max="43200"
								class="input-base w-[10rem] mono"
								:placeholder="TEXT.reminders.customMinutesPlaceholder"
								@input="onInputChange"
								@keydown.enter="createReminder"
							/>

							<AppButton
								variant="primary"
								size="sm"
								icon="plus"
								:loading="isAdding"
								:disabled="!newReminderText.trim() || isAdding || isSafeMode"
								@click="createReminder"
							>
								{{ isAdding ? TEXT.reminders.adding : TEXT.reminders.add }}
							</AppButton>
						</div>
					</div>

					<AppSwitchRow
						:title="TEXT.reminders.badgeDaily"
						:model-value="repeatDaily"
						:disabled="isSafeMode || isAdding"
						@update:model-value="repeatDaily = $event"
					/>

					<!-- 错误校验提示 -->
					<div v-if="validationError" class="flex items-center gap-1 text-xs text-danger-text" role="alert">
						<Icon name="alert" :size="12" class="shrink-0"/>
						<span>{{ validationError }}</span>
					</div>
				</div>

				<!-- 提醒列表区 -->
				<div class="flex flex-col gap-2 mt-1">
					<!-- 空状态 -->
					<AppEmpty
						v-if="reminders.length === 0"
						icon="bell"
						:title="TEXT.reminders.emptyTitle"
						:desc="TEXT.reminders.emptyDesc"
					/>

					<!-- 列表条目 -->
					<div
						v-for="item in reminders"
						:key="item.id"
						class="surface-inset p-3.5 flex items-start justify-between gap-3 transition-all duration-200 hover:(border-line-strong bg-overlay-6)"
					>
						<div class="flex flex-col gap-1.5 min-w-0 flex-1">
							<!-- 标题与状态徽标 -->
							<div class="flex flex-wrap items-center gap-2">
								<span class="text-base text-text-primary font-500 break-words">{{ item.content }}</span>
								<AppChip tone="neutral">
									{{ item.repeatDaily ? TEXT.reminders.badgeDaily : TEXT.reminders.badgeOneTime }}
								</AppChip>
								<AppChip v-if="item.status === 'claimed'" tone="warning">
									{{ TEXT.reminders.statusClaimed }}
								</AppChip>
							</div>

							<!-- 触发时间与相对时间 -->
							<div class="flex flex-wrap items-center gap-2 text-xs text-text-muted">
								<div class="flex items-center gap-1 mono text-nori-teal-bright">
									<Icon name="clock" :size="12" class="shrink-0"/>
									<span>{{ TEXT.reminders.trigger }}:</span>
									<span>{{ formatAbsoluteTime(item.triggerTime) }}</span>
								</div>
								<span class="text-text-faint">•</span>
								<span class="mono text-text-faint">
									{{ formatRelativeTime(item.triggerTime) }}
								</span>
							</div>
						</div>

						<!-- 取消操作按钮 -->
						<button
							type="button"
							class="btn-icon text-text-muted hover:text-danger-text shrink-0"
							:title="TEXT.reminders.cancel"
							:aria-label="TEXT.reminders.cancel"
							@click="promptCancelReminder(item)"
						>
							<Icon name="close" :size="14"/>
						</button>
					</div>
				</div>
			</AppCard>
		</div>

		<!-- 取消提醒确认弹窗 -->
		<AppConfirm
			:show="isCancelConfirmOpen"
			:title="TEXT.reminders.cancelConfirm.title"
			:desc="TEXT.reminders.cancelConfirm.desc"
			tone="danger"
			:confirm-label="TEXT.reminders.cancelConfirm.confirm"
			:cancel-label="TEXT.reminders.cancelConfirm.cancel"
			:close-label="UI_I18N.close"
			:loading="isCancelling"
			@confirm="confirmCancelReminder"
			@update:show="isCancelConfirmOpen = $event"
		/>
	</div>
</template>
