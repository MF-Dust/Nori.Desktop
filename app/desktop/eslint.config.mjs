import eslint from "@eslint/js"
import globals from "globals"
import tseslint from "typescript-eslint"
import vue from "eslint-plugin-vue"

const SOURCE_FILES = ["src/**/*.{ts,vue}"]
const VUE_FILES = ["src/**/*.vue"]
const SOURCE_CONFIG = (files) => (config) => ({...config, files})
const SOURCE_RECOMMENDED_CONFIGS = [
	{...eslint.configs.recommended, files: SOURCE_FILES},
	...tseslint.configs.recommended.map(SOURCE_CONFIG(SOURCE_FILES)),
	...vue.configs["flat/essential"].map(SOURCE_CONFIG(VUE_FILES)),
]

export default tseslint.config(
	{
		ignores: [
			"coverage/**",
			"dist/**",
			"node_modules/**",
			"public/**",
			"scripts/**",
			"tests/**",
			"**/*.cjs",
			"**/*.js",
			"**/*.mjs",
			"**/*.d.ts",
			"components.d.ts",
		],
	},
	{
		linterOptions: {
			reportUnusedDisableDirectives: "off",
		},
	},
	...SOURCE_RECOMMENDED_CONFIGS,
	{
		files: SOURCE_FILES,
		languageOptions: {
			globals: globals.browser,
			parserOptions: {
				projectService: true,
				tsconfigRootDir: import.meta.dirname,
				extraFileExtensions: [".vue"],
			},
		},
		rules: {
			// 只守住正确性，不启用会改写现有命名与缩进约定的风格规则。
			"no-constant-condition": "error",
			"no-duplicate-case": "error",
			"no-empty": ["error", {"allowEmptyCatch": true}],
			"no-self-assign": "error",
			"no-unreachable": "error",
			"no-unsafe-finally": "error",
			"no-unreachable-loop": "error",
			"prefer-const": "off",
			"@typescript-eslint/await-thenable": "error",
			"@typescript-eslint/no-explicit-any": "off",
			"@typescript-eslint/no-empty-object-type": "off",
			"@typescript-eslint/no-floating-promises": ["error", {"ignoreIIFE": true}],
			"@typescript-eslint/no-misused-promises": ["error", {"checksVoidReturn": {"arguments": false, "attributes": false}}],
			"@typescript-eslint/no-unused-vars": ["error", {"argsIgnorePattern": "^_", "varsIgnorePattern": "^_"}],
		},
	},
	{
		files: ["src/**/*.vue"],
		languageOptions: {
			parserOptions: {
				parser: tseslint.parser,
			},
		},
		rules: {
			// essential 配置负责模板语法；这里补充最容易造成运行时错误的 Vue 规则。
			"vue/multi-word-component-names": "off",
			"vue/no-async-in-computed-properties": "error",
			"vue/no-ref-as-operand": "error",
			"vue/no-side-effects-in-computed-properties": "error",
			"vue/no-watch-after-await": "error",
		},
	},
)
