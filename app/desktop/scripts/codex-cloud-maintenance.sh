#!/usr/bin/env bash
set -euo pipefail

REPO_ROOT="$(git rev-parse --show-toplevel)"
DESKTOP_DIR="$REPO_ROOT/app/desktop"

log() {
	printf '[nori-codex] %s\n' "$*"
}

if [[ -x "$HOME/.dotnet/dotnet" ]]; then
	export DOTNET_ROOT="$HOME/.dotnet"
	export PATH="$DOTNET_ROOT:$DOTNET_ROOT/tools:$PATH"
fi

if [[ -d "$HOME/.local/bin" ]]; then
	export PATH="$HOME/.local/bin:$PATH"
fi

if ! command -v pnpm >/dev/null 2>&1; then
	echo "pnpm 不可用，Codex Cloud 缓存环境需要重建。" >&2
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
