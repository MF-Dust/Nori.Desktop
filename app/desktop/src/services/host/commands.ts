import type {LogEventId, LogLevel} from "../runtime/logging"

export type EmptyCommandArgs = undefined
/** 无业务结果的宿主命令保持 Promise<void>，不宣称宿主返回 undefined。 */
type EmptyCommandResult = ReturnType<() => void>

/** 音频宿主仅使用下列桥接命令；宿主仍会校验命令参数和来源窗口。 */
export interface BridgeCommandMap {
	write_log: {args: {level: LogLevel; message: string; eventId?: LogEventId; errorType?: string; suppressedCount?: number}; result: EmptyCommandResult}
	audio_host_ready: {args: EmptyCommandArgs; result: EmptyCommandResult}
	audio_playback_finished: {args: {token: string; error?: string}; result: EmptyCommandResult}
	audio_level: {args: {level: number}; result: EmptyCommandResult}
	audio_record_ready: {args: {token: string}; result: EmptyCommandResult}
	audio_record_failed: {args: {token: string; error?: string}; result: EmptyCommandResult}
	audio_upload_failed: {args: {token: string; error?: string}; result: EmptyCommandResult}
}

export type BridgeCommandName = keyof BridgeCommandMap
export type BridgeCommandArgs<K extends BridgeCommandName> = BridgeCommandMap[K]["args"]
export type BridgeCommandResult<K extends BridgeCommandName> = BridgeCommandMap[K]["result"]
