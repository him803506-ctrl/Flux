#!/usr/bin/env bash
# 打包并签名 Flux 稀疏包
#
# 包内容目录：dist/_pkgroot          （只有清单 + 图标）
# 外部内容位置：dist/Flux            （真实的 exe / dll，安装后是 Program Files\Flux）
# 产物：dist/_pkg/Flux.ShellIntegration.msix

set -e

ROOT="C:/Users/HKN/WorkBuddy/2026-10-04-12-27-25/SmartUnzip"
SDK_BIN="/c/Program Files (x86)/Windows Kits/10/bin/10.0.26100.0/x64"
PKGROOT="$ROOT/dist/_pkgroot"
OUT="$ROOT/dist/_pkg"
PFX="$ROOT/certs/Flux-Sparse.pfx"
PFX_PW="Flux"

mkdir -p "$OUT"

MSIX="$OUT/Flux.ShellIntegration.msix"

echo "== 0/3 刷新包内容目录 =="
cp "$ROOT/src/SmartUnzip.ShellExt/Package/AppxManifest.xml" "$PKGROOT/AppxManifest.xml"
for f in StoreLogo.png Square150x150Logo.png Square44x44Logo.png; do
  cp "$ROOT/src/SmartUnzip.ShellExt/Package/Assets/$f" "$PKGROOT/$f"
done
ls -la "$PKGROOT"

echo
echo "== 1/3 打包 =="
cd "$PKGROOT"
# /nv (noValidation)：外部内容包（AllowExternalContent）的 exe/dll 不在包内，
# Windows 安装时由 -ExternalLocation 解析，因此必须跳过"文件存在"校验。
MSYS_NO_PATHCONV=1 MSYS2_ARG_CONV_EXCL='*' \
  "$SDK_BIN/makeappx.exe" pack /d . /p "$MSIX" /o /nv 2>&1 | tail -3

echo
echo "== 2/3 签名 =="
MSYS_NO_PATHCONV=1 MSYS2_ARG_CONV_EXCL='*' \
  "$SDK_BIN/signtool.exe" sign /fd SHA256 /f "$PFX" /p "$PFX_PW" "$MSIX" 2>&1 | tail -3

echo
echo "== 3/3 校验签名 =="
MSYS_NO_PATHCONV=1 MSYS2_ARG_CONV_EXCL='*' \
  "$SDK_BIN/signtool.exe" verify /pa "$MSIX" 2>&1 | tail -4

echo
ls -la "$OUT"
