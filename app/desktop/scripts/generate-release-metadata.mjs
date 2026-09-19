import crypto from "node:crypto"
import fs from "node:fs"
import path from "node:path"
import {validateProductVersion} from "./version-validation.mjs"

const ROOT = process.cwd()
const ARGUMENT_NAMES = new Set(["publish-dir", "version", "rid", "output-dir"])
const parseArgs = (argv) => {
	const result = new Map()
	for (let index = 0; index < argv.length; index++) {
		const argument = argv[index]
		if (!argument.startsWith("--")) throw new Error(`无法识别参数: ${argument}`)
		const key = argument.slice(2)
		if (!ARGUMENT_NAMES.has(key)) throw new Error(`Unsupported argument: --${key}`)
		if (index + 1 >= argv.length || argv[index + 1].startsWith("--")) throw new Error(`参数缺少值: --${key}`)
		// eslint-disable-next-line -- Codacy误报：key已通过固定参数白名单校验，Map不触发对象原型写入
		result.set(key, argv[++index])
	}
	return result
}

const writeJson = (filePath, value) => {
	// eslint-disable-next-line security/detect-non-literal-fs-filename -- 发布元数据输出路径由发布参数生成
	fs.writeFileSync(filePath, `${JSON.stringify(value, null, "\t")}\n`, "utf8") // nosemgrep
}

const IGNORED_DIRECTORIES = new Set([".git", "node_modules", "bin", "obj", "dist", "coverage"])
const walkFiles = (directory) => {
	const result = []
	// eslint-disable-next-line security/detect-non-literal-fs-filename -- directory来自受控发布目录遍历
	for (const entry of fs.readdirSync(directory, {withFileTypes: true})) { // nosemgrep
		const entryPath = path.join(directory, entry.name)
		if (entry.isDirectory()) {
			if (!IGNORED_DIRECTORIES.has(entry.name)) result.push(...walkFiles(entryPath))
		} else result.push(entryPath)
	}
	return result
}

const sha256 = (filePath) => {
	const hash = crypto.createHash("sha256")
	// eslint-disable-next-line security/detect-non-literal-fs-filename -- filePath来自受控发布目录遍历
	hash.update(fs.readFileSync(filePath)) // nosemgrep
	return hash.digest("hex")
}

const packageJson = JSON.parse(fs.readFileSync(path.join(ROOT, "package.json"), "utf8")) // nosemgrep
const packageEntries = [
	...Object.entries(packageJson.dependencies ?? {}).map(([name, version]) => ({name, version, scope: "runtime", ecosystem: "npm"})),
	...Object.entries(packageJson.devDependencies ?? {}).map(([name, version]) => ({name, version, scope: "build", ecosystem: "npm"})),
]

const csprojEntries = []
for (const filePath of walkFiles(ROOT).filter((file) => file.endsWith(".csproj") && !file.includes(`${path.sep}obj${path.sep}`) && !file.includes(`${path.sep}bin${path.sep}`))) {
	// eslint-disable-next-line security/detect-non-literal-fs-filename -- filePath来自受控项目文件遍历
	const contents = fs.readFileSync(filePath, "utf8") // nosemgrep
	for (const match of contents.matchAll(/<PackageReference\s+Include="([^"]+)"\s+Version="([^"]+)"/g)) {
		csprojEntries.push({name: match[1], version: match[2], scope: "runtime", ecosystem: "NuGet"})
	}
}

const declared = JSON.parse(fs.readFileSync(path.join(ROOT, "third-party-components.json"), "utf8")) // nosemgrep
const specialByName = new Map(declared.components.map((component) => [component.name, component]))
const components = new Map()
const addComponent = (component) => {
	const key = `${component.ecosystem}:${component.name}@${component.version}`
	if (components.has(key)) return
	const special = specialByName.get(component.name)
	components.set(key, {
		name: component.name,
		version: component.version,
		ecosystem: component.ecosystem,
		scope: component.scope ?? "runtime",
		purl: component.purl ?? null,
		license: special?.license ?? null,
		licenseStatus: special?.licenseStatus ?? "not-verified",
		note: special?.note ?? null,
	})
}
for (const entry of packageEntries) {
	addComponent({
		...entry,
		purl: entry.ecosystem === "npm"
			? `pkg:npm/${entry.name.startsWith("@") ? entry.name : entry.name}`
			: `pkg:nuget/${entry.name}@${entry.version}`,
	})
}
for (const entry of csprojEntries) {
	addComponent({
		...entry,
		ecosystem: "NuGet",
		purl: `pkg:nuget/${entry.name}@${entry.version}`,
	})
}
for (const component of declared.components) addComponent(component)

