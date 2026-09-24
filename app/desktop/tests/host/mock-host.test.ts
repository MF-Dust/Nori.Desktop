import {afterEach, describe, expect, it} from "vitest"
import {invoke} from "../../src/services/host/invoke"
import {MockHost} from "../helpers/mockHost"

describe("宿主命令调用", () => {
	let mock: MockHost

	afterEach(() => {
		mock.restore()
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
