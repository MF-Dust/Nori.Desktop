<script setup lang="ts">
import {computed, nextTick, onBeforeUnmount, onMounted, ref} from "vue"
import useLanguages from "../../services/i18n/useLanguages.ts"
import {useSnapshotSave} from "../../composables/useSnapshotSave"
import {MODEL_LIST, type ModelInfo} from "../../services/live2d/models"
import {createLive2D} from "../../services/live2d"
import {resolveModelFileBase} from "../../services/live2d/config"
import {
	calculateModelViewportRect,
	createDefaultInteractionConfig,
	createDefaultInteractionRegion,
	type ViewportPixelRect,
} from "../../services/live2d/interactions"
import {
	RUNTIME,
	type InteractionConfig,
	type InteractionRect,
	type InteractionRegion,
} from "../../services/runtime"
import Icon from "../Icon.vue"
import AppCard from "../ui/AppCard.vue"
import AppChip from "../ui/AppChip.vue"
import AppButton from "../ui/AppButton.vue"
import AppStatTile from "../ui/AppStatTile.vue"
import AppSectionHeader from "../ui/AppSectionHeader.vue"
import AppSegmented, {type SegmentItem} from "../ui/AppSegmented.vue"
import {feedback} from "../../services/feedback"
import AdjustControls from "./AdjustControls.vue"
import Live2dBehaviorControls from "./Live2dBehaviorControls.vue"
import InteractionControls from "./InteractionControls.vue"
import InteractionRegionOverlay from "./InteractionRegionOverlay.vue"

/** 调整面板的标签页 */
type AdjustTab = "display" | "interactions"

/** 导入成功文案的驻留时间 */
const STATUS_HOLD_MS = 3000

const I18N = computed(() => useLanguages().views.main.model)
const UI_TEXT = computed(() => useLanguages().components.ui.state)

// 各模型安装状态与当前选择由后端快照提供
const installedMap = ref<Record<string, boolean>>({})
const selectedModel = ref("")

// 本地导入状态
const importing = ref(false)
const importStatusText = ref("")
let statusTimer: ReturnType<typeof setTimeout> | null = null

// 模型 id → 展示名
const modelNameOf = (id: string): string => MODEL_LIST.find((model) => model.id === id)?.name ?? id

// 卡片顶部摘要: 当前启用的模型与已安装数量
const CURRENT_MODEL_NAME = computed(() =>
	selectedModel.value ? modelNameOf(selectedModel.value) : I18N.value.expressionNone,
)
const INSTALLED_SUMMARY = computed(() => {
	const COUNT = MODEL_LIST.filter(model => installedMap.value[model.id]).length
	return `${COUNT} / ${MODEL_LIST.length}`
})

// 快照刷新后同步模型目录
const refreshStatus = async () => {
	await RUNTIME.refresh()
	const ITEMS = RUNTIME.snapshot.value?.models.items ?? []
	installedMap.value = Object.fromEntries(ITEMS.map(item => [item.id, item.installed]))
	selectedModel.value = RUNTIME.snapshot.value?.models.selected ?? selectedModel.value
}

// 本地导入 Live2D 模型 (文件选择与导入均由宿主完成)
const importLocalModel = async (sourceKind: "zip" | "folder") => {
	if (importing.value) return
	importing.value = true
	importStatusText.value = I18N.value.import.picking
	try {
		const imported = await RUNTIME.importLocalModel(sourceKind)
		if (imported?.length) {
			importStatusText.value = `${I18N.value.import.success}: ${imported.join(", ")}`
			await refreshStatus()
			if (statusTimer) clearTimeout(statusTimer)
			statusTimer = setTimeout(() => {
				importStatusText.value = ""
				statusTimer = null
			}, STATUS_HOLD_MS)
		} else importStatusText.value = ""
	} catch (error) {
		feedback.error(I18N.value.import.failed, error)
		importStatusText.value = ""
	} finally {
		importing.value = false
	}
}

