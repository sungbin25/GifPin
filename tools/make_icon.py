"""GifPin 아이콘 (A안: 창 안에서 빼꼼 내다보는 민트 캐릭터) — 크기별로 따로 그려 작은 크기에서도 또렷하게."""
import io, struct, sys
from PIL import Image, ImageDraw

NAVY = (30, 39, 66, 255)
CREAM = (251, 246, 234, 255)
MINT = (99, 214, 182, 255)
MINT_DARK = (64, 178, 150, 255)
TEAL = (43, 179, 163, 255)
YELLOW = (242, 193, 78, 255)
WHITE = (255, 255, 255, 255)


def draw(size, detail):
    S = 8  # 크게 그려서 줄이면 테두리가 매끄러움
    W = size * S
    im = Image.new("RGBA", (W, W), (0, 0, 0, 0))
    d = ImageDraw.Draw(im)
    u = W / 100.0  # 100 단위 좌표계

    def R(x0, y0, x1, y1):
        return [x0 * u, y0 * u, x1 * u, y1 * u]

    # 작은 크기일수록 선을 굵게
    stroke = {16: 9.0, 24: 7.5, 32: 6.5, 48: 5.5}.get(size, 4.6) * u
    pad = 4 if size >= 48 else 2
    # 창
    win = R(pad, 12, 100 - pad, 92)
    radius = 14 * u
    d.rounded_rectangle(win, radius=radius, fill=CREAM, outline=NAVY, width=int(stroke))
    # 제목 표시줄 선과 점 (작은 크기에선 생략)
    if detail >= 2:
        bar_y = 31 * u
        d.line([pad * u + stroke / 2, bar_y, (100 - pad) * u - stroke / 2, bar_y], fill=NAVY, width=int(stroke * 0.8))
        r = 3.6 * u
        for i, col in enumerate([TEAL, YELLOW, NAVY]):
            cx, cy = (pad + 10 + i * 9) * u, 21.5 * u
            d.ellipse([cx - r, cy - r, cx + r, cy + r], fill=col)
    elif detail == 1:
        bar_y = 33 * u
        d.line([pad * u + stroke / 2, bar_y, (100 - pad) * u - stroke / 2, bar_y], fill=NAVY, width=int(stroke * 0.8))

    # 캐릭터: 창 안 오른쪽 아래에서 빼꼼 (아래쪽은 창 테두리에 가려짐)
    clip = Image.new("L", (W, W), 0)
    ImageDraw.Draw(clip).rounded_rectangle(R(pad, 12, 100 - pad, 92), radius=radius, fill=255)
    char = Image.new("RGBA", (W, W), (0, 0, 0, 0))
    c = ImageDraw.Draw(char)
    bx0, bx1 = (44, 92) if detail >= 1 else (34, 92)
    top = 46 if detail >= 1 else 40
    body = R(bx0, top, bx1, top + (bx1 - bx0) * 1.05)
    c.ellipse(body, fill=MINT, outline=NAVY, width=int(stroke))
    # 반짝임 (큰 크기만)
    if detail >= 2:
        hx, hy = (bx0 + 9) * u, (top + 9) * u
        c.ellipse([hx, hy, hx + 9 * u, hy + 5.5 * u], fill=(255, 255, 255, 190))
    # 눈
    ew, eh = (4.2, 6.2) if detail >= 1 else (6.0, 8.5)
    cx = (bx0 + bx1) / 2
    for dx in (-7.5, 7.5) if detail >= 1 else (-9, 9):
        ex, ey = (cx + dx) * u, (top + 17 + (0 if detail >= 1 else 3)) * u
        c.ellipse([ex - ew / 2 * u, ey - eh / 2 * u, ex + ew / 2 * u, ey + eh / 2 * u], fill=NAVY)
    # 손 (창 아래 테두리에 걸친 두 손)
    if detail >= 2:
        for hx in (cx - 12, cx + 12):
            hand = R(hx - 7, 75, hx + 7, 87)
            c.ellipse(hand, fill=MINT, outline=NAVY, width=int(stroke * 0.6))
    char.putalpha(Image.composite(char.getchannel("A"), Image.new("L", (W, W), 0), clip))
    im.alpha_composite(char)
    # 창 테두리를 한 번 더 (캐릭터 위로, 아래쪽 가장자리가 깔끔하게)
    frame = Image.new("RGBA", (W, W), (0, 0, 0, 0))
    ImageDraw.Draw(frame).rounded_rectangle(win, radius=radius, outline=NAVY, width=int(stroke))
    im.alpha_composite(frame)
    # 반짝 별 (큰 크기만)
    if detail >= 2:
        sx, sy, sr = 30 * u, 52 * u, 6 * u
        star = [(sx, sy - sr), (sx + sr * 0.28, sy - sr * 0.28), (sx + sr, sy), (sx + sr * 0.28, sy + sr * 0.28),
                (sx, sy + sr), (sx - sr * 0.28, sy + sr * 0.28), (sx - sr, sy), (sx - sr * 0.28, sy - sr * 0.28)]
        d2 = ImageDraw.Draw(im)
        d2.polygon(star, fill=YELLOW)
    return im.resize((size, size), Image.LANCZOS)


def bmp_entry(img):
    """32비트 BMP(DIB) 아이콘 항목: BITMAPINFOHEADER + 아래에서 위로 BGRA + AND 마스크."""
    w, h = img.size
    px = img.load()
    rows = []
    for y in range(h - 1, -1, -1):
        row = bytearray()
        for x in range(w):
            r, g, b, a = px[x, y]
            row += bytes((b, g, r, a))
        rows.append(bytes(row))
    mask_row = ((w + 31) // 32) * 4
    header = struct.pack("<IiiHHIIiiII", 40, w, h * 2, 1, 32, 0, 0, 0, 0, 0, 0)
    return header + b"".join(rows) + b"\x00" * (mask_row * h)


def write_ico(path, images):
    entries = []
    for img in images:
        if img.size[0] >= 256:
            buf = io.BytesIO()
            img.save(buf, "PNG")
            entries.append((img.size[0], buf.getvalue()))
        else:
            entries.append((img.size[0], bmp_entry(img)))
    out = io.BytesIO()
    out.write(struct.pack("<HHH", 0, 1, len(entries)))
    offset = 6 + 16 * len(entries)
    for size, data in entries:
        out.write(struct.pack("<BBBBHHII", 0 if size >= 256 else size, 0 if size >= 256 else size, 0, 0, 1, 32, len(data), offset))
        offset += len(data)
    for _, data in entries:
        out.write(data)
    open(path, "wb").write(out.getvalue())


if __name__ == "__main__":
    sizes = {16: 0, 24: 1, 32: 1, 48: 2, 64: 2, 256: 2}
    imgs = [draw(s, d) for s, d in sizes.items()]
    write_ico(sys.argv[1], imgs)
    preview = Image.new("RGBA", (256 + 64 + 48 + 32 + 24 + 16 + 120, 280), (40, 44, 52, 255))
    x = 10
    for img in reversed(imgs):
        preview.alpha_composite(img, (x, 10))
        x += img.size[0] + 20
    light = preview.copy()
    light.paste((240, 240, 240, 255), [0, 0, light.width, light.height])
    x = 10
    for img in reversed(imgs):
        light.alpha_composite(img, (x, 10))
        x += img.size[0] + 20
    both = Image.new("RGBA", (preview.width, 560))
    both.paste(preview, (0, 0)); both.paste(light, (0, 280))
    both.save(sys.argv[2])
