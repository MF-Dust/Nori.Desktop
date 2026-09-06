/**
 * 聊天 Markdown 渲染
 *
 * 两层防线:
 *   1. markdown-it 关闭 html, 原始标签一律转义
 *   2. DOMPurify 白名单再过一遍, 挡掉事件属性与 javascript: 伪协议
 *
 * 链接一律不在 WebView 内导航 (会把控制台界面顶掉), 渲染成带 data-external 的锚点,
 * 由 ChatView 统一拦截 click 交给宿主 open_url。
 */
import MarkdownIt from "markdown-it"
import DOMPurify from "dompurify"

const MD = new MarkdownIt({
	html: false,
	linkify: true,
	breaks: true,
})

/** 允许的标签: 覆盖常见排版, 不含任何可执行/嵌入类标签 */
export const ALLOWED_TAGS = [
	"p", "br", "strong", "em", "del", "code", "pre", "blockquote",
	"ul", "ol", "li", "h1", "h2", "h3", "h4", "h5", "h6",
	"table", "thead", "tbody", "tr", "th", "td", "hr", "a", "span",
]

/** 允许的属性 */
export const ALLOWED_ATTR = ["href", "title", "class", "data-external"]

/**
 * 渲染并消毒 Markdown
 *
 * @param text 助手/用户消息原文
 */
export const renderMarkdown = (text: string): string => {
	const HTML = MD.render(text)
	const CLEAN = DOMPurify.sanitize(HTML, {
		ALLOWED_TAGS,
		ALLOWED_ATTR,
		ALLOW_DATA_ATTR: false,
		FORBID_TAGS: ["script", "style", "iframe", "object", "embed", "form", "input", "img", "svg"],
		FORBID_ATTR: ["style", "srcset", "src", "onerror", "onload", "onclick"],
	})
	// 外链标记: 交给宿主打开, 不在 WebView 内跳转
	return CLEAN.replace(/<a\s+href="(https?:\/\/[^"]+)"/g,
		(_match, url: string) => `<a href="${url}" data-external="1" rel="noopener noreferrer"`)
}
