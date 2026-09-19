import {describe, expect, it} from "vitest"
import {contrastRatio, parseColor, relativeLuminance} from "../../src/services/theme/contrast"
import {COLORS} from "../../src/assets/style/tokens"

/** 会出现文字的背景: 窗口渐变最深处与最浅处 + 玻璃卡面 */
const BACKGROUNDS = {
	abyss: COLORS["bg-abyss"],
	deep: COLORS["bg-deep"],
	panel: COLORS["bg-panel"],
	card: COLORS["bg-card"],
	glass: COLORS["bg-glass"],
} as const

/** 正文级前景 (要求 ≥4.5:1) */
const BODY_FOREGROUNDS = [
	"text-primary",
	"text-body",
	"text-muted",
	"text-faint",
	"nori-teal",
	"nori-teal-bright",
	"nori-teal-soft",
	"success",
	"warning",
	"danger-text",
] as const

describe("对比度计算", () => {
	it("解析 hex 与 rgba 并计算亮度", () => {
		expect(parseColor("#fff")).toEqual({r: 255, g: 255, b: 255, a: 1})
		expect(parseColor("rgba(10, 28, 44, 0.55)")).toEqual({r: 10, g: 28, b: 44, a: 0.55})
		expect(relativeLuminance(parseColor("#000000"))).toBeCloseTo(0, 5)
		expect(relativeLuminance(parseColor("#ffffff"))).toBeCloseTo(1, 5)
		expect(contrastRatio("#ffffff", "#000000")).toBeCloseTo(21, 2)
	})
})

describe("设计令牌可读性门禁", () => {
	it.each(["#ffffff", "#000000"])("系统桌面为 %s 时玻璃背景上的文字 ≥ 4.5:1", (desktop) => {
		for (const token of BODY_FOREGROUNDS) {
			const RATIO = contrastRatio(COLORS[token], COLORS["bg-glass"], desktop)
			expect(RATIO, `${token} on glass over ${desktop} = ${RATIO.toFixed(2)}:1`).toBeGreaterThanOrEqual(4.5)
		}
	})

	it.each(BODY_FOREGROUNDS)("%s 在所有背景上 ≥ 4.5:1", (token) => {
		for (const [name, background] of Object.entries(BACKGROUNDS)) {
			const RATIO = contrastRatio(COLORS[token], background)
			expect(RATIO, `${token} on ${name} = ${RATIO.toFixed(2)}:1`).toBeGreaterThanOrEqual(4.5)
		}
	})

	it("青绿按钮上的深色文字 ≥ 4.5:1", () => {
		expect(contrastRatio("#03101c", COLORS["nori-teal"])).toBeGreaterThanOrEqual(4.5)
		expect(contrastRatio("#03101c", COLORS["nori-teal-bright"])).toBeGreaterThanOrEqual(4.5)
	})

	it("naive 的占位与禁用色分别达到 4.5:1 / 3:1", () => {
		expect(contrastRatio("rgba(157, 178, 192, 0.75)", COLORS["bg-abyss"])).toBeGreaterThanOrEqual(4.5)
		expect(contrastRatio("#6a8496", COLORS["bg-panel"])).toBeGreaterThanOrEqual(3)
	})

	it("回归保护: 被替换掉的旧色值确实不达标", () => {
		// --text-muted 旧值 #7e94a3、--text-faint 旧值 #596f7e、naive 禁用旧值 #4a677d
		expect(contrastRatio("#7e94a3", COLORS["bg-panel"])).toBeLessThan(4.5)
		expect(contrastRatio("#596f7e", COLORS["bg-panel"])).toBeLessThan(4.5)
		expect(contrastRatio("#4a677d", COLORS["bg-panel"])).toBeLessThan(3)
		// 纯 danger 当文字在浅面板上不达标, 所以文字必须用 danger-text
		expect(contrastRatio(COLORS.danger, COLORS["bg-panel"])).toBeLessThan(4.5)
	})
})
