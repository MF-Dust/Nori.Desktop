<script setup lang="ts">
/**
 * 应用内弹窗
 *
 * 统一处理对话框语义、焦点陷阱、Escape 关闭与关闭后的焦点恢复。
 * 内容与底部操作通过插槽注入, 避免各设置页重复实现一套不完整的遮罩层。
 */
import {nextTick, onBeforeUnmount, onMounted, ref, watch} from "vue"
import Icon from "../Icon.vue"

let nextModalId = 0

const props = withDefaults(defineProps<{
	show: boolean
	title?: string
	ariaLabel?: string
	closeLabel: string
	maskClosable?: boolean
	panelClass?: string
}>(), {
	ariaLabel: "",
	maskClosable: true,
	panelClass: "w-[min(48rem,92vw)] max-h-[86vh]",
})

const emit = defineEmits<{
	"update:show": [value: boolean]
	close: []
}>()

const MODAL_ROOT = ref<HTMLElement | null>(null)
const TITLE_ID = `app-modal-title-${++nextModalId}`
let previousActive: HTMLElement | null = null

const focusableElements = (): HTMLElement[] => {
	const ROOT = MODAL_ROOT.value
	if (!ROOT) return []
	return Array.from(ROOT.querySelectorAll<HTMLElement>(
		"button:not([disabled]), [href], input:not([disabled]), select:not([disabled]), textarea:not([disabled]), [tabindex]:not([tabindex=\"-1\"])"
	)).filter(element => !element.hasAttribute("aria-hidden"))
}

const focusFirst = async (): Promise<void> => {
	await nextTick()
	const FIRST = focusableElements().at(0)
	;(FIRST ?? MODAL_ROOT.value)?.focus()
}

const restoreFocus = async (): Promise<void> => {
	await nextTick()
	if (previousActive?.isConnected) previousActive.focus()
	previousActive = null
}

const close = (): void => {
	if (!props.show) return
	emit("update:show", false)
	emit("close")
}

const onKeydown = (event: KeyboardEvent): void => {
	if (!props.show) return
	if (event.key === "Escape") {
		event.preventDefault()
		close()
		return
	}
	if (event.key !== "Tab") return

	const ELEMENTS = focusableElements()
	if (ELEMENTS.length === 0) {
		event.preventDefault()
		MODAL_ROOT.value?.focus()
		return
	}

	const FIRST = ELEMENTS[0]
	const LAST = ELEMENTS[ELEMENTS.length - 1]
	if (event.shiftKey && document.activeElement === FIRST) {
		event.preventDefault()
		LAST.focus()
	} else if (!event.shiftKey && document.activeElement === LAST) {
		event.preventDefault()
		FIRST.focus()
	}
}

const onMaskClick = (event: MouseEvent): void => {
	if (props.maskClosable && event.target === event.currentTarget) close()
}

const onOpen = async (): Promise<void> => {
	previousActive = document.activeElement instanceof HTMLElement ? document.activeElement : null
	document.addEventListener("keydown", onKeydown)
	await focusFirst()
}

const onClose = (): void => {
	document.removeEventListener("keydown", onKeydown)
	void restoreFocus()
}

watch(() => props.show, show => {
	if (show) void onOpen()
	else onClose()
})

onMounted(() => {
	if (props.show) void onOpen()
})

onBeforeUnmount(() => {
	document.removeEventListener("keydown", onKeydown)
	if (props.show) void restoreFocus()
})
</script>

<template>
	<Teleport to="body">
		<div
			v-if="show"
			class="fixed inset-0 z-100 flex items-center justify-center bg-bg-abyss/72 p-4"
			role="presentation"
			@click="onMaskClick"
		>
			<section
				ref="MODAL_ROOT"
				:class="['flex flex-col overflow-hidden rounded-lg border border-line-strong bg-bg-glass-modal shadow-elev-3', panelClass]"
				role="dialog"
				aria-modal="true"
				:aria-labelledby="title ? TITLE_ID : undefined"
				:aria-label="title ? undefined : ariaLabel"
				tabindex="-1"
			>
				<header v-if="title || $slots.header" class="flex items-center justify-between gap-2 border-b border-line-subtle px-4 py-3">
					<slot name="header">
						<h2 :id="TITLE_ID" class="m-0 text-md text-text-primary">{{ title }}</h2>
					</slot>
					<button type="button" class="btn-close" :aria-label="closeLabel" @click="close">
						<Icon name="close" :size="16"/>
					</button>
				</header>

				<div class="min-h-0 flex flex-col gap-3 overflow-auto px-4 py-3.5">
					<slot/>
				</div>

				<footer v-if="$slots.footer" class="flex items-center justify-end gap-2 border-t border-line-subtle px-4 py-3.5">
					<slot name="footer"/>
				</footer>
			</section>
		</div>
	</Teleport>
</template>
