import { createHash } from "node:crypto";
import { execFileSync } from "node:child_process";
import { existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, renameSync } from "node:fs";
import { join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

// 固定官方 v0.2.0 产物及摘要；不向仓库提交二进制文件。
const assets = {
	"linux-x64": ["x86_64-unknown-linux-gnu.tar.gz", "libuda_ffi.so", "30aa24afd915215320c8aed21d40ac53e050b64fda0cb8ab0e9e7bd4e56ca019", "14bc1883da4b89c1e3d5a20ba8d08004ad041592eb364d65d06d984f51da19ba"],
	"win-x64": ["x86_64-pc-windows-gnu.zip", "uda_ffi.dll", "84d26d413da7a515ec9187402c38979442141951c81e6b6aeb13a567e54f1858", "ff919cc7f42bfdc2eff6808399482a5dac1c7c8f7e8a9b41a2aca011b7a6c934"],
};
const rid = process.argv[2];
if (!rid) throw new Error("请指定目标 RID，例如 linux-x64 或 win-x64。");
const root = fileURLToPath(new URL("../UniDesktop/native/", import.meta.url));
const asset = assets[rid];
if (!asset) {
	if (!["linux-arm64", "osx-x64", "osx-arm64"].includes(rid)) throw new Error(`未知 RID：${rid}`);
	console.log(`UniDesktop v0.2.0 不提供 ${rid} 产物，使用现有平台能力。`);
} else {
	const [suffix, library, archiveHash, libraryHash] = asset;
	const output = join(root, rid, library);
	const hash = path => createHash("sha256").update(readFileSync(path)).digest("hex");
	if (!existsSync(output) || hash(output) !== libraryHash) {
		mkdirSync(root, { recursive: true });
		const scratch = mkdtempSync(join(root, ".download-"));
		try {
			const archive = resolve(scratch, suffix);
			execFileSync("curl", ["--fail", "--location", "--retry", "2", "--connect-timeout", "20", "--max-time", "180", "--output", archive, `https://github.com/UniDesktop/SDK/releases/download/v0.2.0/uda-ffi-v0.2.0-${suffix}`], { stdio: "inherit" });
			if (hash(archive) !== archiveHash) throw new Error("UniDesktop 下载文件 SHA-256 校验失败。");
			if (suffix.endsWith(".zip") && process.platform !== "win32") {
				execFileSync("unzip", ["-q", archive, library, "-d", scratch]);
			} else {
				execFileSync("tar", ["-xf", archive, "-C", scratch, library]);
			}
			const extracted = join(scratch, library);
			if (hash(extracted) !== libraryHash) throw new Error("UniDesktop 原生库 SHA-256 校验失败。");
			mkdirSync(join(root, rid), { recursive: true });
			// 同一文件系统内原子替换，避免覆盖正在加载的库或留下半写入文件。
			renameSync(extracted, output);
		} finally { rmSync(scratch, { recursive: true, force: true }); }
	}
	console.log(`UniDesktop v0.2.0 ${rid} 校验完成。`);
}
