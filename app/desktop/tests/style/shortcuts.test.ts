import {readdirSync, readFileSync, statSync} from "node:fs"
import {join, relative, resolve} from "node:path"
import {describe, expect, it} from "vitest"
import UNO_CONFIG from "../../uno.config"

/**
 * shortcut 与实际用法的一致性门禁
 *
 * uno.config.ts 的 shortcuts 承载了 docs/规范.md 里"复用而不是重新推导"这条契约,
 * 但它是否被遵守以前只能靠人肉 review。这里做两个方向的静态检查:
 *   1. 声明了却没人用的 shortcut —— 迟早跟组件漂移的死配置;
 *   2. 组件里手抄 shortcut 的展开式 —— 视觉定义多出一处副本。
 * 只用 fs + 正则扫源码, 不跑 UnoCSS 引擎: 门禁要快, 也不该依赖生成结果。
 */

const ROOT = resolve(__dirname, "../..")
const SRC = join(ROOT, "src")

/** 会写类名的源文件后缀 */
const SOURCE_EXTENSIONS = [".vue", ".ts"]

/** 令牌来源目录: 这里出现的 `glow-teal` 之类是色号定义, 不是类名使用, 不能算 shortcut 有人用 */
const TOKEN_SOURCE_DIR = join(SRC, "assets", "style")

/**
 * 刻意保留、暂时无人使用的 shortcut (键为 shortcut 名, 值为保留理由)
 *
 * 只放"成套刻度里补齐的档位"。名单被下面的自检看守: 条目必须仍是已声明的 shortcut
 * 且写清理由; 等组件用上了就该把它从名单里删掉, 别让白名单变成掩盖腐烂的垃圾场。
 */
const INTENTIONAL_UNUSED: Record<string, string> = {
	"focus-ring-within": "复杂设置页已迁移到原生 Avalonia，保留该无障碍快捷方式供剩余 Vue 表单复用",
}

/**
 * 手抄展开式的高价值检测项
 *
 * 同一段 class 文本里凑够 threshold 个特征类, 就说明在绕过 shortcut 重新推导它。
 * 特征类只取"该 shortcut 独有"的那几个, 阈值留出余量, 避免把正常的单独用法算成违规。
 */
const HAND_ROLL_GUARDS: {shortcut: string, signature: string[], threshold: number, exclude?: RegExp}[] = [
	{
		shortcut: "focus-ring",
		signature: ["outline-none", "outline-2", "outline-offset-[0.2rem]", "outline-nori-teal-bright"],
		threshold: 2,
		// focus-within 是"子控件获得焦点时给包裹层描边", 与 focus-ring 的 focus-visible
		// 语义不同, 不能互换 —— 那一类交给下面的 focus-ring-within 看守, 两条各管一个伪类。
		exclude: /focus-within:/,
	},
	{
		shortcut: "focus-ring-within",
		signature: ["outline", "outline-2", "outline-offset-[0.2rem]", "outline-nori-teal-bright"],
		threshold: 3,
		exclude: /focus-visible:/,
	},
	{
		// 单选药丸的选中/未选中态: 两串各自被四处抄过, 描边与光晕最容易先漂
		shortcut: "pill-choice-on",
		signature: ["border-nori-teal-bright", "bg-nori-teal-bright/14", "text-nori-teal-bright", "font-600"],
		threshold: 4,
	},
	{
		shortcut: "pill-choice-off",
		signature: ["border-line-subtle", "bg-overlay-4", "text-text-muted", "border-nori-teal-soft/60"],
		threshold: 4,
	},
	{
		shortcut: "scroll-area",
		signature: ["min-h-0", "overflow-y-auto", "overflow-x-hidden"],
		threshold: 3,
	},
	{
		shortcut: "surface-card",
		signature: ["bg-bg-card", "border-line-subtle", "rounded-md", "shadow-elev-1", "bg-bg-card-hover"],
		threshold: 4,
	},
	{
		shortcut: "nav-item",
		signature: ["border-transparent", "bg-transparent", "text-text-muted", "cursor-pointer", "bg-overlay-6"],
		threshold: 4,
	},
]

