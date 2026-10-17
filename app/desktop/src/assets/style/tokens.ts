/**
 * Nori 原生设计令牌 (单一色源)
 *
 * Avalonia 主题资源和测量值由 scripts/sync-design-tokens.mjs 从这里生成。
 * 视觉语言对齐 Nori.Web 的 theme-nori (深海玻璃 + 冰青描边)：
 * 内容层取 OKLCH 语义色 (primary / muted / border)，外壳层取冰青 #9ed2d2 不同透明度的发丝线。
 * 调整颜色前先检查原生窗口的文字对比度 (tests/theme/contrast.test.ts)。
 */

/** 颜色令牌 */
export const COLORS = {
	// 品牌与交互强调 (web: --primary / 冰青外壳色 / primary 与白色 75% 混合的焦点高光)
	"nori-teal": "#77c6d4",
	"nori-teal-bright": "#9ed2d2",
	"nori-teal-soft": "#99d4df",
	// primary/90 叠在玻璃上的悬停与按下色
	"nori-teal-pressed": "#6db5c2",

	// 深海背景层级 (web: 玻璃底 #161a22 / 输入井 background/60 / muted / 系统胶囊)
	"bg-base": "#161a22",
	"bg-abyss": "#0d1118",
	"bg-deep": "#1a2026",
	"bg-panel": "#282e3a",

	// 玻璃表面: web 不使用卡片, 分组只是 muted 的淡色底
	"bg-card": "rgba(26, 32, 38, 0.72)",
	"bg-card-hover": "#1f262d",
	"bg-glass": "rgba(22, 26, 34, 0.88)",
	"bg-glass-modal": "rgba(20, 25, 34, 0.965)",
	"bg-sidebar": "rgba(26, 32, 38, 0.3)",
	"bg-input": "#0d1118",
	// primary/10 叠在玻璃上的选中底色
	"bg-selection": "#202c34",

	// 文字层级: 次级文字比 web 的 muted-foreground 略亮, 以便玻璃覆盖白色桌面时仍 ≥4.5:1
	"text-primary": "#e2eaea",
	"text-body": "#c8dcdc",
	"text-muted": "#9ca7ab",
	"text-faint": "#98a3a8",
	"text-placeholder": "#98a3a8",
	"text-disabled": "#7f888c",

	// 冰青发丝线 (web: border 10% / input 14% / 渐变描边高光 32%)
	"line-subtle": "rgba(158, 210, 210, 0.1)",
	"line-strong": "rgba(158, 210, 210, 0.15)",
	"line-glow": "rgba(202, 226, 228, 0.32)",
	"line-input": "rgba(158, 210, 210, 0.14)",

	// 窗口渐变描边: 静止为冰青高光, 聚焦过渡到品牌青 (自上而下三段)
	"ring-rest-from": "rgba(202, 226, 228, 0.32)",
	"ring-rest-via": "rgba(158, 210, 210, 0.12)",
	"ring-rest-to": "rgba(158, 210, 210, 0.05)",
	"ring-focus-from": "#99d4df",
	"ring-focus-via": "rgba(119, 198, 212, 0.44)",
	"ring-focus-to": "rgba(119, 198, 212, 0.12)",
	"window-focus-glow": "rgba(119, 198, 212, 0.5)",
	"window-shadow": "rgba(0, 0, 0, 0.35)",

	// 状态色: danger 只做填充/描边, 文字用 danger-text (web destructive 在玻璃上不足 4.5:1)
	"success": "#00bc7d",
	"warning": "#f1b24a",
	"danger": "#d74745",
	"danger-text": "#ff7d84",

	// 前景语义: 品牌青填充上的深色文字 (web: primary-foreground)
	"on-teal": "#070e16",

	// 中性叠加层: 玻璃面上的浅色覆盖, 数字即不透明度百分比
	"overlay-4": "rgba(255, 255, 255, 0.04)",
	"overlay-8": "rgba(255, 255, 255, 0.08)",
	"overlay-12": "rgba(255, 255, 255, 0.12)",
	"overlay-20": "rgba(255, 255, 255, 0.2)",
	// 冰青悬停层 (web 外壳菜单、顶栏按钮)
	"hover-ice": "rgba(158, 210, 210, 0.1)",
	"hover-ice-strong": "rgba(158, 210, 210, 0.15)",

	// 深色叠加层: 浅色对话气泡 (chat-ai-bg) 上的行内代码、分隔线、引用与表头底色
	"ink-6": "rgba(0, 0, 0, 0.06)",
	"ink-8": "rgba(0, 0, 0, 0.08)",
	"ink-12": "rgba(0, 0, 0, 0.12)",
	"ink-15": "rgba(0, 0, 0, 0.15)",
	"shadow-inset": "rgba(0, 0, 0, 0.4)",

	// 浮层背景 (web: 菜单 #1c202a / 提示条 #1e232d / 遮罩 #03060c)
	"bg-popover": "rgba(28, 32, 42, 0.97)",
	"bg-menu": "rgba(28, 32, 42, 0.97)",
	"bg-tooltip": "rgba(30, 35, 45, 0.97)",
	"scrim": "rgba(3, 6, 12, 0.55)",

	// 浮动对话 (web conversation-panel)
	"chat-user-bg": "#3d3d47",
	"chat-user-bg-end": "#4a4a55",
	"chat-user-text": "#e8e8ec",
	"chat-user-border": "rgba(139, 139, 153, 0.19)",
	"chat-ai-bg": "#d2e8e9",
	"chat-ai-bg-end": "#caf5f1",
	"chat-ai-text": "#45454f",
	"chat-ai-border": "rgba(254, 254, 254, 0.44)",
	"chat-ai-glow": "rgba(202, 245, 241, 0.35)",
	"chat-composer": "#56565f",
	"chat-white": "#fefefe",
	"chat-focus-placeholder": "#32616d",
	"chat-approval-danger": "#b01521",
	"chat-markdown-link": "#0369a1",
	"chat-markdown-quote": "#4a7c82",

	// 交通灯: web 使用 85% 填充, 悬停改为同色外发光, 不显示符号
	"traffic-close": "rgba(255, 95, 87, 0.85)",
	"traffic-close-hover": "rgba(255, 95, 87, 0.5)",
	"traffic-minimize": "rgba(255, 189, 46, 0.85)",
	"traffic-minimize-hover": "rgba(255, 189, 46, 0.5)",
	"traffic-zoom": "rgba(40, 200, 64, 0.85)",
	"traffic-zoom-hover": "rgba(40, 200, 64, 0.5)",
	"traffic-disabled": "rgba(128, 128, 128, 0.3)",
	"traffic-highlight": "rgba(255, 255, 255, 0.25)",
} as const

