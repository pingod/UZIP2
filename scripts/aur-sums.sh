#!/usr/bin/env bash
# AUR 发布辅助：校验 / 回填两个 PKGBUILD 的 sha256sums，并生成 .SRCINFO。
#
# 背景：v3.12 起 Release 会随产物附 .sha256 侧车，但 PKGBUILD 里的 sha256sums
# 仍要人工抄一遍。抄错就是打包失败，抄漏就是 AUR 评论区的 checksum mismatch。
# 这个脚本把「下载 → 计算 → 比对 → 回填」做成一条命令。
#
# 用法：
#   scripts/aur-sums.sh                 检查（默认，只读）：不一致时退出码 1
#   scripts/aur-sums.sh --print         打印应写入的 sha256sums 行，不改文件
#   scripts/aur-sums.sh --fill          按实际 hash 回填两个 PKGBUILD
#   scripts/aur-sums.sh --srcinfo       生成两个 .SRCINFO 到标准输出（AUR 必须带）
#   scripts/aur-sums.sh --srcinfo --write   直接写进 packaging/arch/
#
# 选项：
#   --dir <path>    PKGBUILD 所在目录，默认 <repo>/packaging/arch
#   --offline       不下载，只报告「当前值是不是 SKIP」（无网络时用）
#
# 依赖：curl、sha256sum（或 shasum）。makepkg 仅在 --srcinfo 时需要。
#
# 注意：GitHub 的 tag 归档（archive/<tag>.tar.gz）由服务端按需生成，其 sha256
# 不保证永久稳定。--fill 对源码包写的是当期实测值，重新发布或 GitHub 改动归档
# 格式后必须重跑本脚本。

set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd -- "$SCRIPT_DIR/.." && pwd)"

PKG_DIR="$REPO_ROOT/packaging/arch"
MODE="check"
DO_WRITE="no"
OFFLINE="no"
ARCHES=(x86_64 aarch64)
CACHE_DIR="${TMPDIR:-/tmp}/uzip2-aur-sums"

die()  { printf '错误：%s\n' "$*" >&2; exit 1; }
info() { printf '%s\n' "$*"; }
warn() { printf '注意：%s\n' "$*" >&2; }

while [[ $# -gt 0 ]]; do
  case "$1" in
    --fill)    MODE="fill" ;;
    --srcinfo) MODE="srcinfo" ;;
    --print)   MODE="print" ;;
    --write)   DO_WRITE="yes" ;;
    --offline) OFFLINE="yes" ;;
    --dir)     shift; PKG_DIR="${1:?--dir 需要一个路径}" ;;
    -h|--help)
      sed -n '2,23p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'
      exit 0 ;;
    *) die "未知参数：$1（用 --help 看用法）" ;;
  esac
  shift
done

[[ -d "$PKG_DIR" ]] || die "找不到 PKGBUILD 目录：$PKG_DIR"

hash_file() {
  if command -v sha256sum >/dev/null 2>&1; then
    sha256sum "$1" | awk '{print $1}'
  elif command -v shasum >/dev/null 2>&1; then
    shasum -a 256 "$1" | awk '{print $1}'
  else
    die "既没有 sha256sum 也没有 shasum"
  fi
}

# 在隔离子 shell 里 source PKGBUILD，回读某个变量的值。
# PKGBUILD-bin 顶层有 `case "$CARCH"`，所以必须显式给 CARCH。
pkg_var() {
  local file="$1" arch="$2" var="$3"
  CARCH="$arch" bash -c '
    source "$1" >/dev/null 2>&1
    eval "printf \"%s\" \"\${$2:-}\""
  ' _ "$file" "$var"
}

# 输出该 PKGBUILD 在指定架构下实际会用的 source 条目（与 makepkg 的选法一致：
# 有 source_$CARCH 就用它，否则用 source）。
pkg_source_entries() {
  local file="$1" arch="$2"
  CARCH="$arch" bash -c '
    source "$1" >/dev/null 2>&1
    if declare -p "source_$CARCH" >/dev/null 2>&1; then
      eval "printf \"%s\\n\" \"\${source_$CARCH[@]}\""
    else
      printf "%s\n" "${source[@]}"
    fi
  ' _ "$file"
}

