#!/usr/bin/env bash
#
# UZIP2 Linux 分发打包脚本：把自包含单文件二进制打成 .deb / .rpm / AppImage。
#
# 背景与边界
# ----------
# UZIP2 的 Linux 产物是一个自包含的 .NET 8 单文件可执行（Avalonia GUI + CLI 同一个二进制）。
# 它不依赖 .NET 运行时，但**依赖桌面库**（Avalonia 的 X11 后端会 dlopen libX11 / libICE /
# libSM / fontconfig / freetype），而 7z 命令本身要由发行版的 7zip 包提供。
# 所以包的正确性主要在两处：Depends/Requires 字段，以及「绝不 strip」。
#
# 关键坑（务必保留相关注释）
# --------------------------
# .NET 单文件发布在 ELF 末尾追加了 bundle 数据（manifest + 压缩的托管程序集）。
# 任何 strip / objcopy 都会切坏它，运行时立刻报：
#     Failure processing application bundle; possible file corruption.
#     Arithmetic overflow while reading bundle.
# 因此：
#   * 本脚本全程不调用 strip/objcopy；
#   * 打 deb 时用 `install -m755`（只改权限位，不改内容）；
#   * 打 rpm 的 spec 里显式 `%global __strip /bin/true` + `%global debug_package %{nil}`
#     （见 packaging/rpm/uzip2.spec.in）；
#   * 打包前后用 cmp 校验包内二进制与源二进制逐字节一致，被改写就中止。
#
# 缺工具时的行为
# --------------
# dpkg-deb 缺失时用纯 `ar` 回退（本机实测可行，见 build_deb_with_ar）；rpmbuild /
# appimagetool 缺失时打印中文提示并**跳过该格式**，绝不假装成功。
# 退出码只在「一个产物都没产出」或某格式真失败时非 0。
#
# 用法：bash scripts/package-linux.sh --help

set -euo pipefail

# ---------------------------------------------------------------- 基础工具函数

SCRIPT_DIR=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
REPO_ROOT=$(cd -- "$SCRIPT_DIR/.." && pwd)
# 调用时的工作目录：后面 --from-tarball / -o 的相对路径都按它解析
INVOKE_DIR=$(pwd)

# 注意：本脚本用全局变量返回路径（而不是 $(...) 抓 stdout），
# 因为日志函数也写 stdout，抓字符串会被日志污染。
log()  { printf '==> %s\n' "$*"; }
info() { printf '    %s\n' "$*"; }
warn() { printf '警告: %s\n' "$*" >&2; }
die()  { printf '错误: %s\n' "$*" >&2; exit 1; }

have() { command -v "$1" >/dev/null 2>&1; }

