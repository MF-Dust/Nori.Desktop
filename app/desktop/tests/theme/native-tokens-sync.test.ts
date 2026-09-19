import {spawnSync} from "node:child_process"
import {resolve} from "node:path"
import {describe, expect, it} from "vitest"

describe("Web 与原生设计令牌同步", () => {
	it("原生颜色、画刷、尺寸和 CSS 变量均为当前令牌生成结果", () => {
		const RESULT = spawnSync(process.execPath, ["scripts/sync-design-tokens.mjs", "--check"], {
			cwd: resolve(__dirname, "../.."), encoding: "utf8",
		})
		expect(RESULT.error).toBeUndefined()
		expect(RESULT.status, RESULT.stderr).toBe(0)
	})
})