// 打开整页调整面板的模型 id
const adjustFor = ref<string | null>(null)

// 调整面板内部标签页 (基础/行为 vs 自定义互动区域)
const adjustTab = ref<AdjustTab>("display")

// 独立防抖保存器 (每模型/字段独立 key, 卸载自动 flush); 设置页统一走 useSnapshotSave
const SAVE = useSnapshotSave({
	onError: (key, error) => {
		if (key.startsWith("interactions_")) feedback.error(I18N.value.interactions.saveFailed, error)
		else if (key.startsWith("display_scale_")) feedback.error(I18N.value.behavior.scaleSaveFailed, error)
		else if (key.startsWith("display_expressions_")) feedback.error(UI_TEXT.value.saveFailed, error)
		else feedback.error(I18N.value.behavior.saveFailed, error)
	},
})

// 启用模型: 写入后端 selected_model
const enableModel = async (model: ModelInfo) => {
	if (selectedModel.value === model.id || !installedMap.value[model.id]) return
	try {
		await RUNTIME.selectModel(model.id)
		selectedModel.value = model.id
		await refreshStatus()
	} catch (error) {
		feedback.error(I18N.value.enableFailed, error)
	}
}

// ---- 整页调整: 实时预览 ----
const PREVIEW = createLive2D()
const showcaseRef = ref<HTMLElement>()
const previewReady = ref(false)
const previewClickInteraction = ref(true)

// 自定义互动区域状态
const interactionsConfig = ref<InteractionConfig>(createDefaultInteractionConfig())
const selectedRegionId = ref<string | null>(null)
const isEditingInteractions = ref(true)
const isCreatingRegion = ref(false)
const modelViewport = ref<ViewportPixelRect | null>(null)
const availableMotions = ref<{group: string; names: string[]}[]>([])
const availableExpressions = ref<string[]>([])

// 面板顶部标签页: 互动区域数量直接挂在标签上, 不必进去才知道配过没有
const ADJUST_TABS = computed<SegmentItem<AdjustTab>[]>(() => [
	{key: "display", label: I18N.value.tabDisplay, icon: "settings"},
	{key: "interactions", label: I18N.value.tabInteractions, icon: "tool", count: interactionsConfig.value.regions.length},
])

// 重新计算模型在预览容器中的像素视口
const updateModelViewport = () => {
	const rect = showcaseRef.value?.getBoundingClientRect()
	if (!rect) return
	const state = PREVIEW.getState()
	const baseW = state?.baseWidth ?? rect.width
	const baseH = state?.baseHeight ?? rect.height
	const initW = state?.initialModelWidth ?? 400
	const initH = state?.initialModelHeight ?? 520
	modelViewport.value = calculateModelViewportRect(
		{width: baseW, height: baseH},
		{width: initW, height: initH},
		pvScale.value,
	)
}

const onPreviewClick = (event: MouseEvent) => {
	if (!previewReady.value || !previewClickInteraction.value) return
	// 编辑互动区域时禁止触发全局点击测试
	if (adjustTab.value === "interactions" && isEditingInteractions.value) return
	PREVIEW.tapAt(event.clientX, event.clientY)
}

const bindPreviewClick = () => {
	const CANVAS = PREVIEW.canvas()
	CANVAS?.addEventListener("click", onPreviewClick)
}

const unbindPreviewClick = () => {
	const CANVAS = PREVIEW.canvas()
	CANVAS?.removeEventListener("click", onPreviewClick)
}

const syncPreviewClickInteraction = async () => {
	await RUNTIME.refresh()
	previewClickInteraction.value = RUNTIME.snapshot.value?.behaviors.clickInteraction ?? true
	PREVIEW.setClickInteraction(previewClickInteraction.value)
}

// 预览模型显示参数 (调整的模型, 非伴侣当前模型)
const pvScale = ref(1)
const previewExpressionList = ref<string[]>([])

