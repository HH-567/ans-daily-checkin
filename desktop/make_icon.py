"""生成 ANS 签到桌面应用的图标（多尺寸 .ico）

源图：desktop/icon-source.png（方形立绘）。
做法：居中裁成正方形 → 逐尺寸高质量缩放 → 输出多分辨率 ico。
小尺寸（16/24/32）细节会糊，所以额外做了锐化，让它在任务栏上也能认出轮廓。
"""
from PIL import Image, ImageEnhance

SRC = "desktop/icon-source.png"
SIZES = [16, 24, 32, 48, 64, 128, 256]

img = Image.open(SRC).convert("RGBA")

# 居中裁成正方形（源图本来接近方形，这里只是兜底）
w, h = img.size
side = min(w, h)
left = (w - side) // 2
top = (h - side) // 2
img = img.crop((left, top, left + side, top + side))

# 高分辨率底图，缩放时更干净
base = img.resize((512, 512), Image.LANCZOS)

frames = []
for size in SIZES:
    frame = base.resize((size, size), Image.LANCZOS)
    if size <= 32:
        # 小尺寸加一点锐度和对比，避免糊成一团
        frame = ImageEnhance.Sharpness(frame).enhance(1.6)
        frame = ImageEnhance.Contrast(frame).enhance(1.08)
    frames.append(frame)

frames[-1].save("desktop/app.ico", format="ICO", sizes=[(s, s) for s in SIZES])
frames[-1].save("desktop/app-preview.png")
print("图标已生成：desktop/app.ico", SIZES)
