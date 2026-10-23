#!/bin/sh
# UZIP2 卸载脚本：只删除安装脚本装下去的文件，保留 ~/.config/uzip2 里的配置与密码本。
# 用法：sudo PREFIX=/usr/local ./uninstall.sh
set -eu

PREFIX="${PREFIX:-/usr/local}"

for f in \
    "$PREFIX/bin/uzip2" \
    "$PREFIX/share/applications/uzip2.desktop" \
    "$PREFIX/share/icons/hicolor/128x128/apps/uzip2.png" \
    "$PREFIX/share/icons/hicolor/48x48/apps/uzip2.png"
do
    [ -e "$f" ] && rm -f "$f" && echo "已删除 $f"
done

if command -v update-desktop-database >/dev/null 2>&1; then
    update-desktop-database "$PREFIX/share/applications" >/dev/null 2>&1 || true
fi

echo "配置目录 ~/.config/uzip2 保留未动。"
