import {describe, expect, it} from "vitest"
import {computeLevel} from "../../src/services/audio"

describe("音频电平计算", () => {
	it("静音为 0", () => {
		expect(computeLevel(new Float32Array(64))).toBe(0)
	})

	it("空样本不会 NaN", () => {
		expect(computeLevel([])).toBe(0)
	})

	it("满幅方波被夹到 1", () => {
		const SAMPLES = new Float32Array(64).fill(1)
		expect(computeLevel(SAMPLES)).toBe(1)
	})

	it("正负号不影响结果 (RMS)", () => {
		const POSITIVE = new Float32Array(32).fill(0.2)
		const NEGATIVE = new Float32Array(32).fill(-0.2)
		expect(computeLevel(POSITIVE)).toBeCloseTo(computeLevel(NEGATIVE), 10)
	})

	it("电平随幅度单调上升", () => {
		const LOW = computeLevel(new Float32Array(32).fill(0.05))
		const MID = computeLevel(new Float32Array(32).fill(0.15))
		const HIGH = computeLevel(new Float32Array(32).fill(0.3))
		expect(LOW).toBeLessThan(MID)
		expect(MID).toBeLessThan(HIGH)
		expect(HIGH).toBeLessThanOrEqual(1)
	})

	it("小幅 RMS 被放大 3 倍进入可用动态范围", () => {
		// 0.1 的 RMS → 0.3, 伴侣嘴型才有明显开合
		expect(computeLevel(new Float32Array(32).fill(0.1))).toBeCloseTo(0.3, 6)
	})
})
