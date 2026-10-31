#!/usr/bin/env bash
# 只注册桌面入口；应用数据始终留在发布包根目录的 data 下。
set -euo pipefail
root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"
command="${1:-}"
case "$command" in
	install|uninstall|enable-autostart|disable-autostart) ;;
	*) echo "用法: $0 {install|uninstall|enable-autostart|disable-autostart}" >&2; exit 2 ;;
esac
[[ $# == 1 ]] || { echo "只接受一个操作参数。" >&2; exit 2; }
data_home="${XDG_DATA_HOME:-${HOME:?HOME 未设置}/.local/share}"
config_home="${XDG_CONFIG_HOME:-$HOME/.config}"
for path in "$root" "$data_home" "$config_home"; do
	[[ "$path" == /* && ! "$path" =~ [[:cntrl:]] ]] || { echo "安装路径与 XDG 目录必须是无控制字符的绝对路径。" >&2; exit 2; }
done
id="cn.erhio.noriDesktopPet.desktop"
menu="$data_home/applications/$id"
autostart="$config_home/autostart/$id"

# Exec 先按命令参数规则转义，再按 desktop-entry 字符串规则转义；不经过 shell。
escape_value() {
	local value="$1"
	value="${value//\\/\\\\}"
	value="${value// /\\s}"
	printf '%s' "$value"
}
exec_arg() {
	local value="$1"
	value="${value//\\/\\\\}"
	value="${value//\"/\\\"}"
	value="${value//\$/\\\$}"
	value="${value//\`/\\\`}"
	value="${value//%/%%}"
	# 引号内的空格无需转义；字符串层只需要再次转义反斜线。
	value="${value//\\/\\\\}"
	printf '"%s"' "$value"
}
owner="X-Nori-PackageRoot=$(escape_value "$root")"
temporary=""
trap '[[ -z "$temporary" ]] || rm -f -- "$temporary"' EXIT
write_entry() {
	local destination="$1"
	[[ -x "$root/Nori" && -f "$root/.current" ]] || { echo "请从完整的 Linux 发布包运行此脚本（缺少 Nori 或 .current）。" >&2; exit 1; }
	mkdir -p -- "$(dirname -- "$destination")"
	temporary="$(mktemp "$destination.XXXXXX")"
	{
		printf '%s\n' '[Desktop Entry]' 'Type=Application' 'Version=1.0' 'Name=Nori Desktop Pet' 'Name[zh_CN]=Nori 桌面伴侣'
		# GIO 在展开 %% 前检查首个可执行文件，含百分号的包路径不能放在首位；env 又会把含 = 的参数
		# 当成环境变量赋值而不启动。改由 sh 把包路径作为 $0 原样 exec，路径本身不经过 shell 解释。
		printf 'Exec=%s %s\n' '/bin/sh -c "exec \\"\\$0\\""' "$(exec_arg "$root/Nori")"
		printf 'Path=%s\n' "$(escape_value "$root")"
		printf 'Icon=%s\n' "$(escape_value "$root/nori.png")"
		# 宿主窗口的 WM_CLASS 取自入口程序集名（Avalonia 默认），桌面据此把运行中的窗口归到本入口。
		printf '%s\n' 'Terminal=false' 'Categories=Utility;' 'StartupWMClass=Nori.Desktop' "$owner"
	} > "$temporary"
	chmod 644 "$temporary"
	mv -f -- "$temporary" "$destination"
	temporary=""
	printf '已注册: %s\n' "$destination"
}
remove_entry() {
	local destination="$1"
	if [[ -e "$destination" || -L "$destination" ]]; then
		grep -Fxq -- "$owner" "$destination" || { echo "入口不属于当前发布包，未删除: $destination" >&2; exit 1; }
		rm -- "$destination"
	fi
}
case "$command" in
	install) write_entry "$menu" ;;
	enable-autostart) write_entry "$autostart" ;;
	disable-autostart) remove_entry "$autostart" ;;
	uninstall) remove_entry "$autostart"; remove_entry "$menu" ;;
esac