// 预览画布布局到预览区域 (由控制器容器自动定位)
const refreshPreviewLayout = () => {
	if (!previewReady.value) return
	PREVIEW.resize()
	PREVIEW.setUserScale(pvScale.value)
	updateModelViewport()
}

// 窗口尺寸变化时重新对齐预览画布
const onWindowResize = () => {
	if (adjustFor.value) refreshPreviewLayout()
}

// ---- 预览配置保存 (按模型存储, 伴侣窗口会热更新) ----
// 滑杆连续拖动会刷出大量写入, 统一走 SAVE 的 400ms 防抖 (每模型/字段独立计时器, 关面板与卸载时 flush)
const savePreviewScale = (): void => {
	const MODEL = adjustFor.value
	if (!MODEL) return
	SAVE.save(`display_scale_${MODEL}`, () => RUNTIME.setModelDisplay(MODEL, {scale: pvScale.value}))
}

const savePreviewExpressions = (list: string[]): void => {
	const MODEL = adjustFor.value
	if (!MODEL) return
	SAVE.save(`display_expressions_${MODEL}`, () => RUNTIME.setModelDisplay(MODEL, {expressions: list}))
}

// 预览播放表情
const applyPreviewExpressions = async (list: string[]): Promise<void> => {
	if (!previewReady.value) return
	try {
		await PREVIEW.stopExpression()
		for (const name of list) await PREVIEW.playExpression(name)
	} catch {
		/* 预览未就绪时忽略 */
	}
}

const onPreviewScale = (value: number) => {
	pvScale.value = value
	refreshPreviewLayout()
	savePreviewScale()
}

const onPreviewExpressions = (list: string[]) => {
	previewExpressionList.value = list
	savePreviewExpressions(list)
	void applyPreviewExpressions(list)
}

// ---- 互动区域修改与持久化 ----
const onUpdateRegions = (regions: InteractionRegion[]) => {
	interactionsConfig.value = {version: 1, regions}
	const modelId = adjustFor.value
	if (!modelId) return
	SAVE.save(`interactions_${modelId}`, () => RUNTIME.setModelInteractions(modelId, {version: 1, regions}))
}

const onAddRegion = () => {
	const count = interactionsConfig.value.regions.length + 1
	const newRegion = createDefaultInteractionRegion({
		name: `${I18N.value.interactions.defaultRegionName} ${count}`,
	})
	const regions = [...interactionsConfig.value.regions, newRegion]
	selectedRegionId.value = newRegion.id
	onUpdateRegions(regions)
}

const onCreateRegion = (rect: InteractionRect) => {
	const count = interactionsConfig.value.regions.length + 1
	const newRegion = createDefaultInteractionRegion({
		name: `${I18N.value.interactions.defaultRegionName} ${count}`,
		rect,
	})
	const regions = [...interactionsConfig.value.regions, newRegion]
	selectedRegionId.value = newRegion.id
	onUpdateRegions(regions)
}

const onDeleteRegion = (id: string) => {
	const regions = interactionsConfig.value.regions.filter(r => r.id !== id)
	if (selectedRegionId.value === id) selectedRegionId.value = null
	onUpdateRegions(regions)
}

const onClearRegions = () => {
	selectedRegionId.value = null
	onUpdateRegions([])
}

// 在测试模式下点击区域播放本地预设动作/表情 (不发 AI 请求)
const onRegionClick = async (region: InteractionRegion) => {
	if (isEditingInteractions.value) return

	if (region.motion.mode === "selected" && region.motion.name) {
		void PREVIEW.playMotionByName(region.motion.name)
	} else if (region.motion.mode === "random") {
		const groups = availableMotions.value
		if (groups.length > 0) {
			const group = groups[Math.floor(Math.random() * groups.length)]
			if (group.names.length > 0) {
				const name = group.names[Math.floor(Math.random() * group.names.length)]
				void PREVIEW.playMotionByName(name)
			}
		}
	}

	if (region.expression.mode === "selected" && region.expression.name) {
		void PREVIEW.playExpression(region.expression.name)
	} else if (region.expression.mode === "random" && availableExpressions.value.length > 0) {
		const exp = availableExpressions.value[Math.floor(Math.random() * availableExpressions.value.length)]
		void PREVIEW.playExpression(exp)
	}
}