/** 一段可能写着类名的文本 (报错要能指回文件) */
interface ClassFragment {
	file: string
	text: string
}

const relativeSrc = (file: string): string => relative(SRC, file).replace(/\\/g, "/")

const listSourceFiles = (dir: string): string[] => {
	const OUT: string[] = []
	for (const entry of readdirSync(dir)) {
		const FULL = join(dir, entry)
		if (statSync(FULL).isDirectory()) OUT.push(...listSourceFiles(FULL))
		else if (SOURCE_EXTENSIONS.some(extension => entry.endsWith(extension))) OUT.push(FULL)
	}
	return OUT
}

/**
 * 取一个文件里可能写着类名的文本片段
 *
 * 除 class / :class / v-bind:class 属性 (可跨行, 单独抓一遍), 还要收所有单行字符串
 * 字面量: AppButton / AppChip 这类包装组件用 `{danger: "btn-danger"}` 的映射表把
 * shortcut 名放在 <script> 里 (UnoCSS 静态扫描的是整份文件, 这种写法有效), 只看属性
 * 会把它们误判成死 shortcut。注释里提到的名字不算使用 —— 只收字面量天然排除了注释。
 */
const readClassFragments = (file: string): ClassFragment[] => {
	const SOURCE = readFileSync(file, "utf8")
	const OUT: ClassFragment[] = []
	for (const match of SOURCE.matchAll(/(?::|v-bind:)?class\s*=\s*"([^"]*)"/g)) OUT.push({file, text: match[1]})
	for (const match of SOURCE.matchAll(/(?::|v-bind:)?class\s*=\s*'([^']*)'/g)) OUT.push({file, text: match[1]})
	for (const match of SOURCE.matchAll(/"([^"\n]*)"|'([^'\n]*)'|`([^`\n$]*)`/g)) {
		const TEXT = match[1] ?? match[2] ?? match[3] ?? ""
		if (TEXT.length > 0) OUT.push({file, text: TEXT})
	}
	return OUT
}

/** shortcut 名 → 展开式 (只认静态字符串形式, 函数式 shortcut 无法静态比对) */
const readShortcuts = (): Map<string, string> => {
	const OUT = new Map<string, string>()
	const RAW: unknown = UNO_CONFIG.shortcuts
	for (const entry of Array.isArray(RAW) ? (RAW as unknown[]) : []) {
		if (!Array.isArray(entry)) continue
		const [name, expansion] = entry as unknown[]
		if (typeof name === "string" && typeof expansion === "string") OUT.set(name, expansion)
	}
	return OUT
}

const escapeRegExp = (text: string): string => text.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")

/** 类名是否作为完整 token 出现 (变体前缀 `hover:`、变体组括号、空白都算边界) */
const mentions = (text: string, token: string): boolean =>
	new RegExp(`(^|[^A-Za-z0-9_-])${escapeRegExp(token)}([^A-Za-z0-9_-]|$)`).test(text)

/** 某个 shortcut 传递组合进来的所有 shortcut 名 (window-root → window-chrome, btn-primary → btn-base → focus-ring) */
const composedShortcuts = (name: string, shortcuts: Map<string, string>): Set<string> => {
	const OUT = new Set<string>()
	const QUEUE = [name]
	while (QUEUE.length > 0) {
		const CURRENT = QUEUE.pop() as string
		const EXPANSION = shortcuts.get(CURRENT) ?? ""
		for (const other of shortcuts.keys()) {
			if (other === CURRENT || OUT.has(other) || !mentions(EXPANSION, other)) continue
			OUT.add(other)
			QUEUE.push(other)
		}
	}
	return OUT
}

const SHORTCUTS = readShortcuts()
const FRAGMENTS = listSourceFiles(SRC)
	.filter(file => !file.startsWith(TOKEN_SOURCE_DIR))
	.flatMap(readClassFragments)
const CLASS_BAG = FRAGMENTS.map(fragment => fragment.text).join(" \n ")

