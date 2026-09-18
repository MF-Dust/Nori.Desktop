import {invoke} from "../host/invoke"

/** 宿主枚举的卡片身份；页面消息无权改变所属插件。 */
export interface PluginWidgetInfo {
	pluginId: string
	title: string
	entry: string
}

interface WidgetBinding {
	widget: PluginWidgetInfo
	frame: HTMLIFrameElement
	active: boolean
	dispose: () => void
}

/** 获取当前有权显示的公开插件卡片。 */
export async function ListPluginWidgets(): Promise<PluginWidgetInfo[]> {
	return (await invoke("plugin_widgets")).widgets
}

/** 仅代理绑定卡片所属插件的动作，不转发通用宿主命令或事件。 */
export class PluginWidgetBridge {
	private readonly bindings = new Map<string, WidgetBinding>()

	constructor(private readonly reportError: (message: string, error?: unknown) => void) {}

	/** 绑定实际 iframe 窗口，重复渲染不使正在执行的动作失效。 */
	Bind(widget: PluginWidgetInfo, frame: HTMLIFrameElement | null): void {
		if (!frame) {
			this.unbind(widget.pluginId)
			return
		}
		const CURRENT = this.bindings.get(widget.pluginId)
		if (CURRENT?.frame === frame && CURRENT.widget.entry === widget.entry) return
		this.unbind(widget.pluginId)
		let loaded = false
		const BINDING: WidgetBinding = {
			widget: {...widget}, frame, active: true,
			dispose: () => { frame.removeEventListener("load", onLoad) },
		}
		const onLoad = (): void => {
			// WindowProxy 跨导航仍是同一对象；再次加载后必须撤销旧文档权限和回包。
			if (loaded) BINDING.active = false
			loaded = true
		}
		frame.addEventListener("load", onLoad)
		this.bindings.set(widget.pluginId, BINDING)
	}

	/** 列表刷新时立即撤销已移除或更换入口的卡片。 */
	Retain(widgets: PluginWidgetInfo[]): void {
		for (const [pluginId, binding] of this.bindings) {
			if (!widgets.some(widget => widget.pluginId === pluginId && widget.entry === binding.widget.entry)) {
				this.unbind(pluginId)
			}
		}
	}

	/** 卸载后废弃所有窗口身份和未完成请求的回包。 */
	Clear(): void {
		for (const pluginId of this.bindings.keys()) this.unbind(pluginId)
	}

	private unbind(pluginId: string): void {
		this.bindings.get(pluginId)?.dispose()
		this.bindings.delete(pluginId)
	}

	/** opaque-origin 卡片只接受来自登记窗口的 SDK 动作消息。 */
	async Handle(event: MessageEvent): Promise<void> {
		if (event.origin !== "null" || !event.source) return
		const BINDING = [...this.bindings.values()].find(binding =>
			binding.active && binding.frame.isConnected && binding.frame.contentWindow === event.source,
		)
		if (!BINDING) return
		const SOURCE = BINDING.frame.contentWindow
		if (!SOURCE || SOURCE !== event.source) return
		const DATA: unknown = event.data
		if (!DATA || typeof DATA !== "object" || Array.isArray(DATA)) return
		const CALL = DATA as Record<string, unknown>
		if (CALL.source !== "nori-plugin-widget" || !Number.isSafeInteger(CALL.requestId) || (CALL.requestId as number) < 0) return
		const isCurrent = (): boolean => this.bindings.get(BINDING.widget.pluginId) === BINDING
			&& BINDING.active && BINDING.frame.isConnected && BINDING.frame.contentWindow === SOURCE
		const reply = (result: unknown, error?: string): void => {
			if (!isCurrent()) return
			// sandbox 的 opaque origin 只能使用 *；目标始终是已验证的单个窗口。
			SOURCE.postMessage({source: "nori-plugin-widget-host", requestId: CALL.requestId, result, error}, "*")
		}
		try {
			if (CALL.pluginId !== undefined && CALL.pluginId !== BINDING.widget.pluginId) {
				throw new Error("插件卡片不能调用其他插件的动作。")
			}
			if ("cmd" in CALL || "kind" in CALL || "command" in CALL || "event" in CALL
				|| typeof CALL.actionId !== "string" || !CALL.actionId.trim() || CALL.actionId.length > 256
				|| (CALL.args != null && (typeof CALL.args !== "object" || Array.isArray(CALL.args)))) {
				throw new Error("插件卡片只允许发送有效的插件动作请求。")
			}
			const RESULT = await invoke("plugin_action", {
				pluginId: BINDING.widget.pluginId,
				actionId: CALL.actionId,
				args: CALL.args == null ? undefined : CALL.args as Record<string, unknown>,
			})
			reply(RESULT)
		} catch (error) {
			if (!isCurrent()) return
			const MESSAGE = error instanceof Error ? error.message : String(error)
			reply(null, MESSAGE)
			this.reportError("插件卡片动作执行失败", error)
		}
	}
}
