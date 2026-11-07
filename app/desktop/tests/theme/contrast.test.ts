import {describe, expect, it} from "vitest"
import {contrastRatio, parseColor, relativeLuminance} from "../../src/services/theme/contrast"
import {ACCENT_KEYS, ACCENTS, COLORS} from "../../src/assets/style/tokens"

/** 原生窗口、面板和玻璃背景 */
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

describe("原生主题对比度计算", () => {
	it("解析 hex 与 rgba 并计算亮度", () => {
		expect(parseColor("#fff")).toEqual({r: 255, g: 255, b: 255, a: 1})
		expect(parseColor("rgba(10, 28, 44, 0.55)")).toEqual({r: 10, g: 28, b: 44, a: 0.55})
		expect(relativeLuminance(parseColor("#000000"))).toBeCloseTo(0, 5)
		expect(relativeLuminance(parseColor("#ffffff"))).toBeCloseTo(1, 5)
		expect(contrastRatio("#ffffff", "#000000")).toBeCloseTo(21, 2)
	})
})

describe("原生设计令牌可读性门禁", () => {
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

	it("青绿强调色上的深色文字 ≥ 4.5:1", () => {
		expect(contrastRatio("#03101c", COLORS["nori-teal"])).toBeGreaterThanOrEqual(4.5)
		expect(contrastRatio("#03101c", COLORS["nori-teal-bright"])).toBeGreaterThanOrEqual(4.5)
	})
})

describe("强调色预设可读性门禁", () => {
	const PRESETS = Object.entries(ACCENTS)

	it("默认预设与现有令牌逐字一致", () => {
		for (const key of ACCENT_KEYS) expect(ACCENTS.teal[key], key).toBe(COLORS[key])
	})

	it.each(PRESETS)("%s 的强调文字在所有背景与选中底色上 ≥ 4.5:1", (name, preset) => {
		const SURFACES = {...BACKGROUNDS, selection: preset["bg-selection"]}
		for (const token of ["nori-teal", "nori-teal-bright", "nori-teal-soft"] as const) {
			for (const [surface, background] of Object.entries(SURFACES)) {
				const RATIO = contrastRatio(preset[token], background)
				expect(RATIO, `${name} ${token} on ${surface} = ${RATIO.toFixed(2)}:1`).toBeGreaterThanOrEqual(4.5)
			}
			for (const desktop of ["#ffffff", "#000000"]) {
				const RATIO = contrastRatio(preset[token], COLORS["bg-glass"], desktop)
				expect(RATIO, `${name} ${token} on glass over ${desktop} = ${RATIO.toFixed(2)}:1`).toBeGreaterThanOrEqual(4.5)
			}
		}
	})

	it.each(PRESETS)("%s 的强调填充上的深色文字 ≥ 4.5:1", (name, preset) => {
		for (const fill of ["nori-teal", "nori-teal-bright", "nori-teal-pressed"] as const) {
			const RATIO = contrastRatio(preset["on-teal"], preset[fill])
			expect(RATIO, `${name} on-teal on ${fill} = ${RATIO.toFixed(2)}:1`).toBeGreaterThanOrEqual(4.5)
		}
	})
})
