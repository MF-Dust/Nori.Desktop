import {defineConfig} from "vitest/config"

export default defineConfig({
	test: {
		include: ["tests/**/*.test.ts"],
		environment: "jsdom",
		coverage: {
			provider: "v8",
			reporter: ["text", "json-summary", "lcov"],
			reportsDirectory: "coverage",
			include: ["src/**/*.ts"],
			exclude: ["src/**/*.d.ts"],
			// 隐藏音频宿主的当前覆盖率基线。WebAudio 主路径仍有未覆盖分支，门槛用于防止继续下滑。
			thresholds: {
				statements: 34,
				branches: 33,
				functions: 26,
				lines: 37,
			},
		},
	},
})
