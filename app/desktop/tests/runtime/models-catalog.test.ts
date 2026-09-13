import {readFileSync} from "node:fs"
import {resolve} from "node:path"
import {describe, expect, it} from "vitest"
import {MODEL_LIST} from "../../src/services/live2d/models"

const SRC = resolve(__dirname, "../../src")

describe("原生模型迁移后的共享角色目录", () => {
	it("保留首次运行和主页使用的两种角色及缩略图", () => {
		expect(MODEL_LIST.map(model => [model.id, model.name])).toEqual([
			["arg-nori", "ARG Nori"],
			["nori", "Nori"],
		])
		for (const MODEL of MODEL_LIST) expect(MODEL.thumb).toMatch(/\.webp/)
	})

	it.each(["components/firstRun/ModelSelect.vue", "components/home/HomePanel.vue", "views/Main.vue"])("%s 仍复用共享角色目录", path => {
		expect(readFileSync(resolve(SRC, path), "utf8")).toContain("services/live2d/models")
	})

	it("首次运行的本地导入和完成命令保持不变", () => {
		const RUNTIME = readFileSync(resolve(SRC, "services/runtime/index.ts"), "utf8")
		expect(RUNTIME).toContain("invoke(\"model_import_local\", {resourceType: \"live2d\", sourceKind})")
		expect(RUNTIME).toContain("invoke(\"complete_first_run\", {modelId, telemetryEnabled})")
	})
})
