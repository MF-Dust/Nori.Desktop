<script setup lang="ts">
import {computed, onMounted, ref} from "vue"
import useLanguages from "../../services/i18n/useLanguages.ts"
import {RUNTIME} from "../../services/runtime"
import {feedback} from "../../services/feedback"
import Icon from "../../components/Icon.vue"
import AppButton from "../ui/AppButton.vue"
import {MODEL_LIST} from "../../services/live2d/models"

const I18N = computed(() => useLanguages().components.firstRun.modelSelect)
const WIZARD_I18N = computed(() => useLanguages().views.firstRun)

const emit = defineEmits<{
	error: [message: string]
	selected: [modelId: string]
}>()

const models = MODEL_LIST
const selected = ref("")
const installedMap = ref<Record<string, boolean>>({})
const importing = ref<"zip" | "folder" | "">("")
const importStatus = ref("")

const syncModels = async (preferredId?: string): Promise<void> => {
	await RUNTIME.refresh()
	const ITEMS = RUNTIME.snapshot.value?.models.items ?? []
	installedMap.value = Object.fromEntries(ITEMS.map(item => [item.id, item.installed]))
	const SAVED = preferredId ?? RUNTIME.snapshot.value?.models.selected ?? ""
	const NEXT = models.find(model => model.id === SAVED && installedMap.value[model.id])?.id
		?? models.find(model => installedMap.value[model.id])?.id
		?? ""
	selected.value = NEXT
	emit("selected", NEXT)
	emit("error", NEXT ? "" : I18N.value.importRequired)
}

onMounted(async () => {
	try {
		await RUNTIME.init()
		await syncModels()
	} catch (error) {
		feedback.error(WIZARD_I18N.value.error.selectModel, error)
		emit("error", WIZARD_I18N.value.error.selectModel)
	}
})

const selectModel = (modelId: string): void => {
	// eslint-disable-next-line -- Codacy误报：模型状态仅作只读存在性查询
	if (importing.value || !installedMap.value[modelId]) return
	selected.value = modelId
	emit("selected", modelId)
	emit("error", "")
}

const importModel = async (sourceKind: "zip" | "folder"): Promise<void> => {
	if (importing.value) return
	importing.value = sourceKind
	importStatus.value = I18N.value.importing
	try {
		const IMPORTED = await RUNTIME.importLocalModel(sourceKind)
		if (!IMPORTED?.length) {
			importStatus.value = ""
			return
		}
		const PREFERRED = IMPORTED.find(id => models.some(model => model.id === id))
		await syncModels(PREFERRED)
		importStatus.value = `${I18N.value.importSuccess}: ${IMPORTED.join(", ")}`
	} catch (error) {
		feedback.error(I18N.value.importFailed, error)
		importStatus.value = ""
	} finally {
		importing.value = ""
	}
}
</script>

<template>
	<section key="model-select" data-first-run-step="model" class="w-full flex flex-col items-center gap-2.5 px-7 py-2 my-auto text-center">
		<div class="flex flex-col items-center gap-1">
			<span class="chip-teal">
				<Icon name="package" :size="12"/>
				<span>{{ I18N.badge }}</span>
			</span>
			<h2 class="text-2xl font-700 glow-teal">{{ I18N.title }}</h2>
			<p class="text-xs text-sub">{{ I18N.hint }}</p>
		</div>

		<div class="flex flex-row justify-center gap-4">
			<button
				v-for="model in models"
				:key="model.id"
				type="button"
				class="group relative w-[14.5rem] flex flex-col items-center gap-1.5 p-2 pb-2 rounded-md overflow-hidden
					border-2 border-line-subtle bg-overlay-4 transition-all duration-200 focus-ring
					hover:not-disabled:(bg-nori-teal-bright/8 border-nori-teal-soft shadow-elev-1)
					disabled:(opacity-45 cursor-not-allowed)"
				:class="selected === model.id ? 'border-nori-teal bg-nori-teal-bright/12' : ''"
				:disabled="!installedMap[model.id] || Boolean(importing)"
				:aria-pressed="selected === model.id"
				@click="selectModel(model.id)"
			>
				<span class="relative w-full aspect-[3/4] max-h-[12.5rem] rounded-sm overflow-hidden border border-line-subtle bg-black/30">
					<img
						class="w-full h-full object-cover object-top transition-transform duration-200 group-hover:scale-103"
						:src="model.thumb"
						:alt="model.name"
					/>
					<span class="absolute inset-0 bg-gradient-to-b from-transparent via-transparent to-bg-abyss/80 pointer-events-none"/>
					<span
						v-if="!installedMap[model.id]"
						class="absolute inset-x-2 bottom-2 rounded-pill bg-bg-abyss/90 px-2 py-0.8 text-xs text-text-muted"
					>{{ I18N.notInstalled }}</span>
					<span
						v-else
						class="absolute top-2 right-2 w-[2rem] h-[2rem] rounded-full flex items-center justify-center
							bg-nori-teal text-on-teal shadow-elev-1 transition-all duration-200"
						:class="selected === model.id ? 'opacity-100 scale-100' : 'opacity-0 scale-60'"
					>
						<Icon name="check" :size="11"/>
					</span>
				</span>

				<span
					class="text-base font-500"
					:class="selected === model.id ? 'text-nori-teal-bright font-600' : 'text-text-primary'"
				>{{ model.name }}</span>
			</button>
		</div>

		<div class="flex items-center justify-center gap-2 mt-0.5">
			<AppButton size="sm" icon="package" :loading="importing === 'zip'" :disabled="Boolean(importing)" @click="importModel('zip')">
				{{ I18N.importZip }}
			</AppButton>
			<AppButton size="sm" icon="package" :loading="importing === 'folder'" :disabled="Boolean(importing)" @click="importModel('folder')">
				{{ I18N.importFolder }}
			</AppButton>
		</div>
		<p v-if="importStatus" class="m-0 text-xs text-nori-teal-bright" aria-live="polite">{{ importStatus }}</p>
	</section>
</template>