# 可执行文件可能带路径（--dotnet /path/to/dotnet）
have_cmd() {
    case "$1" in
        */*) [ -x "$1" ] ;;
        *)   have "$1" ;;
    esac
}

# 缺工具的统一出口：中文提示 + 跳过该格式（返回 1 让调用方跳过）
skip_format() {
    printf '跳过 %s：%s\n' "$1" "$2" >&2
    return 1
}

# ---------------------------------------------------------------- 用法

usage() {
    cat <<'EOF'
UZIP2 Linux 打包脚本 —— 生成 .deb / .rpm / AppImage

用法：
  bash scripts/package-linux.sh [选项]

选项：
  -f, --formats LIST      要产出的格式，逗号分隔：deb,rpm,appimage,all
                          默认 all（缺工具的格式自动跳过并说明）
  -a, --arch LIST         目标架构，逗号分隔：x64,arm64,both
                          默认本机架构；也可用 --from-tarball 的 tarball 架构
  -i, --from-tarball PATH 用已有的 UZIP2-<ver>-linux-<arch>.tar.gz 当输入，
                          不重新 dotnet publish（架构从文件名识别）
      --tarball-dir DIR   在这个目录里按 <ver> 和架构找 tarball（默认与 --outdir 相同）
  -o, --outdir DIR        产物目录，默认 artifacts/<ver>
      --version VER       覆盖版本号（默认从 UZIP2.Avalonia/UZIP2.Avalonia.csproj 读）
      --release N         rpm 的 Release 号，默认 1
      --dotnet PATH       dotnet 可执行文件路径，默认取 PATH 上的 dotnet
      --appimagetool PATH appimagetool 路径（默认找 PATH 上的 appimagetool）
      --runtime-file PATH  AppImage runtime 文件（默认让 appimagetool 自己下载；
                          网络受限时用这个，或在 appimagetool 旁边放 runtime-<arch>）
      --keep-stage        保留暂存目录（调试用）
  -h, --help              显示本帮助

依赖（缺了对应格式会被跳过，不会假成功）：
  .deb       dpkg-deb（Debian/Ubuntu）；没有时用 ar + tar 回退，功能等价
  .rpm       rpmbuild（Debian/Ubuntu: apt-get install rpm；Fedora: dnf install rpm-build）
  AppImage   appimagetool（https://github.com/AppImage/appimagetool/releases）

全程不需要 root / sudo：deb 与 rpm 的「打包」都不需要安装，只写文件到产物目录。

示例：
  # 本机最小验证（不需要任何打包工具，deb 用 ar 回退）
  bash scripts/package-linux.sh --formats deb \
      --from-tarball artifacts/3.12.0/UZIP2-3.12.0-linux-x64.tar.gz

  # CI 里一次出全部格式
  bash scripts/package-linux.sh --arch both --formats all --tarball-dir artifacts/3.12.0
EOF
}

# ---------------------------------------------------------------- 参数解析

FORMATS="all"
ARCHES=""
FROM_TARBALL=""
TARBALL_DIR=""
OUTDIR=""
VERSION_OVERRIDE=""
RELEASE="1"
DOTNET_BIN="dotnet"
APPIMAGETOOL_BIN=""
APPIMAGE_RUNTIME_FILE=""
KEEP_STAGE=0

while [ $# -gt 0 ]; do
    case "$1" in
        -h|--help)            usage; exit 0 ;;
        -f|--formats)         FORMATS=${2:?--formats 需要参数}; shift 2 ;;
        -a|--arch)            ARCHES=${2:?--arch 需要参数}; shift 2 ;;
        -i|--from-tarball)    FROM_TARBALL=${2:?--from-tarball 需要参数}; shift 2 ;;
        --tarball-dir)        TARBALL_DIR=${2:?--tarball-dir 需要参数}; shift 2 ;;
        -o|--outdir)          OUTDIR=${2:?--outdir 需要参数}; shift 2 ;;
        --version)            VERSION_OVERRIDE=${2:?--version 需要参数}; shift 2 ;;
        --release)            RELEASE=${2:?--release 需要参数}; shift 2 ;;
        --dotnet)             DOTNET_BIN=${2:?--dotnet 需要参数}; shift 2 ;;
        --appimagetool)       APPIMAGETOOL_BIN=${2:?--appimagetool 需要参数}; shift 2 ;;
        --runtime-file)       APPIMAGE_RUNTIME_FILE=${2:?--runtime-file 需要参数}; shift 2 ;;
        --keep-stage)         KEEP_STAGE=1; shift ;;
        *)                    die "不认识的参数：$1（用 --help 看用法）" ;;
    esac
done

# ---------------------------------------------------------------- 版本号

# 版本号只有一个来源：UZIP2.Avalonia/UZIP2.Avalonia.csproj 的 <Version>。
# 不读 AssemblyInfo（那边是四段 3.12.0.0），也不硬编码。
CSPROJ="$REPO_ROOT/UZIP2.Avalonia/UZIP2.Avalonia.csproj"

read_version() {
    [ -f "$CSPROJ" ] || die "找不到 $CSPROJ"
    local v
    v=$(sed -n 's:.*<Version>[[:space:]]*\([0-9][0-9]*\.[0-9][0-9.]*\)[[:space:]]*</Version>.*:\1:p' "$CSPROJ" | head -1)
    [ -n "$v" ] || die "读不到版本号：$CSPROJ 里没有 <Version>x.y.z</Version>"
    # 统一裁到三段：3.12.0.0 -> 3.12.0
    printf '%s' "$v" | cut -d. -f1-3
}

if [ -n "$VERSION_OVERRIDE" ]; then
    VERSION=$VERSION_OVERRIDE
else
    VERSION=$(read_version)
fi
[ -n "$VERSION" ] || die "版本号为空"

OUTDIR=${OUTDIR:-$REPO_ROOT/artifacts/$VERSION}
TARBALL_DIR=${TARBALL_DIR:-$OUTDIR}

# 统一转成绝对路径：脚本里有几处会 cd 到别处（ar rcD、sha256sum 侧车、
# appimagetool），相对路径在那里会解析错（`ar: xxx.deb: 没有那个文件或目录`）。
abspath() {
    case "$1" in
        /*) printf '%s' "$1" ;;
        *)  printf '%s/%s' "$INVOKE_DIR" "$1" ;;
    esac
}
OUTDIR=$(abspath "$OUTDIR")
TARBALL_DIR=$(abspath "$TARBALL_DIR")
if [ -n "$FROM_TARBALL" ]; then
    FROM_TARBALL=$(abspath "$FROM_TARBALL")
fi

# ---------------------------------------------------------------- 架构映射

deb_arch_for() {
    case "$1" in
        x64)   printf 'amd64' ;;
        arm64) printf 'arm64' ;;
        *) die "不支持的架构：$1（可选 x64 / arm64）" ;;
    esac
}

rpm_arch_for() {
    case "$1" in
        x64)   printf 'x86_64' ;;
        arm64) printf 'aarch64' ;;
        *) die "不支持的架构：$1（可选 x64 / arm64）" ;;
    esac
}

rid_for() {
    case "$1" in
        x64)   printf 'linux-x64' ;;
        arm64) printf 'linux-arm64' ;;
        *) die "不支持的架构：$1（可选 x64 / arm64）" ;;
    esac
}

appimage_arch_for() {
    case "$1" in
        x64)   printf 'x86_64' ;;
        arm64) printf 'aarch64' ;;
        *) die "不支持的架构：$1（可选 x64 / arm64）" ;;
    esac
}

host_arch() {
    case "$(uname -m)" in
        x86_64|amd64)  printf 'x64' ;;
        aarch64|arm64) printf 'arm64' ;;
        *) die "无法识别的本机架构：$(uname -m)" ;;
    esac
}

# 从文件名识别 tarball 架构（publish-linux.ps1 的命名是固定的）
detect_tarball_arch() {
    case "$1" in
        *-linux-x64.tar.gz)   printf 'x64' ;;
        *-linux-arm64.tar.gz) printf 'arm64' ;;
        *) die "无法从文件名识别架构（期望 *-linux-x64.tar.gz 或 *-linux-arm64.tar.gz）：$1" ;;
    esac
}

# ---------------------------------------------------------------- 暂存目录

WORK_ROOT=""

cleanup() {
    if [ "$KEEP_STAGE" -eq 0 ]; then
        [ -n "$WORK_ROOT" ] && [ -d "$WORK_ROOT" ] && rm -rf -- "$WORK_ROOT"
    else
        [ -n "$WORK_ROOT" ] && printf '暂存目录保留在：%s\n' "$WORK_ROOT" >&2
    fi
}
trap cleanup EXIT

make_work_root() {
    [ -n "$WORK_ROOT" ] && return 0
    local base
    # 优先用仓库下的 _tools/scratch（已被 .gitignore 覆盖），
    # 避免某些环境里 /tmp 只读或被清理
    if [ -d "$REPO_ROOT/_tools" ] && [ -w "$REPO_ROOT/_tools" ]; then
        base="$REPO_ROOT/_tools/scratch"
        mkdir -p -- "$base"
    else
        base="${TMPDIR:-/tmp}"
    fi
    WORK_ROOT=$(mktemp -d "$base/uzip2-pkg-XXXXXX")
    info "暂存目录：$WORK_ROOT"
}

# 把「已发布好的目录」整理成包内布局（usr/ 子树）。
#   $1 = 源目录（含 UZIP2 二进制）  $2 = 目标暂存目录
stage_payload() {
    local src=$1 stage=$2 size_mb

    [ -f "$src/UZIP2" ] || die "输入目录里没有 UZIP2 可执行文件：$src"

    mkdir -p -- "$stage/usr/bin" \
              "$stage/usr/share/applications" \
              "$stage/usr/share/icons/hicolor/128x128/apps" \
              "$stage/usr/share/icons/hicolor/48x48/apps" \
              "$stage/usr/share/doc/uzip2" \
              "$stage/usr/share/licenses/uzip2"

    # install -m755 只改权限位，不碰内容 —— 「绝不 strip」的第一道防线
    install -m 755 -- "$src/UZIP2" "$stage/usr/bin/uzip2"

    install -m 644 -- "$REPO_ROOT/packaging/linux/uzip2.desktop"   "$stage/usr/share/applications/uzip2.desktop"
    install -m 644 -- "$REPO_ROOT/packaging/linux/uzip2.png"       "$stage/usr/share/icons/hicolor/128x128/apps/uzip2.png"
    install -m 644 -- "$REPO_ROOT/packaging/linux/uzip2-48.png"    "$stage/usr/share/icons/hicolor/48x48/apps/uzip2.png"

    if [ -f "$REPO_ROOT/LICENSE" ]; then
        install -m 644 -- "$REPO_ROOT/LICENSE" "$stage/usr/share/licenses/uzip2/LICENSE"
        install -m 644 -- "$REPO_ROOT/LICENSE" "$stage/usr/share/doc/uzip2/copyright"
    fi

    # 逐字节校验：源二进制与包内二进制必须完全一致。
    # 如果哪天流程里混进 strip / objcopy / dd，这里立刻中止。
    cmp -s -- "$src/UZIP2" "$stage/usr/bin/uzip2" \
        || die "包内二进制与源二进制不一致（被 strip 或改写了？）—— 中止，.NET 单文件 bundle 会被切坏"

    # 体积启发式：单文件 bundle 约 80 MB 量级；stripped 的 ELF 只有 ~10 MB。
    # 这条只是提前预警，真正的判据是上面的 cmp。
    size_mb=$(( $(wc -c < "$stage/usr/bin/uzip2") / 1024 / 1024 ))
    if [ "$size_mb" -lt 30 ]; then
        warn "可执行文件只有 ${size_mb} MB，疑似被 strip（单文件 bundle 通常 80 MB 上下）；照常继续，请人工确认"
    fi
    info "payload 就绪：$(du -sh -- "$stage/usr" | cut -f1)（二进制 ${size_mb} MB）"
}

# ---------------------------------------------------------------- 发布目录准备

# 输出到全局 RELEASE_DIR；成功返回 0，拿不到返回 1
prepare_release_dir() {
    local arch=$1 tarball="" dest

    RELEASE_DIR=""

    if [ -n "$FROM_TARBALL" ]; then
        local want
        want=$(detect_tarball_arch "$FROM_TARBALL")
        if [ "$want" != "$arch" ]; then
            warn "--from-tarball 是 $want 架构，与请求的 $arch 不符"
            return 1
        fi
        tarball=$FROM_TARBALL
    else
        local cand="$TARBALL_DIR/UZIP2-$VERSION-linux-$arch.tar.gz"
        [ -f "$cand" ] && tarball=$cand
    fi

    if [ -n "$tarball" ]; then
        local destdir="$WORK_ROOT/extract/$arch"
        [ -f "$tarball" ] || { warn "找不到 tarball：$tarball"; return 1; }
        info "输入：$(basename -- "$tarball")"
        mkdir -p -- "$destdir"
        tar -xzf "$tarball" -C "$destdir"
        local top
        top=$(find "$destdir" -mindepth 1 -maxdepth 1 -type d | head -1)
        [ -n "$top" ] || { warn "tarball 里没有顶层目录：$tarball"; return 1; }
        [ -f "$top/UZIP2" ] || { warn "tarball 顶层目录里没有 UZIP2：$top"; return 1; }
        RELEASE_DIR=$top
        return 0
    fi

    # 没有 tarball：自己 publish
    have_cmd "$DOTNET_BIN" || {
        warn "既没有 $arch 的 tarball（$TARBALL_DIR/UZIP2-$VERSION-linux-$arch.tar.gz），也找不到 dotnet（$DOTNET_BIN）"
        return 1
    }
    local rid
    rid=$(rid_for "$arch")
    dest="$WORK_ROOT/release/$arch"
    rm -rf -- "$dest"
    mkdir -p -- "$dest"
    log "dotnet publish -r $rid（自包含单文件）"
    if ! "$DOTNET_BIN" publish "$CSPROJ" \
            -c Release -r "$rid" --self-contained true \
            -p:PublishSingleFile=true \
            -p:IncludeNativeLibrariesForSelfExtract=true \
            -o "$dest" --nologo -v minimal 2>&1 | sed 's/^/    /'; then
        warn "dotnet publish 失败（$rid）"
        return 1
    fi
    [ -f "$dest/UZIP2.Avalonia" ] || { warn "publish 没有产出 UZIP2.Avalonia（$rid）"; return 1; }
    # 发布出来的可执行名是 UZIP2.Avalonia，包内统一叫 UZIP2
    mv -- "$dest/UZIP2.Avalonia" "$dest/UZIP2"
    chmod 755 -- "$dest/UZIP2"
    RELEASE_DIR=$dest
    return 0
}

# ---------------------------------------------------------------- .deb

# 纯 ar 实现（dpkg-deb 的等价最小集）：
#   debian-binary（内容 "2.0\n"）+ control.tar.gz + data.tar.gz，成员顺序即扫描顺序。
# 非 root 也能做：tar 的 --owner/--group 只是「记录」属主，不需要真的切换用户。
build_deb_with_ar() {
    local stage=$1 out=$2 tmp
    tmp=$(mktemp -d "$WORK_ROOT/debar-XXXXXX")

    printf '2.0\n' > "$tmp/debian-binary"

    # data.tar：包内实际文件（排除 DEBIAN 元数据目录）
    tar --format=gnu --owner=0 --group=0 --numeric-owner \
        -cf "$tmp/data.tar" -C "$stage" --exclude=./DEBIAN .
    gzip -9n -c "$tmp/data.tar" > "$tmp/data.tar.gz"

    # control.tar：control / md5sums / postinst / postrm
    tar --format=gnu --owner=0 --group=0 --numeric-owner \
        -cf "$tmp/control.tar" -C "$stage/DEBIAN" .
    gzip -9n -c "$tmp/control.tar" > "$tmp/control.tar.gz"

    rm -f -- "$out"
    ( cd -- "$tmp" && ar rcD "$out" debian-binary control.tar.gz data.tar.gz )

    rm -rf -- "$tmp"
}

build_deb() {
    local stage=$1 out=$2 arch=$3
    local debarch debroot controldir size_kb

    debarch=$(deb_arch_for "$arch")

    # 独立的 deb 根目录：DEBIAN/ 元数据只能出现在这里。
    # 早期版本直接在共用暂存目录里建 DEBIAN/，结果 rpm 打包时整棵树（含 DEBIAN/）
    # 被拷进 buildroot，rpmbuild 报
    #   Installed (but unpackaged) file(s) found: /DEBIAN/control ...
    # 直接失败。所以 deb 自己复制一份根目录，与 rpm / AppImage 互不影响。
    debroot="$WORK_ROOT/deb-root/$arch"
    rm -rf -- "$debroot"
    mkdir -p -- "$debroot"
    cp -a -- "$stage/." "$debroot/"

    controldir="$debroot/DEBIAN"
    rm -rf -- "$controldir"
    mkdir -p -- "$controldir"

    size_kb=$(du -sk -- "$debroot/usr" | cut -f1)

    # control：模板替换占位符（control.in 的多行 Description 以空格续行）。
    # 注意 control.in 里**不能写注释行**：它被原样 sed 进 deb 的 control 文件，
    # 而 dpkg 的二进制 control 解析器不接受以 # 开头的行（会报
    # "error parsing file ... line N: syntax error"）。所以模板保持纯字段，
    # 关于 Maintainer 为什么必须是 "Name <email>" 形式的说明放在这里：
    # Debian policy 5.6.2 要求可路由的邮箱，写成 <https://github.com/pingod>
    # 会被 lintian 判 maintainer-address-malformed（进仓库的硬性门槛）。
    sed -e "s|@VERSION@|$VERSION|g" \
        -e "s|@ARCH@|$debarch|g" \
        -e "s|@INSTALLED_SIZE@|$size_kb|g" \
        "$REPO_ROOT/packaging/deb/control.in" > "$controldir/control"

    install -m 755 -- "$REPO_ROOT/packaging/deb/postinst" "$controldir/postinst"
    install -m 755 -- "$REPO_ROOT/packaging/deb/postrm"   "$controldir/postrm"

    # md5sums：dpkg-deb 不生成，但 dpkg 的文件校验与 debsums 用得上
    (
        cd -- "$debroot"
        find . -type f ! -path './DEBIAN/*' -print0 \
            | sort -z \
            | while IFS= read -r -d '' f; do
                printf '%s  %s\n' "$(md5sum -- "$f" | cut -d' ' -f1)" "${f#./}"
              done
    ) > "$controldir/md5sums"

    # control 文件自检：这是包的元数据，写坏了 dpkg 装不上，而 ar 回退路径
    # 没有 dpkg 帮我们校验。三条硬性规则：
    #   1. 不能有以 # 开头的行（dpkg 的控制文件解析器不认注释）；
    #   2. 必须以 Package/Version/Architecture/Maintainer/Description 这几个必填字段开头；
    #   3. Maintainer 必须是 "名字 <邮箱>" 形式。
    if grep -q '^[[:space:]]*#' "$controldir/control"; then
        die "control 里有注释行（dpkg 不接受）：$controldir/control"
    fi
    for field in Package Version Architecture Maintainer Description; do
        grep -q "^$field: " "$controldir/control" \
            || die "control 缺少必填字段 $field：$controldir/control"
    done
    grep -qE '^Maintainer: .+ <[^ <>]+@[^ <>]+>' "$controldir/control" \
        || die "control 的 Maintainer 不是 \"名字 <邮箱>\" 形式：$controldir/control"

    if have dpkg-deb; then
        log "dpkg-deb --build -> $(basename -- "$out")"
        local rootopt=()
        # 老版本 dpkg（<1.19）没有 --root-owner-group
        if dpkg-deb --help 2>&1 | grep -q -- '--root-owner-group'; then
            rootopt=(--root-owner-group)
        fi
        if [ ${#rootopt[@]} -gt 0 ]; then
            dpkg-deb --build "${rootopt[@]}" -- "$debroot" "$out" >/dev/null
        else
            dpkg-deb --build -- "$debroot" "$out" >/dev/null
        fi
    else
        log "没有 dpkg-deb，用 ar + tar 回退构建 -> $(basename -- "$out")"
        build_deb_with_ar "$debroot" "$out"
    fi

    [ -f "$out" ] || die "deb 构建失败，产物不存在：$out"
    return 0
}

verify_deb() {
    local deb=$1 tmp members

    [ -f "$deb" ] || { warn "deb 不存在，跳过校验：$deb"; return 1; }

    if have dpkg-deb; then
        log "dpkg-deb -I 校验元数据"
        dpkg-deb -I "$deb" 2>&1 | sed 's/^/    /'
        log "dpkg-deb -c 列内容（前 20 项）"
        dpkg-deb -c "$deb" 2>&1 | head -20 | sed 's/^/    /'
        return 0
    fi

    log "没有 dpkg-deb：手工拆 ar 成员校验 deb 结构"
    members=$(ar t "$deb" | tr '\n' ' ')
    info "ar 成员（顺序即 dpkg 的扫描顺序）：$members"
    [ "$members" = "debian-binary control.tar.gz data.tar.gz " ] \
        || die "deb 成员顺序不对：$members"

    tmp=$(mktemp -d "$WORK_ROOT/debverify-XXXXXX")
    ( cd -- "$tmp" && ar x "$deb" )
    info "debian-binary = $(tr -d '\n' < "$tmp/debian-binary")"
    log "control 内容"
    if gzip -dc "$tmp/control.tar.gz" | tar -xO ./control > "$tmp/control" 2>/dev/null; then :; else
        gzip -dc "$tmp/control.tar.gz" | tar -xO > "$tmp/control"
    fi
    sed 's/^/    /' "$tmp/control"
    log "data.tar 内容（前 12 项）"
    # `|| true`：head 提前关管道会让 tar 报 "写错误"，那是正常的 SIGPIPE，不是校验失败
    gzip -dc "$tmp/data.tar.gz" | tar -tf - 2>/dev/null | head -12 | sed 's/^/    /' || true
    rm -rf -- "$tmp"
    return 0
}

# ---------------------------------------------------------------- .rpm

build_rpm() {
    local stage=$1 out=$2 arch=$3
    local rpmarch topdir spec built

    rpmarch=$(rpm_arch_for "$arch")

    if ! have rpmbuild; then
        skip_format "rpm" "没有 rpmbuild（Debian/Ubuntu: apt-get install -y rpm；Fedora: dnf install rpm-build）"
        return 1
    fi

    log "rpmbuild -> $(basename -- "$out")"
    topdir="$WORK_ROOT/rpmbuild"
    mkdir -p -- "$topdir"/{BUILD,BUILDROOT,RPMS,SOURCES,SPECS,SRPMS}

    # Source0 是「已装好布局的暂存树」，不是源码。
    # 打成**带顶层目录** uzip2-<ver>/ 的 tar.gz：这样 spec 里可以用标准的
    # `%setup -q`（rpm 的惯例是归档里有一个 %{name}-%{version} 顶层目录），
    # 不必把暂存目录的绝对路径塞进 spec，spec 本身保持可复用。
    srcroot="$WORK_ROOT/rpmbuild/srctree/uzip2-$VERSION"
    rm -rf -- "$WORK_ROOT/rpmbuild/srctree"
    mkdir -p -- "$srcroot"
    cp -a -- "$stage/." "$srcroot/"
    tar -czf "$topdir/SOURCES/uzip2-$VERSION.tar.gz" \
        -C "$WORK_ROOT/rpmbuild/srctree" "uzip2-$VERSION"

    spec="$topdir/SPECS/uzip2.spec"
    sed -e "s|@VERSION@|$VERSION|g" \
        -e "s|@RELEASE@|$RELEASE|g" \
        -e "s|@ARCH@|$rpmarch|g" \
        "$REPO_ROOT/packaging/rpm/uzip2.spec.in" > "$spec"

    # --target：允许在 x86_64 上产出 aarch64 的 rpm（纯数据打包，不需要交叉编译器）
    if ! rpmbuild -bb --target "$rpmarch" --define "_topdir $topdir" "$spec" \
            > "$WORK_ROOT/rpm.log" 2>&1; then
        printf '\n' >&2
        tail -n 40 -- "$WORK_ROOT/rpm.log" >&2 || true
        die "rpmbuild 失败，完整日志：$WORK_ROOT/rpm.log"
    fi

    built=$(find "$topdir/RPMS" -name '*.rpm' | head -1)
    [ -n "$built" ] || die "rpmbuild 成功但没找到 .rpm 产物"
    mv -f -- "$built" "$out"
    return 0
}

verify_rpm() {
    local rpm=$1
    [ -f "$rpm" ] || { warn "rpm 不存在，跳过校验：$rpm"; return 1; }
    if have rpm; then
        log "rpm -qip 校验元数据"
        rpm -qip "$rpm" 2>&1 | sed 's/^/    /'
        log "rpm -qlp 列内容"
        rpm -qlp "$rpm" 2>&1 | sed 's/^/    /'
    else
        info "没有 rpm 命令，跳过元数据校验（文件已生成：$(wc -c < "$rpm") 字节）"
    fi
    return 0
}

# ---------------------------------------------------------------- AppImage

build_appimage() {
    local stage=$1 out=$2 arch=$3
    local archimg appdir tool

    archimg=$(appimage_arch_for "$arch")

    tool=$APPIMAGETOOL_BIN
    if [ -z "$tool" ]; then
        have appimagetool && tool=$(command -v appimagetool)
    fi
    if [ -z "$tool" ] || [ ! -x "$tool" ]; then
        skip_format "AppImage" "没有 appimagetool（下载：https://github.com/AppImage/appimagetool/releases/download/continuous/appimagetool-x86_64.AppImage；或用 --appimagetool 指定路径）"
        return 1
    fi

    appdir="$WORK_ROOT/appimage/$arch/AppDir"
    log "组装 AppDir -> $(basename -- "$out")"
    rm -rf -- "$appdir"
    mkdir -p -- "$appdir"

    # AppDir = usr/ 里的真实文件 + 根目录的 AppRun / desktop / 图标（appimagetool 要求）
    cp -a -- "$stage/usr" "$appdir/usr"
    install -m 644 -- "$REPO_ROOT/packaging/linux/uzip2.desktop" "$appdir/uzip2.desktop"
    install -m 644 -- "$REPO_ROOT/packaging/linux/uzip2.png"     "$appdir/uzip2.png"
    install -m 644 -- "$REPO_ROOT/packaging/linux/uzip2.png"     "$appdir/.DirIcon"
    install -m 755 -- "$REPO_ROOT/packaging/appimage/AppRun"     "$appdir/AppRun"

    # AppStream 元数据：新版 appimagetool（2025 起）缺它会直接失败，
    # 而老版本不认 --no-appstream 开关 —— 所以这里一律带一个合法文件，
    # 版本号与日期现场从 @VERSION@ / @DATE@ 填充，避免发版时漏改。
    # 只放 usr/share/metainfo/：这是 AppStream 规范位置，新旧 appimagetool 都认；
    # 再往 AppDir 根目录放一份会被当成重复元数据（同一个 id 出现两次）。
    mkdir -p -- "$appdir/usr/share/metainfo"
    sed -e "s|@VERSION@|$VERSION|g" -e "s|@DATE@|$(date -u +%Y-%m-%d)|g" \
        "$REPO_ROOT/packaging/appimage/io.github.pingod.UZIP2.appdata.xml" \
        > "$appdir/usr/share/metainfo/io.github.pingod.UZIP2.appdata.xml"

    # appimagetool 自己也是 AppImage：没有 FUSE 时用 --appimage-extract-and-run。
    # 先探测哪种调用方式可用；两种都不行就明确失败（不要用一个必然失败的调用硬撞）。
    local -a runner=()
    if "$tool" --version >/dev/null 2>&1; then
        runner=("$tool")
    elif "$tool" --appimage-extract-and-run --version >/dev/null 2>&1; then
        runner=("$tool" --appimage-extract-and-run)
    else
        warn "appimagetool 既不能直接运行也不能 --appimage-extract-and-run（缺 FUSE？）"
        warn "提示：AppImage 打包装箱只要它能跑，不需要 root；可用 --appimagetool 指定另一份"
        return 1
    fi

    # appimagetool 在最后一步要往产物前面拼一段 runtime，默认会去 GitHub
    # （AppImage/type2-runtime）现场下载。网络受限时这一步必然失败，报的是
    #   Failed to download runtime: server returned status code 0
    # 看起来像打包脚本的 bug，其实只是下载不来。传 --runtime-file 就能完全离线：
    # 既接受显式 --runtime-file，也接受在 appimagetool 旁边放一份 runtime-<arch>
    # 自动发现，避免每次打包都去 GitHub 撞一次。
    local runtime=${APPIMAGE_RUNTIME_FILE:-}
    if [ -z "$runtime" ]; then
        local sibling
        sibling="$(dirname -- "$tool")/runtime-$archimg"
        if [ -f "$sibling" ]; then runtime=$sibling; fi
    fi
    if [ -n "$runtime" ]; then
        [ -f "$runtime" ] || die "--runtime-file 指定的文件不存在：$runtime"
        runner+=("--runtime-file" "$runtime")
        info "使用本地 runtime：$runtime"
    fi

    # ARCH 必须显式给：交叉打 aarch64 时 appimagetool 靠它决定 ELF 架构
    if ! ARCH="$archimg" "${runner[@]}" "$appdir" "$out" > "$WORK_ROOT/appimage.log" 2>&1; then
        tail -n 40 -- "$WORK_ROOT/appimage.log" >&2 || true
        if grep -q 'Failed to download runtime' "$WORK_ROOT/appimage.log" 2>/dev/null; then
            warn "appimagetool 下载 runtime 失败（网络受限），可用 --runtime-file 指定本地 runtime"
        fi
        die "appimagetool 失败，完整日志：$WORK_ROOT/appimage.log"
    fi
    [ -f "$out" ] || die "appimagetool 没有产出文件：$out"
    chmod 755 -- "$out"
    return 0
}

verify_appimage() {
    local img=$1 src_bin=${2:-}
    [ -f "$img" ] || { warn "AppImage 不存在，跳过校验：$img"; return 1; }

    log "AppImage 结构校验（解包看内容，不启动 GUI）"
    local tmp root
    tmp=$(mktemp -d "$WORK_ROOT/aimg-XXXXXX")
    if ! ( cd -- "$tmp" && "$img" --appimage-extract >/dev/null 2>&1 ); then
        warn "AppImage 解包失败（可能缺 FUSE）"
        rm -rf -- "$tmp"
        return 1
    fi
    root="$tmp/squashfs-root"
    [ -x "$root/AppRun" ]        || die "AppImage 里没有可执行的 AppRun"
    [ -f "$root/uzip2.desktop" ] || die "AppImage 里没有 uzip2.desktop"
    [ -f "$root/uzip2.png" ]     || die "AppImage 里没有 uzip2.png（appimagetool 要求根目录有图标）"
    [ -x "$root/usr/bin/uzip2" ] || die "AppImage 里没有 usr/bin/uzip2"
    [ -f "$root/usr/share/metainfo/io.github.pingod.UZIP2.appdata.xml" ] \
        || die "AppImage 里没有 AppStream 元数据（新版 appimagetool 会拒绝构建）"
    # 二进制必须与源文件逐字节一致（AppImage 打包不该动它）
    if [ -n "$src_bin" ]; then
        cmp -s -- "$root/usr/bin/uzip2" "$src_bin" \
            || die "AppImage 里的二进制与源二进制不一致（被改写了？）"
    fi
    info "AppRun / uzip2.desktop / 图标 / metainfo / usr/bin/uzip2 齐全（二进制未改写）"
    rm -rf -- "$tmp"
    return 0
}

# ---------------------------------------------------------------- 主流程

mkdir -p -- "$OUTDIR"

want_deb=0; want_rpm=0; want_appimage=0
case "$FORMATS" in
    all) want_deb=1; want_rpm=1; want_appimage=1 ;;
    *)
        IFS=',' read -r -a _fmts <<< "$FORMATS"
        for f in "${_fmts[@]}"; do
            case "$f" in
                deb)      want_deb=1 ;;
                rpm)      want_rpm=1 ;;
                appimage) want_appimage=1 ;;
                all)      want_deb=1; want_rpm=1; want_appimage=1 ;;
                '')       : ;;
                *)        die "不认识的格式：$f（可选：deb,rpm,appimage,all）" ;;
            esac
        done
        ;;
esac

if [ -z "$ARCHES" ]; then
    if [ -n "$FROM_TARBALL" ]; then
        ARCHES=$(detect_tarball_arch "$FROM_TARBALL")
    else
        ARCHES=$(host_arch)
    fi
fi
case "$ARCHES" in
    both) ARCH_LIST=(x64 arm64) ;;
    *)    IFS=',' read -r -a ARCH_LIST <<< "$ARCHES" ;;
esac

log "UZIP2 $VERSION —— 格式：$FORMATS，架构：${ARCH_LIST[*]}，输出：$OUTDIR"

make_work_root

PRODUCED=()
SKIPPED=()
FAILED=()

for arch in "${ARCH_LIST[@]}"; do
    log "==== 架构 $arch ===="

    if ! prepare_release_dir "$arch"; then
        SKIPPED+=("$arch/*")
        continue
    fi

    stage="$WORK_ROOT/stage/$arch"
    rm -rf -- "$stage"
    mkdir -p -- "$stage"
    stage_payload "$RELEASE_DIR" "$stage"

    if [ "$want_deb" -eq 1 ]; then
        out="$OUTDIR/uzip2_${VERSION}_$(deb_arch_for "$arch").deb"
        if build_deb "$stage" "$out" "$arch"; then
            verify_deb "$out" || true
            PRODUCED+=("$out")
            info "生成 $(basename -- "$out")（$(du -h -- "$out" | cut -f1)）"
        else
            FAILED+=("deb/$arch")
        fi
    fi

    if [ "$want_rpm" -eq 1 ]; then
        out="$OUTDIR/uzip2-${VERSION}-${RELEASE}.$(rpm_arch_for "$arch").rpm"
        if build_rpm "$stage" "$out" "$arch"; then
            verify_rpm "$out" || true
            PRODUCED+=("$out")
            info "生成 $(basename -- "$out")（$(du -h -- "$out" | cut -f1)）"
        else
            SKIPPED+=("rpm/$arch")
        fi
    fi

    if [ "$want_appimage" -eq 1 ]; then
        out="$OUTDIR/UZIP2-${VERSION}-linux-${arch}.AppImage"
        if build_appimage "$stage" "$out" "$arch"; then
            verify_appimage "$out" "$RELEASE_DIR/UZIP2" || true
            PRODUCED+=("$out")
            info "生成 $(basename -- "$out")（$(du -h -- "$out" | cut -f1)）"
        else
            SKIPPED+=("appimage/$arch")
        fi
    fi
done

# 侧车 sha256（与 publish-linux.ps1 的 .sha256 风格一致：小写 hash + 两个空格 + 文件名）
if [ ${#PRODUCED[@]} -gt 0 ]; then
    log "写 sha256 侧车"
    sums="$OUTDIR/SHA256SUMS-linux-packages.txt"
    : > "$sums"
    for f in "${PRODUCED[@]}"; do
        local_leaf=$(basename -- "$f")
        (
            cd -- "$(dirname -- "$f")"
            sha256sum "$local_leaf" > "$local_leaf.sha256"
        )
        info "$local_leaf.sha256"
        ( cd -- "$(dirname -- "$f")" && sha256sum "$local_leaf" ) >> "$sums"
    done
    info "汇总：$sums"
fi

echo
log "完成"
printf '  产出 %d 个文件\n' "${#PRODUCED[@]}"
for f in "${PRODUCED[@]}"; do printf '    %s\n' "$f"; done
if [ ${#SKIPPED[@]} -gt 0 ]; then
    printf '  跳过（缺工具/输入）：%s\n' "${SKIPPED[*]}"
fi
if [ ${#FAILED[@]} -gt 0 ]; then
    printf '  失败：%s\n' "${FAILED[*]}" >&2
    exit 1
fi
if [ ${#PRODUCED[@]} -eq 0 ]; then
    printf '一个产物都没生成 —— 请按上面的提示补工具，或检查 --from-tarball 路径。\n' >&2
    exit 1
fi
exit 0
