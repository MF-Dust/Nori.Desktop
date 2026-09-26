import {readdirSync, readFileSync} from "node:fs"
import {join, resolve} from "node:path"
import {describe, expect, it} from "vitest"

const ROOT = resolve(__dirname, "../..")

const readSource = (path: string): string => readFileSync(join(ROOT, path), "utf8").replace(/\r\n?/g, "\n")

const BRIDGE_COMMANDS_SOURCE = readSource("Nori.Desktop/Bridge/BridgeCommands.cs")
const BRIDGE_ROUTER_SOURCE = readSource("Nori.Desktop/Bridge/BridgeCommandRouter.cs")
const PLUGIN_MANAGEMENT_SOURCE = readSource("Nori.PluginRuntime/PluginManagementCommands.cs")
const PLUGIN_BRIDGE_SOURCE = readSource("Nori.PluginRuntime/PluginBridge.cs")
const TYPED_COMMANDS_SOURCE = readSource("src/services/host/commands.ts")

type ScanMode = "code" | "string" | "verbatim" | "raw" | "char" | "lineComment" | "blockComment"

/** 读取一个 C# 字符串，并跳过其中的花括号和注释标记。 */
const readString = (source: string, start: number): {value: string; end: number} => {
	if (source.startsWith('"""', start)) {
		const END = source.indexOf('"""', start + 3)
		if (END < 0) throw new Error("C# 原始字符串没有闭合")
		return {value: source.slice(start + 3, END), end: END + 3}
	}

	const VERBATIM = source[start - 1] === "@"
	let value = ""
	for (let INDEX = start + 1; INDEX < source.length; INDEX++) {
		const CHAR = source[INDEX]
		if (VERBATIM) {
			if (CHAR === '"') {
				if (source[INDEX + 1] === '"') {
					value += '"'
					INDEX++
					continue
				}
				return {value, end: INDEX + 1}
			}
			value += CHAR
			continue
		}
		if (CHAR === "\\") {
			const NEXT = source[INDEX + 1]
			if (NEXT === undefined) throw new Error("C# 字符串转义不完整")
			value += NEXT
			INDEX++
			continue
		}
		if (CHAR === '"') return {value, end: INDEX + 1}
		value += CHAR
	}
	throw new Error("C# 字符串没有闭合")
}

/** 取指定 switch 表达式的花括号内容，避免把文件中其它 switch 当成命令分支。 */
const readSwitchBody = (source: string, marker: string): string => {
	const MARKER_INDEX = source.indexOf(marker)
	if (MARKER_INDEX < 0) throw new Error(`未找到 switch 标记: ${marker}`)
	const OPEN_INDEX = source.indexOf("{", MARKER_INDEX + marker.length)
	if (OPEN_INDEX < 0) throw new Error(`未找到 switch 花括号: ${marker}`)

	let depth = 1
	let mode: ScanMode = "code"
	for (let INDEX = OPEN_INDEX + 1; INDEX < source.length; INDEX++) {
		const CHAR = source[INDEX]
		const NEXT = source[INDEX + 1]
		if (mode === "lineComment") {
			if (CHAR === "\n") mode = "code"
			continue
		}
		if (mode === "blockComment") {
			if (CHAR === "*" && NEXT === "/") {
				mode = "code"
				INDEX++
			}
			continue
		}
		if (mode === "string") {
			if (CHAR === "\\") INDEX++
			else if (CHAR === '"') mode = "code"
			continue
		}
		if (mode === "verbatim") {
			if (CHAR === '"') {
				if (NEXT === '"') INDEX++
				else mode = "code"
			}
			continue
		}
		if (mode === "raw") {
			if (source.startsWith('"""', INDEX)) {
				mode = "code"
				INDEX += 2
			}
			continue
		}
		if (mode === "char") {
			if (CHAR === "\\") INDEX++
			else if (CHAR === "'") mode = "code"
			continue
		}

		if (CHAR === "/" && NEXT === "/") {
			mode = "lineComment"
			INDEX++
			continue
		}
		if (CHAR === "/" && NEXT === "*") {
			mode = "blockComment"
			INDEX++
			continue
		}
		if (CHAR === '"') {
			if (source.startsWith('"""', INDEX)) {
				mode = "raw"
				INDEX += 2
			} else if (source[INDEX - 1] === "@") {
				mode = "verbatim"
			} else {
				mode = "string"
			}
			continue
		}
		if (CHAR === "'") {
			mode = "char"
			continue
		}
		if (CHAR === "{") {
			depth++
			continue
		}
		if (CHAR === "}") {
			depth--
			if (depth === 0) return source.slice(OPEN_INDEX + 1, INDEX)
		}
	}
	throw new Error(`switch 没有闭合: ${marker}`)
}