const args = parseArgs(process.argv.slice(2))
const PUBLISH_ARG = args.get("publish-dir")
const publishDir = path.resolve(PUBLISH_ARG ?? "")
const version = args.get("version")
const rid = args.get("rid") ?? "win-x64"
const outputDir = path.resolve(args.get("output-dir") ?? "bin/release")
if (!version || !PUBLISH_ARG) throw new Error("需要 --publish-dir、--version")
validateProductVersion(version)
// eslint-disable-next-line security/detect-non-literal-fs-filename -- publishDir是发布流程显式目录
if (!fs.existsSync(publishDir)) throw new Error(`发布目录不存在: ${publishDir}`) // nosemgrep
// eslint-disable-next-line security/detect-non-literal-fs-filename -- outputDir是发布流程显式目录
fs.mkdirSync(outputDir, {recursive: true}) // nosemgrep

const files = walkFiles(publishDir)
	.map((filePath) => ({
		path: path.relative(publishDir, filePath).split(path.sep).join("/"),
		sha256: sha256(filePath),
	}))
	.sort((left, right) => left.path.localeCompare(right.path))
const externalComponents = [...components.values()].sort((left, right) => `${left.ecosystem}:${left.name}`.localeCompare(`${right.ecosystem}:${right.name}`))
const ownComponent = {
	name: "Nori Desktop Pet",
	version,
	type: "application",
	license: "GPL-3.0-only",
	licenseStatus: "repository-license",
	notice: "LICENSE",
}

const packaging = rid.startsWith("linux-") ? "framework-dependent-linux-tar-gz" : rid.startsWith("osx-") ? "framework-dependent-macos-zip" : "framework-dependent-windows-zip"
const notices = {
	schemaVersion: 1,
	generatedFor: {name: ownComponent.name, version, rid, packaging},
	policy: declared.policy,
	components: [
		ownComponent,
		...externalComponents.map((component) => ({
			name: component.name,
			version: component.version,
			ecosystem: component.ecosystem,
			scope: component.scope,
			purl: component.purl,
			license: component.license,
			licenseStatus: component.licenseStatus,
			notice: component.license ? component.note : null,
			note: component.note,
		})),
	],
}
writeJson(path.join(outputDir, "THIRD-PARTY-NOTICES.json"), notices)

const bomComponents = externalComponents.map((component) => {
	const bomComponent = {
		type: component.ecosystem === "npm" ? "library" : component.ecosystem === "runtime" ? "framework" : "library",
		name: component.name,
		version: component.version,
		purl: component.purl ?? undefined,
		properties: [
			{name: "nori:scope", value: component.scope},
			{name: "nori:license-status", value: component.licenseStatus},
		],
	}
	if (component.license) bomComponent.licenses = [{license: {id: component.license}}]
	return bomComponent
})
const serialSeed = crypto.createHash("sha256").update(`${version}:${rid}`).digest("hex")
const sbom = {
	bomFormat: "CycloneDX",
	specVersion: "1.5",
	serialNumber: `urn:uuid:${serialSeed.slice(0, 8)}-${serialSeed.slice(8, 12)}-4${serialSeed.slice(13, 16)}-8${serialSeed.slice(17, 20)}-${serialSeed.slice(20, 32)}`,
	version: 1,
	metadata: {
		component: {
			type: "application",
			name: ownComponent.name,
			version,
			licenses: [{license: {id: ownComponent.license}}],
		},
	},
	components: bomComponents,
}
writeJson(path.join(outputDir, "SBOM.cdx.json"), sbom)

const manifest = {
	schemaVersion: 1,
	name: ownComponent.name,
	version,
	rid,
	packaging,
	prerequisites: rid.startsWith("linux-") ? [".NET 10 Runtime", "WebKitGTK 4.x"] : rid.startsWith("osx-") ? [".NET 10 Runtime", "macOS WebKit"] : [".NET 10 Runtime", "Microsoft Edge WebView2 Evergreen Runtime"],
	bundledNativeLibraries: [rid.startsWith("win-") ? "Live2DCubismCore.dll" : rid.startsWith("osx-") ? "libLive2DCubismCore.dylib" : "libLive2DCubismCore.so"],
	files,
	metadata: ["THIRD-PARTY-NOTICES.json", "SBOM.cdx.json"],
}
writeJson(path.join(outputDir, "RELEASE-MANIFEST.json"), manifest)

const markdown = [
	"# Third-party notices",
	"",
	`Nori Desktop Pet ${version} (${rid}) 的组件清单。许可证只有在仓库证据明确或发布前确认后才填写；null 表示本次没有作许可证断言。`,
	"",
	"| Component | Version | License | Status |",
	"| --- | --- | --- | --- |",
	...notices.components.map((component) => `| ${component.name} | ${component.version} | ${component.license ?? "未确认"} | ${component.licenseStatus} |`),
	"",
	"项目自身许可证见仓库根目录 LICENSE。未确认条目不会被此文件推断为任何具体许可证。",
	"",
].join("\n")
// eslint-disable-next-line security/detect-non-literal-fs-filename -- outputDir是发布流程显式目录
fs.writeFileSync(path.join(outputDir, "THIRD-PARTY-NOTICES.md"), markdown, "utf8") // nosemgrep
console.log(`已生成发布元数据: ${outputDir}`)
