#!/usr/bin/env bash
set -euo pipefail

REPO_ROOT="$(git rev-parse --show-toplevel)"
DESKTOP_DIR="$REPO_ROOT/app/desktop"

log() {
	printf '[nori-codex] %s\n' "$*"
}

if command -v mise >/dev/null 2>&1; then
	NODE24_DIR="$(mise where node@24 2>/dev/null || true)"
	if [[ -n "$NODE24_DIR" ]]; then
		export PATH="$NODE24_DIR/bin:$PATH"
	fi

	PNPM11_BIN="$(mise which pnpm --tool=pnpm@11 2>/dev/null || true)"
	if [[ -n "$PNPM11_BIN" ]]; then
		export PATH="$(dirname "$PNPM11_BIN"):$PATH"
	fi

	hash -r
	unset NODE24_DIR PNPM11_BIN
fi

if [[ -x "$HOME/.dotnet/dotnet" ]]; then
	export DOTNET_ROOT="$HOME/.dotnet"
	export PATH="$DOTNET_ROOT:$DOTNET_ROOT/tools:$PATH"
fi

if [[ -d "$HOME/.local/bin" ]]; then
	export PATH="$HOME/.local/bin:$PATH"
fi

if ! command -v node >/dev/null 2>&1 || (( $(node -p 'process.versions.node.split(".")[0]') < 24 )); then
	echo "Node.js 24 不可用，Codex Cloud 缓存环境需要重建。" >&2
	exit 1
fi

if ! command -v pnpm >/dev/null 2>&1 || [[ "$(pnpm --version | cut -d. -f1)" != "11" ]]; then
	echo "pnpm 11 不可用，Codex Cloud 缓存环境需要重建。" >&2
	exit 1
fi

if ! command -v dotnet >/dev/null 2>&1 || ! dotnet --list-sdks 2>/dev/null | grep -Eq '^10\.0\.'; then
	echo ".NET 10 SDK 不可用，Codex Cloud 缓存环境需要重建。" >&2
	exit 1
fi

cd "$DESKTOP_DIR"

log "同步前端依赖。"
pnpm install --frozen-lockfile

log "同步 .NET solution 依赖。"
dotnet restore Nori.slnx

log "同步 Linux 发布 RID 依赖。"
dotnet restore Nori.Desktop/Nori.Desktop.csproj --runtime linux-x64

log "Codex Cloud 缓存维护完成。"
