# sim_waves.py — worldmap_water.hlsl の ③砂浜の波 を numpy で同じ計算をして、何コマか描く（CM7 を使わずに見た目を確かめる用）
#   py -3.12 sim_waves.py [time...]  → sim_t{t}.png（入り江・左の砂浜・右下の砂浜の切り抜きを並べたもの）
import sys, os
import numpy as np
from PIL import Image

D = os.path.dirname(os.path.abspath(__file__))
img = np.asarray(Image.open(r'D:\actools-build\run\Data\Static\gt7\worldmap.jpg').convert('RGB')).astype(np.float32) / 255
mask = np.asarray(Image.open(os.path.join(D, 'worldmap_mask.png'))).astype(np.float32) / 255
H, W, _ = img.shape
R, G, B = mask[..., 0], mask[..., 1], mask[..., 2]
A = np.asarray(Image.open(os.path.join(D, 'worldmap_noise.png'))).astype(np.float32) / 255
foamRGB = np.array([0.96, 0.97, 1.0]); foamA = 0.85

def smoothstep(e0, e1, x):
    t = np.clip((x - e0) / (e1 - e0), 0, 1)
    return t * t * (3 - 2 * t)

def frame(t):
    col = img.copy()
    beach = B; g = G
    sd = np.where(g >= 0.5, (g - 0.5) * 128.0, -(0.5 - g) * 64.0)
    act = (beach > 0.02) & (sd < 60) & (sd > -30)
    seed = A
    period = 9.0
    ph = np.mod(t / period + seed * 0.22, 1.0)
    runup = 6.0 + (13.0 - 6.0) * (0.5 + 0.5 * np.sin(seed * 9.0))
    jag = 1.2 * np.sin(seed * 60.0 + 0.7) + 0.6 * np.sin(seed * 130.0 - 1.3)
    k1 = (ph / 0.45) ** 2
    k2 = smoothstep(0.08, 1.0, (ph - 0.45) / 0.55)
    appr = ph < 0.45
    front = np.where(appr, 26.0 + (-runup - 26.0) * k1, -runup + (8.0 + runup) * k2) + jag
    stre = np.where(appr, smoothstep(0.0, 0.5, k1), 1 - k2)
    width = np.where(appr, 2.5 + 2.0 * k1, 4.5)
    # 水の膜は簡略（沖の色の代わりに少し暗い青緑）
    sheet = np.where((sd < 0) & (sd > front), np.clip((sd - front) / 3.0, 0, 1), 0)
    waterCol = np.array([0.45, 0.62, 0.66])
    col = col + (waterCol - col) * (sheet * 0.55 * stre * beach)[..., None]
    wet = np.where(sd < 0, np.clip(1 + sd / runup, 0, 1), np.clip(1 - sd / 4.0, 0, 1))
    col = col * (1 - 0.10 * wet * beach)[..., None]
    broken = 0.72 + 0.28 * np.sin(seed * 95.0 + t * 0.35) * np.sin(seed * 41.0 - t * 0.25)
    breaking = smoothstep(12.0, 3.0, front)
    foamLine = np.clip(1 - np.abs(sd - front) / width, 0, 1) * broken * breaking
    swellBand = np.clip(1 - np.abs(sd - front) / 9.0, 0, 1) * (1 - breaking) * 0.12
    swell = np.where(sd > 0, (0.5 + 0.5 * np.sin(sd * 0.39 + t * 6.2832 / period * 2.0 + seed * 6.0)) * np.clip(1 - sd / 45.0, 0, 1) * np.clip(sd / 6.0, 0, 1) * 0.10, 0)
    trail = np.where(sd > front, np.clip(1 - (sd - front) / 10.0, 0, 1) * 0.35 * breaking * broken, 0)
    f = np.clip((foamLine + trail + swellBand) * stre + swell, 0, 1) * beach * foamA
    f = np.where(act, f, 0)
    col = np.where(act[..., None], col + (foamRGB - col) * f[..., None], col)
    return np.clip(col, 0, 1)

times = [float(x) for x in sys.argv[1:]] or [100.0, 102.0, 104.0, 106.0]
boxes = [(700, 600, 1100, 860), (0, 260, 320, 520), (1380, 780, 1780, 1040)]
rows = []
for t in times:
    fr = (frame(t) * 255).astype(np.uint8)
    crops = [Image.fromarray(fr[y0:y1, x0:x1]).resize(((x1 - x0), (y1 - y0))) for (x0, y0, x1, y1) in boxes]
    wsum = sum(c.width for c in crops); hmax = max(c.height for c in crops)
    row = Image.new('RGB', (wsum, hmax))
    x = 0
    for c in crops: row.paste(c, (x, 0)); x += c.width
    rows.append(row)
sheet = Image.new('RGB', (rows[0].width, sum(r.height for r in rows)))
y = 0
for r in rows: sheet.paste(r, (0, y)); y += r.height
out = os.path.join(D, 'sim_' + '_'.join(str(int(t)) for t in times) + '.png')
sheet.save(out)
print(out)
