import fs from "node:fs"
import path from "node:path"

const ROOT = process.cwd()
const FIRST_PARTY_ROOTS = [
	"Nori.Core",
	"Nori.Core.Tests",
	"Nori.Desktop",
	"Nori.Desktop.Tests",
	"src",
	"tests",
]
const TEXT_EXTENSIONS = new Set([
	".cs",
	".csproj",
	".props",
	".targets",
	".ts",
	".tsx",
	".vue",
	".less",
	".json",
	".md",
])
const EXCLUDED_PARTS = new Set([
	"bin",
	"obj",
	"dist",
	"node_modules",
	"coverage",
	".git",
])
const GENERATED_FILE_NAMES = new Set(["components.d.ts"])
const MARKER_PATTERN = /\b(?:TODO|FIXME)\b/gi

const SHOULD_SKIP = (filePath) => {
	const relative = path.relative(ROOT, filePath)
	const parts = relative.split(path.sep)
	if (parts.some((part) => EXCLUDED_PARTS.has(part))) return true
	if (GENERATED_FILE_NAMES.has(path.basename(filePath))) return true
	if (/\.generated\.|\.g\./i.test(path.basename(filePath))) return true
	return !TEXT_EXTENSIONS.has(path.extname(filePath).toLowerCase())
}

const WALK = (directory) => {
	const files = []
	// Codacy误报：directory只由固定first-party根目录递归生成。
	// eslint-disable-next-line security/detect-non-literal-fs-filename -- 受控源码扫描路径
	for (const entry of fs.readdirSync(directory, {withFileTypes: true})) { // nosemgrep
		const entryPath = path.join(directory, entry.name)
		if (entry.isDirectory()) {
			if (!EXCLUDED_PARTS.has(entry.name)) files.push(...WALK(entryPath))
		} else if (!SHOULD_SKIP(entryPath)) {
			files.push(entryPath)
		}
	}
	return files
}

const matches = []
for (const relativeRoot of FIRST_PARTY_ROOTS) {
	const absoluteRoot = path.join(ROOT, relativeRoot)
	// eslint-disable-next-line security/detect-non-literal-fs-filename -- absoluteRoot来自固定根目录清单
	if (!fs.existsSync(absoluteRoot)) continue // nosemgrep
	for (const filePath of WALK(absoluteRoot)) {
		// eslint-disable-next-line security/detect-non-literal-fs-filename -- filePath来自受控源码遍历
		const lines = fs.readFileSync(filePath, "utf8").split(/\r?\n/) // nosemgrep
		lines.forEach((line, index) => {
			if (MARKER_PATTERN.test(line)) {
				matches.push(`${path.relative(ROOT, filePath)}:${index + 1}: ${line.trim()}`)
			}
			MARKER_PATTERN.lastIndex = 0
		})
	}
}

if (matches.length > 0) {
	console.error("发现未关闭的 first-party TODO/FIXME:")
	for (const match of matches) console.error(`- ${match}`)
	process.exitCode = 1
} else {
	console.log("first-party TODO/FIXME 扫描通过 (vendor/generated/docs backlog 已排除)")
}
