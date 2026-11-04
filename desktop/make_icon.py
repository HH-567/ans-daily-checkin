"""生成 ANS 签到桌面应用的图标（多尺寸 .ico）"""
from PIL import Image, ImageDraw

SIZE = 512
img = Image.new("RGBA", (SIZE, SIZE), (0, 0, 0, 0))
d = ImageDraw.Draw(img)

# 圆角方形渐变背景
radius = int(SIZE * 0.22)
grad = Image.new("RGB", (SIZE, SIZE))
gd = ImageDraw.Draw(grad)
c1 = (79, 70, 229)    # indigo-600
c2 = (168, 85, 247)   # purple-500
for y in range(SIZE):
    t = y / (SIZE - 1)
    gd.line(
        [(0, y), (SIZE, y)],
        fill=(
            int(c1[0] + (c2[0] - c1[0]) * t),
            int(c1[1] + (c2[1] - c1[1]) * t),
            int(c1[2] + (c2[2] - c1[2]) * t),
        ),
    )
mask = Image.new("L", (SIZE, SIZE), 0)
ImageDraw.Draw(mask).rounded_rectangle([0, 0, SIZE - 1, SIZE - 1], radius=radius, fill=255)
img.paste(grad, (0, 0), mask)

# 白色对勾
w = int(SIZE * 0.095)
pts = [
    (int(SIZE * 0.26), int(SIZE * 0.53)),
    (int(SIZE * 0.43), int(SIZE * 0.70)),
    (int(SIZE * 0.75), int(SIZE * 0.31)),
]
d.line(pts, fill=(255, 255, 255, 255), width=w, joint="curve")
for p in (pts[0], pts[2]):
    d.ellipse([p[0] - w // 2, p[1] - w // 2, p[0] + w // 2, p[1] + w // 2], fill=(255, 255, 255, 255))

img.save("desktop/app.ico", sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)])
img.resize((256, 256), Image.LANCZOS).save("desktop/app-preview.png")
print("图标已生成：desktop/app.ico")
