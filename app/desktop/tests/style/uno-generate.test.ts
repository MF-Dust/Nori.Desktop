import {describe, expect, it} from "vitest"
import {createGenerator, expandVariantGroup} from "unocss"
import UNO_CONFIG from "../../uno.config"

/**
 * shortcut 生成结果的门禁
 *
 * shortcuts.test.ts 只扫源码文本, 看不见"这个 shortcut 名到底生成了什么 CSS"。
 * 这里真跑一遍生成器, 是因为出过一次静态检查全绿的事故: Uno 默认把连字符也当
 * 变体分隔符, `focus-ring` 于是先被解析成「focus 变体 + ring 工具类」, 直接写在
 * class 里的 shortcut 名静默失效, 全站焦点环没有描边。
 *
 * 契约: 写 shortcut 名, 必须和写它的展开式得到同一套声明。
 */

/** uno.config.ts 里的 shortcut 全是 [名字, 展开式] 二元组 */
const SHORTCUTS = (UNO_CONFIG.shortcuts ?? []) as [string, string][]

/** 变体分隔符: 与 uno.config.ts 一致 (只认冒号) */
const SEPARATORS = [":"]

const GENERATOR = createGenerator(UNO_CONFIG)

/** 生成一段 class 文本对应的 CSS (不带 preflight, 只看这几个类自己的规则) */
const cssOf = async (tokens: string): Promise<string> => {
	const UNO = await GENERATOR
	const {css} = await UNO.generate(expandVariantGroup(tokens, SEPARATORS), {preflights: false})
	return css
}

/** 抽出 CSS 里的所有声明 (property:value), 用来比对两种写法是否等价 */
const declarationsOf = (css: string): string[] => {
	const OUT: string[] = []
	for (const BLOCK of css.matchAll(/\{([^{}]*)\}/g)) {
		for (const ITEM of BLOCK[1].split(";")) {
			const TEXT = ITEM.trim()
			if (TEXT) OUT.push(TEXT)
		}
	}
	return [...new Set(OUT)].sort()
}

describe("shortcut 生成结果", () => {
	it("每个 shortcut 名都生成了 CSS", async () => {
		for (const [NAME] of SHORTCUTS) {
			expect(await cssOf(NAME), NAME).not.toBe("")
		}
	})

	it("写 shortcut 名与写展开式得到同一套声明", async () => {
		for (const [NAME, EXPANSION] of SHORTCUTS) {
			const FROM_NAME = declarationsOf(await cssOf(NAME))
			const FROM_EXPANSION = declarationsOf(await cssOf(EXPANSION))
			// 不一致的第一嫌疑: uno.config.ts 的 separators 又认了连字符, 名字被拆成变体+工具类
			expect(FROM_NAME, `${NAME} 的 shortcut 名与展开式生成结果不一致`).toEqual(FROM_EXPANSION)
		}
	})

	it("焦点环落在正确的伪类上 (键盘可达性回归)", async () => {
		const RING = await cssOf("focus-ring")
		expect(RING).toContain(".focus-ring:focus-visible")
		expect(RING).toContain("outline-color")
		// ring 是内阴影而不是描边: 出现它就说明名字又被解析成 focus 变体 + ring 工具类
		expect(RING).not.toContain("--un-ring-width")
	})
})
