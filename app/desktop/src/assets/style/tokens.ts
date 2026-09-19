/**
 * Nori 设计令牌 (单一色源)
 *
 * uno.config.ts、naiveTheme.ts 与 theme.less 的 :root 变量全部派生自这里。
 * 三处一致性由 tests/theme/tokens-sync.test.ts 看守, 任何一处漂移都会红。
 *
 * 参考 Nori.Web 的深灰蓝、低饱和青色与薄荷色对话。
 * Web 与原生资源由 scripts/sync-design-tokens.mjs 同步，文字优先满足可读性。
 */

/** 颜色令牌 (键即 CSS 变量名去掉 -- 前缀) */
export const COLORS = {
	// 品牌与交互强调
	"nori-teal": "#77c6d4",
	"nori-teal-bright": "#9ed2d2",
	"nori-teal-soft": "#b6d5d9",

	// 深灰蓝背景层级
	"bg-base": "#161a22",
	"bg-abyss": "#13171e",
	"bg-deep": "#1b2029",
	"bg-panel": "#272e39",

	// 玻璃拟态表面
	"bg-card": "rgba(30, 35, 45, 0.92)",
	"bg-card-hover": "rgba(39, 46, 57, 0.94)",
	"bg-card-active": "rgba(45, 55, 65, 0.96)",
	"bg-glass": "rgba(22, 26, 34, 0.86)",
	"bg-glass-modal": "rgba(22, 26, 34, 0.96)",
	"bg-sidebar": "rgba(19, 23, 30, 0.55)",
	"bg-input": "#252c36",
	"bg-selection": "#293e46",

	// 次级文字在玻璃覆盖最亮背景时仍可读
	"text-primary": "#e8efef",
	"text-body": "#cedbdc",
	"text-muted": "#a7b9bf",
	"text-faint": "#95a9b1",

	// 边框 / 分隔
	"line-subtle": "rgba(158, 210, 210, 0.16)",
	"line-strong": "rgba(158, 210, 210, 0.30)",
	"line-glow": "rgba(158, 210, 210, 0.45)",

	// 状态色: danger 只做填充/描边, 文字用 danger-text (纯 #fb3c44 当文字在浅面板上只有 3.9:1)
	"success": "#80c9aa",
	"warning": "#f1b24a",
	"danger": "#fb3c44",
	"danger-text": "#ff7d84",

	// 克制的焦点光晕
	"glow-teal": "rgba(158, 210, 210, 0.20)",
	"glow-teal-soft": "rgba(158, 210, 210, 0.08)",
	"glow-teal-strong": "rgba(158, 210, 210, 0.30)",

	// 前景语义: 亮青绿填充上的深色文字 (对比度由 contrast.test.ts 看守)
	"on-teal": "#16232b",
	// 占位符与禁用态: 占位 ≥4.5:1, 禁用 ≥3:1
	"text-placeholder": "#95a9b1",
	"text-disabled": "#798f99",

	// 品牌交互态 (按下 / info 悬停按下)
	"nori-teal-pressed": "#64b1c0",
	"info-hover": "#b6dfe1",
	"info-pressed": "#6aafbc",

	// 中性叠加层: 玻璃面上的浅色覆盖, 数字即不透明度百分比
	"overlay-2": "rgba(255, 255, 255, 0.02)",
	"overlay-4": "rgba(255, 255, 255, 0.04)",
	"overlay-6": "rgba(255, 255, 255, 0.06)",
	"overlay-8": "rgba(255, 255, 255, 0.08)",
	"overlay-12": "rgba(255, 255, 255, 0.12)",
	"overlay-20": "rgba(255, 255, 255, 0.2)",

	// 浮层背景 (下拉菜单 / 气泡 / 提示条)
	"bg-popover": "rgba(30, 35, 45, 0.98)",
	"bg-menu": "rgba(22, 26, 34, 0.98)",
	"bg-tooltip": "rgba(30, 35, 45, 0.98)",
	"scrim": "rgba(3, 6, 12, 0.65)",

	// 视觉焕新控件与对话令牌
	"slider-fill": "#77c6d4",
	"slider-fill-hover": "#9ed2d2",
	"window-focus-border": "#9ed2d2",
	"window-focus-glow": "rgba(158, 210, 210, 0.14)",
	"chat-user-bg": "#3d3d47",
	"chat-user-bg-end": "#4a4a55",
	"chat-user-text": "#e8e8ec",
	"chat-ai-bg": "#d2e8e9",
	"chat-ai-bg-end": "#caf5f1",
	"chat-ai-text": "#45454f",
	"chat-ai-border": "rgba(254, 254, 254, 0.44)",
	"chat-composer": "#56565f",
	"chat-white": "#fefefe",
	"chat-focus-placeholder": "#32616d",
	"chat-approval-danger": "#b01521",
	"chat-markdown-link": "#0369a1",
	"chat-markdown-quote": "#4a7c82",
	"traffic-close": "#ff5f56",
	"traffic-close-hover": "#e0443e",
	"traffic-minimize": "#ffbd2e",
	"traffic-minimize-hover": "#dea123",
	"traffic-zoom": "#27c93f",
	"traffic-zoom-hover": "#1aab29",
} as const

