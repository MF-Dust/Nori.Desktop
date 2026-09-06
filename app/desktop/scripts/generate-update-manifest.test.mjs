import assert from "node:assert/strict"
import crypto from "node:crypto"
import {mkdtempSync, mkdirSync, readFileSync, rmSync, writeFileSync} from "node:fs"
import {tmpdir} from "node:os"
import {join, dirname} from "node:path"
import {spawnSync} from "node:child_process"

const SCRIPT = join(import.meta.dirname, "generate-update-manifest.mjs")
const TEMP = mkdtempSync(join(tmpdir(), "nori-update-manifest-"))
try {
	for (const [RID, ENTRYPOINT, EXTENSION] of [
		["win-x64", "Nori.Desktop.exe", "zip"],
		["linux-x64", "Nori.Desktop", "tar.gz"],
		["osx-arm64", "Nori.Desktop.app/Contents/MacOS/Nori.Desktop", "zip"],
	]) {
		const PUBLISH = join(TEMP, RID)
		const SLOT = "app-1.0.4-7"
		const ENTRY = join(PUBLISH, SLOT, ENTRYPOINT)
		mkdirSync(dirname(ENTRY), {recursive: true})
		writeFileSync(ENTRY, "程序")
		writeFileSync(join(PUBLISH, ".current"), `${SLOT}\n`)
		const DEPLOYMENT = {
			schema_version: 1, product_version: "1.0.4-codename", numeric_version: "1.0.4", revision: 7, rid: RID, entrypoint: ENTRYPOINT,
		}
		writeFileSync(join(PUBLISH, SLOT, "deployment.json"), JSON.stringify(DEPLOYMENT))
		const ARCHIVE = join(TEMP, `nori-1.0.4-${RID}.${EXTENSION}`)
		const CONTENT = "更新包内容 fixture"
		writeFileSync(ARCHIVE, CONTENT)
		const ARGS = [SCRIPT, "--version", "1.0.4-codename", "--rid", RID, "--archive-path", ARCHIVE, "--publish-dir", PUBLISH, "--output-dir", TEMP]
		const RESULT = spawnSync(process.execPath, ARGS, {encoding: "utf8"})
		assert.equal(RESULT.status, 0, RESULT.stderr)
		const MANIFEST = JSON.parse(readFileSync(join(TEMP, `UPDATE-${RID}.json`), "utf8"))
		assert.equal(MANIFEST.schema_version, 1)
		assert.equal(MANIFEST.revision, 7)
		assert.equal(MANIFEST.numeric_version, "1.0.4")
		assert.equal(MANIFEST.product_version, DEPLOYMENT.product_version)
		assert.equal(MANIFEST.rid, RID)
		assert.equal(MANIFEST.release_tag, "v1.0.4-codename")
		assert.equal(MANIFEST.archive_type, EXTENSION)
		assert.equal(MANIFEST.entrypoint, ENTRYPOINT)
		assert.equal(MANIFEST.launcher_protocol, 1)
		assert.equal(MANIFEST.size_bytes, Buffer.byteLength(CONTENT))
		assert.equal(MANIFEST.sha256, crypto.createHash("sha256").update(CONTENT).digest("hex"))
		assert.equal(MANIFEST.download_url, `https://github.com/MF-Dust/Nori-Desktop-Pet/releases/download/v1.0.4-codename/nori-1.0.4-${RID}.${EXTENSION}`)
		for (const EXTRA of [["--revision", "0"], ["--revision", "7bad"], ["--version", "1.0.5-other"], ["--version", "Dev"], ["--rid", "osx-x64"]]) {
			assert.notEqual(spawnSync(process.execPath, [...ARGS, ...EXTRA], {encoding: "utf8"}).status, 0, EXTRA.join(" "))
		}
		writeFileSync(join(PUBLISH, SLOT, "deployment.json"), JSON.stringify({...DEPLOYMENT, entrypoint: "../outside"}))
		assert.notEqual(spawnSync(process.execPath, ARGS, {encoding: "utf8"}).status, 0)
	}
} finally {
	rmSync(TEMP, {recursive: true, force: true})
}
console.log("三平台更新清单及参数绑定测试通过")