/** 只提取 switch 表达式当前层的字符串命令分支，嵌套 switch 会被深度过滤。 */
const extractSwitchCommands = (source: string, marker: string): string[] => {
	const BODY = readSwitchBody(source, marker)
	const COMMANDS: string[] = []
	let depth = 0
	let mode: ScanMode = "code"
	for (let INDEX = 0; INDEX < BODY.length; INDEX++) {
		const CHAR = BODY[INDEX]
		const NEXT = BODY[INDEX + 1]
		if (mode === "lineComment") {
			if (CHAR === "\n") mode = "code"
			continue
		}
		if (mode === "blockComment") {
			if (CHAR === "*" && NEXT === "/") {
				mode = "code"
				INDEX++
			}
			continue
		}
		if (mode !== "code") {
			if (mode === "string" || mode === "char") {
				if (CHAR === "\\") INDEX++
				else if ((mode === "string" && CHAR === '"') || (mode === "char" && CHAR === "'")) mode = "code"
			} else if (mode === "verbatim") {
				if (CHAR === '"') {
					if (NEXT === '"') INDEX++
					else mode = "code"
				}
			} else if (mode === "raw" && BODY.startsWith('"""', INDEX)) {
				mode = "code"
				INDEX += 2
			}
			continue
		}

		if (CHAR === "/" && NEXT === "/") {
			mode = "lineComment"
			INDEX++
			continue
		}
		if (CHAR === "/" && NEXT === "*") {
			mode = "blockComment"
			INDEX++
			continue
		}
		if (CHAR === "{") {
			depth++
			continue
		}
		if (CHAR === "}") {
			depth--
			continue
		}
		if (CHAR === '"') {
			const TOKEN = readString(BODY, INDEX)
			const AFTER = TOKEN.end + (BODY.slice(TOKEN.end).match(/^\s*/) ?? [""])[0].length
			if (depth === 0 && /^[a-z][a-z0-9_]*$/.test(TOKEN.value) && BODY.slice(AFTER, AFTER + 2) === "=>")
				COMMANDS.push(TOKEN.value)
			INDEX = TOKEN.end - 1
			continue
		}
		if (CHAR === "'") {
			mode = "char"
			continue
		}
	}
	return COMMANDS
}

const extractTypedCommands = (source: string): string[] => {
	const START = source.indexOf("export interface BridgeCommandMap")
	if (START < 0) throw new Error("未找到 BridgeCommandMap")
	const END = source.indexOf("\n}", START)
	if (END < 0) throw new Error("BridgeCommandMap 没有闭合")
	return [...source.slice(START, END).matchAll(/^\t([a-z][a-z0-9_]*)\s*:/gm)].map(MATCH => MATCH[1])
}

const sorted = (commands: string[]): string[] => [...new Set(commands)].sort()

/** 提取 FrozenSet 初始化数组里的命令名。 */
const extractFrozenSetCommands = (source: string, marker: string): string[] => {
	const START = source.indexOf(marker)
	if (START < 0) throw new Error(`未找到集合标记: ${marker}`)
	const OPEN = source.indexOf("{", START)
	const CLOSE = source.indexOf("}.ToFrozenSet", OPEN)
	if (OPEN < 0 || CLOSE < 0) throw new Error(`未找到 FrozenSet: ${marker}`)
	return [...source.slice(OPEN, CLOSE).matchAll(/"([a-z][a-z0-9_]*)"/g)].map(MATCH => MATCH[1])
}

/** 递归列出目录下的 C# 源文件。 */
const listCsFiles = (directory: string): string[] => {
	const FILES: string[] = []
	for (const ENTRY of readdirSync(directory, {withFileTypes: true})) {
		const PATH = join(directory, ENTRY.name)
		if (ENTRY.isDirectory()) FILES.push(...listCsFiles(PATH))
		else if (ENTRY.name.endsWith(".cs")) FILES.push(PATH)
	}
	return FILES
}

