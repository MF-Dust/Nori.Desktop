import {defineConfig, presetWind3, transformerDirectives, transformerVariantGroup} from "unocss"
import {COLORS, FONT_SIZES, RADIUS, SHADOWS, SPACING} from "./src/assets/style/tokens"

/**
 * UnoCSS 配置 (Nori 深海蓝设计体系)
 *
 * 纪律:
 * - 不引入任何 reset: 基础重置留在 theme.less, 避免与 naive-ui 的运行时样式打架。
 * - 颜色一律走 CSS 变量间接层, 与 theme.less :root 同源 (src/assets/style/tokens.ts)。
 * - 刻度只有 rem: 间距按 4px 网格 (1 = 0.4rem), 字号最小 xs = 1.15rem。
 * - naive-ui 组件的外观只经 naiveThemeOverrides 调整, 不用原子类覆盖其内部 DOM。
 */
export default defineConfig({
	presets: [presetWind3({preflight: false})],
	transformers: [transformerVariantGroup(), transformerDirectives()],
	// 只认冒号做变体分隔符。Uno 默认还认连字符, 于是 `focus-ring` 会先被解析成
	// 「focus 变体 + ring 工具类」, 直接写在 class 里的 shortcut 名根本不生效
	// (只有被别的 shortcut 组合进去时才对), 焦点环因此静默失效 —— 关掉这个分隔符。
	separators: [":"],
	theme: {
		colors: COLORS,
		spacing: SPACING,
		fontSize: FONT_SIZES,
		fontFamily: {sans: "\"Noto Sans SC\", system-ui, sans-serif"},
		// 边框/描边宽度也走 rem, 避开 Uno 默认的 1px
		lineWidth: {
			DEFAULT: "0.1rem",
			"0": "0",
			"1": "0.1rem",
			"2": "0.2rem",
			"3": "0.3rem",
			"4": "0.4rem",
		},
		borderRadius: {
			none: "0",
			xs: RADIUS.xs,
			sm: RADIUS.sm,
			DEFAULT: RADIUS.sm,
			md: RADIUS.md,
			lg: RADIUS.lg,
			pill: RADIUS.pill,
			full: "50%",
		},
		boxShadow: SHADOWS,
		animation: {
			keyframes: {
				breathe: "{0%,100%{transform:scale(1)}50%{transform:scale(1.025)}}",
			},
			durations: {
				breathe: "2.4s",
			},
			counts: {
				breathe: "infinite",
			},
			timingFns: {
				breathe: "ease-in-out",
			},
		},
	},
	shortcuts: [
		// ---- 窗口与容器 ----
		// 窗口根节点的视觉定义只留在这里。
		// window-chrome 只管圆角/描边/阴影, window-root 再补满屏尺寸,
		// window-surface 是默认底纹 (first-run 用自己的分步渐变替换它)。
		[
			"window-chrome",
			"relative flex flex-col rounded-lg overflow-hidden select-none text-text-body transition-[box-shadow] duration-200 "
			+ "shadow-[var(--shadow-window),inset_0_0_0_0.1rem_var(--line-subtle)]",
		],
		[
			"window-chrome-focused",
			"!shadow-[var(--shadow-window),inset_0_0_0_0.1rem_var(--window-focus-border)]",
		],
		["window-root", "w-100vw h-100vh window-chrome"],
		[
			"window-surface",
			"bg-[var(--window-background,var(--bg-base))]",
		],
		// init 是启动窗: 光晕聚在正中, 与主窗的右上角光源区分
		[
			"window-surface-boot",
			"window-surface",
		],
		["surface-card", "bg-bg-card border border-line-subtle rounded-md shadow-elev-1 transition-colors duration-200 hover:(border-line-strong bg-bg-card-hover)"],
		// 卡片里再套卡片时用这一档, 与外层拉开层次
		["surface-inset", "bg-overlay-4 border border-line-subtle rounded-sm"],
		["scroll-area", "min-h-0 overflow-y-auto overflow-x-hidden"],

		// ---- 文字 ----
		["glow-teal", "text-text-primary"],
		["title-lg", "text-xl font-700 text-text-primary tracking-[-0.015em]"],
		["title-md", "text-lg font-600 text-text-primary tracking-[-0.01em]"],
		["title-sm", "text-md font-600 text-text-primary"],
		["text-sub", "text-sm text-text-muted"],
		["text-hint", "text-xs text-text-faint"],
		["mono", "font-mono tabular-nums"],

		// ---- 焦点可见 (键盘可达性: 所有可交互元素统一焦点环) ----
		[
			"focus-ring",
			"outline-none focus-visible:(outline outline-2 outline-offset-[0.2rem] outline-nori-teal-bright)",
		],

		// ---- 按钮 ----
		[
			"btn-base",
			"inline-flex items-center justify-center gap-2 font-inherit cursor-pointer border-none "
			+ "transition-all duration-150 focus-ring select-none disabled:(opacity-50 cursor-not-allowed pointer-events-none)",
		],
		[
			"btn-primary",
			"btn-base px-4 py-1.8 rounded-sm text-sm font-600 text-on-teal "
			+ "bg-nori-teal shadow-elev-1 hover:not-disabled:bg-nori-teal-bright "
			+ "active:not-disabled:bg-nori-teal-pressed",
		],
		[
			"btn-ghost",
			"btn-base px-3.5 py-1.8 rounded-sm text-sm font-500 text-text-body bg-overlay-4 border border-line-subtle "
			+ "hover:not-disabled:(text-text-primary bg-overlay-8 border-line-strong) "
			+ "active:not-disabled:bg-overlay-12",
		],
		[
			"btn-danger",
			"btn-base px-3.5 py-1.8 rounded-sm text-sm font-500 text-danger-text bg-danger/10 border border-danger/35 "
			+ "hover:not-disabled:(bg-danger/20 border-danger/60) active:not-disabled:bg-danger/25",
		],
		[
			"btn-icon",
			"btn-base w-7 h-7 rounded-full bg-transparent text-text-muted transition-all duration-150 "
			+ "hover:(bg-overlay-8 text-nori-teal-bright scale-108) active:scale-92",
		],
		// macOS 红绿灯窗口控制按钮
		[
			"btn-traffic",
			"inline-flex items-center justify-center w-[2.4rem] h-[2.8rem] rounded-sm p-0 border-none bg-transparent "
			+ "select-none cursor-pointer focus-ring outline-none disabled:cursor-default",
		],
		[
			"traffic-dot",
			"inline-flex items-center justify-center w-[1.2rem] h-[1.2rem] rounded-full text-on-teal transition-colors duration-150",
		],

		// 三个窗口标题栏的关闭按钮 (与 btn-icon 同尺寸, 悬停转危险色)
		[
			"btn-close",
			"btn-base w-7 h-7 rounded-full bg-transparent text-text-muted transition-all duration-150 "
			+ "hover:(bg-danger/20 text-danger-text scale-108) active:scale-92",
		],
		["close-icon", "w-3.5 h-3.5"],

		// ---- 徽标 / 药丸 ----
		[
			"chip",
			"inline-flex items-center gap-1.5 px-2.5 py-0.5 rounded-pill text-xs "
			+ "bg-overlay-6 border border-line-subtle text-text-muted transition-all duration-150",
		],
		["chip-teal", "chip bg-nori-teal-bright/10 border-nori-teal-bright/30 text-nori-teal-bright"],
		["chip-success", "chip bg-success/10 border-success/35 text-success"],
		["chip-warning", "chip bg-warning/10 border-warning/35 text-warning"],
		["chip-danger", "chip bg-danger/10 border-danger/35 text-danger-text"],

		// ---- 表单 ----
		["field", "flex flex-col gap-1.5"],
		["field-label", "text-sm font-500 text-text-muted"],
		[
			"input-base",
			"w-full px-3.5 py-2 rounded-sm text-sm font-inherit text-text-primary bg-overlay-4 "
			+ "border border-line-subtle outline-none transition-all duration-150 "
			+ "placeholder:text-text-placeholder hover:border-nori-teal-soft/50 "
			+ "focus:(border-nori-teal-bright bg-overlay-6 shadow-[0_0_0_0.2rem_var(--line-subtle)])",
		],

		// ---- 列表项 / 导航 ----
		[
			"nav-item",
			"relative flex items-center gap-2.5 px-3 py-2 rounded-sm border border-transparent bg-transparent "
			+ "text-sm text-text-muted font-inherit cursor-pointer overflow-hidden focus-ring "
			+ "transition-all duration-150 hover:(bg-overlay-6 text-text-primary)",
		],
		[
			"nav-item-active",
			"bg-nori-teal-bright/12 border-nori-teal-bright/30 text-nori-teal-bright font-600",
		],
	],
})
