import {afterEach, describe, expect, it} from "vitest"
import {invoke} from "../../src/services/host/invoke"
import {MockHost} from "../helpers/mockHost"

describe("宿主命令调用", () => {
	let mock: MockHost

	afterEach(() => {
		mock.restore()
	})

	it("音频状态调用按 typed 命令契约透传", async () => {
		mock = new MockHost({audio_level: () => undefined})
		mock.install()

		await invoke("audio_level", {level: 0.42})

		expect(mock.calls).toEqual([{command: "audio_level", args: {level: 0.42}}])
	})

})
