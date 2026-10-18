# make_mask.py — ワールドマップ絵(worldmap.jpg)から海・砂浜・波打ち際の距離を割り出して、シェーダー用のマスク画像を作る
#   出力 worldmap_mask.png (RGB 1920x1072・4 枚の時間帯の絵は同じ構図なので 1 枚で共通):
#     R = 水面(海・川)の度合い 0..1（縁はなめらか）
#     G = 砂浜の波打ち際からの符号付き距離: 0.5=波打ち際 / 1.0=沖へ 64px / 0.0=陸へ 32px
#     B = 「砂浜の岸」の度合い（岩・岸壁・港は 0 → 打ち寄せる波を出さない）
#   worldmap_noise.png (グレー) = 岸沿いのゆらぎ（波の来る時刻と駆け上がりの長さを場所ごとにずらす低周波ノイズ）
#   確認用 debug_*.png も出す。
import os
import numpy as np
from PIL import Image, ImageDraw
from scipy import ndimage as ndi

SRC = r'D:\actools-build\run\Data\Static\gt7\worldmap.jpg'
OUT = os.path.dirname(os.path.abspath(__file__))

img = np.asarray(Image.open(SRC).convert('RGB')).astype(np.float32) / 255.0
H, W, _ = img.shape
r, g, b = img[..., 0], img[..., 1], img[..., 2]
mx = img.max(-1); mn = img.min(-1)
v = mx
s = np.where(mx > 1e-4, (mx - mn) / np.maximum(mx, 1e-4), 0)
d = np.maximum(mx - mn, 1e-6)
hue = np.where(mx == r, (60 * ((g - b) / d)) % 360,
      np.where(mx == g, 60 * ((b - r) / d) + 120, 60 * ((r - g) / d) + 240))

def poly_mask(polys):
    m = Image.new('L', (W, H), 0)
    dr = ImageDraw.Draw(m)
    for p in polys: dr.polygon(p, fill=255)
    return np.asarray(m) > 0

# 港（桟橋・岸壁・船）: 打ち寄せ波は出さない（揺らぎだけ）
HARBOR = [[(110, 470), (700, 470), (700, 700), (560, 790), (300, 800), (110, 700)]]
# 水と間違えやすい物（ガラス張りのディーラー・崖の影）
NOT_WATER = [[(540, 450), (720, 450), (720, 640), (540, 640)]]
# 打ち寄せ波を出さない所（港の横の岩場・川と橋）
NO_WAVES = [[(640, 680), (780, 680), (780, 760), (640, 760)], [(700, 0), (1920, 0), (1920, 560), (700, 560)]]
harbor = poly_mask(HARBOR) | poly_mask(NO_WAVES)
notwater = poly_mask(NOT_WATER)

# 水らしい色: 青〜シアン（明るさ 0.38 以上＝崖の影やガラスは外れる）＋浅瀬の灰色（低彩度・中くらいの明るさ）
deep_like = (hue > 150) & (hue < 240) & (s > 0.12) & (v > 0.38)
shallow_like = (s < 0.14) & (v > 0.40) & (v < 0.80)
water_like = (deep_like | shallow_like) & ~notwater
water_like = ndi.binary_opening(water_like, iterations=1)

# 沖と川の種から、つながっている部分だけ
lab, n = ndi.label(water_like)
seeds = [(900, 400), (1000, 900), (150, 950), (470, 1750), (1050, 1500), (330, 60)]   # (y, x) 沖の海・川
keep_ids = set()
for (y, x) in seeds:
    if lab[y, x] > 0: keep_ids.add(lab[y, x])
water = np.isin(lab, list(keep_ids))
# 浅瀬の灰色は「深い水(青〜シアン)から 14px 以内」だけ水にする（影になった砂も同じ灰色で、海につながって
#   影の砂まで水扱いになり、影の境目に泡の線が出た・2026-09-28）
deep = water & deep_like
d_to_deep = ndi.distance_transform_edt(~deep)
water = water & (deep_like | (d_to_deep <= 14))
lab2, n2 = ndi.label(water)
keep2 = set(lab2[y, x] for (y, x) in seeds if lab2[y, x] > 0)
water = np.isin(lab2, list(keep2))
# 小さな穴（ノイズ）は埋める。船の穴（数千 px）は残す
holes = ndi.binary_fill_holes(water) & ~water
hl, hn = ndi.label(holes)
hs = ndi.sum(holes, hl, range(1, hn + 1))
small = np.zeros(hn + 1, bool); small[1:] = hs < 400
water = water | small[hl]
water = ndi.binary_closing(water, iterations=1)

