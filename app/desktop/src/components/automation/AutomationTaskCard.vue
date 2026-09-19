<script setup lang="ts">
import {computed, ref} from "vue"
import useLanguages from "../../services/i18n/useLanguages"
import Icon from "../Icon.vue"
import AppChip from "../ui/AppChip.vue"
import AppButton from "../ui/AppButton.vue"
import type {AutomationTaskDto, BrowserTaskResultDto} from "../../services/runtime/types"

const props = defineProps<{
	task: AutomationTaskDto
	approving?: boolean
	rejecting?: boolean
	cancelling?: boolean
}>()

const emit = defineEmits<{
	approve: [taskId: string, requestId?: string]
	reject: [taskId: string, requestId?: string]
	cancel: [taskId: string]
}>()

const TEXT = computed(() => useLanguages().views.main.actionCenter)

// 脱敏短 ID (最多取 8 位)
const shortTaskId = computed(() => {
	const ID = props.task.id || ""
	return ID.length > 8 ? ID.slice(0, 8) : ID
})

// 脱敏展示标题
const displayTitle = computed(() => {
	if (props.task.title && props.task.title.trim().length > 0) {
		return props.task.title
	}
	return `${TEXT.value.card.taskId}: ${shortTaskId.value}`
})

// 任务类型 (browser / desktop / custom)
const taskKindLabel = computed(() => {
	const K = props.task.taskKind || ""
	const KINDS = TEXT.value.taskKinds as Record<string, string>
	// Codacy 误报：键来自后端枚举，仅用于只读本地化查询。
	// eslint-disable-next-line -- Codacy误报：后端枚举只读查询
	return KINDS[K] || (K ? K : null)
})

// 任务类型对应图标
const taskKindIcon = computed(() => {
	if (props.task.taskKind === "browser") return "globe"
	if (props.task.taskKind === "desktop") return "monitor"
	return "bot"
})

// 是否安全页面暂停
const isSafePagePaused = computed(() =>
	props.task.state === "paused" && props.task.pauseReason === "safe_page")

// 暂停原因可读文本
const pauseReasonLabel = computed(() => {
	if (!props.task.pauseReason) return null
	const REASONS = TEXT.value.pauseReasons as Record<string, string>
	// eslint-disable-next-line -- Codacy误报：后端枚举只读查询
	return REASONS[props.task.pauseReason] || props.task.pauseReason
})

// 状态色与文案
const stateTone = computed<"neutral" | "teal" | "success" | "warning" | "danger">(() => {
	if (isSafePagePaused.value) return "warning"
	switch (props.task.state) {
		case "running":
			return "teal"
		case "awaiting_approval":
			return "warning"
		case "succeeded":
		case "completed":
			return "success"
		case "failed":
			return "danger"
		case "paused":
		case "queued":
		case "cancelled":
		default:
			return "neutral"
	}
})

const stateLabel = computed(() => {
	if (isSafePagePaused.value) {
		return TEXT.value.capsule.safePagePaused
	}
	const S = props.task.state
	const STATES = TEXT.value.states as Record<string, string>
	// eslint-disable-next-line -- Codacy误报：状态键仅用于只读本地化查询
	return STATES[S] || S
})

// 错误分类可读文案
const failureReasonText = computed(() => {
	if (!props.task.failureCode) return null
	const ERRORS = TEXT.value.errors as Record<string, string>
	// eslint-disable-next-line -- Codacy误报：错误码只读本地化查询
	return ERRORS[props.task.failureCode] || props.task.failureCode
})

// 格式化时间
const formatTime = (timeStr?: string | null) => {
	if (!timeStr) return null
	try {
		const D = new Date(timeStr)
		if (Number.isNaN(D.getTime())) return timeStr
		const H = String(D.getHours()).padStart(2, "0")
		const M = String(D.getMinutes()).padStart(2, "0")
		const S = String(D.getSeconds()).padStart(2, "0")
		return `${H}:${M}:${S}`
	} catch {
		return timeStr
	}
}

// 动作种类映射
const actionKindLabels = computed(() => {
	const KINDS = props.task.actionKinds || []
	const MAP = TEXT.value.actionKinds as Record<string, string>
	// eslint-disable-next-line -- Codacy误报：后端枚举只读查询
	return KINDS.map(k => MAP[k] || MAP.unknown || k)
})

// 进度百分比
const progressPercent = computed(() => {
	if (typeof props.task.progress === "number") {
		const P = props.task.progress
		return Math.min(100, Math.max(0, P <= 1 ? Math.round(P * 100) : Math.round(P)))
	}
	if (props.task.totalSteps && props.task.totalSteps > 0 && props.task.currentStep) {
		return Math.min(100, Math.max(0, Math.round((props.task.currentStep / props.task.totalSteps) * 100)))
	}
	return null
})

