/**
 * 对比度计算 (WCAG 2.1 相对亮度)
 *
 * 设计令牌的可读性由 tests/theme/contrast.test.ts 表驱动校验:
 * 正文 ≥ 4.5:1, 大字/加粗 ≥ 3:1。这里只做纯计算, 不依赖 DOM。
 */

import {COLORS} from "../../assets/style/tokens"

/** RGB 颜色 (0-255) */
export interface Rgb {
	r: number
	g: number
	b: number
	/** 透明度 0-1, 缺省 1 */
	a?: number
}

/**
 * 解析 #rrggbb / #rgb / rgb() / rgba() 文本
 *
 * @throws 无法识别的颜色文本
 */
export const parseColor = (input: string): Rgb => {
	const TEXT = input.trim()
	const HEX = /^#([0-9a-f]{3}|[0-9a-f]{6})$/i.exec(TEXT)
	if (HEX) {
		const BODY = HEX[1]
		const FULL = BODY.length === 3 ? BODY.split("").map(char => char + char).join("") : BODY
		return {
			r: Number.parseInt(FULL.slice(0, 2), 16),
			g: Number.parseInt(FULL.slice(2, 4), 16),
			b: Number.parseInt(FULL.slice(4, 6), 16),
			a: 1,
		}
	}
	// Codacy 误报：各捕获组为非嵌套字符类，表达式线性匹配。
	// eslint-disable-next-line -- Codacy误报：颜色格式正则无灾难性回溯
	const RGB = /^rgba?\(\s*([\d.]+)[\s,]+([\d.]+)[\s,]+([\d.]+)(?:[\s,/]+([\d.]+))?\s*\)$/i.exec(TEXT)
	if (RGB) {
		return {
			r: Number(RGB[1]),
			g: Number(RGB[2]),
			b: Number(RGB[3]),
			a: Number(RGB.at(4) ?? 1),
		}
	}
	throw new Error(`无法解析颜色: ${input}`)
}

/**
 * 把半透明前景/表面合成到背景上
 */
export const composite = (foreground: Rgb, background: Rgb): Rgb => {
	const ALPHA = foreground.a ?? 1
	if (ALPHA >= 1) return {...foreground, a: 1}
	return {
		r: foreground.r * ALPHA + background.r * (1 - ALPHA),
		g: foreground.g * ALPHA + background.g * (1 - ALPHA),
		b: foreground.b * ALPHA + background.b * (1 - ALPHA),
		a: 1,
	}
}

const channel = (value: number): number => {
	const NORMALIZED = value / 255
	return NORMALIZED <= 0.03928 ? NORMALIZED / 12.92 : ((NORMALIZED + 0.055) / 1.055) ** 2.4
}

/**
 * 相对亮度 (WCAG)
 */
export const relativeLuminance = (color: Rgb): number =>
	0.2126 * channel(color.r) + 0.7152 * channel(color.g) + 0.0722 * channel(color.b)

/**
 * 对比度 (1 - 21)
 *
 * 前景/背景中的透明色会先合成到 base 上 (缺省用最深的窗口底色)。
 */
export const contrastRatio = (foreground: string, background: string, base = COLORS["bg-abyss"]): number => {
	const BASE = parseColor(base)
	const BG = composite(parseColor(background), BASE)
	const FG = composite(parseColor(foreground), BG)
	const L1 = relativeLuminance(FG)
	const L2 = relativeLuminance(BG)
	const [BRIGHT, DARK] = L1 >= L2 ? [L1, L2] : [L2, L1]
	return (BRIGHT + 0.05) / (DARK + 0.05)
}
