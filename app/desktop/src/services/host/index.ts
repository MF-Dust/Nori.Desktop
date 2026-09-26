/**
 * 宿主桥接层
 *
 * 上层代码只认这里导出的接口, 不关心底层是 NativeWebView 还是纯浏览器调试。
 * 底层实现在 index.html 的引导脚本里 (window.__nori), 必须在应用代码之前同步就位。
 */

import type {BridgeCommandArgs, BridgeCommandName, BridgeCommandResult} from "./commands"
export type {BridgeCommandArgs, BridgeCommandMap, BridgeCommandName, BridgeCommandResult} from "./commands"

/**
 * 引导脚本注入的宿主对象
 */
export interface NoriHost {
	label: string | null
	invoke: <K extends BridgeCommandName>(cmd: K, args?: BridgeCommandArgs<K>) => Promise<BridgeCommandResult<K>>
	listen: (event: string, handler: (message: {payload: unknown}) => void) => () => void
	dispatch: (raw: string) => void
}

declare global {
	interface Window {
		__nori?: NoriHost
	}
}

/**
 * 取宿主对象, 纯浏览器调试 (未经宿主打开) 时返回 null
 */
export const host = (): NoriHost | null => window.__nori ?? null

export {}
