"""生成稀疏包所需的 Assets 图片（StoreLogo / Square150 / Square44）。

用软件图标 Flux.png 缩放出各尺寸，避免包清单校验时报缺图。
"""
import os
from PIL import Image

# 仓库根目录 = 本脚本所在目录
BASE = os.path.dirname(os.path.abspath(__file__))
SRC = os.path.join(BASE, "src", "SmartUnzip", "Assets", "Flux.png")
OUT = os.path.join(BASE, "src", "SmartUnzip.ShellExt", "Package", "Assets")

SIZES = {
    "StoreLogo.png": 50,
    "Square44x44Logo.png": 44,
    "Square150x150Logo.png": 150,
    # 高对比/缩放变体也一并生成，避免某些 Windows 版本挑剔
    "Square44x44Logo.targetsize-44_altform-unplated.png": 44,
    "Square44x44Logo.targetsize-44_altform-lightunplated.png": 44,
}

os.makedirs(OUT, exist_ok=True)

if not os.path.isfile(SRC):
    raise SystemExit("找不到源图标：" + SRC)

img = Image.open(SRC).convert("RGBA")
print("源图尺寸:", img.size)

for name, size in SIZES.items():
    dst = os.path.join(OUT, name)
    img.resize((size, size), Image.LANCZOS).save(dst, "PNG")
    print("  生成", name, size, os.path.getsize(dst), "bytes")

print("\n输出目录:", OUT)