// 受限结果提取 (脱敏且受限)
const boundedResultSummary = computed<string | null>(() => {
	if (props.task.resultSummary && props.task.resultSummary.trim().length > 0) {
		return props.task.resultSummary
	}
	if (props.task.result) {
		const R = props.task.result as BrowserTaskResultDto
		if (typeof R.summary === "string" && R.summary.trim().length > 0) {
			return R.summary
		}
		if (typeof R.data === "string" && R.data.trim().length > 0) {
			return R.data
		}
	}
	return null
})

// 是否存在受限结果
const hasBoundedResult = computed(() =>
	Boolean(props.task.hasResult || boundedResultSummary.value || (props.task.state === "succeeded" && props.task.result)))

// 查看结果展开状态
const showResult = ref(false)

const toggleResult = () => {
	showResult.value = !showResult.value
}

// 是否可取消
const canCancel = computed(() => {
	const S = props.task.state
	return S === "running" || S === "queued" || S === "paused"
})

// 是否待审批
const isAwaitingApproval = computed(() => props.task.state === "awaiting_approval")

const onApprove = () => {
	emit("approve", props.task.id, props.task.approvalRequestId)
}

const onReject = () => {
	emit("reject", props.task.id, props.task.approvalRequestId)
}

const onCancel = () => {
	emit("cancel", props.task.id)
}
</script>

