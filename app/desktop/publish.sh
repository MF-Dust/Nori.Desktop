#!/usr/bin/env bash
# Nori Desktop Pet — Linux / macOS 槽式 framework-dependent 发布。
set -euo pipefail
cd "$(dirname "$0")"

APP_VERSION="${NORI_VERSION:-${NORI_PRODUCT_VERSION:-Dev}}"
NUMERIC_VERSION="${APP_VERSION#v}"
NUMERIC_VERSION="${NUMERIC_VERSION%%-*}"
NUMERIC_VERSION="${NUMERIC_VERSION%%+*}"
if [[ "$APP_VERSION" != "Dev" && ! "$NUMERIC_VERSION" =~ ^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$ ]]; then
	echo "版本必须是无前导零的数字 major.minor.patch 或 Dev。" >&2; exit 2
fi
if [[ "$APP_VERSION" == "Dev" ]]; then NUMERIC_VERSION="0.0.0"; fi
CODENAME="${NORI_CODENAME:-}"
if [[ "$APP_VERSION" != "Dev" ]]; then
	if [[ -z "$CODENAME" ]]; then CODENAME="${APP_VERSION#*-}"; CODENAME="${CODENAME%%+*}"; fi
	[[ "$CODENAME" =~ ^[A-Za-z0-9][A-Za-z0-9.-]*$ ]] || { echo "Codename 无效。" >&2; exit 2; }
fi
REVISION="${NORI_DEPLOYMENT_REVISION:-0}"
[[ "$REVISION" =~ ^(0|[1-9][0-9]*)$ ]] || { echo "部署 revision 必须是无前导零的非负整数。" >&2; exit 2; }
if [[ -z "${NORI_COMMIT_SHA:-}" ]]; then NORI_COMMIT_SHA="$(git rev-parse HEAD 2>/dev/null || true)"; fi
export NORI_VERSION="$APP_VERSION" NORI_PRODUCT_VERSION="$APP_VERSION" NORI_COMMIT_SHA

if [[ "${NORI_INCLUDE_RUNTIME:-0}" =~ ^(1|true|TRUE|yes)$ ]]; then
	echo "不支持 self-contained 发布；目标机必须预装 .NET 10 Runtime。" >&2
	exit 2
fi
if [[ "${NORI_SKIP_FRONTEND:-0}" != "1" ]]; then pnpm build; fi
KEEP_SYMBOLS="${NORI_KEEP_SYMBOLS:-0}"

runtime_rid() {
	local os arch
	os="$(uname -s)"; arch="$(uname -m)"
	case "$arch" in x86_64|amd64) arch=x64 ;; arm64|aarch64) arch=arm64 ;; *) exit 1 ;; esac
	case "$os" in Linux) echo "linux-$arch" ;; Darwin) echo "osx-$arch" ;; *) echo "不支持的系统: $os" >&2; exit 1 ;; esac
}

make_manifest() {
	local slot="$1" rid="$2" entry="$3"
	node scripts/write-deployment-json.mjs "$slot/deployment.json" "$APP_VERSION" "$NUMERIC_VERSION" "$REVISION" "$rid" "$entry"
}

make_macos_launcher() {
	local root="$1" rid="$2" temp="bin/publish/$rid/launcher"
	mkdir -p "$root/Nori.app/Contents/MacOS" "$root/Nori.app/Contents/Resources"
	cp "$temp/Nori" "$root/Nori.app/Contents/MacOS/Nori"
	for file in Nori.dll Nori.deps.json Nori.runtimeconfig.json; do
		[[ -f "$temp/$file" ]] || { echo "macOS launcher 缺少 $file。" >&2; exit 2; }
		cp "$temp/$file" "$root/Nori.app/Contents/MacOS/$file"
	done
	if [[ "$KEEP_SYMBOLS" == "1" || "$KEEP_SYMBOLS" == "true" ]]; then
		[[ -f "$temp/Nori.pdb" ]] && cp "$temp/Nori.pdb" "$root/Nori.app/Contents/MacOS/Nori.pdb"
	fi
	chmod +x "$root/Nori.app/Contents/MacOS/Nori"
	cat > "$root/Nori.app/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
	<key>CFBundleName</key><string>Nori</string>
	<key>CFBundleDisplayName</key><string>Nori Desktop Pet</string>
	<key>CFBundleIdentifier</key><string>cn.erhio.noriDesktopPet</string>
	<key>CFBundleExecutable</key><string>Nori</string>
	<key>CFBundlePackageType</key><string>APPL</string>
	<key>CFBundleVersion</key><string>$NUMERIC_VERSION</string>
	<key>CFBundleShortVersionString</key><string>$NUMERIC_VERSION</string>
	<key>NSHighResolutionCapable</key><true/>
	<key>NSMicrophoneUsageDescription</key><string>Nori 需要使用麦克风来进行语音对话。</string>
</dict></plist>
PLIST
}

