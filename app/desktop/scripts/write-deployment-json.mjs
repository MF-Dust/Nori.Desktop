import { writeFileSync } from "node:fs";
import { numericVersionFromProduct, validateNumericVersion, validateProductVersion, validateRevision } from "./version-validation.mjs";

const [output, productVersion, numericVersion, revision, rid, entrypoint] = process.argv.slice(2);
try {
	validateProductVersion(productVersion);
	validateNumericVersion(numericVersion);
	if (numericVersionFromProduct(productVersion) !== numericVersion) throw new Error("产品版本与数字版本不匹配");
	validateRevision(revision);
} catch (error) {
	console.error(`deployment.json 参数无效: ${error.message}`);
	process.exit(2);
}
if (!output || !/^(win|linux|osx)-[A-Za-z0-9-]+$/.test(rid ?? "") || !entrypoint || entrypoint.length > 256 || [...entrypoint].some((char) => char.charCodeAt(0) < 0x20) || entrypoint.includes("\\") || entrypoint.split("/").some((part) => !part || part === "." || part === "..")) {
	console.error("deployment.json 参数无效");
	process.exit(2);
}
const revisionNumber = Number(revision);
// eslint-disable-next-line security/detect-non-literal-fs-filename -- output是发布流程显式目标
writeFileSync(output, JSON.stringify({
	schema_version: 1,
	product_version: productVersion,
	numeric_version: numericVersion,
	revision: revisionNumber,
	rid,
	entrypoint,
}) + "\n", { encoding: "utf8", mode: 0o600 });