<template>
	<div
		class="surface-card relative flex flex-col gap-3 p-3.5 border transition-all duration-200"
		:class="isAwaitingApproval || isSafePagePaused ? 'border-warning/50 bg-warning/6' : 'border-line-subtle'"
		role="article"
		:aria-labelledby="`task-title-${task.id}`"
	>
		<!-- 卡片头部: 标题与状态 -->
		<div class="flex items-center justify-between gap-2 min-w-0">
			<div class="flex items-center gap-2 min-w-0">
				<Icon
					:name="isAwaitingApproval || isSafePagePaused ? 'shield' : task.state === 'running' ? 'activity' : taskKindIcon"
					:size="15"
					:class="isAwaitingApproval || isSafePagePaused ? 'text-warning' : task.state === 'running' ? 'text-nori-teal-bright spin' : 'text-text-muted'"
				/>
				<span
					:id="`task-title-${task.id}`"
					class="text-sm font-600 text-text-primary truncate"
				>
					{{ displayTitle }}
				</span>
			</div>

			<div class="flex items-center gap-1.5 shrink-0">
				<AppChip v-if="taskKindLabel" tone="teal" size="sm">
					{{ taskKindLabel }}
				</AppChip>
				<AppChip :tone="stateTone" dot size="sm">
					{{ stateLabel }}
				</AppChip>
			</div>
		</div>

		<!-- 步骤与进度条 -->
		<div v-if="task.state === 'running' || progressPercent !== null || task.totalSteps" class="flex flex-col gap-1.5">
			<div class="flex items-center justify-between text-xs text-text-faint">
				<span v-if="task.currentStep && task.totalSteps">
					{{ TEXT.card.step }} <span class="mono text-text-primary">{{ task.currentStep }}</span> {{ TEXT.card.of }} <span class="mono">{{ task.totalSteps }}</span>
				</span>
				<span v-else>{{ TEXT.card.progress }}</span>

				<span v-if="progressPercent !== null" class="mono text-text-muted">
					{{ progressPercent }}%
				</span>
			</div>

			<!-- 进度指示条 -->
			<div class="h-1.5 w-full rounded-pill bg-overlay-6 overflow-hidden">
				<div
					v-if="progressPercent !== null"
					class="h-full rounded-pill bg-nori-teal transition-all duration-200"
					:style="{width: `${progressPercent}%`}"
				/>
				<div
					v-else-if="task.state === 'running'"
					class="h-full w-1/3 rounded-pill bg-nori-teal-bright/70 animate-pulse"
				/>
			</div>
		</div>

		<!-- 安全页面暂停提示 -->
		<div v-if="isSafePagePaused" class="flex flex-col gap-2 p-2.5 rounded-sm bg-overlay-4 border border-warning/30">
			<div class="flex items-center gap-1.5 text-xs font-500 text-warning">
				<Icon name="shield" :size="13"/>
				<span>{{ TEXT.card.safePagePauseTitle }}</span>
			</div>
			<p class="text-xs text-text-muted leading-relaxed m-0">
				{{ TEXT.card.safePagePauseNotice }}
			</p>
			<div v-if="pauseReasonLabel" class="text-xs text-text-faint">
				{{ TEXT.card.pauseReason }}: {{ pauseReasonLabel }}
			</div>
		</div>

		<!-- 通用暂停原因 (非 safe_page) -->
		<div v-else-if="task.state === 'paused' && pauseReasonLabel" class="flex items-center gap-1.5 px-2.5 py-1.5 rounded-sm bg-overlay-4 border border-line-subtle text-xs text-text-muted">
			<Icon name="pause" :size="13" class="text-warning shrink-0"/>
			<span>{{ TEXT.card.pauseReason }}: {{ pauseReasonLabel }}</span>
		</div>

		<!-- 审批动作提示与动作标签 -->
		<div v-if="isAwaitingApproval" class="flex flex-col gap-2 p-2.5 rounded-sm bg-overlay-4 border border-warning/30">
			<div class="flex items-center gap-1.5 text-xs font-500 text-warning">
				<Icon name="shield" :size="13"/>
				<span>{{ TEXT.card.approvalRequired }}</span>
			</div>
			<p class="text-xs text-text-muted leading-relaxed m-0">
				{{ TEXT.card.approvalNotice }}
			</p>

			<div v-if="actionKindLabels.length > 0" class="flex flex-wrap gap-1.5 mt-0.5">
				<span
					v-for="kind in actionKindLabels"
					:key="kind"
					class="px-2 py-0.5 rounded-xs text-xs bg-overlay-6 border border-line-subtle text-text-body font-500"
				>
					{{ kind }}
				</span>
			</div>
		</div>

		<!-- 错误原因展示 -->
		<div v-if="failureReasonText" class="flex items-center gap-1.5 px-2.5 py-1.5 rounded-sm bg-danger/10 border border-danger/30 text-xs text-danger-text">
			<Icon name="alert" :size="13" class="shrink-0"/>
			<span class="truncate">{{ TEXT.card.errorCategory }}: {{ failureReasonText }}</span>
		</div>

		<!-- 受限结果展示区域 (受控/脱敏) -->
		<div v-if="hasBoundedResult && showResult" class="flex flex-col gap-1.5 p-2.5 rounded-sm bg-overlay-4 border border-line-subtle text-xs">
			<div class="flex items-center justify-between text-text-muted">
				<span class="font-500">{{ TEXT.card.resultTitle }}</span>
			</div>
			<p class="mono text-text-primary leading-relaxed m-0 break-words whitespace-pre-wrap">
				{{ boundedResultSummary || TEXT.card.noResult }}
			</p>
		</div>

		<!-- 时间指标 -->
		<div class="flex items-center justify-between text-xs text-text-faint mono">
			<span v-if="task.createdAt">
				{{ TEXT.card.created }}: {{ formatTime(task.createdAt) }}
			</span>
			<span v-if="task.finishedAt">
				{{ TEXT.card.finished }}: {{ formatTime(task.finishedAt) }}
			</span>
		</div>

		<!-- 操作按钮栏 -->
		<div v-if="isAwaitingApproval || canCancel || hasBoundedResult" class="flex items-center justify-between gap-2 pt-1 border-t border-line-subtle/50">
			<!-- 左侧结果查看切换 -->
			<div class="flex items-center">
				<button
					v-if="hasBoundedResult"
					type="button"
					class="btn-base text-xs text-nori-teal-bright hover:underline gap-1 p-0 focus-ring"
					:aria-expanded="showResult"
					@click="toggleResult"
				>
					<Icon :name="showResult ? 'arrow-up' : 'arrow-down'" :size="12"/>
					<span>{{ showResult ? TEXT.card.closeResult : TEXT.card.viewResult }}</span>
				</button>
			</div>

			<!-- 右侧操作按钮 -->
			<div class="flex items-center gap-2">
				<template v-if="isAwaitingApproval">
					<AppButton
						variant="ghost"
						size="sm"
						:disabled="approving || rejecting"
						:loading="rejecting"
						@click="onReject"
					>
						{{ TEXT.card.reject }}
					</AppButton>
					<AppButton
						variant="primary"
						size="sm"
						icon="check"
						:disabled="approving || rejecting"
						:loading="approving"
						@click="onApprove"
					>
						{{ TEXT.card.approve }}
					</AppButton>
				</template>

				<template v-else-if="canCancel">
					<AppButton
						variant="danger"
						size="sm"
						icon="close"
						:disabled="cancelling"
						:loading="cancelling"
						@click="onCancel"
					>
						{{ TEXT.card.cancel }}
					</AppButton>
				</template>
			</div>
		</div>
	</div>
</template>