// 打开整页调整面板: 挂载该模型的实时预览
const openAdjust = async (model: ModelInfo) => {
	adjustFor.value = model.id
	adjustTab.value = "display"
	selectedRegionId.value = null
	isEditingInteractions.value = true
	isCreatingRegion.value = false
	previewReady.value = false
	await nextTick()

	// 读取该模型的显示配置、元数据与互动配置 (空缺时使用空配置)
	try {
		const META = await RUNTIME.modelMeta(model.id)
		pvScale.value = META.scale
		const SNAPSHOT = RUNTIME.snapshot.value
		previewExpressionList.value = SNAPSHOT?.models.selected === model.id ? [...SNAPSHOT.models.expressions] : []
		interactionsConfig.value = META.interactions?.regions
			? {version: 1, regions: [...META.interactions.regions]}
			: createDefaultInteractionConfig()
		availableMotions.value = META.motions ?? []
		availableExpressions.value = META.expressions ?? []
	} catch (error) {
		feedback.error(I18N.value.previewFailed, error)
		pvScale.value = 1
		previewExpressionList.value = []
		interactionsConfig.value = createDefaultInteractionConfig()
		availableMotions.value = []
		availableExpressions.value = []
	}

	// 以预览区域尺寸挂载预览, 避免画布变形
	const RECT = showcaseRef.value?.getBoundingClientRect()
	if (!RECT) return
	try {
		unbindPreviewClick()
		await PREVIEW.destroy()
		await PREVIEW.mount(
			{directory: model.id, fileBase: resolveModelFileBase(model.id)},
			{container: showcaseRef.value, canvasWidth: Math.max(0, Math.round(RECT.width)), canvasHeight: Math.max(0, Math.round(RECT.height))}
		)
		PREVIEW.setUserScale(pvScale.value)
	} catch (error) {
		feedback.error(I18N.value.previewFailed, error)
	}
	previewReady.value = true
	await syncPreviewClickInteraction()
	bindPreviewClick()
	refreshPreviewLayout()
	updateModelViewport()
	await applyPreviewExpressions(previewExpressionList.value)
}

// 关闭调整面板: 卸载预览并刷新保存
const closeAdjust = () => {
	SAVE.flush()
	adjustFor.value = null
	previewReady.value = false
	unbindPreviewClick()
	void PREVIEW.destroy()
}

// Esc 关闭调整面板; 互动区域蒙版自己的 Esc (取消选中) 会 preventDefault, 这里不抢
const onWindowKeydown = (event: KeyboardEvent) => {
	if (event.key !== "Escape" || event.defaultPrevented || !adjustFor.value) return
	closeAdjust()
}

onMounted(async () => {
	await RUNTIME.init()
	await refreshStatus()
	window.addEventListener("resize", onWindowResize)
	window.addEventListener("keydown", onWindowKeydown)
})

onBeforeUnmount(() => {
	SAVE.flush()
	if (statusTimer) clearTimeout(statusTimer)
	window.removeEventListener("resize", onWindowResize)
	window.removeEventListener("keydown", onWindowKeydown)
	unbindPreviewClick()
	void PREVIEW.destroy()
})
</script>

