import {readFileSync} from "node:fs"
import {join} from "node:path"
import {describe, expect, it} from "vitest"

const ROOT = process.cwd()

describe("设置卡片滚动布局", () => {
	it("AppCard 不被 flex 容器压缩导致内部内容裁切", () => {
		const CARD = readFileSync(join(ROOT, "src/components/ui/AppCard.vue"), "utf8")
		const ROOT_CLASS = CARD.match(/<section class="([^"]+)"/)?.[1] ?? ""

		expect(ROOT_CLASS.split(/\s+/)).toContain("shrink-0")
	})

	it("主页分区保留内容高度，由外层滚动而不是压缩角色卡片", () => {
		const HOME = readFileSync(join(ROOT, "src/components/home/HomePanel.vue"), "utf8")
		const SECTIONS = [...HOME.matchAll(/<section\b[^>]*?class="([^"]+)"/g)]
		expect(SECTIONS.length).toBeGreaterThan(0)
		for (const section of SECTIONS) expect(section[1].split(/\s+/)).toContain("shrink-0")
	})
})