# source 条目 → URL：`alias::url` 取 `::` 之后，否则整条就是 URL。
entry_url() {
  local e="$1"
  if [[ "$e" == *::* ]]; then printf '%s' "${e#*::}"; else printf '%s' "$e"; fi
}

# URL → 本地缓存文件名。GitHub 的 archive 链接末段是 tar.gz，用仓库名+tag 更可读。
cache_name() {
  local url="$1"
  if [[ "$url" =~ /archive/([^/]+)\.tar\.gz$ ]]; then
    printf 'UZIP2-%s.tar.gz' "${BASH_REMATCH[1]#v}"
  else
    printf '%s' "${url##*/}"
  fi
}

fetch() {
  local url="$1" out="$2" i
  [[ -s "$out" ]] && { info "    已缓存 $out"; return 0; }
  info "    下载 $url"
  # GitHub 对大文件偶尔提前断流；-C - 续传配几次重试就够
  for i in 1 2 3 4 5; do
    if curl -fsSL -C - --retry 2 --connect-timeout 20 -o "$out" "$url"; then
      return 0
    fi
    info "    第 $i 次失败，重试…"
    sleep 2
  done
  die "下载失败：$url"
}

# 下载并核对：能拿到 <url>.sha256 侧车就以它为准，否则退回购脚本自己下载。
#
# 为什么必须核对：GitHub 对 35 MB 的 Release 包偶尔会中途断流，`curl -f` 只保证没收到
# 4xx/5xx，断流仍可能以退出码 0 结束，留下一个残缺文件。直接对残缺文件算 sha256，
# 会得出「PKGBUILD 里的值和实测不一致」这种吓人的误报，而实际错的是本地缓存。
# 侧车是发布时生成的，用它做交叉验证能把「缓存坏了」和「hash 填错了」区分开。
fetch_verified() {
  local url="$1" out="$2"
  [[ -s "$out" ]] || fetch "$url" "$out"

  local sidecar_url="$url.sha256" sidecar
  if ! sidecar="$(curl -fsSL --max-time 20 "$sidecar_url" 2>/dev/null | awk '{print $1; exit}')"; then
    info "    （没有 .sha256 侧车，跳过完整性交叉验证）"
    return 0
  fi
  [[ "$sidecar" =~ ^[0-9a-f]{64}$ ]] || { info "    （侧车格式无法识别，跳过交叉验证）"; return 0; }

  local got; got="$(hash_file "$out")"
  [[ "$got" == "$sidecar" ]] && return 0

  info "    缓存与官方侧车不符（实测 $got ≠ 侧车 $sidecar），重下一次"
  rm -f "$out"
  fetch "$url" "$out"
  got="$(hash_file "$out")"
  if [[ "$got" != "$sidecar" ]]; then
    die "下载始终不完整：$out
  实测 $got
  侧车 $sidecar
  多半是网络中途断流。删掉缓存目录重试，或手动下载该文件放进缓存目录。"
  fi
}

# 读出 PKGBUILD 里某个 sha256sums 数组的全部元素（一行一个）
current_sums() {
  local file="$1" key="$2"
  bash -c '
    source "$1" >/dev/null 2>&1
    declare -p "$2" >/dev/null 2>&1 || exit 0
    eval "printf \"%s\\n\" \"\${$2[@]}\""
  ' _ "$file" "$key"
}

mkdir -p "$CACHE_DIR"
info "PKGBUILD 目录：$PKG_DIR"
info "缓存目录：    $CACHE_DIR"
FAIL=0

