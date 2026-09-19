import {createI18n} from "vue-i18n"
import useLanguages from "./useLanguages.ts"
import {mergePluginMessages} from "./pluginMessages"

/**
 * 语言类型
 */
export type LanguageType = string

interface LocaleModule {
	default: unknown
}

const MESSAGES = new Map(Object.entries(import.meta.glob<LocaleModule>("./locales/*.ts")))

/**
 * 获取系统语言, 映射到可用的 locale 文件名 (宿主不可用时回退浏览器语言)
 */
const getSystemLanguage = (): string => {
	let lang = navigator.language || "zh-CN"
	const KEY = `./locales/${lang}.ts`
	if (MESSAGES.has(KEY)) return lang
	const PREFIX = lang.split("-")[0]
	const PREFIX_KEY = `./locales/${PREFIX}.ts`
	if (MESSAGES.has(PREFIX_KEY)) return PREFIX
	return "zh-CN"
}

/**
 * 国际化实例
 */
export const i18n = createI18n({
	legacy: false,
	locale: "zh-CN",
	fallbackLocale: "zh-CN",
	messages: {}
})

const useLanguage = {
	useLang: {} as ReturnType<typeof useLanguages>,
	/**
	 * 初始化: 优先使用后端快照中的持久化语言, 缺省回退系统语言
	 */
	async init(savedLanguage?: string): Promise<void> {
		const LANG = savedLanguage && MESSAGES.has(`./locales/${savedLanguage}.ts`) ? savedLanguage : getSystemLanguage()
		await this.setLanguage(LANG)
	},
	/**
	 * 切换当前语言包 (仅本地生效; 持久化由设置页经 runtime 提交后端)
	 */
	async setLanguage(lang: LanguageType): Promise<void> {
		if (!i18n.global.availableLocales.includes(lang)) {
			const LOADER = this.getLoader(lang)
			if (LOADER) {
				const MODULE = await LOADER()
				i18n.global.setLocaleMessage(lang, mergePluginMessages(lang, MODULE.default))
			}
		}
		i18n.global.locale.value = lang
		this.useLang = useLanguages()
	},
	/**
	 * 获取可用语言列表
	 */
	getLanguages(): string[] {
		return [...MESSAGES.keys()].map((key) => key.replace("./locales/", "").replace(".ts", ""))
	},
	/**
	 * 获取 loader
	 */
	getLoader(lang: string) {
		const KEY = `./locales/${lang}.ts`
		return MESSAGES.get(KEY)
	}
}

export default useLanguage
