import { existsSync, lstatSync, readFileSync, readdirSync } from "node:fs";
import { join, relative } from "node:path";
import { numericVersionFromProduct, validateNumericVersion, validateProductVersion, validateRevision } from "./version-validation.mjs";

const [rootArg, rid] = process.argv.slice(2);
if (!rootArg || !rid) throw new Error("用法: validate-publish-structure.mjs <package-root> <rid>");
const root = rootArg;
const fail = (message) => { throw new Error(message); };
const file = (path, executable = false) => {
	// eslint-disable-next-line security/detect-non-literal-fs-filename -- path来自受控发布根目录
	if (!existsSync(path) || !lstatSync(path).isFile()) fail(`发布目录缺少文件: ${relative(root, path)}`);
	// eslint-disable-next-line security/detect-non-literal-fs-filename -- path来自受控发布根目录
	if (executable && process.platform !== "win32" && (lstatSync(path).mode & 0o111) === 0) fail(`发布入口不可执行: ${relative(root, path)}`);
};
const directory = (path) => {
	// eslint-disable-next-line security/detect-non-literal-fs-filename -- path来自受控发布根目录
	if (!existsSync(path) || !lstatSync(path).isDirectory()) fail(`发布目录缺少目录: ${relative(root, path)}`);
};
const scan = (path) => {
	// eslint-disable-next-line security/detect-non-literal-fs-filename -- path来自受控发布根目录递归
	const stat = lstatSync(path);
	if (stat.isSymbolicLink()) fail(`发布目录不得包含符号链接: ${relative(root, path)}`);
	if (!stat.isDirectory()) return;
	// eslint-disable-next-line security/detect-non-literal-fs-filename -- path来自受控发布根目录递归
	for (const name of readdirSync(path)) scan(join(path, name));
};
if (!["win-x64", "linux-x64", "linux-arm64", "osx-x64", "osx-arm64"].includes(rid)) fail(`RID 无效: ${rid}`);
directory(root);
scan(root);
const rootLauncher = rid.startsWith("win-") ? join(root, "Nori.exe") : rid.startsWith("osx-") ? join(root, "Nori.app", "Contents", "MacOS", "Nori") : join(root, "Nori");
file(rootLauncher, true);
const rootSidecarBase = rid.startsWith("osx-") ? join(root, "Nori.app", "Contents", "MacOS", "Nori") : join(root, "Nori");
file(`${rootSidecarBase}.dll`);
file(`${rootSidecarBase}.deps.json`);
file(`${rootSidecarBase}.runtimeconfig.json`);
file(join(root, "LICENSE"));
file(join(root, ".current"));
// eslint-disable-next-line security/detect-non-literal-fs-filename -- root是显式发布目录
const slotName = readFileSync(join(root, ".current"), "utf8").trim();
const slotMatch = /^app-(\d+\.\d+\.\d+)-(\d+)$/.exec(slotName);
if (!slotMatch) fail(`.current 无效: ${slotName}`);
const slot = join(root, slotName);
directory(slot);
const manifestPath = join(slot, "deployment.json");
file(manifestPath);
const entryRelative = rid.startsWith("osx-")
	? "Nori.Desktop.app/Contents/MacOS/Nori.Desktop"
	: rid.startsWith("win-") ? "Nori.Desktop.exe" : "Nori.Desktop";
let manifest;
try {
	// eslint-disable-next-line security/detect-non-literal-fs-filename -- manifestPath由已校验发布槽生成
	manifest = JSON.parse(readFileSync(manifestPath, "utf8"));
	validateProductVersion(manifest.product_version);
	validateNumericVersion(manifest.numeric_version);
	validateRevision(String(manifest.revision));
} catch (error) {
	fail(`deployment.json 无效: ${error.message}`);
}
if (manifest.schema_version !== 1 || manifest.numeric_version !== slotMatch[1]
	|| numericVersionFromProduct(manifest.product_version) !== manifest.numeric_version
	|| String(manifest.revision) !== slotMatch[2] || manifest.rid !== rid || manifest.entrypoint !== entryRelative) {
	fail("deployment.json 与槽目录、RID 或入口不匹配");
}
if (rid.startsWith("osx-")) directory(join(slot, "Nori.Desktop.app", "Contents", "MacOS"));
const entry = join(slot, ...entryRelative.split("/"));
file(entry, true);
const sidecarBase = rid.startsWith("osx-") ? join(slot, "Nori.Desktop.app", "Contents", "MacOS", "Nori.Desktop") : join(slot, rid.startsWith("win-") ? "Nori.Desktop" : "Nori.Desktop");
file(`${sidecarBase}.dll`);
file(`${sidecarBase}.deps.json`);
file(`${sidecarBase}.runtimeconfig.json`);
const native = rid.startsWith("win-") ? "Live2DCubismCore.dll" : rid.startsWith("osx-") ? "libLive2DCubismCore.dylib" : "libLive2DCubismCore.so";
file(rid.startsWith("osx-")
	? join(slot, "Nori.Desktop.app", "Contents", "MacOS", native)
	: join(slot, native));
const forbidden = new Set(["data", "dotnet", "shared", "coreclr", "hostfxr", "hostpolicy"]);
const lower = (value) => value.toLowerCase();
const walkForbidden = (path) => {
	// eslint-disable-next-line security/detect-non-literal-fs-filename -- path来自受控发布根目录递归
	for (const name of readdirSync(path)) {
		const full = join(path, name);
		const normalized = lower(name.replace(/\.[^.]+$/, ""));
		const withoutLib = normalized.replace(/^lib/, "");
		if (forbidden.has(normalized) || forbidden.has(withoutLib) || lower(name).endsWith(".map")) fail(`FDD 发布目录包含禁止项: ${relative(root, full)}`);
		// eslint-disable-next-line security/detect-non-literal-fs-filename -- full来自受控发布根目录递归
		if (lstatSync(full).isDirectory()) walkForbidden(full);
	}
};
walkForbidden(root);
console.log(`发布结构有效: ${rid} ${slotName}`);
