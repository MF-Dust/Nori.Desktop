import {mkdirSync, rmSync} from "node:fs"
import {resolve} from "node:path"
import {spawnSync} from "node:child_process"

const APP_ROOT = resolve(import.meta.dirname, "..")
const COVERAGE_ROOT = resolve(APP_ROOT, "artifacts/dotnet-coverage")
const SETTINGS_FILE = resolve(APP_ROOT, "scripts/dotnet-coverage.runsettings")
const PROJECTS = [
	{
		name: "Nori.Core",
		path: "Nori.Core.Tests/Nori.Core.Tests.csproj",
	},
	{
		name: "Nori.Desktop",
		path: "Nori.Desktop.Tests/Nori.Desktop.Tests.csproj",
	},
	{
		name: "Nori.PluginRuntime",
		path: "Nori.PluginRuntime.Tests/Nori.PluginRuntime.Tests.csproj",
	},
]
const OPTIONS = new Set(process.argv.slice(2).filter(option => option !== "--"))
const SUPPORTED_OPTIONS = new Set(["--no-build", "--no-restore"])
const UNKNOWN_OPTIONS = [...OPTIONS].filter(option => !SUPPORTED_OPTIONS.has(option))

if (UNKNOWN_OPTIONS.length > 0) {
	console.error(`无法识别 .NET 覆盖率参数: ${UNKNOWN_OPTIONS.join(", ")}`)
	console.error("用法: pnpm coverage:dotnet [--no-build] [--no-restore]")
	process.exit(2)
}

const DOTNET_OPTIONS = [...SUPPORTED_OPTIONS].filter(option => OPTIONS.has(option))

// 每次从干净目录开始，保证本地与 CI 都能得到可定位的 Cobertura 产物。
rmSync(COVERAGE_ROOT, {recursive: true, force: true})
// eslint-disable-next-line security/detect-non-literal-fs-filename -- 输出目录由脚本固定计算
mkdirSync(COVERAGE_ROOT, {recursive: true})

for (const PROJECT of PROJECTS) {
	const RESULT_DIR = resolve(COVERAGE_ROOT, PROJECT.name)
	const TEST_ARGS = [
		"test",
		PROJECT.path,
		"--configuration",
		"Release",
		"--collect",
		"XPlat Code Coverage",
		"--settings",
		SETTINGS_FILE,
		"--results-directory",
		RESULT_DIR,
		"-m:1",
		...DOTNET_OPTIONS,
	]
	const RESULT = spawnSync("dotnet", TEST_ARGS, {cwd: APP_ROOT, stdio: "inherit"})

	if (RESULT.error) {
		console.error(`.NET 覆盖率命令执行失败 (${PROJECT.name}): ${RESULT.error.message}`)
		process.exit(1)
	}
	if (RESULT.status !== 0) {
		console.error(`.NET 覆盖率测试失败 (${PROJECT.name})`)
		process.exit(RESULT.status ?? 1)
	}
}

console.log(`.NET 覆盖率产物已写入 ${COVERAGE_ROOT}`)
