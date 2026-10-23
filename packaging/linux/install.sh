#!/bin/sh
# UZIP2 安装脚本：把解压目录里的二进制与桌面入口装到系统。
# 用法：
#   sudo ./install.sh                 # 装到 /usr/local
#   sudo PREFIX=/usr ./install.sh     # 装到 /usr（Arch 包之外的手动安装）
set -eu

PREFIX="${PREFIX:-/usr/local}"
here=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)

if [ ! -f "$here/UZIP2" ]; then
    echo "找不到 $here/UZIP2 —— 请在解压后的目录里运行本脚本" >&2
    exit 1
fi

install -Dm755 "$here/UZIP2" "$PREFIX/bin/uzip2"
install -Dm644 "$here/uzip2.desktop" "$PREFIX/share/applications/uzip2.desktop"
install -Dm644 "$here/uzip2.png" "$PREFIX/share/icons/hicolor/128x128/apps/uzip2.png"
install -Dm644 "$here/uzip2-48.png" "$PREFIX/share/icons/hicolor/48x48/apps/uzip2.png"

if command -v update-desktop-database >/dev/null 2>&1; then
    update-desktop-database "$PREFIX/share/applications" >/dev/null 2>&1 || true
fi
if command -v gtk-update-icon-cache >/dev/null 2>&1; then
    gtk-update-icon-cache -q -t "$PREFIX/share/icons/hicolor" >/dev/null 2>&1 || true
fi

echo "已安装到 $PREFIX/bin/uzip2"
echo "配置文件在 ~/.config/uzip2，卸载不会删除。"