/** 提取 ExecuteAsync( 后紧跟的命令字符串，允许参数跨行。 */
const extractExecuteAsyncCommands = (source: string): string[] => {
	const COMMANDS: string[] = []
	let mode: ScanMode = "code"
	for (let INDEX = 0; INDEX < source.length; INDEX++) {
		const CHAR = source[INDEX]
		const NEXT = source[INDEX + 1]
		if (mode === "lineComment") {
			if (CHAR === "\n") mode = "code"
			continue
		}
		if (mode === "blockComment") {
			if (CHAR === "*" && NEXT === "/") {
				mode = "code"
				INDEX++
			}
			continue
		}
		if (mode !== "code") {
			if (mode === "string" || mode === "char") {
				if (CHAR === "\\") INDEX++
				else if ((mode === "string" && CHAR === '"') || (mode === "char" && CHAR === "'")) mode = "code"
			} else if (mode === "verbatim") {
				if (CHAR === '"') {
					if (NEXT === '"') INDEX++
					else mode = "code"
				}
			} else if (mode === "raw" && source.startsWith('"""', INDEX)) {
				mode = "code"
				INDEX += 2
			}
			continue
		}

		if (CHAR === "/" && NEXT === "/") {
			mode = "lineComment"
			INDEX++
			continue
		}
		if (CHAR === "/" && NEXT === "*") {
			mode = "blockComment"
			INDEX++
			continue
		}
		if (source.startsWith("ExecuteAsync(", INDEX) && (INDEX === 0 || !/[A-Za-z0-9_]/.test(source[INDEX - 1]))) {
			let CURSOR = INDEX + "ExecuteAsync(".length
			while (CURSOR < source.length && /\s/.test(source[CURSOR])) CURSOR++
			if (source[CURSOR] === '"') {
				const TOKEN = readString(source, CURSOR)
				if (/^[a-z][a-z0-9_]*$/.test(TOKEN.value)) COMMANDS.push(TOKEN.value)
				INDEX = TOKEN.end - 1
				continue
			}
		}
		if (CHAR === '"') {
			if (source.startsWith('"""', INDEX)) {
				mode = "raw"
				INDEX += 2
			} else if (source[INDEX - 1] === "@") {
				mode = "verbatim"
			} else {
				mode = "string"
			}
			continue
		}
		if (CHAR === "'") mode = "char"
	}
	return COMMANDS
}

const SETTINGS_SERVICE_SOURCE = readSource("Nori.Desktop/Settings/SettingsService.cs")
const SETTINGS_ALLOWED = extractFrozenSetCommands(SETTINGS_SERVICE_SOURCE, "AllowedCommands = new[]")
const SETTINGS_EXECUTED = listCsFiles(join(ROOT, "Nori.Desktop/Settings")).flatMap(PATH =>
	extractExecuteAsyncCommands(readFileSync(PATH, "utf8").replace(/\r\n?/g, "\n")))

const HOST_COMMANDS = extractSwitchCommands(BRIDGE_COMMANDS_SOURCE, "object? result = cmd switch")
const PLUGIN_MANAGEMENT_COMMANDS = extractSwitchCommands(PLUGIN_MANAGEMENT_SOURCE, "return command switch")
const PLUGIN_PAGE_COMMANDS = extractSwitchCommands(PLUGIN_BRIDGE_SOURCE, "return command switch")
const TYPED_COMMANDS = extractTypedCommands(TYPED_COMMANDS_SOURCE)
const AUDIO_HOST_COMMANDS = [
	"audio_host_ready",
	"audio_level",
	"audio_playback_finished",
	"audio_record_failed",
	"audio_record_ready",
	"audio_upload_failed",
	"write_log",
]

describe("Bridge 跨语言命令契约", () => {
	it("设置页 ExecuteAsync 字面量命令都在设置白名单内", () => {
		expect(SETTINGS_EXECUTED.length).toBeGreaterThan(0)
		const MISSING = sorted(SETTINGS_EXECUTED).filter(COMMAND => !SETTINGS_ALLOWED.includes(COMMAND))
		expect(MISSING).toEqual([])
	})

	it("音频宿主 typed map 只公开实际调用并已注册的命令", () => {
		expect(sorted(TYPED_COMMANDS)).toEqual(sorted(AUDIO_HOST_COMMANDS))
		for (const command of TYPED_COMMANDS) expect(HOST_COMMANDS).toContain(command)
		expect(BRIDGE_ROUTER_SOURCE).toContain('"audio_host_ready"')
		expect(BRIDGE_ROUTER_SOURCE).toContain('"audio_upload_failed"')
		expect(BRIDGE_ROUTER_SOURCE).toContain('"write_log"')
	})

	it("插件管理命令继续走独立路由", () => {
		expect(sorted(PLUGIN_MANAGEMENT_COMMANDS)).toEqual(sorted([
			"plugin_list",
			"plugin_install_local",
			"plugin_enable",
			"plugin_disable",
			"plugin_uninstall",
		]))
		expect(BRIDGE_COMMANDS_SOURCE).not.toContain('"plugin_list" =>')
		expect(BRIDGE_ROUTER_SOURCE).toContain('command.StartsWith("plugin_", StringComparison.Ordinal)')
		expect(BRIDGE_ROUTER_SOURCE).toContain("runtime.InvokeManagementAsync")
	})

	it("插件 WebView RPC 仍保留独立页面白名单", () => {
		expect(sorted(PLUGIN_PAGE_COMMANDS)).toEqual([
			"ping",
			"plugin_get_capabilities",
			"plugin_get_info",
			"window_close",
			"window_get_info",
		])
		expect(PLUGIN_BRIDGE_SOURCE).toContain("plugin_get_capabilities")
		expect(PLUGIN_BRIDGE_SOURCE).toContain("plugin_get_info")
		expect(PLUGIN_BRIDGE_SOURCE).toContain("window_get_info")
	})
})