# 波打ち際は手でなぞった線（2026-09-28）: 色の判定では「影になった砂」と「砂が透けた浅瀬」が同じ灰色で分けられず、
#   影の境目に泡が出たり、波が浅瀬の中で寄せ引きしたりした。絵は 1 枚きりで変わらないので、乾いた砂の縁を
#   格子付きの拡大図から読み取って指定する（trace_*.png）。sea = 線を海側で閉じた多角形（どちらが海かを決めるだけ）
SHORES = [
    # 左上の砂浜（上端から港の岸壁の手前まで）
    ([(0, 288), (20, 300), (40, 310), (60, 316), (80, 320), (100, 324), (120, 327), (140, 331), (160, 339), (180, 351),
      (196, 365), (208, 381), (216, 400), (218, 420), (216, 440), (212, 460), (208, 480), (204, 500)],
     [(0, 520)]),
    # 入り江（岩場の先から、上の影の縁→日なたの弧→崖の下の細い砂の帯の先まで）
    ([(720, 698), (745, 685), (770, 676), (810, 666), (850, 663), (880, 665), (900, 669), (930, 674), (960, 685), (985, 696),
      (1005, 709), (1020, 724), (1030, 740), (1038, 760), (1040, 780), (1038, 800), (1030, 818), (1018, 835), (1008, 855),
      (1004, 875), (1006, 895), (1013, 913), (1025, 926), (1040, 935), (1060, 942), (1080, 947), (1100, 950), (1140, 954),
      (1180, 958), (1220, 959), (1256, 956), (1272, 951)],
     [(1272, 1072), (640, 1072), (640, 700)]),
    # 右下の砂浜
    ([(1440, 898), (1470, 898), (1500, 899), (1520, 908), (1550, 920), (1585, 931), (1620, 942), (1650, 955), (1680, 970),
      (1705, 988), (1725, 1008), (1740, 1030), (1750, 1050), (1758, 1072)],
     [(1400, 1072), (1400, 898)]),
]
best_d = np.full((H, W), 1e9, np.float32)
best_sd = np.zeros((H, W), np.float32)
best_fade = np.zeros((H, W), np.float32)
yy, xx = np.mgrid[0:H, 0:W]
for pts, close in SHORES:
    lm = Image.new('L', (W, H), 0); ImageDraw.Draw(lm).line(pts, fill=255, width=1)
    line = np.asarray(lm) > 0
    pm = Image.new('L', (W, H), 0); ImageDraw.Draw(pm).polygon(pts + close, fill=255)
    sea = np.asarray(pm) > 0
    dl = ndi.distance_transform_edt(~line).astype(np.float32)
    sd = np.where(sea, dl, -dl)
    # 線の両端では波を弱める（端点から 36px で 0→1）
    e0 = np.hypot(xx - pts[0][0], yy - pts[0][1]); e1 = np.hypot(xx - pts[-1][0], yy - pts[-1][1])
    fade = np.clip(np.minimum(e0, e1) / 36.0, 0, 1)
    better = dl < best_d
    best_d = np.where(better, dl, best_d)
    best_sd = np.where(better, sd, best_sd)
    best_fade = np.where(better, fade, best_fade)
sd = best_sd
# 範囲: 海側 64px・砂側 32px まで。縁はなめらかに
inside = ((sd >= 0) & (sd <= 64)) | ((sd < 0) & (sd >= -32))
edge = np.where(sd >= 0, np.clip((64 - sd) / 10.0, 0, 1), np.clip((32 + sd) / 6.0, 0, 1))
B = np.where(inside, edge * best_fade, 0.0).astype(np.float32)
B = np.where(harbor, 0.0, B)
G = np.where(sd >= 0, 0.5 + np.clip(sd, 0, 64) / 128.0, 0.5 - np.clip(-sd, 0, 32) / 64.0)
G = np.where(inside, G, np.where(water, 1.0, 0.0))
d_to_beach = np.where(sd >= 0, sd, 0)
sea_side = inside & (sd >= 0)
beach = inside & (sd < 0)
dist_to_water = np.where(sd < 0, -sd, 0)

R = ndi.gaussian_filter(water.astype(np.float32), 0.8)

rng = np.random.default_rng(7)
A = ndi.gaussian_filter(rng.random((H, W)).astype(np.float32), 45)
A = (A - A.min()) / (A.max() - A.min())

# ★WPF は画像を読むと「色×透明度」の形(Pbgra32)に直すので、透明度の欄にデータを入れると R/G/B が狂う
#   （A のゆらぎが距離 G に掛かり、泡が沖にずれた・2026-09-28）→ マスクは RGB だけ、ゆらぎは別画像(グレー)
rgb = np.stack([R, G, B], -1)
Image.fromarray((np.clip(rgb, 0, 1) * 255 + 0.5).astype(np.uint8), 'RGB').save(os.path.join(OUT, 'worldmap_mask.png'))
Image.fromarray((np.clip(A, 0, 1) * 255 + 0.5).astype(np.uint8), 'L').save(os.path.join(OUT, 'worldmap_noise.png'))

# ---- 確認用 ----
base = (img * 255).astype(np.uint8)
ov = base.copy().astype(np.float32)
ov[water] = ov[water] * 0.45 + np.array([0, 90, 255]) * 0.55
ov[beach] = ov[beach] * 0.45 + np.array([255, 200, 0]) * 0.55
Image.fromarray(ov.astype(np.uint8)).save(os.path.join(OUT, 'debug_classes.png'))
# G/B の見え方: 波打ち際から沖へ 0..64px を虹色の縞、砂側は赤
vis = base.astype(np.float32) * 0.35
band = (np.sin(np.clip(d_to_beach, 0, 64) * 0.6) * 0.5 + 0.5)
m = sea_side & (B > 0.5) & (d_to_beach < 64)
vis[m] = vis[m] * 0.3 + np.stack([band[m] * 255, 255 - band[m] * 200, np.full(m.sum(), 255.0)], -1) * 0.7
ms = beach & (dist_to_water <= 32)
vis[ms] = vis[ms] * 0.3 + np.array([255, 60, 60]) * 0.7
Image.fromarray(vis.astype(np.uint8)).save(os.path.join(OUT, 'debug_shore.png'))
print('water', int(water.sum()), 'beach', int(beach.sum()))
