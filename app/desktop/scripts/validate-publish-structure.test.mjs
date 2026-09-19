import assert from "node:assert/strict"
import {chmodSync, mkdtempSync, mkdirSync, writeFileSync} from "node:fs"
import {tmpdir} from "node:os"
import {join} from "node:path"
import {spawnSync} from "node:child_process"
import {validateProductVersion, validateRevision} from "./version-validation.mjs"

validateProductVersion("Dev")
validateProductVersion("v1.2.3-Nori+abcdef0")
validateRevision("0")
assert.throws(() => validateProductVersion("01.2.3-Nori"))
assert.throws(() => validateProductVersion("1.2.3-bad/name"))
assert.throws(() => validateRevision("01"))

const script = join(import.meta.dirname, "validate-publish-structure.mjs")
const createFixture = (rid) => {
	const root = mkdtempSync(join(tmpdir(), "nori-publish-fixture-"))
	const slot = join(root, "app-1.2.3-4")
	// eslint-disable-next-line security/detect-non-literal-fs-filename -- 测试fixture临时目录
	mkdirSync(slot, {recursive: true})
	const mac = rid.startsWith("osx-")
	const rootBase = mac ? join(root, "Nori.app", "Contents", "MacOS", "Nori") : join(root, "Nori")
	const rootExecutable = mac ? rootBase : rid.startsWith("win-") ? `${rootBase}.exe` : rootBase
	const slotBase = mac ? join(slot, "Nori.Desktop.app", "Contents", "MacOS", "Nori.Desktop") : join(slot, "Nori.Desktop")
	const slotExecutable = mac ? slotBase : rid.startsWith("win-") ? `${slotBase}.exe` : slotBase
	const native = rid.startsWith("win-") ? "Live2DCubismCore.dll" : rid.startsWith("osx-") ? "libLive2DCubismCore.dylib" : "libLive2DCubismCore.so"
	const files = [rootExecutable, `${rootBase}.dll`, `${rootBase}.deps.json`, `${rootBase}.runtimeconfig.json`, join(root, "LICENSE"), join(root, ".current"), join(slot, "deployment.json"), slotExecutable, `${slotBase}.dll`, `${slotBase}.deps.json`, `${slotBase}.runtimeconfig.json`, mac ? join(slot, "Nori.Desktop.app", "Contents", "MacOS", native) : join(slot, native)]
	for (const file of files) {
		// eslint-disable-next-line security/detect-non-literal-fs-filename -- 测试fixture临时目录
		mkdirSync(join(file, ".."), {recursive: true})
		// eslint-disable-next-line security/detect-non-literal-fs-filename -- 测试fixture临时文件
		writeFileSync(file, "fixture")
		// eslint-disable-next-line security/detect-non-literal-fs-filename -- 测试fixture临时文件
		if (process.platform !== "win32") chmodSync(file, 0o755)
	}
	// eslint-disable-next-line security/detect-non-literal-fs-filename -- 测试fixture临时文件
	writeFileSync(join(root, ".current"), "app-1.2.3-4\n")
	// eslint-disable-next-line security/detect-non-literal-fs-filename -- 测试fixture临时文件
	writeFileSync(join(slot, "deployment.json"), JSON.stringify({schema_version: 1, product_version: "v1.2.3-test", numeric_version: "1.2.3", revision: 4, rid, entrypoint: mac ? "Nori.Desktop.app/Contents/MacOS/Nori.Desktop" : rid.startsWith("win-") ? "Nori.Desktop.exe" : "Nori.Desktop"}))
	return root
}

for (const rid of ["win-x64", "linux-x64", "osx-arm64"]) {
	const root = createFixture(rid)
	const result = spawnSync(process.execPath, [script, root, rid], {encoding: "utf8"})
	assert.equal(result.status, 0, `${rid}: ${result.stderr}`)
}
const root = createFixture("linux-x64")
// eslint-disable-next-line security/detect-non-literal-fs-filename -- 测试fixture临时文件
writeFileSync(join(root, "libhostfxr.so"), "forbidden")
const rejected = spawnSync(process.execPath, [script, root, "linux-x64"], {encoding: "utf8"})
assert.notEqual(rejected.status, 0)

const mismatched = createFixture("linux-x64")
// eslint-disable-next-line security/detect-non-literal-fs-filename -- 测试fixture临时文件
writeFileSync(join(mismatched, "app-1.2.3-4", "deployment.json"), JSON.stringify({schema_version: 1, product_version: "v1.2.3-test", numeric_version: "1.2.3", revision: 5, rid: "linux-x64", entrypoint: "Nori.Desktop"}))
const manifestRejected = spawnSync(process.execPath, [script, mismatched, "linux-x64"], {encoding: "utf8"})
assert.notEqual(manifestRejected.status, 0)
console.log("发布结构 fixture 测试通过")
