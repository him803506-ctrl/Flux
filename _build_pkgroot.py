"""构建 Flux 稀疏包的**内容目录**（严格对照 WinRAR 的做法）。

WinRAR 的包目录只有：
    AppxManifest.xml
    AppxBlockMap.xml        （打包时自动生成）
    AppxSignature.p7x       （签名时自动生成）
    StoreLogo.png / Square150x150Logo.png / Square44x44Logo.png / RarExtLogo*.png

**没有 DLL，没有 EXE** —— 二进制通过 uap10:AllowExternalContent
指向 <安装目录>，由 Add-AppxPackage -ExternalLocation 建立映射。

因此我们也只放清单 + 图标。包外的真实文件在 dist/Flux/ 下。
"""
import os
import shutil

# 仓库根目录 = 本脚本所在目录（clone 到任意路径都能直接跑）
BASE = os.path.dirname(os.path.abspath(__file__))
PKG_SRC = os.path.join(BASE, "src", "SmartUnzip.ShellExt", "Package")
OUT = os.path.join(BASE, "dist", "_pkgroot")   # 稀疏包内容目录

if os.path.isdir(OUT):
    shutil.rmtree(OUT)
os.makedirs(OUT)

# 清单
shutil.copy2(os.path.join(PKG_SRC, "AppxManifest.xml"),
             os.path.join(OUT, "AppxManifest.xml"))
print("AppxManifest.xml")

# 图标（扁平放包根，与 WinRAR 一致）
assets = os.path.join(PKG_SRC, "Assets")
mapping = {
    "StoreLogo.png": "StoreLogo.png",
    "Square150x150Logo.png": "Square150x150Logo.png",
    "Square44x44Logo.png": "Square44x44Logo.png",
}
for src, dst in mapping.items():
    s = os.path.join(assets, src)
    if os.path.isfile(s):
        shutil.copy2(s, os.path.join(OUT, dst))
        print(dst)

print("\n包内容目录：", OUT)
for f in sorted(os.listdir(OUT)):
    print("  %-30s %8d" % (f, os.path.getsize(os.path.join(OUT, f))))
