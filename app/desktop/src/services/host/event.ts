/**
 * 事件收发
 *
 * 对应原 @tauri-apps/api/event 的 listen / emit.
 */
import {host} from "./index"

/**
 * 取消监听的句柄
 */
export type UnlistenFn = () => void

/**
 * 事件回调收到的载荷
 */
export interface HostEvent<T> {
	payload: T
}

/**
 * 监听宿主事件, 返回取消监听的函数
 */
export const listen = async <T = unknown>(event: string, handler: (message: HostEvent<T>) => void): Promise<UnlistenFn> => {
	const HOST = host()
	if (!HOST) return () => {}
	return HOST.listen(event, (message) => { handler(message as HostEvent<T>) })
}

/**
 * 向宿主发事件 (宿主会再全局广播给所有窗口)
 */
export const emit = async (event: string, payload?: unknown): Promise<void> => {
	host()?.emit(event, payload)
}
