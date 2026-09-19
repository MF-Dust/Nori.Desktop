import {appendFile, readdir, stat} from "node:fs/promises"
import {relative, resolve} from "node:path"

const args = process.argv.slice(2)
const value = name => {
	const index = args.indexOf(name)
	return index >= 0 ? args[index + 1] : undefined
}

const targetArg = value("--path")
const label = value("--label") || "publish"
const maxMiB = Number(value("--max-mib") || "80")

if (!targetArg || !Number.isFinite(maxMiB) || maxMiB <= 0) {
	// eslint-disable-next-line -- Codacy误报：这是固定用法提示文本，不包含HTML变量
	console.error("usage: node check-package-size.mjs --path <file-or-dir> [--label name] [--max-mib 80]")
	process.exit(2)
}

const target = resolve(targetArg)
const files = []

const sizeOf = async path => {
	// eslint-disable-next-line security/detect-non-literal-fs-filename -- path是显式构建检查目标
	const info = await stat(path)
	if (info.isFile()) {
		files.push({path: relative(target, path) || path, size: info.size})
		return info.size
	}
	if (!info.isDirectory()) return 0
	let total = 0
	// eslint-disable-next-line security/detect-non-literal-fs-filename -- path是显式构建检查目标
	for (const entry of await readdir(path, {withFileTypes: true})) {
		if (entry.isSymbolicLink()) continue
		total += await sizeOf(resolve(path, entry.name))
	}
	return total
}

try {
	const bytes = await sizeOf(target)
	const mib = bytes / 1024 / 1024
	const summary = `${label}: ${mib.toFixed(2)} MiB / budget ${maxMiB.toFixed(2)} MiB`
	console.log(`[package-size] ${summary}`)
	if (process.env.GITHUB_STEP_SUMMARY) {
		// eslint-disable-next-line security/detect-non-literal-fs-filename -- GitHub提供的摘要文件路径
		await appendFile(process.env.GITHUB_STEP_SUMMARY, `- ${summary}\n`, "utf8")
	}
	if (mib > maxMiB) {
		console.error(`[package-size] 发布体积超过预算 ${maxMiB.toFixed(2)} MiB；请检查新增运行时、原生资源或重复产物。`)
		console.error("[package-size] 最大文件:")
		for (const file of files.sort((a, b) => b.size - a.size).slice(0, 12)) {
			console.error(`  ${(file.size / 1024 / 1024).toFixed(2).padStart(8)} MiB  ${file.path}`)
		}
		process.exit(1)
	}
} catch (error) {
	console.error(`[package-size] 无法读取 ${target}:`, error instanceof Error ? error.message : error)
	process.exit(2)
}
