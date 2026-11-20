#!/usr/bin/env bash
# 构建 Windows 版。可以在 macOS / Linux 上交叉构建（本项目当前就是这么开发的），
# 也可以在 Windows 上直接跑，命令完全一样。
#
#   ./build.sh                发布 win-x64 框架依赖版（默认）并打包 zip
#   ./build.sh debug          调试构建
#   ./build.sh --self-contained  改成自包含（把 .NET 一起打进 exe，包会大很多）
#
# 默认框架依赖：包只有几 MB，运行时由用户装。缺少运行时时 Windows 会自己弹框
# 引导去下载 .NET 8 Desktop Runtime，用户点一下就行 —— 比塞 160MB 进包里合理。
set -euo pipefail

cd "$(dirname "$0")"

CONFIG=Release
SELF_CONTAINED=false
for arg in "$@"; do
  case "$arg" in
    debug) CONFIG=Debug ;;
    --self-contained) SELF_CONTAINED=true ;;
    *) echo "未知参数：$arg" >&2; exit 2 ;;
  esac
done

# .NET SDK 位置：优先用 PATH 里的，其次用 ~/.dotnet（dotnet-install.sh 的默认位置）
if ! command -v dotnet >/dev/null 2>&1 && [ -x "$HOME/.dotnet/dotnet" ]; then
  export DOTNET_ROOT="$HOME/.dotnet"
  export PATH="$HOME/.dotnet:$PATH"
fi
command -v dotnet >/dev/null 2>&1 || { echo "找不到 dotnet，请先安装 .NET 8 SDK" >&2; exit 1; }

PROJECT=src/GitLabPipelineMonitor/GitLabPipelineMonitor.csproj
VERSION=$(grep -oE '<Version>[^<]+' "$PROJECT" | sed 's/<Version>//')
VERSION=${VERSION:-1.0.0}
NAME="GitLab Pipeline Monitor"
SUFFIX=$([ "$SELF_CONTAINED" = true ] && echo "win-x64-selfcontained" || echo "win-x64")
OUT="$(pwd)/artifacts/$VERSION"   # 绝对路径：打包时要 cd 进临时目录，相对路径会指错地方
EXE="$OUT/exe-$SUFFIX"

echo "▸ dotnet $(dotnet --version) / $CONFIG / win-x64 / 自包含=$SELF_CONTAINED"

# 两种模式都是单文件（把自己的 dll 收进 exe），区别只在于要不要带上 .NET 运行时。
if [ "$SELF_CONTAINED" = true ]; then
  dotnet publish "$PROJECT" -c "$CONFIG" -r win-x64 --self-contained true \
    -p:EnableWindowsTargeting=true -p:PublishSingleFile=true \
    -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true \
    -p:DebugType=none -p:DebugSymbols=false -o "$EXE" | tail -3
else
  dotnet publish "$PROJECT" -c "$CONFIG" -r win-x64 --self-contained false \
    -p:EnableWindowsTargeting=true -p:PublishSingleFile=true \
    -p:DebugType=none -p:DebugSymbols=false -o "$EXE" | tail -3
fi

echo "▸ 产物清单"
ls -la "$EXE" | tail -n +2 | awk '{printf "   %10s  %s\n", $5, $9}'

# 框架依赖模式下做个自检：产物里不该出现 .NET 运行时自己的 dll
if [ "$SELF_CONTAINED" = false ]; then
  BLOATED=$(find "$EXE" -maxdepth 1 -type f \( -name 'PresentationCore.dll' -o -name 'System.Private.CoreLib.dll' \) | wc -l | tr -d ' ')
  if [ "$BLOATED" != "0" ]; then
    echo "⚠ 框架依赖包里出现了运行时程序集，说明 self-contained 没关掉" >&2
    exit 1
  fi
  echo "   ✓ 确认没有把 .NET 运行时打进包"
fi

if [ "$CONFIG" != Release ]; then
  echo "✅ $EXE"
  exit 0
fi

echo "▸ 打包"
rm -rf "$OUT/pkg"
mkdir -p "$OUT/pkg"
cp -R "$EXE/." "$OUT/pkg/"
[ -f docs/安装说明.txt ] && cp docs/安装说明.txt "$OUT/pkg/"
# 用 python 打包：中文文件名会正确打上 UTF-8 标志位（zip 命令不打，
# Windows 自带解压会把字节当 GBK 读，文件名变乱码）。没有 python3 时退回 zip。
rm -f "$OUT/$NAME-$VERSION-$SUFFIX.zip"
if command -v python3 >/dev/null 2>&1; then
  ( cd "$OUT/pkg" && python3 - "$OUT/$NAME-$VERSION-$SUFFIX.zip" <<'PY'
import os, sys, zipfile
out = os.path.abspath(sys.argv[1])
with zipfile.ZipFile(out, "w", zipfile.ZIP_DEFLATED) as z:
    for root, _, files in os.walk("."):
        for name in sorted(files):
            path = os.path.join(root, name)
            z.write(path, os.path.relpath(path, "."))
PY
  )
else
  ( cd "$OUT/pkg" && zip -qr "../$NAME-$VERSION-$SUFFIX.zip" . )
fi
rm -rf "$OUT/pkg"

SIZE=$(du -h "$OUT/$NAME-$VERSION-$SUFFIX.zip" | cut -f1)
echo "✅ $OUT/$NAME-$VERSION-$SUFFIX.zip ($SIZE)"
