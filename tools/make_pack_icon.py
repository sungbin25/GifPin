"""GifPin 팩(.gifpin) 파일 아이콘 — 열린 상자에서 민트 캐릭터가 빼꼼. 사용법: python3 make_pack_icon.py out.ico preview.png"""
import sys
from PIL import Image, ImageDraw
from make_icon import NAVY, CREAM, MINT, YELLOW, write_ico

BOX_SIDE = (236, 226, 204, 255)


def draw(size, detail):
    S = 8
    W = size * S
    im = Image.new("RGBA", (W, W), (0, 0, 0, 0))
    d = ImageDraw.Draw(im)
    u = W / 100.0

    def P(*pts):
        return [(x * u, y * u) for x, y in pts]

    stroke = {16: 9.0, 24: 7.5, 32: 6.5, 48: 5.5}.get(size, 4.6) * u
    sw = int(stroke)
    top = 50 if detail == 0 else 46
    # 상자 뚜껑 (열린 날개) — 캐릭터 뒤
    if detail >= 1:
        d.polygon(P((10, top), (40, top), (30, top - 15), (2, top - 10)), fill=BOX_SIDE, outline=NAVY, width=int(sw * 0.8))
        d.polygon(P((60, top), (90, top), (98, top - 10), (70, top - 15)), fill=BOX_SIDE, outline=NAVY, width=int(sw * 0.8))
    # 캐릭터: 상자 위로 빼꼼 (아래쪽은 상자 앞면에 가려짐)
    bx0, bx1 = (26, 74) if detail >= 1 else (22, 78)
    btop = top - (30 if detail >= 1 else 34)
    d.ellipse(P((bx0, btop), (bx1, btop + (bx1 - bx0) * 1.05)), fill=MINT, outline=NAVY, width=sw)
    if detail >= 2:
        d.ellipse(P((bx0 + 9, btop + 8), (bx0 + 18, btop + 13.5)), fill=(214, 246, 236, 255))
    cx = (bx0 + bx1) / 2
    ew, eh = (4.2, 6.2) if detail >= 1 else (6.5, 9.0)
    ey = btop + (15 if detail >= 1 else 16)
    for dx in ((-7, 7) if detail >= 1 else (-9, 9)):
        d.ellipse(P((cx + dx - ew / 2, ey - eh / 2), (cx + dx + ew / 2, ey + eh / 2)), fill=NAVY)
    # 상자 앞면
    d.rounded_rectangle(P((8, top), (92, 94)), radius=8 * u, fill=CREAM, outline=NAVY, width=sw)
    # 테이프 (노란 띠)
    if detail >= 1:
        d.rectangle(P((44, top + stroke / u / 2), (56, 94 - stroke / u / 2)), fill=YELLOW)
    # 손 (상자 가장자리를 잡음)
    if detail >= 2:
        for hx in (cx - 14, cx + 14):
            d.ellipse(P((hx - 7, top - 6), (hx + 7, top + 6)), fill=MINT, outline=NAVY, width=int(sw * 0.6))
    return im.resize((size, size), Image.LANCZOS)


if __name__ == "__main__":
    sizes = {16: 0, 24: 1, 32: 1, 48: 2, 64: 2, 256: 2}
    imgs = [draw(s, d) for s, d in sizes.items()]
    write_ico(sys.argv[1], imgs)
    pv = Image.new("RGBA", (sum(i.size[0] + 20 for i in imgs) + 10, 560), (40, 44, 52, 255))
    pv.paste((240, 240, 240, 255), [0, 280, pv.width, 560])
    x = 10
    for img in reversed(imgs):
        pv.alpha_composite(img, (x, 10))
        pv.alpha_composite(img, (x, 290))
        x += img.size[0] + 20
    pv.save(sys.argv[2])
