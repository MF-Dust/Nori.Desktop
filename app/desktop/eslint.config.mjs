import eslint from "@eslint/js"
import globals from "globals"
import tseslint from "typescript-eslint"

const SOURCE_FILES = ["src/**/*.ts"]

export default tseslint.config(
	{
		ignores: [
			"coverage/**",
			"dist/**",
			"node_modules/**",
			"scripts/**",
			"tests/**",
			"**/*.cjs",
			"**/*.js",
			"**/*.mjs",
			"**/*.d.ts",
		],
	},
	{
		linterOptions: {
			reportUnusedDisableDirectives: "off",
		},
	},
	{
		...eslint.configs.recommended,
		files: SOURCE_FILES,
	},
	...tseslint.configs.recommended.map(config => ({...config, files: SOURCE_FILES})),
	{
		files: SOURCE_FILES,
		languageOptions: {
			globals: globals.browser,
			parserOptions: {
				projectService: true,
				tsconfigRootDir: import.meta.dirname,
			},
		},
		rules: {
			// 只检查正确性，不改写现有命名和缩进风格。
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
)
