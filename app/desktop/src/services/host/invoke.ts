/**
 * 命令调用
 *
 * 对应原 @tauri-apps/api/core 的 invoke.
 * 宿主不存在时 (纯 vite 调试) 一律 reject, 上层沿用原来的 try/catch 兜底逻辑.
 */
import {host} from "./index"
import type {BridgeCommandArgs, BridgeCommandName, BridgeCommandResult} from "./commands"

/**
 * 调用宿主命令。
 * 未显式指定返回泛型时, 命令名会同时推导精确参数和返回值；旧调用可显式保留返回类型。
 * @param cmd 命令名 (snake_case, 与宿主 BridgeCommands 一致)
 * @param args 命令参数 (camelCase)
 */
export const invoke = async <K extends BridgeCommandName>(
	cmd: K,
	args?: BridgeCommandArgs<K>,
): Promise<BridgeCommandResult<K>> => {
	const HOST = host()
	if (!HOST) throw new Error(`宿主不可用, 无法调用命令: ${cmd}`)
	return HOST.invoke(cmd, args)
}
