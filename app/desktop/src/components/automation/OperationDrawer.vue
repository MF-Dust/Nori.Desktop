<script setup lang="ts">
import {computed, onBeforeUnmount, onMounted, ref, watch} from "vue"
import useLanguages from "../../services/i18n/useLanguages"
import Icon from "../Icon.vue"
import AppButton from "../ui/AppButton.vue"
import AppChip from "../ui/AppChip.vue"
import AppEmpty from "../ui/AppEmpty.vue"
import AppSegmented, {type SegmentItem} from "../ui/AppSegmented.vue"
import AutomationTaskCard from "./AutomationTaskCard.vue"
import {RUNTIME} from "../../services/runtime"
import {feedback} from "../../services/feedback"
import type {AutomationAuditRecordDto, AutomationTaskDto} from "../../services/runtime/types"

const props = withDefaults(defineProps<{
	/** 是否强制打开抽屉 */
	modelValue?: boolean
}>(), {
	modelValue: false,
})

const emit = defineEmits<{
	"update:modelValue": [value: boolean]
}>()

const TEXT = computed(() => useLanguages().views.main.actionCenter)

// 抽屉展开状态
const isOpen = ref(false)
const drawerRef = ref<HTMLElement | null>(null)
const capsuleBtnRef = ref<HTMLButtonElement | null>(null)

type ActionCenterTab = "active" | "audit"
const currentTab = ref<ActionCenterTab>("active")

watch(() => props.modelValue, (val) => {
	isOpen.value = val
}, {immediate: true})

const toggleDrawer = () => {
	isOpen.value = !isOpen.value
	emit("update:modelValue", isOpen.value)
}

const closeDrawer = () => {
	isOpen.value = false
	emit("update:modelValue", false)
	capsuleBtnRef.value?.focus()
}

// 自动化状态投影 (纯从快照读取，不在组件自建业务第二真相)
const automationState = computed(() => RUNTIME.snapshot.value?.automation)

// 当前所有任务列表
const tasks = computed<AutomationTaskDto[]>(() => {
	const S = automationState.value
	if (!S) return []
	const LIST: AutomationTaskDto[] = []
	if (S.activeTask) {
		LIST.push(S.activeTask)
	}
	if (Array.isArray(S.tasks)) {
		for (const T of S.tasks) {
			if (!LIST.some(item => item.id === T.id)) {
				LIST.push(T)
			}
		}
	}
	return LIST
})

// 活跃任务数 (queued / running / awaiting_approval / paused)
const activeTasks = computed(() =>
	tasks.value.filter(t => t.state === "running" || t.state === "awaiting_approval" || t.state === "queued" || t.state === "paused"))

// 是否有待审批任务
const hasAwaitingApproval = computed(() =>
	tasks.value.some(t => t.state === "awaiting_approval"))

// 是否有任何活动或近期任务 (决定胶囊是否显示)
const hasTasksToShow = computed(() => tasks.value.length > 0)

// 导航分段
const TAB_ITEMS = computed<SegmentItem<ActionCenterTab>[]>(() => [
	{
		key: "active",
		label: `${TEXT.value.tabs.active}${activeTasks.value.length > 0 ? ` (${activeTasks.value.length})` : ""}`,
	},
	{
		key: "audit",
		label: TEXT.value.tabs.audit,
	},
])

// 胶囊状态文案与配色
const capsuleTone = computed<"neutral" | "teal" | "success" | "warning" | "danger">(() => {
	if (hasAwaitingApproval.value) return "warning"
	const RUNNING = tasks.value.find(t => t.state === "running")
	if (RUNNING) return "teal"
	const SAFE_PAUSED = tasks.value.find(t => t.state === "paused" && t.pauseReason === "safe_page")
	if (SAFE_PAUSED) return "warning"
	const FAILED = tasks.value.find(t => t.state === "failed")
	if (FAILED) return "danger"
	const SUCCEEDED = tasks.value.find(t => t.state === "succeeded" || t.state === "completed")
	if (SUCCEEDED) return "success"
	return "neutral"
})

