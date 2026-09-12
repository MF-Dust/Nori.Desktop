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
				breathe: "{0%,100%{transform:scale(1);filter:drop-shadow(0 0 1.4rem rgba(94,234,212,0.35))}"
					+ "50%{transform:scale(1.05);filter:drop-shadow(0 0 2.4rem rgba(125,227,255,0.65))}}",
				"glow-pulse": "{0%,100%{opacity:0.3;transform:scale(1)}50%{opacity:0.65;transform:scale(1.08)}}",
			},
			durations: {
				breathe: "2.4s",
				"glow-pulse": "2.5s",
			},
			counts: {
				breathe: "infinite",
				"glow-pulse": "infinite",
			},
			timingFns: {
				breathe: "ease-in-out",
				"glow-pulse": "ease-in-out",
			},
		},
	},
	shortcuts: [
		// ---- 窗口与容器 ----
		// 三个 WebView 窗口 (main / init / first-run) 的根节点一律用这几个,
		// 不要在 .vue 里手抄这串: 抄一遍就多一处会漂移的视觉定义。
		// window-chrome 只管圆角/描边/阴影, window-root 再补满屏尺寸,
		// window-surface 是默认底纹 (first-run 用自己的分步渐变替换它)。
		[
			"window-chrome",
			"relative flex flex-col rounded-lg overflow-hidden select-none text-text-body transition-[box-shadow] duration-200 "
			+ "shadow-[var(--shadow-window),inset_0_0_0_0.1rem_var(--line-subtle)]",
		],
		[
			"window-chrome-focused",
			"!shadow-[var(--shadow-window),inset_0_0_0_0.15rem_var(--window-focus-border),0_0_1.4rem_var(--window-focus-glow)]",
		],
		["window-root", "w-100vw h-100vh window-chrome"],
		[
			"window-surface",
			"bg-[radial-gradient(110rem_70rem_at_90%_0%,var(--glow-teal-soft)_0%,transparent_60%),"
			+ "radial-gradient(60rem_50rem_at_0%_100%,var(--bg-panel)_0%,transparent_60%),"
			+ "linear-gradient(165deg,var(--bg-panel)_0%,var(--bg-deep)_45%,var(--bg-abyss)_100%)]",
		],
		// init 是启动窗: 光晕聚在正中, 与主窗的右上角光源区分
		[
			"window-surface-boot",
			"bg-[radial-gradient(56rem_36rem_at_50%_45%,var(--glow-teal-soft),transparent_70%),"
			+ "radial-gradient(36rem_24rem_at_50%_60%,var(--line-glow),transparent_65%),"
			+ "linear-gradient(165deg,var(--bg-panel)_0%,var(--bg-deep)_50%,var(--bg-abyss)_100%)]",
		],
		["glass-panel", "bg-bg-card border border-line-subtle rounded-md backdrop-blur-[1.4rem] transition-all duration-200 hover:(border-line-strong shadow-elev-1)"],
		["surface-card", "bg-bg-card border border-line-subtle rounded-md shadow-elev-1 backdrop-blur-[1.2rem] transition-all duration-200 hover:(border-line-strong bg-bg-card-hover shadow-elev-2)"],
		// 卡片里再套卡片时用这一档, 与外层拉开层次
		["surface-inset", "bg-overlay-4 border border-line-subtle rounded-sm backdrop-blur-[0.8rem]"],
		["glow-card", "bg-bg-card/85 border border-nori-teal-bright/30 rounded-md shadow-elev-2 backdrop-blur-[1.6rem]"],
		["scroll-area", "min-h-0 overflow-y-auto overflow-x-hidden"],

		// ---- 文字 ----
		["glow-teal", "text-text-primary [text-shadow:0_0_1.2rem_var(--glow-teal-soft)]"],
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
			+ "bg-gradient-to-r from-nori-teal-bright via-nori-teal to-nori-teal-soft shadow-[0_0.2rem_1.4rem_var(--glow-teal-soft)] "
			+ "hover:not-disabled:(brightness-110 -translate-y-[0.1rem] shadow-[0_0.4rem_2rem_var(--glow-teal)]) "
			+ "active:not-disabled:(translate-y-0 scale-98)",
		],
		[
			"btn-ghost",
			"btn-base px-3.5 py-1.8 rounded-sm text-sm font-500 text-text-body bg-overlay-4 border border-line-subtle "
			+ "hover:not-disabled:(text-text-primary bg-overlay-8 border-nori-teal-soft/60 -translate-y-[0.1rem] shadow-elev-1) "
			+ "active:not-disabled:(translate-y-0 scale-98)",
		],
		[
			"btn-danger",
			"btn-base px-3.5 py-1.8 rounded-sm text-sm font-500 text-danger-text bg-danger/10 border border-danger/35 "
			+ "hover:not-disabled:(bg-danger/20 border-danger/60 -translate-y-[0.1rem]) active:not-disabled:(translate-y-0 scale-98)",
		],
		[
			"btn-icon",
			"btn-base w-7 h-7 rounded-full bg-transparent text-text-muted transition-all duration-150 "
			+ "hover:(bg-overlay-8 text-nori-teal-bright scale-108) active:scale-92",
		],
		// macOS 红绿灯窗口控制按钮
		[
			"btn-traffic",
			"inline-flex items-center justify-center w-[1.2rem] h-[1.2rem] rounded-full p-0 border-none "
			+ "transition-all duration-150 select-none cursor-pointer focus-ring outline-none "
			+ "hover:(scale-110 brightness-105) active:scale-92 disabled:(opacity-40 cursor-not-allowed pointer-events-none)",
		],
		[
			"btn-traffic-close",
			"btn-traffic bg-traffic-close hover:shadow-[0_0_0.6rem_rgba(255,95,86,0.6)] text-[#4c0002]/0 hover:text-[#4c0002]/85 active:text-[#4c0002]",
		],
		[
			"btn-traffic-min",
			"btn-traffic bg-traffic-minimize hover:shadow-[0_0_0.6rem_rgba(255,189,46,0.6)] text-[#5c3c00]/0 hover:text-[#5c3c00]/85 active:text-[#5c3c00]",
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

		// ---- 单选药丸组 (语言 / 空闲时长 / 日志级别 / TTS 协议 / 表情开关) ----
		// 这些组以前各页手抄三串: 外形、选中态、未选中态。尺寸与内部结构仍由调用方定,
		// 只把这三串收进来 —— 选中态的描边与光晕一旦漂移, 同一个页面里就能一眼看出两套观感。
		// 焦点环由使用方通过 focus-ring 补充。
		["pill-choice", "inline-flex items-center rounded-pill border font-inherit cursor-pointer transition-all duration-200"],
		["pill-choice-on", "border-nori-teal-bright bg-nori-teal-bright/14 text-nori-teal-bright font-600 shadow-glow"],
		[
			"pill-choice-off",
			"border-line-subtle bg-overlay-4 text-text-muted "
			+ "hover:(text-text-primary bg-overlay-8 border-nori-teal-soft/60)",
		],

		// ---- 表单 ----
		["field", "flex flex-col gap-1.5"],
		["field-label", "text-sm font-500 text-text-muted"],
		[
			"input-base",
			"w-full px-3.5 py-2 rounded-sm text-sm font-inherit text-text-primary bg-overlay-4 "
			+ "border border-line-subtle outline-none transition-all duration-150 "
			+ "placeholder:text-text-placeholder hover:border-nori-teal-soft/50 "
			+ "focus:(border-nori-teal-bright bg-nori-teal-bright/6 shadow-[0_0_1.4rem_var(--glow-teal-soft)])",
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
			"bg-nori-teal-bright/12 border-nori-teal-bright/30 text-nori-teal-bright font-600 shadow-glow",
		],
	],
})
