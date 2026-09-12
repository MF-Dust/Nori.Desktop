import {afterEach, describe, expect, it} from "vitest"
import {invoke} from "../../src/services/host/invoke"
import {MockHost} from "../helpers/mockHost"

describe("Mock Host", () => {
	let mock: MockHost

	afterEach(() => {
		mock.restore()
	})

	it("按命令契约记录调用并返回结果", async () => {
		mock = new MockHost({
			clipboard_write_text: () => undefined,
			run_gc_collect: () => ({released_bytes: 42}),
		})
		mock.install()

		await invoke("clipboard_write_text", {text: "hello"})
		const RESULT = await invoke("run_gc_collect")

		expect(RESULT.released_bytes).toBe(42)
		expect(mock.calls).toEqual([
			{command: "clipboard_write_text", args: {text: "hello"}},
			{command: "run_gc_collect", args: undefined},
		])
	})

	it("模型列表探测的空密钥回退宿主已保存密钥", async () => {
		mock = new MockHost({llm_fetch_models: () => ["model-a"]})
		mock.install()

		await invoke("llm_fetch_models", {
			provider: "openai",
			baseUrl: "https://api.deepseek.com",
			apiKey: "",
		})
		await invoke("llm_fetch_models", {
			provider: "openai",
			baseUrl: "https://api.deepseek.com",
			apiKey: "temporary-key",
		})

		expect(mock.calls).toEqual([
			{
				command: "llm_fetch_models",
				args: {provider: "openai", baseUrl: "https://api.deepseek.com"},
			},
			{
				command: "llm_fetch_models",
				args: {provider: "openai", baseUrl: "https://api.deepseek.com", apiKey: "temporary-key"},
			},
		])
	})

})