const capsuleLabel = computed(() => {
	if (hasAwaitingApproval.value) return TEXT.value.capsule.awaitingApproval
	const RUNNING = tasks.value.find(t => t.state === "running")
	if (RUNNING) return TEXT.value.capsule.running
	const SAFE_PAUSED = tasks.value.find(t => t.state === "paused" && t.pauseReason === "safe_page")
	if (SAFE_PAUSED) return TEXT.value.capsule.safePagePaused
	const QUEUED = tasks.value.find(t => t.state === "queued")
	if (QUEUED) return TEXT.value.capsule.queued
	const PAUSED = tasks.value.find(t => t.state === "paused")
	if (PAUSED) return TEXT.value.capsule.paused
	const FAILED = tasks.value.find(t => t.state === "failed")
	if (FAILED) return TEXT.value.capsule.failed
	const SUCCEEDED = tasks.value.find(t => t.state === "succeeded" || t.state === "completed")
	if (SUCCEEDED) return TEXT.value.capsule.succeeded
	const CANCELLED = tasks.value.find(t => t.state === "cancelled")
	if (CANCELLED) return TEXT.value.capsule.cancelled
	return TEXT.value.capsule.idle
})

// 动作操作中的 loading 状态
const approvingIds = ref<Set<string>>(new Set())
const rejectingIds = ref<Set<string>>(new Set())
const cancellingIds = ref<Set<string>>(new Set())
const stoppingAll = ref(false)

// 审批同意
const handleApprove = async (taskId: string, requestId?: string) => {
	const REQ_ID = requestId || taskId
	approvingIds.value.add(taskId)
	try {
		await RUNTIME.respondAutomationApproval(REQ_ID, true)
		await RUNTIME.refresh()
	} catch (error) {
		feedback.error(TEXT.value.feedback.approveFailed, error)
	} finally {
		approvingIds.value.delete(taskId)
	}
}

// 审批拒绝
const handleReject = async (taskId: string, requestId?: string) => {
	const REQ_ID = requestId || taskId
	rejectingIds.value.add(taskId)
	try {
		await RUNTIME.respondAutomationApproval(REQ_ID, false)
		await RUNTIME.refresh()
	} catch (error) {
		feedback.error(TEXT.value.feedback.rejectFailed, error)
	} finally {
		rejectingIds.value.delete(taskId)
	}
}

// 单个任务取消
const handleCancelTask = async (taskId: string) => {
	cancellingIds.value.add(taskId)
	try {
		const TARGET = tasks.value.find(t => t.id === taskId)
		if (TARGET?.taskKind === "browser") {
			try {
				await RUNTIME.automationBrowserStopTask(taskId)
			} catch {
				await RUNTIME.stopAutomationTask(taskId)
			}
		} else {
			await RUNTIME.stopAutomationTask(taskId)
		}
		await RUNTIME.refresh()
	} catch (error) {
		feedback.error(TEXT.value.feedback.stopTaskFailed, error)
	} finally {
		cancellingIds.value.delete(taskId)
	}
}

// 全部停止
const handleStopAll = async () => {
	stoppingAll.value = true
	try {
		await RUNTIME.stopAllAutomation()
		await RUNTIME.refresh()
	} catch (error) {
		feedback.error(TEXT.value.feedback.stopAllFailed, error)
	} finally {
		stoppingAll.value = false
	}
}

// ------------------------------------------------------------------
// 执行审计历史
// ------------------------------------------------------------------

const auditRecords = ref<AutomationAuditRecordDto[]>([])
const auditLoading = ref(false)
const auditError = ref("")

const loadAuditLog = async () => {
	auditLoading.value = true
	auditError.value = ""
	try {
		auditRecords.value = await RUNTIME.automationAuditList(50)
	} catch (error) {
		auditError.value = error instanceof Error ? error.message : String(error)
		feedback.error(TEXT.value.feedback.loadAuditFailed, error)
	} finally {
		auditLoading.value = false
	}
}

watch(currentTab, (tab) => {
	if (tab === "audit") {
		void loadAuditLog()
	}
})

watch(isOpen, (open) => {
	if (open && currentTab.value === "audit") {
		void loadAuditLog()
	}
})