/** 直接写在 src 里的 shortcut */
const DIRECT_USED = [...SHORTCUTS.keys()].filter(name => mentions(CLASS_BAG, name))

/** 直接使用 + 被在用 shortcut 组合进去的 (被组合的档位不是死配置) */
const REACHED_USED = new Set([
	...DIRECT_USED,
	...DIRECT_USED.flatMap(name => [...composedShortcuts(name, SHORTCUTS)]),
])

/** 谁能提供这个 shortcut: 它自己, 以及把它组合进去的其它 shortcut */
const providersOf = (target: string): string[] =>
	[...SHORTCUTS.keys()].filter(name => name === target || composedShortcuts(name, SHORTCUTS).has(target))

describe("uno shortcut 与实际用法一致", () => {
	it("能从 uno.config.ts 解析出静态 shortcut 表", () => {
		expect(SHORTCUTS.size).toBeGreaterThan(20)
		// 抽查几条最常被复用的, 防止解析静默退化成空表
		for (const name of ["window-root", "surface-card", "scroll-area", "focus-ring", "btn-primary", "nav-item"]) {
			expect(SHORTCUTS.has(name), `shortcut ${name} 未被解析到`).toBe(true)
		}
		expect(FRAGMENTS.length).toBeGreaterThan(100)
	})

	it("每个 shortcut 都被 src 用到 (或被在用的 shortcut 组合进去)", () => {
		const DEAD = [...SHORTCUTS.keys()]
			.filter(name => !REACHED_USED.has(name) && !(name in INTENTIONAL_UNUSED))
			.map(name => `${name} (uno.config.ts): 无人使用, 请删掉或写进 INTENTIONAL_UNUSED 并说明理由`)
		expect(DEAD).toEqual([])
	})

	it("INTENTIONAL_UNUSED 名单不腐烂 (条目仍是已声明的 shortcut 且写了理由)", () => {
		for (const [name, reason] of Object.entries(INTENTIONAL_UNUSED)) {
			expect(SHORTCUTS.has(name), `${name} 已不是 shortcut, 请从 INTENTIONAL_UNUSED 移除`).toBe(true)
			expect(reason.length, `${name} 必须写清保留理由`).toBeGreaterThan(0)
		}
	})

	it("手抄检测项与 shortcut 定义同步 (特征类必须还在展开式里)", () => {
		// 展开式改了却忘了改这里, 检测就会静默失效 —— 这条断言把两边钉在一起
		for (const guard of HAND_ROLL_GUARDS) {
			const EXPANSION = SHORTCUTS.get(guard.shortcut)
			expect(EXPANSION, `shortcut ${guard.shortcut} 不存在, 请更新 HAND_ROLL_GUARDS`).toBeTypeOf("string")
			for (const token of guard.signature) {
				expect(mentions(EXPANSION ?? "", token), `${guard.shortcut} 的展开式里已经没有 ${token}`).toBe(true)
			}
			expect(guard.threshold, `${guard.shortcut} 的阈值要落在 2..特征类数量 之间`).toBeGreaterThanOrEqual(2)
			expect(guard.threshold).toBeLessThanOrEqual(guard.signature.length)
		}
	})

	it("组件不手抄 shortcut 的展开式", () => {
		const OFFENDERS: string[] = []
		for (const guard of HAND_ROLL_GUARDS) {
			const PROVIDERS = providersOf(guard.shortcut)
			for (const fragment of FRAGMENTS) {
				if (guard.exclude?.test(fragment.text)) continue
				// 已经用上了该 shortcut (或组合了它的上层 shortcut), 追加微调不算手抄
				if (PROVIDERS.some(name => mentions(fragment.text, name))) continue
				const HITS = guard.signature.filter(token => mentions(fragment.text, token))
				if (HITS.length < guard.threshold) continue
				OFFENDERS.push(`${relativeSrc(fragment.file)}: 手写了 ${guard.shortcut} 的展开式 (${HITS.join(" ")}), 请改用 ${guard.shortcut}`)
			}
		}
		expect([...new Set(OFFENDERS)].sort()).toEqual([])
	})
})
