import { createHash } from "node:crypto";
import { execFileSync } from "node:child_process";
import { existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, renameSync } from "node:fs";
import { join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

// 固定官方产物及摘要；原生库摘要须与 Nori.Desktop.csproj 的发布校验一致。
const version = "v0.2.1";
const assets = new Map([
	["linux-x64", ["x86_64-unknown-linux-gnu.tar.gz", "libuda_ffi.so", "87999c3d4ffb9ac10411f480cf7745182c3a1702bd67fa911b645eb638981919", "793ac30f6ba18dde8b2b7ec0ba72ce2fbb3b0a40ec3d816e44365f400d76f4bf"]],
	["win-x64", ["x86_64-pc-windows-gnu.zip", "uda_ffi.dll", "3e1655e7b5c20f8031c1ab2e58d3d19e2fc26ddb8c812c3fb2584285989dbe73", "c6864481c30fba7dce8cc814000f8f9e918ee0ab75932d7af08efd7e13d492fd"]],
]);
const rid = process.argv[2];
if (!rid) throw new Error("请指定目标 RID，例如 linux-x64 或 win-x64。");
const root = fileURLToPath(new URL("../UniDesktop/native/", import.meta.url));
const asset = assets.get(rid);
if (!asset) {
	if (!["linux-arm64", "osx-x64", "osx-arm64"].includes(rid)) throw new Error(`未知 RID：${rid}`);
	console.log(`UniDesktop ${version} 不提供 ${rid} 产物，使用现有平台能力。`);
} else {
	const [suffix, library, archiveHash, libraryHash] = asset;
	const output = join(root, rid, library);
	// eslint-disable-next-line security/detect-non-literal-fs-filename -- 路径仅来自固定应用目录、白名单产物与受控临时目录
	const hash = path => createHash("sha256").update(readFileSync(path)).digest("hex"); // nosemgrep
	// eslint-disable-next-line security/detect-non-literal-fs-filename -- output由固定root与Map白名单RID及库名生成
	if (!existsSync(output) || hash(output) !== libraryHash) { // nosemgrep
		// eslint-disable-next-line security/detect-non-literal-fs-filename -- root由当前脚本相对URL生成，不接受命令行路径
		mkdirSync(root, { recursive: true }); // nosemgrep
		const scratch = mkdtempSync(join(root, ".download-"));
		try {
			const archive = resolve(scratch, suffix);
			execFileSync("curl", ["--fail", "--location", "--retry", "2", "--connect-timeout", "20", "--max-time", "180", "--output", archive, `https://github.com/UniDesktop/SDK/releases/download/${version}/uda-ffi-${version}-${suffix}`], { stdio: "inherit" });
			if (hash(archive) !== archiveHash) throw new Error("UniDesktop 下载文件 SHA-256 校验失败。");
			if (suffix.endsWith(".zip") && process.platform !== "win32") {
				execFileSync("unzip", ["-q", archive, library, "-d", scratch]);
			} else {
				execFileSync("tar", ["-xf", archive, "-C", scratch, library]);
			}
			const extracted = join(scratch, library);
			if (hash(extracted) !== libraryHash) throw new Error("UniDesktop 原生库 SHA-256 校验失败。");
			// eslint-disable-next-line security/detect-non-literal-fs-filename -- rid必须匹配Map白名单，不能包含任意路径
			mkdirSync(join(root, rid), { recursive: true }); // nosemgrep
			// 同一文件系统内原子替换，避免覆盖正在加载的库或留下半写入文件。
			// eslint-disable-next-line security/detect-non-literal-fs-filename -- 白名单库名，源文件已验证固定SHA-256，目标只在固定native目录
			renameSync(extracted, output); // nosemgrep
		} finally { rmSync(scratch, { recursive: true, force: true }); }
	}
	console.log(`UniDesktop ${version} ${rid} 校验完成。`);
}