/** 圆角令牌 (web Tailwind v4: sm 4 / md 6 / lg 8 / xl 12 / 2xl 16) */
export const RADIUS = {
	xs: "0.4rem",
	sm: "0.6rem",
	md: "0.8rem",
	lg: "1.2rem",
	xl: "1.6rem",
	pill: "99.9rem",
} as const

/**
 * 字号刻度 (1rem = 10px)
 *
 * web 层级很平: 正文、行标签与小标题都是 14, 说明文字 12, 页面主标题 24。
 * 最小档位 1.2rem: 更小的说明文字在深色玻璃上几乎不可读, 原生代码不得低于 xs。
 */
export const FONT_SIZES = {
	xs: ["1.2rem", "1.5"],
	sm: ["1.3rem", "1.5"],
	base: ["1.4rem", "1.5"],
	md: ["1.5rem", "1.5"],
	lg: ["1.6rem", "1.5"],
	xl: ["1.8rem", "1.45"],
	"2xl": ["2rem", "1.4"],
	"3xl": ["2.4rem", "1.3"],
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

/**
 * 不透明度令牌
 *
 * disabled: web 所有禁用控件统一 0.5。
 * pressed: 无独立按下色的幽灵按钮在按下时的内容不透明度。
 */
export const OPACITY = {
	disabled: "0.5",
	pressed: "0.65",
} as const