const formatAuditTime = (timeStr?: string | null) => {
	if (!timeStr) return ""
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

const getOutcomeTone = (outcome?: string): "success" | "danger" | "warning" | "neutral" => {
	switch (outcome) {
		case "succeeded":
		case "completed":
			return "success"
		case "failed":
			return "danger"
		case "rejected":
			return "warning"
		case "cancelled":
		default:
			return "neutral"
	}
}

const getOutcomeLabel = (outcome?: string): string => {
	if (!outcome) return TEXT.value.audit.outcomes.unknown
	const OUTCOMES = TEXT.value.audit.outcomes as Record<string, string>
	// eslint-disable-next-line -- Codacy误报：审计枚举只读本地化查询
	return OUTCOMES[outcome] || outcome
}

const getTaskKindLabel = (kind?: string): string => {
	if (!kind) return TEXT.value.taskKinds.unknown
	const KINDS = TEXT.value.taskKinds as Record<string, string>
	// eslint-disable-next-line -- Codacy误报：后端枚举只读本地化查询
	return KINDS[kind] || kind
}

const getActionKindLabel = (kind?: string): string => {
	if (!kind) return TEXT.value.actionKinds.unknown
	const ACTIONS = TEXT.value.actionKinds as Record<string, string>
	// eslint-disable-next-line -- Codacy误报：后端枚举只读本地化查询
	return ACTIONS[kind] || kind
}

const getFailureReasonText = (code?: string | null): string | null => {
	if (!code) return null
	const ERRORS = TEXT.value.errors as Record<string, string>
	// eslint-disable-next-line -- Codacy误报：错误码只读本地化查询
	return ERRORS[code] || code
}

// 键盘 Escape 监听与焦点陷阱
const onKeydown = (event: KeyboardEvent) => {
	if (event.key === "Escape" && isOpen.value) {
		event.preventDefault()
		closeDrawer()
	}
}

onMounted(() => {
	window.addEventListener("keydown", onKeydown)
})

onBeforeUnmount(() => {
	window.removeEventListener("keydown", onKeydown)
})
</script>

<template>
	<!--
		行动中心胶囊与抽屉:
		无活动/历史任务时不渲染胶囊，完全不抢主聊天视觉中心；
		有任务时以紧凑微光胶囊呈现，点击展开侧边抽屉。
	-->
	<div class="relative inline-flex items-center">
		<!-- 紧凑悬浮胶囊 -->
		<button
			v-if="hasTasksToShow"
			ref="capsuleBtnRef"
			type="button"
			class="btn-base gap-2 px-2.5 py-1 rounded-pill text-xs font-500 transition-all duration-200 border"
			:class="[
				hasAwaitingApproval || capsuleTone === 'warning'
					? 'bg-warning/15 border-warning/50 text-warning shadow-[0_0_1.2rem_var(--warning)] animate-pulse'
					: capsuleTone === 'teal'
						? 'bg-nori-teal-bright/12 border-nori-teal-bright/40 text-nori-teal-bright shadow-[0_0_1.2rem_var(--glow-teal-soft)]'
						: 'bg-overlay-6 border-line-subtle text-text-muted hover:border-line-strong hover:text-text-primary',
			]"
			:aria-expanded="isOpen"
			:aria-label="TEXT.capsule.toggle"
			@click="toggleDrawer"
		>
			<Icon
				:name="hasAwaitingApproval || capsuleTone === 'warning' ? 'shield' : capsuleTone === 'teal' ? 'activity' : 'bot'"
				:size="13"
				:class="capsuleTone === 'teal' ? 'spin' : ''"
			/>
			<span class="truncate max-w-[12rem]">{{ capsuleLabel }}</span>
			<span v-if="activeTasks.length > 1" class="mono text-xs opacity-75">
				({{ activeTasks.length }})
			</span>
		</button>

		<!-- 抽屉蒙层与滑动面板 -->
		<Transition name="drawer-fade">
			<div
				v-if="isOpen"
				class="fixed inset-0 z-50 flex justify-end bg-bg-abyss/60 backdrop-blur-[0.4rem] transition-opacity duration-200"
				@click.self="closeDrawer"
			>
				<aside
					ref="drawerRef"
					class="w-[32rem] max-w-[90vw] h-full flex flex-col bg-bg-card/95 border-l border-line-subtle shadow-elev-3 backdrop-blur-[1.6rem] transition-transform duration-250 focus-ring"
					role="dialog"
					aria-modal="true"
					:aria-label="TEXT.title"
					tabindex="-1"
				>
					<!-- 抽屉顶部栏 -->
					<header class="shrink-0 flex items-center justify-between gap-3 px-4 py-3 border-b border-line-subtle bg-overlay-2">
						<div class="flex items-center gap-2 min-w-0">
							<span class="w-7 h-7 rounded-sm flex items-center justify-center bg-nori-teal-bright/10 border border-nori-teal-bright/20 text-nori-teal-bright shrink-0">
								<Icon name="bot" :size="15"/>
							</span>
							<div class="flex flex-col min-w-0">
								<h2 class="text-sm font-600 text-text-primary truncate m-0">
									{{ TEXT.title }}
								</h2>
								<span class="text-xs text-text-faint truncate">
									{{ TEXT.subtitle }}
								</span>
							</div>
						</div>

						<div class="flex items-center gap-2 shrink-0">
							<AppButton
								v-if="currentTab === 'active' && activeTasks.length > 0"
								variant="danger"
								size="sm"
								icon="stop"
								:loading="stoppingAll"
								@click="handleStopAll"
							>
								{{ TEXT.stopAll }}
							</AppButton>

							<button
								type="button"
								class="btn-close"
								:title="TEXT.close"
								:aria-label="TEXT.close"
								@click="closeDrawer"
							>
								<Icon name="close" class="close-icon"/>
							</button>
						</div>
					</header>

					<!-- 标签切换导航 (当前任务 vs 执行审计) -->
					<div class="shrink-0 px-4 pt-3 pb-1">
						<AppSegmented
							v-model="currentTab"
							:items="TAB_ITEMS"
							:label="TEXT.title"
							size="sm"
						/>
					</div>

					<!-- 任务列表容器 (当前任务) -->
					<div v-if="currentTab === 'active'" class="flex-1 min-h-0 scroll-area p-4 flex flex-col gap-3">
						<template v-if="tasks.length > 0">
							<AutomationTaskCard
								v-for="task in tasks"
								:key="task.id"
								:task="task"
								:approving="approvingIds.has(task.id)"
								:rejecting="rejectingIds.has(task.id)"
								:cancelling="cancellingIds.has(task.id)"
								@approve="handleApprove"
								@reject="handleReject"
								@cancel="handleCancelTask"
							/>
						</template>

						<AppEmpty
							v-else
							icon="bot"
							:title="TEXT.emptyTitle"
							:desc="TEXT.emptyDesc"
						/>
					</div>

					<!-- 审计历史容器 (执行审计) -->
					<div v-else-if="currentTab === 'audit'" class="flex-1 min-h-0 scroll-area p-4 flex flex-col gap-3">
						<div class="flex items-center justify-between pb-1">
							<span class="text-xs text-text-faint">{{ TEXT.audit.subtitle }}</span>
							<AppButton
								variant="ghost"
								size="sm"
								icon="refresh"
								:loading="auditLoading"
								@click="loadAuditLog"
							>
								{{ TEXT.audit.refresh }}
							</AppButton>
						</div>

						<!-- 错误提示与重试 -->
						<div
							v-if="auditError"
							class="surface-card flex items-center justify-between p-3 text-xs text-danger-text border border-danger/30"
							role="alert"
						>
							<span>{{ auditError }}</span>
							<AppButton size="sm" variant="ghost" @click="loadAuditLog">
								{{ TEXT.audit.refresh }}
							</AppButton>
						</div>

						<!-- 加载中 -->
						<div
							v-else-if="auditLoading"
							class="py-8 text-center text-xs text-text-faint flex items-center justify-center gap-2"
						>
							<Icon name="loading" :size="15" class="spin"/>
							<span>{{ TEXT.audit.loading }}</span>
						</div>

						<!-- 空状态 -->
						<AppEmpty
							v-else-if="auditRecords.length === 0"
							icon="clock"
							:title="TEXT.audit.empty"
						/>

						<!-- 审计记录列表 (紧凑/清晰/层级明确) -->
						<div v-else class="flex flex-col gap-2">
							<div
								v-for="record in auditRecords"
								:key="record.id"
								class="surface-card flex flex-col gap-1.5 p-2.5 border border-line-subtle text-xs"
							>
								<div class="flex items-center justify-between gap-2">
									<div class="flex items-center gap-1.5 min-w-0">
										<span class="mono text-text-faint shrink-0">
											{{ formatAuditTime(record.timestamp) }}
										</span>
										<AppChip tone="teal" size="sm">
											{{ getTaskKindLabel(record.taskKind) }}
										</AppChip>
										<span class="px-1.5 py-0.5 rounded-xs bg-overlay-4 border border-line-subtle text-text-body font-500 truncate">
											{{ getActionKindLabel(record.actionCategory) }}
										</span>
									</div>

									<AppChip :tone="getOutcomeTone(record.outcome)" dot size="sm" class="shrink-0">
										{{ getOutcomeLabel(record.outcome) }}
									</AppChip>
								</div>

								<!-- 失败原因展示 -->
								<div
									v-if="record.failureReason"
									class="flex items-center gap-1.5 text-xs text-danger-text pt-1 border-t border-line-subtle/40"
								>
									<Icon name="alert" :size="12" class="shrink-0"/>
									<span class="truncate">{{ TEXT.audit.failureReason }}: {{ getFailureReasonText(record.failureReason) }}</span>
								</div>
							</div>
						</div>
					</div>
				</aside>
			</div>
		</Transition>
	</div>
</template>
