import type {NoriHost} from "../../src/services/host"
import type {BridgeCommandArgs, BridgeCommandName, BridgeCommandResult} from "../../src/services/host/commands"

type Handler<K extends BridgeCommandName> = (args: BridgeCommandArgs<K>) => BridgeCommandResult<K> | Promise<BridgeCommandResult<K>>
type HandlerMap = Partial<{[K in BridgeCommandName]: Handler<K>}>

/** Vitest 共用宿主替身；协议仍通过 NoriHost 的类型契约调用。 */
export class MockHost {
	readonly calls: Array<{command: BridgeCommandName; args: unknown}> = []
	private readonly handlers: HandlerMap
	private readonly listeners = new Map<string, Set<(message: {payload: unknown}) => void>>()
	private previous: NoriHost | undefined
	readonly host: NoriHost

	constructor(handlers: HandlerMap = {}) {
		this.handlers = handlers
		this.host = {
			label: "pet",
			invoke: <K extends BridgeCommandName>(command: K, args?: BridgeCommandArgs<K>) => {
				this.calls.push({command, args})
				const HANDLER = this.handlers[command] as Handler<K> | undefined // nosemgrep
				if (!HANDLER) return Promise.reject(new Error(`未配置 Mock Host 命令: ${command}`))
				return Promise.resolve(HANDLER((args ?? undefined) as BridgeCommandArgs<K>))
			},
			listen: (event, handler) => {
				const SET = this.listeners.get(event) ?? new Set()
				SET.add(handler)
				this.listeners.set(event, SET)
				return () => SET.delete(handler)
			},
			dispatch: raw => {
				const MESSAGE = JSON.parse(raw) as {kind?: string; event?: string; payload?: unknown}
				if (MESSAGE.kind !== "event" || !MESSAGE.event) return
				for (const HANDLER of this.listeners.get(MESSAGE.event) ?? []) HANDLER({payload: MESSAGE.payload})
			},
		}
	}

	install(): void {
		this.previous = window.__nori
		window.__nori = this.host
	}

	restore(): void {
		window.__nori = this.previous
		this.previous = undefined
	}
}