<template>
	<div class="w-full h-full flex flex-col gap-4 px-6 py-4 scroll-area">
		<AppSectionHeader :title="I18N.title" :subtitle="I18N.sub"/>

		<div class="flex flex-col gap-3.5 pb-5">
			<!-- 1. 模型库: 顶部两块摘要 (当前使用 / 已安装数), 下面每个模型一条操作行 -->
			<AppCard :title="I18N.installed" icon="sparkles">
				<div class="grid grid-cols-1 gap-3 sm:grid-cols-2">
					<AppStatTile :label="I18N.current" :value="CURRENT_MODEL_NAME" icon="sparkles" tone="teal"/>
					<AppStatTile :label="I18N.installed" :value="INSTALLED_SUMMARY" icon="package"/>
				</div>

				<div class="grid grid-cols-1 gap-3 xl:grid-cols-2">
					<article
						v-for="model in MODEL_LIST"
						:key="model.id"
						class="surface-inset flex gap-3 p-2.5 transition-all duration-200"
						:class="selectedModel === model.id
							? 'border-nori-teal-bright/45 bg-nori-teal-bright/8'
							: 'hover:border-line-strong'"
					>
						<!-- 缩略图只做展示: 旧版要先点图才弹蒙版菜单, 现在操作直接摊在右侧 -->
						<div class="relative w-[10.4rem] h-[15rem] shrink-0 overflow-hidden rounded-sm border border-line-subtle bg-bg-abyss/60">
							<img :src="model.thumb" :alt="model.name" class="block w-full h-full object-cover"/>
							<span class="absolute inset-0 pointer-events-none bg-gradient-to-b from-transparent via-transparent to-bg-abyss/85"/>
						</div>

						<div class="min-w-0 flex flex-1 flex-col gap-1.5">
							<span class="title-sm truncate">{{ model.name }}</span>
							<span class="mono text-hint truncate">{{ model.id }}</span>

							<div class="flex flex-wrap items-center gap-1.5">
								<AppChip :tone="installedMap[model.id] ? 'success' : 'neutral'" dot>
									{{ installedMap[model.id] ? I18N.installed : I18N.notInstalled }}
								</AppChip>
								<AppChip v-if="selectedModel === model.id" tone="teal" icon="check">
									{{ I18N.current }}
								</AppChip>
							</div>

							<div class="mt-auto flex flex-wrap items-center gap-2">
								<template v-if="installedMap[model.id]">
									<AppButton
										:variant="selectedModel === model.id ? 'ghost' : 'primary'"
										size="sm"
										icon="check"
										:disabled="selectedModel === model.id"
										@click="enableModel(model)"
									>
										{{ selectedModel === model.id ? I18N.enabled : I18N.enable }}
									</AppButton>
									<AppButton size="sm" icon="settings" @click="openAdjust(model)">
										{{ I18N.adjust }}
									</AppButton>
								</template>
								<!-- 未安装的模型给一个直达导入的入口 (旧版这里点不出任何东西) -->
								<AppButton
									v-else
									size="sm"
									icon="package"
									:loading="importing"
									:disabled="importing"
									@click="importLocalModel('zip')"
								>
									{{ I18N.import.zipButton }}
								</AppButton>
							</div>
						</div>
					</article>
				</div>
			</AppCard>

			<!-- 2. 本地导入: ZIP 与文件夹两条来源, 状态行就地反馈 -->
			<AppCard :title="I18N.import.button" icon="package">
				<div class="flex flex-wrap items-center gap-2.5">
					<AppButton
						variant="primary"
						icon="package"
						:loading="importing"
						:disabled="importing"
						@click="importLocalModel('zip')"
					>
						{{ I18N.import.zipButton }}
					</AppButton>
					<AppButton icon="package" :disabled="importing" @click="importLocalModel('folder')">
						{{ I18N.import.folderButton }}
					</AppButton>
				</div>

				<p
					v-if="importStatusText"
					class="surface-inset m-0 flex items-center gap-2 px-3 py-2 text-sub"
					aria-live="polite"
				>
					<Icon
						:name="importing ? 'loading' : 'check'"
						:size="13"
						:class="importing ? 'spin text-nori-teal-bright' : 'text-success'"
					/>
					<span class="min-w-0 truncate">{{ importStatusText }}</span>
				</p>
			</AppCard>

			<!-- 3. 伴侣交互行为: 组件自带小标题, 这里只补卡片外壳 -->
			<AppCard>
				<Live2dBehaviorControls :model-id="selectedModel"/>
			</AppCard>
		</div>

		<!-- 整页调整面板: 左侧固定尺寸预览舞台, 右侧按标签分组的控制卡 -->
		<Transition name="fade">
			<div
				v-if="adjustFor"
				class="fixed inset-0 z-100 flex flex-col bg-bg-glass-modal backdrop-blur-[1.6rem]"
				role="dialog"
				aria-modal="true"
				:aria-label="I18N.adjustTitle"
			>
				<header class="shrink-0 flex flex-wrap items-center gap-2.5 px-5 py-3 border-b border-line-subtle">
					<AppButton variant="icon" icon="arrow-left" :label="I18N.back" @click="closeAdjust"/>
					<span class="title-sm">{{ I18N.adjustTitle }}</span>
					<AppChip tone="teal" icon="sparkles">{{ modelNameOf(adjustFor) }}</AppChip>
					<AppSegmented
						class="ml-auto"
						size="sm"
						:model-value="adjustTab"
						:items="ADJUST_TABS"
						:label="I18N.adjustTitle"
						@update:model-value="adjustTab = $event"
					/>
					<AppButton variant="primary" size="sm" icon="check" @click="closeAdjust">
						{{ I18N.done }}
					</AppButton>
				</header>

				<div class="flex-1 min-h-0 flex gap-4 px-5 py-4">
					<!-- 预览舞台: 容器尺寸会被 getBoundingClientRect 读去建画布 -->
					<div class="shrink-0 flex flex-col items-center justify-center">
						<div class="glow-card p-2">
							<div ref="showcaseRef" class="relative w-[28rem] sm:w-[32rem] max-h-[calc(100vh-11rem)] aspect-[3/4] max-w-full overflow-hidden rounded-sm">
								<!-- 互动区域编辑蒙版 (覆盖在 Pixi 画布上方) -->
								<InteractionRegionOverlay
									v-if="previewReady && adjustTab === 'interactions'"
									:regions="interactionsConfig.regions"
									:selected-id="selectedRegionId"
									:model-viewport="modelViewport"
									:editing="adjustTab === 'interactions' && isEditingInteractions"
									:creating="isCreatingRegion"
									@update:selected-id="selectedRegionId = $event"
									@update:regions="onUpdateRegions"
									@update:creating="isCreatingRegion = $event"
									@create-region="onCreateRegion"
									@delete-region="onDeleteRegion"
									@region-click="onRegionClick"
								/>
							</div>
						</div>
					</div>

					<div class="flex-1 min-w-0 flex flex-col gap-3.5 scroll-area">
						<!-- 标签页 1: 基础显示 + 交互行为 (两组各自成卡) -->
						<AppCard v-show="adjustTab === 'display'">
							<AdjustControls
								:model-id="adjustFor"
								:model-name="modelNameOf(adjustFor)"
								:expression-enabled="true"
								:initial-scale="pvScale"
								:initial-expressions="previewExpressionList"
								@scale="onPreviewScale"
								@expressions="onPreviewExpressions"
							/>
						</AppCard>
						<AppCard v-show="adjustTab === 'display'">
							<Live2dBehaviorControls :model-id="adjustFor"/>
						</AppCard>

						<!-- 标签页 2: 自定义互动区域 -->
						<AppCard v-show="adjustTab === 'interactions'">
							<InteractionControls
								:model-id="adjustFor"
								:regions="interactionsConfig.regions"
								:selected-id="selectedRegionId"
								:available-motions="availableMotions"
								:available-expressions="availableExpressions"
								:editing="isEditingInteractions"
								:creating="isCreatingRegion"
								@update:selected-id="selectedRegionId = $event"
								@update:regions="onUpdateRegions"
								@update:editing="isEditingInteractions = $event"
								@update:creating="isCreatingRegion = $event"
								@add-region="onAddRegion"
								@delete-region="onDeleteRegion"
								@clear-regions="onClearRegions"
							/>
						</AppCard>
					</div>
				</div>
			</div>
		</Transition>
	</div>
</template>