for rid in "${@:-$(runtime_rid)}"; do
	case "$rid" in linux-x64|linux-arm64|osx-x64|osx-arm64) ;; *) echo "不支持的 RID: $rid" >&2; exit 1 ;; esac
	node scripts/validate-publish-input.mjs "$APP_VERSION" "$REVISION" "$rid"
	root="bin/publish/$rid"; slot="$root/app-$NUMERIC_VERSION-$REVISION"; desktop_temp="$root/desktop"; launcher_temp="$root/launcher"
	node scripts/prepare-unidesktop.mjs "$rid"
	rm -rf "$root"; mkdir -p "$slot" "$desktop_temp" "$launcher_temp"
	dotnet publish Nori.Desktop/Nori.Desktop.csproj -c Release -r "$rid" --self-contained false -p:NoriProductVersion="$APP_VERSION" -p:NoriDeploymentRevision="$REVISION" -p:NoriSentryDsnNative="${NORI_SENTRY_DSN_NATIVE:-}" -p:NoriSentryRelease="${NORI_SENTRY_RELEASE:-}" -p:NoriSentryEnvironment="${NORI_SENTRY_ENVIRONMENT:-production}" -p:PublishSingleFile=false -p:PublishReadyToRun=false -o "$desktop_temp"
	if [[ "$KEEP_SYMBOLS" != "1" && "$KEEP_SYMBOLS" != "true" ]]; then find "$desktop_temp" -name '*.pdb' -delete; fi
	if [[ "$rid" == osx-* ]]; then
		mkdir -p "$slot/Nori.Desktop.app/Contents/MacOS" "$slot/Nori.Desktop.app/Contents/Resources"
		cp -R "$desktop_temp/." "$slot/Nori.Desktop.app/Contents/MacOS/"
		chmod +x "$slot/Nori.Desktop.app/Contents/MacOS/Nori.Desktop"
		cat > "$slot/Nori.Desktop.app/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?><plist version="1.0"><dict>
<key>CFBundleName</key><string>Nori Desktop</string><key>CFBundleExecutable</key><string>Nori.Desktop</string><key>CFBundlePackageType</key><string>APPL</string>
<key>CFBundleIdentifier</key><string>cn.erhio.noriDesktopPet.desktop</string><key>CFBundleVersion</key><string>$NUMERIC_VERSION</string><key>CFBundleShortVersionString</key><string>$NUMERIC_VERSION</string>
<key>NSMicrophoneUsageDescription</key><string>Nori 需要使用麦克风来进行语音对话。</string></dict></plist>
PLIST
		make_manifest "$slot" "$rid" "Nori.Desktop.app/Contents/MacOS/Nori.Desktop"
	else
		cp -R "$desktop_temp/." "$slot/"
		chmod +x "$slot/Nori.Desktop"
		make_manifest "$slot" "$rid" "Nori.Desktop"
	fi
	dotnet publish Nori.AppLauncher/Nori.AppLauncher.csproj -c Release -r "$rid" --self-contained false -p:NoriProductVersion="$APP_VERSION" -p:NoriDeploymentRevision="$REVISION" -p:PublishSingleFile=false -p:PublishReadyToRun=false -o "$launcher_temp"
	if [[ "$KEEP_SYMBOLS" != "1" && "$KEEP_SYMBOLS" != "true" ]]; then find "$launcher_temp" -name '*.pdb' -delete; fi
	if [[ "$rid" == osx-* ]]; then make_macos_launcher "$root" "$rid"; else cp -R "$launcher_temp/." "$root/"; chmod +x "$root/Nori"; fi
	cp ../../LICENSE "$root/LICENSE"
	rm -rf "$desktop_temp" "$launcher_temp"
	printf '%s\n' "$(basename "$slot")" > "$root/.current.tmp"; mv -f "$root/.current.tmp" "$root/.current"
	node scripts/validate-publish-structure.mjs "$root" "$rid"
	node scripts/check-package-size.mjs --path "$root" --label "$rid package" --max-mib 180
done