for pkgfile in PKGBUILD PKGBUILD-bin; do
  path="$PKG_DIR/$pkgfile"
  [[ -f "$path" ]] || { warn "跳过：$path 不存在"; continue; }
  info ""
  info "== $pkgfile =="

  # 收集每个架构下 source 的 (url, hash) 列表
  declare -a A_URLS=()   # 全部见过的 URL（去重后）
  declare -A A_HASH=()   # url -> hash
  declare -a X86=()      # x86_64 的 url 序列
  declare -a ARM=()      # aarch64 的 url 序列

  for arch in "${ARCHES[@]}"; do
    declare -a list=()
    while IFS= read -r entry; do
      [[ -n "$entry" ]] || continue
      url="$(entry_url "$entry")"
      case "$url" in
        http://*|https://*) ;;
        *) warn "  [$arch] 跳过非远程源：$entry"; continue ;;
      esac
      list+=("$url")
      if [[ -z "${A_HASH[$url]:-}" ]]; then
        cached="$CACHE_DIR/$(cache_name "$url")"
        info "  [$arch] $(cache_name "$url")"
        if [[ "$OFFLINE" == "yes" ]]; then
          A_HASH[$url]="(offline)"
        else
          fetch_verified "$url" "$cached"
          A_HASH[$url]="$(hash_file "$cached")"
          info "          sha256 ${A_HASH[$url]}"
        fi
        A_URLS+=("$url")
      fi
    done < <(pkg_source_entries "$path" "$arch")

    if [[ "$arch" == "x86_64" ]]; then X86=("${list[@]}"); else ARM=("${list[@]}"); fi
  done

  [[ ${#A_URLS[@]} -gt 0 ]] || die "$pkgfile 里没找到任何远程 source"

  # 判断是否用架构专属数组
  if grep -qE '^sha256sums_x86_64=' "$path"; then
    ARCHED="yes"
    DECL_X86=("$(current_sums "$path" sha256sums_x86_64)")
    DECL_ARM=("$(current_sums "$path" sha256sums_aarch64)")
  else
    ARCHED="no"
    mapfile -t DECL_SINGLE < <(current_sums "$path" sha256sums)
    DECL_X86=("${DECL_SINGLE[@]}")
    DECL_ARM=("${DECL_SINGLE[@]}")
  fi

  # 目标数组
  WANT_X86=(); for u in "${X86[@]}"; do WANT_X86+=("${A_HASH[$u]}"); done
  WANT_ARM=(); for u in "${ARM[@]}"; do WANT_ARM+=("${A_HASH[$u]}"); done

  # 两个架构的 source 不同但只有一个 sha256sums 数组 → 结构上表达不了
  if [[ "$ARCHED" == "no" ]]; then
    if [[ "${X86[*]}" != "${ARM[*]}" ]]; then
      warn "  ✘ 两个架构的 source 不同，但只声明了一个 sha256sums 数组"
      warn "    makepkg 按 $CARCH 选 source，单数组无法同时覆盖两个架构。"
      warn "    请改成架构专属数组：source_x86_64 / source_aarch64 /"
      warn "    sha256sums_x86_64 / sha256sums_aarch64"
      FAIL=1
      continue
    fi
    if [[ ${#WANT_X86[@]} -ne ${#DECL_X86[@]} ]]; then
      warn "  ✘ sha256sums 元素数 ${#DECL_X86[@]} 与 source 条数 ${#WANT_X86[@]} 不符"
      FAIL=1
      continue
    fi
  fi

  # ---- 三种模式对目标数组做动作 ----
  fmt_array() {  # fmt_array name elem...
    local name="$1"; shift
    local out="$name=(" e
    for e in "$@"; do out+="'$e' "; done
    out="${out% }"
    printf '%s)' "$out"
  }

  case "$MODE" in
    print|fill)
      [[ "$OFFLINE" == "yes" ]] && die "--offline 不能与 --print/--fill 同用（没下载就没有 hash）"
      if [[ "$ARCHED" == "yes" ]]; then
        L1="$(fmt_array sha256sums_x86_64 "${WANT_X86[@]}")"
        L2="$(fmt_array sha256sums_aarch64 "${WANT_ARM[@]}")"
        CHANGED="no"
        [[ "$(current_sums "$path" sha256sums_x86_64)" == "${WANT_X86[*]// /$'\n'}" ]] || CHANGED="yes"
        info "  当前 x86_64： $(current_sums "$path" sha256sums_x86_64 | tr '\n' ' ')"
        info "  当前 aarch64：$(current_sums "$path" sha256sums_aarch64 | tr '\n' ' ')"
      else
        L1="$(fmt_array sha256sums "${WANT_X86[@]}")"
        L2=""
        info "  当前：$(current_sums "$path" sha256sums | tr '\n' ' ')"
      fi
      info "  应为：$(printf '%s' "${WANT_X86[*]}")"
      if [[ "$MODE" == "print" ]]; then
        printf '  %s\n' "$L1"
        [[ -n "$L2" ]] && printf '  %s\n' "$L2"
        continue
      fi
      if [[ "$ARCHED" == "yes" ]]; then
        sed -i -E "s|^sha256sums_x86_64=.*|$(printf '%s' "$L1" | sed 's/[|&]/\\&/g')|" "$path"
        sed -i -E "s|^sha256sums_aarch64=.*|$(printf '%s' "$L2" | sed 's/[|&]/\\&/g')|" "$path"
      else
        sed -i -E "s|^sha256sums=.*|$(printf '%s' "$L1" | sed 's/[|&]/\\&/g')|" "$path"
      fi
      info "  已回填。"
      ;;

    *)
      # 检查
      check_one() {  # check_one 标签 声明值数组 期望值数组
        local label="$1"; shift
        local -a decl=() want=()
        local n="$1"; shift
        while [[ $# -gt 0 && $# -gt ${#want[@]} ]]; do :; done  # 占位，实际下面直接比
      }
      _decl_x86=("${DECL_X86[@]}")
      _decl_arm=("${DECL_ARM[@]}")
      ok="yes"
      if [[ "$OFFLINE" == "yes" ]]; then
        for v in "${_decl_x86[@]}"; do
          [[ "$v" == "SKIP" || -z "$v" ]] && ok="no"
        done
        if [[ "$ok" == "no" ]]; then
          warn "  ✘ sha256sums 仍是 SKIP（--offline 给不出真值；联网跑一次即可）"
          FAIL=1
        else
          info "  已填：${_decl_x86[*]}"
        fi
        continue
      fi
      # 逐元素比对
      for i in "${!WANT_X86[@]}"; do
        d="${_decl_x86[$i]:-}"
        w="${WANT_X86[$i]}"
        if [[ "$d" == "$w" ]]; then
          info "  ✔ [$i] 一致"
        elif [[ "$d" == "SKIP" || -z "$d" ]]; then
          warn "  ✘ [$i] 仍是 SKIP，应填 $w"; ok="no"
        else
          warn "  ✘ [$i] 不一致"; warn "      文件：$d"; warn "      实测：$w"; ok="no"
        fi
      done
      if [[ "$ARCHED" == "yes" ]]; then
        for i in "${!WANT_ARM[@]}"; do
          d="${_decl_arm[$i]:-}"
          w="${WANT_ARM[$i]}"
          if [[ "$d" == "$w" ]]; then
            info "  ✔ aarch64[$i] 一致"
          elif [[ "$d" == "SKIP" || -z "$d" ]]; then
            warn "  ✘ aarch64[$i] 仍是 SKIP，应填 $w"; ok="no"
          else
            warn "  ✘ aarch64[$i] 不一致：$d（实测 $w）"; ok="no"
          fi
        done
      fi
      [[ "$ok" == "yes" ]] || FAIL=1
      ;;
  esac
done

if [[ "$MODE" == "srcinfo" ]]; then
  command -v makepkg >/dev/null 2>&1 || die "没有 makepkg（只支持 Arch 系）"
  for pkgfile in PKGBUILD PKGBUILD-bin; do
    path="$PKG_DIR/$pkgfile"
    [[ -f "$path" ]] || continue
    if [[ "$pkgfile" == "PKGBUILD" ]]; then out="$PKG_DIR/.SRCINFO"; else out="$PKG_DIR/.SRCINFO-bin"; fi
    info ""
    info "== $pkgfile → $(basename "$out") =="
    if [[ "$DO_WRITE" == "yes" ]]; then
      ( cd "$PKG_DIR" && makepkg --printsrcinfo -p "$pkgfile" > "$out" ) || die "makepkg --printsrcinfo 失败：$pkgfile"
      info "  已写入 $out"
    else
      ( cd "$PKG_DIR" && makepkg --printsrcinfo -p "$pkgfile" ) || die "makepkg --printsrcinfo 失败：$pkgfile"
    fi
  done
  exit 0
fi

info ""
if [[ "$FAIL" -ne 0 ]]; then
  warn "有未填或对不上的 sha256sums。修法：scripts/aur-sums.sh --fill"
  exit 1
fi
info "全部一致。"
