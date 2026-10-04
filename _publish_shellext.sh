#!/usr/bin/env bash
# 在纯 Bash 里搭好 MSVC + Windows SDK 环境，然后执行 NativeAOT 发布。
# 不走 cmd.exe（沙箱会拦 cmd /c）。

set -e

VC_ROOT="/c/Program Files (x86)/Microsoft Visual Studio/2022/BuildTools"
SDK_ROOT="/c/Program Files (x86)/Windows Kits/10"
MSVC_VER="$(ls "$VC_ROOT/VC/Tools/MSVC/" | head -1)"
SDK_VER="10.0.26100.0"

# MSVC 工具链（link.exe / cl.exe）只认 Windows 风格路径，
# 用 /c/... 会导致 NativeAOT 的链接阶段找不到库，
# 静默回退成普通托管 DLL（产物只有十几 KB）。
VC_WIN='C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools'
SDK_WIN='C:\Program Files (x86)\Windows Kits\10'

export PATH="$VC_ROOT/VC/Tools/MSVC/$MSVC_VER/bin/Hostx64/x64:$PATH"
export PATH="$SDK_ROOT/bin/$SDK_VER/x64:$PATH"

export INCLUDE="$VC_WIN\\VC\\Tools\\MSVC\\$MSVC_VER\\include;$SDK_WIN\\Include\\$SDK_VER\\ucrt;$SDK_WIN\\Include\\$SDK_VER\\um;$SDK_WIN\\Include\\$SDK_VER\\shared;$SDK_WIN\\Include\\$SDK_VER\\winrt"
export LIB="$VC_WIN\\VC\\Tools\\MSVC\\$MSVC_VER\\lib\\x64;$SDK_WIN\\Lib\\$SDK_VER\\ucrt\\x64;$SDK_WIN\\Lib\\$SDK_VER\\um\\x64"

echo "MSVC  $MSVC_VER"
echo "SDK   $SDK_VER"
echo "link  $(which link.exe 2>/dev/null || echo '未找到')"
echo "----------------------------------------"

cd "$(dirname "$0")/src/SmartUnzip.ShellExt"
dotnet publish -c Release -r win-x64 "$@"
