/**
 * 窗口操作
 *
 * 对应原 @tauri-apps/api/webviewWindow 与 /dpi.
 *
 * 伴侣头部跟踪每帧都要读窗口位置与缩放, 走 JSON 桥比 Tauri IPC 贵得多,
 * 因此这里缓存这两项, 由宿主在窗口移动/缩放变化时推 nori:window-metrics 更新,
 * 调用方 (PetView) 不需要任何改动就能少掉两次往返.
 */
import {host} from "./index"
import {invoke} from "./invoke"
import {listen} from "./event"

/**
 * 物理像素尺寸
 */
export class PhysicalSize {
	constructor(public width: number, public height: number) {}
}

/**
 * 物理像素坐标
 */
export class PhysicalPosition {
	constructor(public x: number, public y: number) {}
}

/**
 * 伴侣窗口命中图
 */
export interface WindowInputMask {
	width: number
	height: number
	data: string
	enabled: boolean
}

/**
 * 窗口度量缓存 (label → 值)
 */
interface Metrics {
	scaleFactor?: number
	position?: PhysicalPosition
	size?: PhysicalSize
}

const CACHE = new Map<string, Metrics>()

const metricsOf = (label: string): Metrics => {
	let entry = CACHE.get(label)
	if (!entry) {
		entry = {}
		CACHE.set(label, entry)
	}
	return entry
}

// 宿主推送的窗口度量变更
void listen<{label: string; x: number; y: number; width: number; height: number; scaleFactor: number}>(
	"nori:window-metrics",
	({payload}) => {
		const ENTRY = metricsOf(payload.label)
		ENTRY.position = new PhysicalPosition(payload.x, payload.y)
		ENTRY.size = new PhysicalSize(payload.width, payload.height)
		ENTRY.scaleFactor = payload.scaleFactor
	}
)

/**
 * 一个宿主窗口
 */
export class HostWindow {
	constructor(public readonly label: string) {}

	async show(): Promise<void> {
		await invoke("window_show", {label: this.label})
	}

	async hide(): Promise<void> {
		await invoke("window_hide", {label: this.label})
	}

	async close(): Promise<void> {
		await invoke("window_close", {label: this.label})
	}

	async setFocus(): Promise<void> {
		await invoke("window_focus", {label: this.label})
	}

	async isVisible(): Promise<boolean> {
		return await invoke("window_is_visible", {label: this.label})
	}

	async scaleFactor(): Promise<number> {
		const ENTRY = metricsOf(this.label)
		if (ENTRY.scaleFactor == null) ENTRY.scaleFactor = await invoke("window_scale_factor", {label: this.label})
		return ENTRY.scaleFactor
	}

	async outerPosition(): Promise<PhysicalPosition> {
		const ENTRY = metricsOf(this.label)
		if (!ENTRY.position) {
			const POS = await invoke("window_outer_position", {label: this.label})
			ENTRY.position = new PhysicalPosition(POS.x, POS.y)
		}
		return ENTRY.position
	}

	async outerSize(): Promise<PhysicalSize> {
		const ENTRY = metricsOf(this.label)
		if (!ENTRY.size) {
			const SIZE = await invoke("window_outer_size", {label: this.label})
			ENTRY.size = new PhysicalSize(SIZE.width, SIZE.height)
		}
		return ENTRY.size
	}

	async setSize(size: PhysicalSize): Promise<void> {
		await invoke("window_set_size", {label: this.label, width: size.width, height: size.height})
		metricsOf(this.label).size = size
	}

	async setPosition(position: PhysicalPosition): Promise<void> {
		await invoke("window_set_position", {label: this.label, x: position.x, y: position.y})
		metricsOf(this.label).position = position
	}

	/**
	 * 从当前鼠标按下状态发起窗口拖动 (取代 data-tauri-drag-region)
	 */
	async startDragging(): Promise<void> {
		await invoke("window_start_drag", {label: this.label})
	}

	async setInputMask(_mask: WindowInputMask): Promise<void> {
		// 原生 Avalonia 窗口自管模型交互矩形与透明穿透，前端不再推送掩码
	}
}

/**
 * 当前窗口, 未运行在宿主中时 label 为空串
 */
export const getCurrentWindow = (): HostWindow => new HostWindow(host()?.label ?? "")

/**
 * 按 label 取窗口
 */
export const getWindowByLabel = (label: string): HostWindow => new HostWindow(label)
