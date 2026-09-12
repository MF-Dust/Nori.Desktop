#!/usr/bin/env bash
set -euo pipefail

REPO_ROOT="$(git rev-parse --show-toplevel)"
DESKTOP_DIR="$REPO_ROOT/app/desktop"

log() {
	printf '[nori-codex] %s\n' "$*"
}

install_linux_dependencies() {
	if ! command -v apt-get >/dev/null 2>&1; then
		log "当前环境不是 apt 系 Linux，跳过 Linux 原生依赖安装。"
		return
	fi

	local sudo_cmd=()
	if [[ "$(id -u)" -ne 0 ]]; then
		if ! command -v sudo >/dev/null 2>&1; then
			echo "需要 root 或 sudo 才能安装 WebKitGTK/GTK 依赖。" >&2
			exit 1
		fi
		sudo_cmd=(sudo)
	fi

	log "安装 Linux WebView/GTK 依赖。"
	"${sudo_cmd[@]}" apt-get update
	"${sudo_cmd[@]}" env DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends \
		ca-certificates \
		libgtk-3-0 \
		libwebkit2gtk-4.1-0
}

ensure_node() {
	if ! command -v node >/dev/null 2>&1; then
		echo "未找到 Node.js。请在 Codex Cloud 的包版本中选择 Node.js 24。" >&2
		exit 1
	fi

	local node_major
	node_major="$(node -p 'process.versions.node.split(".")[0]')"
	if (( node_major < 24 )); then
		echo "Nori.Desktop 需要 Node.js 24+，当前为 $(node --version)。请在 Codex Cloud 中切换到 Node.js 24。" >&2
		exit 1
	fi

	log "Node.js $(node --version) 可用。"
}

ensure_pnpm() {
	if command -v pnpm >/dev/null 2>&1 && [[ "$(pnpm --version | cut -d. -f1)" == "11" ]]; then
		log "pnpm $(pnpm --version) 可用。"
		return
	fi

	log "激活 pnpm 11。"
	if command -v corepack >/dev/null 2>&1; then
		corepack enable
		corepack prepare pnpm@11 --activate
	else
		mkdir -p "$HOME/.local"
		npm config set prefix "$HOME/.local"
		export PATH="$HOME/.local/bin:$PATH"
		if ! grep -Fq 'export PATH="$HOME/.local/bin:$PATH"' "$HOME/.bashrc" 2>/dev/null; then
			printf '\nexport PATH="$HOME/.local/bin:$PATH"\n' >> "$HOME/.bashrc"
		fi
		npm install --global pnpm@11
	fi
}

ensure_dotnet() {
	if command -v dotnet >/dev/null 2>&1 && dotnet --list-sdks 2>/dev/null | grep -Eq '^10\.0\.'; then
		log ".NET 10 SDK 已可用。"
		return
	fi

	log "安装 .NET 10 SDK 到 ~/.dotnet。"
	local installer="/tmp/nori-dotnet-install.sh"
	curl -fsSL https://dot.net/v1/dotnet-install.sh -o "$installer"
	bash "$installer" --channel 10.0 --quality GA --install-dir "$HOME/.dotnet" --no-path
	rm -f "$installer"

	export DOTNET_ROOT="$HOME/.dotnet"
	export PATH="$DOTNET_ROOT:$DOTNET_ROOT/tools:$PATH"

	if ! grep -Fq '# Nori Codex Cloud .NET' "$HOME/.bashrc" 2>/dev/null; then
		cat >> "$HOME/.bashrc" <<'EOF'

# Nori Codex Cloud .NET
export DOTNET_ROOT="$HOME/.dotnet"
export PATH="$DOTNET_ROOT:$DOTNET_ROOT/tools:$PATH"
EOF
	fi

	if ! dotnet --list-sdks | grep -Eq '^10\.0\.'; then
		echo ".NET 10 SDK 安装后仍不可用。" >&2
		exit 1
	fi
}

restore_dependencies() {
	cd "$DESKTOP_DIR"

	log "安装前端依赖。"
	pnpm install --frozen-lockfile

	log "还原 .NET solution 依赖。"
	dotnet restore Nori.slnx

	log "预还原 Codex Cloud Linux 发布 RID 依赖。"
	dotnet restore Nori.Desktop/Nori.Desktop.csproj --runtime linux-x64
}

install_linux_dependencies
ensure_node
ensure_pnpm
ensure_dotnet
restore_dependencies

log "Codex Cloud 环境准备完成。"
log "Node: $(node --version) | pnpm: $(pnpm --version) | dotnet: $(dotnet --version)"