/** 圆角令牌 */
export const RADIUS = {
	xs: "0.4rem",
	sm: "0.8rem",
	md: "1.2rem",
	lg: "1.6rem",
	pill: "99.9rem",
} as const

/**
 * 阴影令牌
 *
 * elev-1/2/3 是层级刻度: 页面 → 卡片 → 卡片内卡片 → 浮层, 每升一级换一档。
 * 之前只有 soft/glow/window 三档, 嵌套卡片没有可用的层次差, 观感扁平。
 */
export const SHADOWS = {
	soft: "0 0.8rem 2.4rem rgba(0, 0, 0, 0.35)",
	glow: "0 0 1.2rem rgba(158, 210, 210, 0.08)",
	window: "0 0.8rem 3.2rem rgba(0, 0, 0, 0.35)",
	"elev-1": "0 0.2rem 0.8rem rgba(0, 0, 0, 0.16)",
	"elev-2": "0 0.4rem 1.6rem rgba(0, 0, 0, 0.24)",
	"elev-3": "0 0.8rem 3.2rem rgba(0, 0, 0, 0.35)",
} as const

/**
 * 字号刻度 (1rem = 10px)
 *
 * 最小档位 1.15rem: 旧代码里 1rem / 1.05rem 的说明文字在深色玻璃上几乎不可读。
 */
export const FONT_SIZES = {
	xs: ["1.2rem", "1.5"],
	sm: ["1.2rem", "1.55"],
	base: ["1.3rem", "1.6"],
	md: ["1.4rem", "1.55"],
	lg: ["1.6rem", "1.5"],
	xl: ["1.8rem", "1.45"],
	"2xl": ["2.2rem", "1.35"],
	"3xl": ["2.6rem", "1.3"],
} as const

/**
 * 间距刻度 (4px 网格 → rem)
 *
 * 规范要求 rem-only, 因此这里不出现任何 px。
 */
export const SPACING = {
	"0": "0",
	"0.5": "0.2rem",
	"1": "0.4rem",
	"1.5": "0.6rem",
	"2": "0.8rem",
	"2.5": "1rem",
	"3": "1.2rem",
	"3.5": "1.4rem",
	"4": "1.6rem",
	"5": "2rem",
	"6": "2.4rem",
	"7": "2.8rem",
	"8": "3.2rem",
	"9": "3.6rem",
	"10": "4rem",
	"12": "4.8rem",
	"14": "5.6rem",
	"16": "6.4rem",
	"20": "8rem",
} as const

/** 供运行时使用的 CSS 变量表 (theme.less 的 :root 必须与之逐条一致) */
export const CSS_VARIABLES: Record<string, string> = {
	...Object.fromEntries(Object.entries(COLORS).map(([key, value]) => [`--${key}`, value])),
	...Object.fromEntries(Object.entries(RADIUS).map(([key, value]) => [`--radius-${key}`, value])),
	...Object.fromEntries(Object.entries(SHADOWS).map(([key, value]) => [`--shadow-${key}`, value])),
}

/** 颜色令牌名 */
export type ColorToken = keyof typeof COLORS

/** 取 CSS 变量引用, 组件与 Uno 主题共用同一份间接层 */
export const cssVar = (token: ColorToken): string => `var(--${token})`
