import {readdirSync, readFileSync, statSync} from "node:fs"
import {join, relative, resolve, sep} from "node:path"
import {describe, expect, it} from "vitest"

/**
 * naive-ui 用量收敛门禁
 *
 * 这轮重构定下的约定是「保留但收敛」: 只留自建成本过高的下拉, 其余控件
 * 全部换成 src/components/ui 下的 App* 组件。naive 的外观是运行时 CSS-in-JS,
 * 注入顺序不受我们控制 —— 混用的直接后果是同一种控件在不同页面长得不一样,
 * 而且只能在 naiveOverrides.ts 里调。没有门禁, 下次顺手写个 n-switch 就把这条
 * 约定悄悄退回去了。
 */

const ROOT = resolve(__dirname, "../..")
const SRC = join(ROOT, "src")

/** 允许保留的 naive 组件 → 保留理由 */
const ALLOWED: Record<string, string> = {
	"n-select": "下拉要处理浮层定位、键盘导航与选项虚拟滚动, 自建不划算",
}

/** 已被自建实现取代的 naive 组件 → 该用什么 */
const REPLACED: Record<string, string> = {
	"n-switch": "AppSwitch.vue",
	"n-popconfirm": "AppConfirm.vue",
	"n-modal": "AppModal.vue",
	"n-dialog": "AppConfirm.vue",
	"n-button": "AppButton.vue",
	"n-input": "input-base (uno shortcut)",
	"n-input-number": "input-base (uno shortcut)",
	"n-card": "AppCard.vue",
	"n-tag": "AppChip.vue",
	"n-tabs": "AppSegmented.vue",
	"n-scrollbar": "scroll-area (uno shortcut)",
	"n-empty": "AppEmpty.vue",
	"n-skeleton": "AppSkeleton.vue",
	"n-spin": "AppButton 的 loading 态",
	"n-collapse": "AppCard.vue + 自己的展开状态",
}

/** 允许直接 import naive-ui 的文件: 主题定义与全局反馈宿主 */
const NAIVE_IMPORTERS = [
	"assets/style/naiveOverrides.ts",
	"assets/style/naiveTheme.ts",
	"components/ui/FeedbackHost.vue",
	"services/feedback/index.ts",
]

const listFiles = (dir: string, extension: string): string[] => {
	const OUT: string[] = []
	for (const entry of readdirSync(dir)) {
		const FULL = join(dir, entry)
		if (statSync(FULL).isDirectory()) OUT.push(...listFiles(FULL, extension))
		else if (entry.endsWith(extension)) OUT.push(FULL)
	}
	return OUT
}

const relativeSrc = (file: string): string => relative(SRC, file).split(sep).join("/")

/** 扫出模板里所有 naive 标签 (只认 `<n-xxx`, 免得把散文里的名字算进来) */
const naiveTags = (): {tag: string; where: string}[] => {
	const OUT: {tag: string; where: string}[] = []
	for (const file of listFiles(SRC, ".vue")) {
		const LINES = readFileSync(file, "utf8").split("\n")
		LINES.forEach((line, index) => {
			for (const match of line.matchAll(/<(n-[a-z][a-z-]*)/g)) {
				OUT.push({tag: match[1], where: `${relativeSrc(file)}:${index + 1}`})
			}
		})
	}
	return OUT
}

describe("naive-ui 用量收敛", () => {
	it("模板里只出现允许保留的 naive 组件", () => {
		const OFFENDERS = naiveTags()
			.filter(item => !(item.tag in ALLOWED))
			.map(item => `${item.where}: <${item.tag}> 请改用 ${REPLACED[item.tag] ?? "src/components/ui 下的 App* 组件"}`)
		expect(OFFENDERS).toEqual([])
	})

	it("允许名单不腐烂: 每一项都还在用, 且写了保留理由", () => {
		const USED = new Set(naiveTags().map(item => item.tag))
		for (const [tag, reason] of Object.entries(ALLOWED)) {
			expect(USED.has(tag), `${tag} 已无人使用, 请从 ALLOWED 移除`).toBe(true)
			expect(reason.length, `${tag} 缺少保留理由`).toBeGreaterThan(8)
		}
	})

	it("失败提示指向的自建替代品真实存在", () => {
		const ALL = new Set(listFiles(SRC, ".vue").map(relativeSrc))
		for (const target of Object.values(REPLACED)) {
			if (!target.endsWith(".vue")) continue
			expect(ALL.has(`components/ui/${target}`), `${target} 不存在, 请更新 REPLACED 的替代品`).toBe(true)
		}
	})

	it("组件不写 naive 内部 DOM 的选择器", () => {
		const OFFENDERS: string[] = []
		for (const file of listFiles(SRC, ".vue")) {
			const MATCH = readFileSync(file, "utf8").match(/(:deep\(|\.n-[a-z][a-z-]*)/g)
			if (MATCH) OFFENDERS.push(`${relativeSrc(file)}: ${[...new Set(MATCH)].join(", ")} —— naive 外观只在 naiveOverrides.ts 里调`)
		}
		expect(OFFENDERS).toEqual([])
	})

	it("只有主题定义与反馈宿主可以直接 import naive-ui", () => {
		const OFFENDERS: string[] = []
		const HITS = new Set<string>()
		for (const file of [...listFiles(SRC, ".vue"), ...listFiles(SRC, ".ts")]) {
			if (!readFileSync(file, "utf8").includes("naive-ui")) continue
			const WHERE = relativeSrc(file)
			HITS.add(WHERE)
			if (!NAIVE_IMPORTERS.includes(WHERE)) OFFENDERS.push(`${WHERE}: 组件层不要直接引 naive-ui`)
		}
		expect(OFFENDERS).toEqual([])
		// 名单反向校验: 列进来的文件必须确实还在引, 否则名单就是死配置
		for (const file of NAIVE_IMPORTERS) {
			expect(HITS.has(file), `${file} 已不再 import naive-ui, 请从名单移除`).toBe(true)
		}
	})
})
