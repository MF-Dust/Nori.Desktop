import {readdirSync, readFileSync, statSync} from "node:fs"
import {join, relative, resolve} from "node:path"
import {describe, expect, it} from "vitest"

const ROOT = resolve(__dirname, "../..")
const SRC = join(ROOT, "src")

/**
 * 尚未完成原子化迁移的文件 (P2 已全量清空)
 *
 * 迁移完成的文件不允许再出现 scoped 样式块、px 长度、裸 hex 色值与超小字号。
 * 名单保留作为逗号后置的逗号阀: 日后新增组件如果要临时开特例, 必须显式列在这里。
 */
const LEGACY_FILES: string[] = []

/** 裸 hex 的唯一豁免: 设计令牌单一色源 */
const COLOR_SOURCE = "assets/style/tokens.ts"

const listFiles = (dir: string, extension: string): string[] => {
	const OUT: string[] = []
	for (const entry of readdirSync(dir)) {
		const FULL = join(dir, entry)
		if (statSync(FULL).isDirectory()) OUT.push(...listFiles(FULL, extension))
		else if (entry.endsWith(extension)) OUT.push(FULL)
	}
	return OUT
}

const relativeSrc = (file: string): string => relative(SRC, file).replace(/\\/g, "/")

const migratedVueFiles = (): string[] =>
	listFiles(SRC, ".vue").filter(file => !LEGACY_FILES.includes(relativeSrc(file)))

/** 参与裸 hex 检查的 .ts 文件: src 下全覆盖, 只放过单一色源 */
const scannedTsFiles = (): string[] =>
	listFiles(SRC, ".ts").filter(file => relativeSrc(file) !== COLOR_SOURCE)

