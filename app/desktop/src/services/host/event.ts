/**
 * 宿主事件监听
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
