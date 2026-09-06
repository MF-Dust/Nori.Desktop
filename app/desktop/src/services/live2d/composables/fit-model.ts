/**
 * 模型适配缩放
 *
 * 标准化模型缩放: scale=1 时模型完整居中适配视口,
 * x=canvas.width/2, y=canvas.height/2 (anchor 0.5,0.5).
 *
 * 参考 AIRI: packages/stage-ui-live2d/src/composables/live2d/fit-model.ts
 */
export interface CanvasDim {
	width: number
	height: number
}

export interface ModelDim {
	width: number
	height: number
}

export interface NormalizedParams {
	scale: number
	x: number
	y: number
}

/**
 * 计算标准化参数
 * @param canvas 画布容器尺寸 (CSS px)
 * @param model 模型原始画布尺寸 (Live2D canvas 尺寸)
 */
export const calcFitModel = (
	canvas: CanvasDim,
	model: ModelDim,
): NormalizedParams => {
	const heightScale = canvas.height / model.height
	const widthScale = canvas.width / model.width
	const minScale = Math.max(1e-6, Math.min(heightScale, widthScale))

	return {
		scale: minScale,
		x: canvas.width / 2,
		y: canvas.height / 2,
	}
}

export const DEFAULT_PET_WIDTH = 400
export const DEFAULT_PET_HEIGHT = 520
export const MAX_PET_BASE_WIDTH = 600
export const MAX_PET_BASE_HEIGHT = 700
export const MIN_PET_BASE_WIDTH = 240
export const MIN_PET_BASE_HEIGHT = 320

/**
 * 计算安全的伴侣基准视口尺寸 (DIP)
 *
 * 避免模型原始画布过大 (如 2048x2048 或 4096) 导致窗口尺寸爆炸、
 * 产生巨大矩形透明区域或 WebGL 显存溢出。
 */
export const calculateSafeBaseSize = (
	rawWidth?: number,
	rawHeight?: number,
): {width: number; height: number} => {
	if (
		!rawWidth ||
		!rawHeight ||
		rawWidth <= 0 ||
		rawHeight <= 0 ||
		!Number.isFinite(rawWidth) ||
		!Number.isFinite(rawHeight)
	) {
		return {width: DEFAULT_PET_WIDTH, height: DEFAULT_PET_HEIGHT}
	}

	// 原始尺寸如果已经在合理的伴侣视窗范围内，直接使用
	if (
		rawWidth <= MAX_PET_BASE_WIDTH &&
		rawHeight <= MAX_PET_BASE_HEIGHT &&
		rawWidth >= MIN_PET_BASE_WIDTH &&
		rawHeight >= MIN_PET_BASE_HEIGHT
	) {
		return {width: Math.round(rawWidth), height: Math.round(rawHeight)}
	}

	const ASPECT = Math.max(0.3, Math.min(3.0, rawWidth / rawHeight))

	// 优先以 DEFAULT_PET_HEIGHT 为高度基准适配
	let fitW = Math.round(DEFAULT_PET_HEIGHT * ASPECT)
	let fitH = DEFAULT_PET_HEIGHT

	if (fitW > MAX_PET_BASE_WIDTH) {
		fitW = MAX_PET_BASE_WIDTH
		fitH = Math.round(MAX_PET_BASE_WIDTH / ASPECT)
	} else if (fitW < MIN_PET_BASE_WIDTH) {
		fitW = MIN_PET_BASE_WIDTH
		fitH = Math.round(MIN_PET_BASE_WIDTH / ASPECT)
	}

	fitH = Math.max(MIN_PET_BASE_HEIGHT, Math.min(MAX_PET_BASE_HEIGHT, fitH))

	return {width: fitW, height: fitH}
}