describe("样式规范静态检查", () => {
	it("迁移完成的组件不再带 scoped 样式块", () => {
		const OFFENDERS = migratedVueFiles().filter(file => readFileSync(file, "utf8").includes("<style scoped"))
		expect(OFFENDERS.map(relativeSrc)).toEqual([])
	})

	it("迁移完成的组件不使用 px 长度", () => {
		const OFFENDERS: string[] = []
		for (const file of migratedVueFiles()) {
			const MATCH = readFileSync(file, "utf8").match(/\b\d+(\.\d+)?px\b/g)
			if (MATCH) OFFENDERS.push(`${relativeSrc(file)}: ${MATCH.join(", ")}`)
		}
		expect(OFFENDERS).toEqual([])
	})

	it("组件与 src 下的 ts 都不写裸 hex 色值 (统一走令牌)", () => {
		const OFFENDERS: string[] = []
		for (const file of [...migratedVueFiles(), ...scannedTsFiles()]) {
			// 后面不能再跟十六进制字符 (Uno 任意值里的 #xxx_0% 也要能抓到)
			const MATCH = readFileSync(file, "utf8").match(/#[0-9a-fA-F]{3,8}(?![0-9a-fA-F])/g)
			if (MATCH) OFFENDERS.push(`${relativeSrc(file)}: ${MATCH.join(", ")} —— 请改用 tokens.ts 的令牌`)
		}
		expect(OFFENDERS).toEqual([])
	})

	it("裸 hex 的唯一豁免就是单一色源 tokens.ts", () => {
		const ALL = new Set(listFiles(SRC, ".ts").map(relativeSrc))
		expect(ALL.has(COLOR_SOURCE), `${COLOR_SOURCE} 不存在, 裸 hex 的豁免路径已失效`).toBe(true)
		expect(readFileSync(join(SRC, COLOR_SOURCE), "utf8")).toMatch(/#[0-9a-fA-F]{6}/)
	})

	it("白色叠加统一走 overlay 令牌 (不写 bg-white/N)", () => {
		// tokens.ts 的 overlay-2/4/6/8/12/20 就是这套白色蒙版刻度。写 bg-white/3 等于
		// 在刻度外另开一档, 同一种"浮起一层"的底纹于是按文件各差一个百分点。
		// 不带透明度的 bg-white 不在管辖范围: 区域编辑器的手柄要压在任意像素上, 那是实心白。
		const OFFENDERS: string[] = []
		for (const file of [...migratedVueFiles(), ...listFiles(SRC, ".ts")]) {
			const MATCH = readFileSync(file, "utf8").match(/\b(?:bg|border|text|from|to|via|shadow|outline)-white\/\d+/g)
			if (MATCH) OFFENDERS.push(`${relativeSrc(file)}: ${MATCH.join(", ")} —— 请改用 overlay-2/4/6/8/12/20`)
		}
		expect(OFFENDERS).toEqual([])
	})

	it("迁移完成的组件没有小于 1.15rem 的字号", () => {
		const OFFENDERS: string[] = []
		for (const file of [...migratedVueFiles(), ...listFiles(SRC, ".ts")]) {
			const SOURCE = readFileSync(file, "utf8")
			for (const match of SOURCE.matchAll(/font-size:\s*([\d.]+)rem|text-\[([\d.]+)rem\]/g)) {
				const SIZE = Number(match[1] ?? match[2])
				if (SIZE < 1.15) OFFENDERS.push(`${relativeSrc(file)}: ${SIZE}rem`)
			}
		}
		expect(OFFENDERS).toEqual([])
	})

	it("设置二级页共用同一套外壳 (内边距与滚动容器)", () => {
		// 侧栏切页时外壳必须逐像素对齐, 不然每次切换都能看到标题与内容轻微跳动。
		// 判定口径: 带 AppSectionHeader 的 Vue 页面仍保留统一外壳；技能等复杂设置页已迁移到原生 Avalonia。
		const SHELL = "w-full h-full flex flex-col gap-4 px-6 py-4 scroll-area"
		const PAGES = listFiles(join(SRC, "components/settings"), ".vue")
			.filter(file => readFileSync(file, "utf8").includes("<AppSectionHeader"))
		expect(PAGES.length, "保留的 Vue 二级页都已迁移, 判定口径已失效").toBeGreaterThanOrEqual(2)
		const OFFENDERS = PAGES
			.filter(file => !readFileSync(file, "utf8").includes(SHELL))
			.map(file => `${relativeSrc(file)}: 外壳需为 class="${SHELL}"`)
		expect(OFFENDERS).toEqual([])
	})

	it("窗口级页面保留纵向滚动兜底", () => {
		// html/body/#app 与 window-chrome 都会裁掉外层溢出, 所以真正的页面宿主必须显式接管滚动。
		// 滚动容器与居中容器分层, 避免超高内容被 justify-center 推到不可滚动的负方向。
		const APP = readFileSync(join(SRC, "App.vue"), "utf8")
		const MAIN = readFileSync(join(SRC, "views/Main.vue"), "utf8")
		const INIT = readFileSync(join(SRC, "views/InitView.vue"), "utf8")
		const FIRST_RUN = readFileSync(join(SRC, "views/FirstRunView.vue"), "utf8")

		expect(APP).toContain("w-100vw h-100vh scroll-area bg-bg-abyss p-6")
		expect(APP).toContain("min-h-full flex items-center justify-center")
		expect(MAIN).toContain("flex-1 min-h-0 flex flex-col scroll-area")
		expect(INIT).toContain("flex-1 min-h-0 scroll-area")
		expect(INIT).toContain("min-h-full flex flex-col items-center justify-center gap-7 pb-6 relative")
		expect(FIRST_RUN).toContain("relative flex-1 w-full min-h-0 scroll-area flex flex-col")
	})

	it("legacy 名单只包含真实存在且仍未迁移的文件", () => {
		const ALL = new Set(listFiles(SRC, ".vue").map(relativeSrc))
		for (const file of LEGACY_FILES) {
			expect(ALL.has(file), `${file} 已不存在, 请从 legacy 名单移除`).toBe(true)
			expect(readFileSync(join(SRC, file), "utf8")).toContain("<style scoped")
		}
	})
})
