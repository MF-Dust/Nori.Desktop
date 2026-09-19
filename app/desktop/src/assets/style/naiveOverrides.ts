import type {GlobalThemeOverrides} from "naive-ui"
import {COLORS, FONT_SIZES, RADIUS, SHADOWS} from "./tokens"

/**
 * Naive UI 深海微光暗黑主题覆盖表 (纯令牌派生)
 *
 * 纪律:
 * - naive 组件的外观只在这里调, 组件里不要用原子类去覆盖 naive 的内部 DOM
 *   (naive 的样式是运行时注入的 CSS-in-JS, 注入顺序不受我们控制)。
 * - 本表里不允许出现颜色字面量, 一律引 tokens.ts; 圆角一律引 RADIUS 刻度。
 *   由 tests/theme/tokens-sync.test.ts 看守。
 * - 只覆盖真正在用的组件: Select / Slider / Switch / Input 与 Dialog / Message
 *   两个服务式组件。其余 naive 组件项目里一处都没用, 覆盖它们是死配置。
 * 本文件不引入 naive 运行时, 因此可在 node 环境直接单测。
 */

/** naive 不接受 var(...) 作为部分主题值, 因此这里直接用令牌字面量 */
const C = COLORS

export const naiveThemeOverrides: GlobalThemeOverrides = {
	common: {
		primaryColor: C["nori-teal"],
		primaryColorHover: C["nori-teal-bright"],
		primaryColorPressed: C["nori-teal-pressed"],
		primaryColorSuppl: C["glow-teal-soft"],
		infoColor: C["nori-teal-bright"],
		infoColorHover: C["info-hover"],
		infoColorPressed: C["info-pressed"],
		successColor: C.success,
		warningColor: C.warning,
		errorColor: C["danger-text"],
		textColorBase: C["text-primary"],
		textColor1: C["text-primary"],
		textColor2: C["text-body"],
		textColor3: C["text-muted"],
		textColorDisabled: C["text-disabled"],
		placeholderColor: C["text-placeholder"],
		bodyColor: C["bg-abyss"],
		cardColor: C["bg-glass"],
		modalColor: C["bg-glass-modal"],
		popoverColor: C["bg-popover"],
		borderColor: C["line-subtle"],
		dividerColor: C["line-subtle"],
		borderRadius: RADIUS.sm,
		borderRadiusSmall: RADIUS.xs,
		fontFamily: "'Noto Sans SC', system-ui, sans-serif",
		fontSize: FONT_SIZES.base[0],
		fontSizeSmall: FONT_SIZES.sm[0],
	},
	Slider: {
		fillColor: C["slider-fill"],
		fillColorHover: C["slider-fill-hover"],
		dotColor: C["slider-fill"],
		dotBorder: `0.2rem solid ${C["bg-base"]}`,
		handleSize: "1.4rem",
		railColor: C["overlay-12"],
		railColorHover: C["overlay-20"],
		railHeight: "0.5rem",
	},
	Switch: {
		railColorActive: C["nori-teal"],
		buttonColor: C["bg-abyss"],
		boxShadowFocus: `0 0 0 0.2rem ${C["line-strong"]}`,
	},
	Select: {
		peers: {
			InternalSelection: {
				color: C["overlay-4"],
				colorActive: C["glow-teal-soft"],
				border: `0.1rem solid ${C["line-subtle"]}`,
				borderHover: `0.1rem solid ${C["glow-teal"]}`,
				borderFocus: `0.1rem solid ${C["nori-teal"]}`,
				boxShadowFocus: `0 0 0 0.2rem ${C["line-subtle"]}`,
				borderRadius: RADIUS.sm,
				textColor: C["text-primary"],
				placeholderColor: C["text-placeholder"],
			},
			InternalSelectMenu: {
				color: C["bg-menu"],
				optionTextColor: C["text-body"],
				optionTextColorActive: C["nori-teal-bright"],
				optionColorPending: C["line-subtle"],
				optionColorActive: C["line-strong"],
				borderRadius: RADIUS.sm,
			},
		},
	},
	Input: {
		color: C["overlay-4"],
		colorFocus: C["glow-teal-soft"],
		colorDisabled: C["overlay-2"],
		border: `0.1rem solid ${C["line-subtle"]}`,
		borderHover: `0.1rem solid ${C["glow-teal"]}`,
		borderFocus: `0.1rem solid ${C["nori-teal"]}`,
		borderDisabled: `0.1rem solid ${C["line-subtle"]}`,
		boxShadowFocus: `0 0 0 0.2rem ${C["line-subtle"]}`,
		borderRadius: RADIUS.sm,
		textColor: C["text-primary"],
		textColorDisabled: C["text-disabled"],
		placeholderColor: C["text-placeholder"],
	},
	Dialog: {
		borderRadius: RADIUS.md,
		color: C["bg-menu"],
		titleTextColor: C["text-primary"],
		contentTextColor: C["text-body"],
		boxShadow: SHADOWS["elev-3"],
	},
	Message: {
		borderRadius: RADIUS.sm,
		// 消息类型已由图标与文字颜色表达, 阴影不再按类型染色 (统一层级刻度)
		boxShadowInfo: SHADOWS["elev-2"],
		boxShadowSuccess: SHADOWS["elev-2"],
		boxShadowError: SHADOWS["elev-2"],
	},
}
