import crypto from "node:crypto"
import fs from "node:fs"
import path from "node:path"
import {numericVersionFromProduct, validateProductVersion, validateRevision} from "./version-validation.mjs"

const ARGS = {}
for (let index = 2; index < process.argv.length; index += 2) {
	const KEY = process.argv[index]
	const VALUE = process.argv[index + 1]
	if (!KEY.startsWith("--") || !VALUE || VALUE.startsWith("--")) throw new Error(`更新清单参数缺少值: ${KEY}`)
	ARGS[KEY.slice(2)] = VALUE
}
for (const KEY of ["version", "rid", "archive-path", "publish-dir"]) {
	if (!ARGS[KEY]) throw new Error(`缺少必要参数: --${KEY}`)
}
const VERSION = validateProductVersion(ARGS.version)
if (VERSION === "Dev") throw new Error("开发版本不能生成更新清单")
const RID = ARGS.rid
if (!["win-x64", "linux-x64", "osx-arm64"].includes(RID)) throw new Error("此架构未开放正式自动更新")
const REPOSITORY = ARGS.repo ?? "MF-Dust/Nori-Desktop-Pet"
if (!/^[A-Za-z0-9_.-]+\/[A-Za-z0-9_.-]+$/.test(REPOSITORY)) throw new Error("GitHub 仓库名称无效")
const PUBLISH = path.resolve(ARGS["publish-dir"])
const SLOT = fs.readFileSync(path.join(PUBLISH, ".current"), "utf8").trim()
if (!/^app-\d+\.\d+\.\d+-\d+$/.test(SLOT)) throw new Error("发布槽指针无效")
const DEPLOYMENT = JSON.parse(fs.readFileSync(path.join(PUBLISH, SLOT, "deployment.json"), "utf8"))
const NUMERIC = numericVersionFromProduct(VERSION)
const REVISION = validateRevision(String(DEPLOYMENT.revision))
if (DEPLOYMENT.schema_version !== 1 || DEPLOYMENT.product_version !== VERSION || DEPLOYMENT.numeric_version !== NUMERIC
	|| DEPLOYMENT.rid !== RID || SLOT !== `app-${NUMERIC}-${REVISION}`
	|| (ARGS.revision !== undefined && validateRevision(ARGS.revision) !== REVISION)) {
	throw new Error("更新清单参数与实际发布槽 deployment.json 不一致")
}
const ENTRYPOINT = DEPLOYMENT.entrypoint
if (typeof ENTRYPOINT !== "string" || ENTRYPOINT.includes("\\") || ENTRYPOINT.includes(":")
	|| ENTRYPOINT.split("/").some(part => !part || part === "." || part === "..")
	|| !fs.statSync(path.join(PUBLISH, SLOT, ENTRYPOINT)).isFile()) throw new Error("发布槽入口无效")
const ARCHIVE = path.resolve(ARGS["archive-path"])
const PACKAGE = path.basename(ARCHIVE)
const ARCHIVE_TYPE = PACKAGE.endsWith(".tar.gz") ? "tar.gz" : PACKAGE.endsWith(".zip") ? "zip" : null
if (!ARCHIVE_TYPE) throw new Error("只支持 ZIP 或 tar.gz 更新包")
const SIZE = fs.statSync(ARCHIVE).size
if (SIZE <= 0 || SIZE > 512 * 1024 * 1024) throw new Error("更新包大小必须在 1 字节至 512 MiB 之间")
const HASH = crypto.createHash("sha256")
for await (const CHUNK of fs.createReadStream(ARCHIVE)) HASH.update(CHUNK)
const TAG = `v${VERSION.replace(/^v/i, "")}`
const MANIFEST = {
	schema_version: 1,
	product_version: VERSION,
	numeric_version: NUMERIC,
	revision: REVISION,
	rid: RID,
	release_tag: TAG,
	package_name: PACKAGE,
	download_url: `https://github.com/${REPOSITORY}/releases/download/${encodeURIComponent(TAG)}/${encodeURIComponent(PACKAGE)}`,
	sha256: HASH.digest("hex"),
	size_bytes: SIZE,
	archive_type: ARCHIVE_TYPE,
	entrypoint: ENTRYPOINT,
	launcher_protocol: 1,
	published_at: new Date().toISOString(),
}
const OUTPUT = path.resolve(ARGS["output-dir"] ?? "bin/release")
fs.mkdirSync(OUTPUT, {recursive: true})
const OUTPUT_PATH = path.join(OUTPUT, `UPDATE-${RID}.json`)
fs.writeFileSync(OUTPUT_PATH, `${JSON.stringify(MANIFEST, null, "\t")}\n`, "utf8")
console.log(`已生成更新清单: ${OUTPUT_PATH}`)
