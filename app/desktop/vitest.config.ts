import {defineConfig} from "vitest/config"
import vue from "@vitejs/plugin-vue"

export default defineConfig({
	plugins: [vue()],
	test: {
		include: ["tests/**/*.test.ts"],
		environment: "jsdom",
		coverage: {
			provider: "v8",
			reporter: ["text", "json-summary", "lcov"],
			reportsDirectory: "coverage",
			include: ["src/**/*.ts", "src/**/*.vue"],
			exclude: [
				"src/**/*.d.ts",
				// 原生窗口迁移后不再由生产入口加载的 Vue 兼容层；保留源码与测试，避免把遗留代码伪装成当前运行时覆盖。
				"src/services/feedback/**",
				"src/services/i18n/**",
				"src/services/plugins/**",
				"src/services/runtime/index.ts",
				"src/services/runtime/types.ts",
				"src/services/telemetry/**",
			],
			// 当前受维护的原生宿主前端基线 (14 个测试文件 / 59 个用例): statements 40.96%、branches 38.60%、functions 36.36%、lines 44.20%。阈值为该范围预留小幅回归空间。
			thresholds: {
				statements: 40,
				branches: 35,
				functions: 30,
				lines: 40,
			},
		},
	},
})
