"""生成 ANS 签到桌面应用的图标（多尺寸 .ico）

源图：desktop/icon-source.png（方形立绘，带文字）。

两个关键点：
1. 同一构图在 16~32px 会糊成一团，所以按尺寸用不同裁切（小尺寸取脸部特写）。
2. Pillow 的 ico 保存会忽略"每个尺寸单独处理"，只拿一张图缩放出所有尺寸。
   所以这里自己拼 ICO 容器，每个尺寸写入各自的位图（经典 DIB 格式，兼容性最好）。
"""
import struct
from PIL import Image, ImageEnhance

SRC = "desktop/icon-source.png"
SIZES = [16, 24, 32, 48, 64, 128, 256]

# 每种尺寸的取景比例（1.0 = 完整画面，越小越贴近脸部）
CROP = {16: 0.36, 24: 0.38, 32: 0.40, 48: 0.58, 64: 1.0, 128: 1.0, 256: 1.0}

# 取景中心（纵向）：实测眼睛在 0.35~0.42、嘴在 0.52~0.58，取 0.47 正好框住整张脸
CENTER_Y = {16: 0.47, 24: 0.47, 32: 0.47, 48: 0.47, 64: 0.5, 128: 0.5, 256: 0.5}


def load_base():
    img = Image.open(SRC).convert("RGBA")
    w, h = img.size
    side = min(w, h)
    left, top = (w - side) // 2, (h - side) // 2
    return img.crop((left, top, left + side, top + side))


def zoom_center(im, ratio, cy_ratio):
    """按比例从指定纵向位置裁切"""
    if ratio >= 1.0:
        return im
    w, h = im.size
    nw, nh = int(w * ratio), int(h * ratio)
    cx, cy = w // 2, int(h * cy_ratio)
    left = max(0, min(w - nw, cx - nw // 2))
    top = max(0, min(h - nh, cy - nh // 2))
    return im.crop((left, top, left + nw, top + nh))


def dib_bytes(img):
    """把 RGBA 图转成 ICO 里用的 DIB（BITMAPINFOHEADER + BGRA 自底向上 + AND 掩码）"""
    w, h = img.size
    header = struct.pack(
        "<IiiHHIIiiII",
        40,      # biSize
        w,       # biWidth
        h * 2,   # biHeight（包含掩码，所以 x2）
        1,       # biPlanes
        32,      # biBitCount
        0,       # biCompression = BI_RGB
        0, 0, 0, 0, 0,
    )
    px = img.tobytes()  # RGBA
    rows = []
    for y in range(h - 1, -1, -1):
        row = bytearray()
        base = y * w * 4
        for x in range(w):
            i = base + x * 4
            row += bytes((px[i + 2], px[i + 1], px[i], px[i + 3]))  # BGRA
        rows.append(bytes(row))
    pixels = b"".join(rows)
    # AND 掩码：1bpp，每行按 4 字节对齐，全 0（全部不透明）
    mask_stride = ((w + 31) // 32) * 4
    mask = b"\x00" * (mask_stride * h)
    return header + pixels + mask


def write_ico(path, frames):
    """frames: [(size, pil_image), ...]"""
    blobs = [(size, dib_bytes(frame)) for size, frame in frames]
    count = len(blobs)
    header = struct.pack("<HHH", 0, 1, count)
    offset = 6 + 16 * count
    entries, data = b"", b""
    for size, blob in blobs:
        dim = 0 if size >= 256 else size
        entries += struct.pack("<BBBBHHII", dim, dim, 0, 0, 1, 32, len(blob), offset)
        offset += len(blob)
        data += blob
    with open(path, "wb") as f:
        f.write(header + entries + data)


def face_focus(im):
    """自动找出脸部焦点：口鼻是画面里最大的一片浅奶油色区域，
    用它的包围盒中心作为裁切中心，比手工猜比例靠谱。"""
    small = im.resize((256, 256), Image.LANCZOS).convert("RGB")
    w, h = small.size
    px = small.load()
    xs, ys = [], []
    for y in range(0, h, 2):
        for x in range(0, w, 2):
            r, g, b = px[x, y]
            # 奶油色/浅粉：亮、偏暖
            if r > 228 and g > 200 and b > 185 and r >= g >= b:
                xs.append(x)
                ys.append(y)
    if len(xs) < 50:
        return 0.5, 0.55
    cx = (min(xs) + max(xs)) / 2 / w
    cy = (min(ys) + max(ys)) / 2 / h
    return cx, cy


base = load_base()
FACE_X, FACE_Y = 0.5, 0.47

frames = []
for size in SIZES:
    # 中心对准脸部；小尺寸放大倍数更高
    ratio = CROP[size]
    cy_ratio = CENTER_Y[size]
    w, h = base.size
    nw = nh = int(w * ratio)
    cx, cy = int(w * FACE_X), int(h * cy_ratio)
    left = max(0, min(w - nw, cx - nw // 2))
    top = max(0, min(h - nh, cy - nh // 2))
    frame = base.crop((left, top, left + nw, top + nh)).resize((size, size), Image.LANCZOS)
    if size <= 32:
        frame = ImageEnhance.Sharpness(frame).enhance(1.7)
        frame = ImageEnhance.Contrast(frame).enhance(1.1)
    elif size <= 48:
        frame = ImageEnhance.Sharpness(frame).enhance(1.2)
    frames.append((size, frame))

write_ico("desktop/app.ico", frames)
frames[-1][1].save("desktop/app-preview.png")

# 界面内使用的 logo（走 base64 内联，尺寸越小越好）
base.resize((256, 256), Image.LANCZOS).save("desktop/logo-256.png", optimize=True)
base.resize((128, 128), Image.LANCZOS).save("desktop/logo-128.png", optimize=True)
zoom_center(base, 0.56, 0.60).resize((64, 64), Image.LANCZOS).save("desktop/logo-64.png", optimize=True)
zoom_center(base, 0.56, 0.60).resize((32, 32), Image.LANCZOS).save("desktop/logo-32.png", optimize=True)

print("图标已生成：desktop/app.ico", SIZES)
