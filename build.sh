#!/usr/bin/env bash
# 构建 Windows 版。可以在 macOS / Linux 上交叉构建（本项目当前就是这么开发的），
# 也可以在 Windows 上直接跑，命令完全一样。
#
#   ./build.sh              发布 win-x64 自包含单文件并打包 zip
#   ./build.sh debug        调试构建
#   ./build.sh --no-single  发布多文件版（启动略快，但文件多）
set -euo pipefail

cd "$(dirname "$0")"

CONFIG=Release
SINGLE=true
for arg in "$@"; do
  case "$arg" in
    debug) CONFIG=Debug ;;
    --no-single) SINGLE=false ;;
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
OUT=artifacts/$VERSION
COMMON="-c $CONFIG -r win-x64 --self-contained true -p:EnableWindowsTargeting=true -p:DebugType=none -p:DebugSymbols=false"

echo "▸ dotnet $(dotnet --version) / $CONFIG / win-x64 / 单文件=$SINGLE"

if [ "$SINGLE" = true ]; then
  # 单文件 + 内部压缩：自包含包原本 160MB 左右，压缩后明显变小
  eval dotnet publish "$PROJECT" $COMMON \
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
    -p:EnableCompressionInSingleFile=true -o "$OUT/exe" | tail -3
else
  eval dotnet publish "$PROJECT" $COMMON -o "$OUT/exe" | tail -3
fi

if [ "$CONFIG" != Release ]; then
  echo "✅ $OUT/exe"
  exit 0
fi

echo "▸ 打包"
rm -rf "$OUT/pkg"
mkdir -p "$OUT/pkg"
cp -R "$OUT/exe/." "$OUT/pkg/"
[ -f docs/安装说明.txt ] && cp docs/安装说明.txt "$OUT/pkg/"
( cd "$OUT/pkg" && zip -qr "../$NAME-$VERSION-win-x64.zip" . )
rm -rf "$OUT/pkg"

SIZE=$(du -h "$OUT/$NAME-$VERSION-win-x64.zip" | cut -f1)
echo "✅ $OUT/$NAME-$VERSION-win-x64.zip ($SIZE)"
