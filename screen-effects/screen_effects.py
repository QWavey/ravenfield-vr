"""Suggested on-screen effects, v3: effects only on black (no scene). Both eyes per panel, right-handed player.
Muzzle flashes as a see-through lens flash (bloom, faint starburst, ghosts and halo across the lens, a soft wash from
the gun's side); water drops shaded from a height field (rain on a window at night)."""
import math
import sys
import numpy as np
from PIL import Image, ImageDraw, ImageFilter

OUT = sys.argv[1]
W, H = 300, 250
rng = np.random.default_rng(11)
yy, xx = np.mgrid[0:H, 0:W].astype(np.float32)
CX, CY = W / 2, H / 2


def black():
    return np.zeros((H, W, 3), np.float32)


def g2(cx, cy, s, sx=1.0, sy=1.0):
    return np.exp(-(((xx - cx) / sx) ** 2 + ((yy - cy) / sy) ** 2) / (2 * s * s))[..., None]


def tone(img):
    """Soft roll-off instead of clipping (bright flashes stay smooth)."""
    return 1.0 - np.exp(-np.maximum(img, 0) * 1.25)


def to_im(a):
    return Image.fromarray((np.clip(a, 0, 1) * 255).astype(np.uint8))


def round_frame(im, radius=46):
    """Clips the picture to a rounded rectangle (black outside, anti-aliased) and draws its faint outline, so the
    corners are rounded in the picture itself, not only in the outline."""
    w, h = im.size
    k = 4
    mask = Image.new("L", (w * k, h * k), 0)
    ImageDraw.Draw(mask).rounded_rectangle([k, k, w * k - 1 - k, h * k - 1 - k], radius=radius * k, fill=255)
    mask = mask.resize((w, h), Image.LANCZOS)
    out = Image.new("RGB", (w, h), (0, 0, 0))
    out.paste(im, (0, 0), mask)
    ImageDraw.Draw(out).rounded_rectangle([1, 1, w - 2, h - 2], radius=radius, outline=(38, 40, 46), width=1)
    return out


def eye_frame(im):
    """One eye's view, clipped to the lens's rounded shape (no background drawn)."""
    return round_frame(im)


def pair(left, right, title, desc):
    pad = 6
    im = Image.new("RGB", (2 * W + pad, H + 44), (0, 0, 0))
    im.paste(eye_frame(to_im(tone(left))), (0, 22))
    im.paste(eye_frame(to_im(tone(right))), (W + pad, 22))
    d = ImageDraw.Draw(im)
    d.text((6, 5), title, fill=(255, 255, 255))
    d.text((6, H + 26), desc, fill=(170, 180, 190))
    d.text((8, 28), "L", fill=(110, 110, 115))
    d.text((W + pad + 8, 28), "R", fill=(110, 110, 115))
    return im


# ---- muzzle flash as a lens flash --------------------------------------------------------------------------------
HOT = np.array([1.0, 0.93, 0.78])
FIRE = np.array([1.0, 0.62, 0.22])
EMBER = np.array([0.95, 0.38, 0.12])


def lens_flash(img, mx, my, power, side=(1.0, 1.0), spikes=True):
    """power ~ flash size x facing / distance (0..1.5). Additive and see-through."""
    dx, dy = xx - mx, yy - my
    r = np.sqrt(dx * dx + dy * dy) + 1e-3
    # bloom: a white-hot core, a fire body with ragged petals (the gas plume seen end-on), a long faint ember tail
    ang = np.arctan2(dy, dx)
    petals = 0.72 + 0.18 * np.sin(5 * ang + 0.7) * np.sin(3 * ang + 2.1) + 0.1 * np.sin(11 * ang + 0.3)
    img += (np.exp(-(r / (2.5 + 3.5 * power)) ** 2) * 2.4 * power)[..., None] * HOT
    img += (np.exp(-(r / ((8 + 15 * power) * petals)) ** 2) * 1.0 * power)[..., None] * FIRE
    img += (np.exp(-(r / ((16 + 26 * power) * petals)) ** 2) * 0.35 * power)[..., None] * EMBER
    img += (power * 0.10 / (1 + (r / (35 + 45 * power)) ** 2))[..., None] * EMBER
    if spikes:
        # faint, short starburst (the eye's own, from lashes and the pupil), not a camera's star filter
        star = np.abs(np.cos(3 * (ang + 0.26))) ** 90
        img += (star * np.exp(-r / (9 + 22 * power)) * 0.35 * power)[..., None] * HOT
    # ghosts: reflections inside the lens on the line from the flash through the lens centre
    vx, vy = CX - mx, CY - my
    for t, rad, col, a in ((0.55, 9, (1.0, 0.7, 0.35), 0.10), (1.15, 18, (0.45, 0.85, 0.7), 0.06),
                           (1.55, 6, (0.9, 0.5, 0.9), 0.09), (1.9, 30, (1.0, 0.75, 0.45), 0.035)):
        gx, gy = mx + vx * t, my + vy * t
        d = np.sqrt((xx - gx) ** 2 + (yy - gy) ** 2)
        disc = np.clip((rad - d) / 2.5, 0, 1) * (0.6 + 0.4 * (d / rad))      # soft disc, brighter edge
        img += (disc * a * power * 3)[..., None] * np.array(col)
    # a thin halo ring round the flash
    ring = np.exp(-((r - (55 + 30 * power)) / 4.0) ** 2)
    img += (ring * 0.05 * power)[..., None] * np.array([1.0, 0.8, 0.55])
    # see-through wash from the gun's side, shaped like the explosion's in D but much lighter
    sx_, sy_ = side
    img += g2(W * 0.5 + sx_ * W * 0.62, H * 0.5 + sy_ * H * 0.42, 100, 1.05, 1.25) * np.array([1.0, 0.7, 0.38]) * 0.42 * power
    return img


def smooth_noise(seed_shift, cells=12):
    """Soft cloudy noise 0..1 (a few octaves of blurred random grids)."""
    acc = np.zeros((H, W), np.float32)
    for o, wgt in ((1, 0.55), (2, 0.3), (4, 0.15)):
        n_ = rng.random((max(2, int(H / (cells * 4) * o)) + 1, max(2, int(W / (cells * 4) * o)) + 1)).astype(np.float32)
        im_ = Image.fromarray((n_ * 255).astype(np.uint8)).resize((W, H), Image.BICUBIC).filter(ImageFilter.GaussianBlur(cells / o))
        acc += np.asarray(im_).astype(np.float32) / 255.0 * wgt
    acc -= acc.min()
    return acc / max(1e-6, acc.max())


panels = []

# A. rifle at the shoulder: the muzzle is ahead and a little right of centre; right eye nearer, stronger
L, R = black(), black()
lens_flash(L, 156, 132, 0.45)
lens_flash(R, 142, 132, 0.85)
panels.append(pair(L, R, "A  Muzzle flash: rifle at the shoulder", "Lens flash, stronger in the right eye"))

# B. pistol close / turned toward the eyes: stronger, plus a veil of glare over the whole lens for a frame
L, R = black(), black()
lens_flash(L, 176, 196, 0.8, side=(1.0, 1.0))
lens_flash(R, 160, 196, 1.35, side=(1.0, 1.0))
for img, k in ((L, 0.6), (R, 1.0)):
    img += np.array([1.0, 0.75, 0.45]) * 0.05 * k                     # veiling glare
panels.append(pair(L, R, "B  Muzzle flash: pistol close to the face", "Bigger bloom, a brief warm veil"))

# C. how it scales (right eye): suppressed, SMG at arm's length, shotgun at the cheek
strip = []
for label, p in (("suppressed", 0.08), ("SMG", 0.5), ("shotgun", 1.4)):
    R = black()
    lens_flash(R, 142, 132, p)
    im = eye_frame(to_im(tone(R)))
    ImageDraw.Draw(im).text((8, 8), label, fill=(200, 200, 205))
    strip.append(im.resize((W * 2 // 3, H * 2 // 3), Image.LANCZOS))
im = Image.new("RGB", (2 * W + 6, H + 44), (0, 0, 0))
for i, s_ in enumerate(strip):
    im.paste(s_, (i * (W * 2 // 3 + 3), 22 + H // 6))
d = ImageDraw.Draw(im)
d.text((6, 5), "C  Flash strength by gun", fill=(255, 255, 255))
d.text((6, H + 26), "Right eye", fill=(170, 180, 190))
panels.append(im)

# F. hit from the right: red glow from that side, both eyes, right stronger
L, R = black(), black()
for img, k in ((L, 0.55), (R, 1.0)):
    img += g2(W + 25, H / 2, 70, 0.8, 1.9) * np.array([0.95, 0.06, 0.03]) * 1.1 * k
    img += g2(W + 25, H / 2, 25, 0.8, 1.9) * np.array([1.0, 0.25, 0.15]) * 0.5 * k
panels.append(pair(L, R, "F  Hit from the right", "Red glow on that side, half a second"))

# G. low health: the earlier picture's red edge (and its lens fall-off), without the scene
L, R = black(), black()
for img in (L, R):
    r = np.sqrt(((xx - CX) / (W * 0.5)) ** 2 + ((yy - CY) / (H * 0.5)) ** 2)
    lensv = np.clip(1.25 - np.sqrt(((xx - CX) / (W * 0.56)) ** 2 + ((yy - CY) / (H * 0.6)) ** 2), 0, 1) ** 0.6
    img += (np.clip(r - 0.55, 0, 1) * lensv)[..., None] * np.array([0.55, 0.0, 0.0])
inv0 = lambda a: -np.log(np.clip(1 - a, 1e-4, 1)) / 1.25
panels.append(pair(inv0(L), inv0(R), "G  Low health", "Red edges, pulsing with the heartbeat"))


# The mod's mud on the view today (src/Rig/MudSplash.cs), drawn the way it draws it: spots on a see-through visor
# 25 cm in front of the eyes, alpha-blended (Sprites/Default), a blast or a near hit on the left.
def value_noise(x, y, seed):
    rr_ = np.random.default_rng(seed)
    grid = rr_.random((64, 64)).astype(np.float32)
    x0, y0 = np.floor(x).astype(int), np.floor(y).astype(int)
    fx, fy = x - x0, y - y0
    fx, fy = fx * fx * (3 - 2 * fx), fy * fy * (3 - 2 * fy)
    g = lambda i, j: grid[(j) % 64, (i) % 64]
    a_ = g(x0, y0) * (1 - fx) + g(x0 + 1, y0) * fx
    b_ = g(x0, y0 + 1) * (1 - fx) + g(x0 + 1, y0 + 1) * fx
    return a_ * (1 - fy) + b_ * fy


def spot(px, py, seed):
    """MudSplash.DrawSpot: a soft, slightly irregular disc, denser at its rim, grainy inside (alpha, grey)."""
    rr_ = np.random.default_rng(seed)
    freq = [2 + k * 2 + int(rr_.integers(0, 2)) for k in range(4)]
    phase = [float(rr_.random()) * 6.283 for _ in range(4)]
    rim_s = 0.15 + 0.25 * float(rr_.random())
    r = np.sqrt(px * px + py * py)
    th = np.arctan2(py, px)
    edge = 0.82 + sum(0.035 / (k + 1) * np.sin(freq[k] * th + phase[k]) for k in range(4))
    ss = lambda t: t * t * (3 - 2 * t)
    body = ss(np.clip((edge - r) / 0.25, 0, 1))
    rim = 1 - rim_s + rim_s * ss(np.clip((r - (edge - 0.4)) / 0.3, 0, 1))
    grain = 0.72 + 0.28 * value_noise(px * 7 + seed * 0.37 + 10, py * 7 + seed * 0.11 + 10, seed)
    return np.clip(body * rim * grain, 0, 1), 0.85 + 0.15 * grain


def mud_view(eye_x, splats, age):
    img = black()
    HW, HH, PXM = 0.34, 0.27, (W / 2) / 0.34
    for sp in splats:
        u = age / sp["life"]
        fall = sp["fall"] * max(0.0, age - 0.3) ** 2
        a_t = 1.0 if u < 0.45 else 1 - (u - 0.45) / 0.55
        half, stretch = sp["size"] * 0.5, 1 + sp["stretch"] * u
        cx = W / 2 + (sp["x"] - eye_x) * PXM
        cy = H / 2 - (sp["y"] - fall) * PXM
        dx, dy = (xx - cx) / PXM, -(yy - cy) / PXM
        dy2 = dy / stretch
        c, s_ = np.cos(sp["angle"]), np.sin(sp["angle"])
        lx, ly = dx * c + dy2 * s_, -dx * s_ + dy2 * c
        px, py = lx / half, ly / half
        inside = (np.abs(px) <= 1) & (np.abs(py) <= 1)
        if not inside.any():
            continue
        alpha, grey = spot(np.clip(px, -1, 1), np.clip(py, -1, 1), 100 + sp["variant"])
        alpha = alpha * inside * sp["alpha"] * a_t * a_t
        col = sp["color"][None, None, :] * grey[..., None]
        img = img * (1 - alpha[..., None]) + col * alpha[..., None]
    return img


mr = np.random.default_rng(4)
strength, rng_m = 0.75, 6.0
big = min(1.5, max(0.7, rng_m / 6))
count = int(np.clip(round(8 + 34 * strength * big), 6, 48))
lean = np.array([-1.0, -0.2]); lean /= np.linalg.norm(lean)
splats = []
for i in range(count):
    x, y = (mr.random() * 2 - 1) * 0.34, (mr.random() * 2 - 1) * 0.27
    if i % 3 == 0:
        x, y = x + (lean[0] * 0.34 - x) * 0.5, y + (lean[1] * 0.27 - y) * 0.5
    r = mr.random()
    size = (0.06 + 0.05 * mr.random()) if r < 0.15 else (0.03 + 0.03 * mr.random()) if r < 0.55 else (0.012 + 0.018 * mr.random())
    shade = 0.8 + 0.4 * mr.random()
    splats.append(dict(x=x, y=y, size=size * (0.8 + 0.4 * strength), life=3 + 3 * mr.random(),
                       fall=(0.002 + 0.004 * mr.random()) if size > 0.05 else 0.0,
                       stretch=(0.05 + 0.25 * mr.random()) if size > 0.05 else 0.0,
                       alpha=(0.75 + (0.4 - 0.75) * min(1.0, size / 0.11)) * (0.7 + 0.3 * strength),
                       color=np.array([0.33, 0.27, 0.2]) * shade, variant=int(mr.integers(0, 4)), angle=mr.random() * 6.283))
L = mud_view(-0.032, splats, 0.8)
R = mud_view(+0.032, splats, 0.8)
panels.append(pair(inv0(L), inv0(R), "Mud on the view (in the mod now)", "Blast or near hit on the left"))

# ---- I. water drops: a height field, shaded --------------------------------------------------------------------
def drop_field(drops, w, h, s):
    """Height of the water film (pixels at scale s) for round drops, teardrop runners and their trails."""
    Y, X = np.mgrid[0:h, 0:w].astype(np.float32)
    Hf = np.zeros((h, w), np.float32)
    for cx, cy, r, e, trail, wob in drops:
        cx, cy, r, trail = cx * s, cy * s, r * s, trail * s
        u, v = (X - cx) / r, (Y - cy) / (r * e)
        body = np.sqrt(np.clip(1 - u * u - v * v, 0, 1)) * r * 0.6
        Hf = np.maximum(Hf, body)
        if trail > 0:
            tail = r * 2.2
            t = np.clip((cy - Y) / tail, 0, 1)
            off = wob * s * np.sin((cy - Y) / (9 * s))
            hw = r * (1 - t) ** 1.25 + 1e-3
            q = (X - cx - off) / hw
            tl = np.where((Y < cy) & (Y > cy - tail), np.sqrt(np.clip(1 - q * q, 0, 1)) * r * 0.5 * (1 - t) ** 0.7, 0)
            Hf = np.maximum(Hf, tl)
            # the wet trail above: a thin, slightly uneven film
            tt = np.clip((cy - tail - Y) / trail, 0, 1)
            film = np.where((Y < cy - tail) & (Y > cy - tail - trail),
                            np.exp(-((X - cx - off) / (0.32 * r)) ** 2) * (1 - tt) * 0.9 * s * (0.75 + 0.25 * np.sin(Y / (3 * s))), 0)
            Hf = np.maximum(Hf, film)
    return Hf


def shade_drops(Hf, s):
    """Rain on a window at night: only light the water itself catches. Fresnel reflection of a soft light above-left
    (the highlight and the bright rim), light refracted through each drop gathering at its lower inside edge (caustic),
    and a faint body. Black where there is no water."""
    gy, gx = np.gradient(Hf)
    n = np.stack([-gx, -gy, np.ones_like(Hf)], -1)
    n /= np.linalg.norm(n, axis=-1, keepdims=True)
    nz = n[..., 2]
    wet = np.clip(Hf / (0.6 * s), 0, 1)
    fres = 0.02 + 0.98 * (1 - nz) ** 5
    R = np.stack([2 * nz * n[..., 0], 2 * nz * n[..., 1], 2 * nz * nz - 1], -1)
    Ld = np.array([-0.42, -0.72, 0.55]); Ld /= np.linalg.norm(Ld)
    c = R @ Ld
    env = 45.0 * np.exp(-(1 - c) / 0.003) + 7.0 * np.exp(-(1 - c) / 0.02) + 1.2 * np.clip(-R[..., 1], 0, 1)
    refl = fres * env
    # the rim: light caught round the edge, brighter on top (light from above)
    rim = (1 - nz) ** 2.5 * (0.35 + 0.45 * np.clip(-n[..., 1], 0, 1))
    # caustic: light from above leaves through the lower part of the drop as a bright crescent
    caus = np.clip(n[..., 1], 0, 1) ** 1.6 * (1 - nz) ** 0.4 * 1.1
    body = 0.06 * wet
    lum = (refl + rim + caus + body) * (Hf > 0.02 * s)
    img = lum[..., None] * np.array([0.92, 0.96, 1.0])
    img += (caus * (Hf > 0.02 * s))[..., None] * np.array([0.06, 0.03, 0.0])   # caustic a touch warmer
    return img


def make_drops(seed):
    r_ = np.random.default_rng(seed)
    out = []
    for _ in range(13):
        while True:
            cx, cy = r_.uniform(12, W - 12), r_.uniform(12, H - 12)
            if ((cx - CX) / W) ** 2 + ((cy - CY) / H) ** 2 > 0.04:     # gather toward the edges
                break
        r = r_.uniform(4.5, 11)
        running = r_.random() < 0.4
        trail = r_.uniform(35, 100) if running else 0.0
        out.append((cx, cy, r, 1.1 if running else r_.uniform(0.88, 1.02), trail, r_.uniform(0.5, 2.0)))
        if running:
            for _ in range(int(trail / 16)):
                out.append((cx + r_.uniform(-1.5, 1.5), cy - r * 2.2 - r_.uniform(4, trail), r_.uniform(1.0, 2.0), 1.0, 0.0, 0))
    for _ in range(70):
        out.append((r_.uniform(0, W), r_.uniform(0, H), r_.uniform(0.8, 2.2), r_.uniform(0.9, 1.1), 0.0, 0))
    return out


S = 3   # supersampling for clean edges and highlights
drops = make_drops(5)
eyes = []
for shift in (+2.5, -2.5):                       # the same drops in both eyes, at one fixed depth
    dd = [(cx + shift, cy, r, e, tr, wb) for cx, cy, r, e, tr, wb in drops]
    big = shade_drops(drop_field(dd, W * S, H * S, S), S)
    small = np.asarray(to_im(tone(big)).resize((W, H), Image.LANCZOS)).astype(np.float32) / 255.0
    eyes.append((small, big))
L, R = eyes[0][0], eyes[1][0]
# pair() tones again: undo by feeding the already-toned image through an inverse
inv = lambda a: -np.log(np.clip(1 - a, 1e-4, 1)) / 1.25
panels.append(pair(inv(L), inv(R), "I  Water drops", "After surfacing or in heavy rain"))

# close-up of the right eye at 2x (from the supersampled image)
bigR = to_im(tone(eyes[1][1]))
crop = bigR.crop((0, 0, W * S, int(W * S * H / (2 * W + 6)))).resize((2 * W + 6, H), Image.LANCZOS)
cl = Image.new("RGB", (2 * W + 6, H + 44), (0, 0, 0))
cl.paste(round_frame(crop), (0, 22))
d = ImageDraw.Draw(cl)
d.text((6, 5), "I  Water drops, close-up (2x)", fill=(255, 255, 255))
panels.append(cl)

cols = 2
pw, ph = panels[0].size
sheet = Image.new("RGB", (cols * pw + 10, ((len(panels) + 1) // cols) * (ph + 8) + 36), (0, 0, 0))
ImageDraw.Draw(sheet).text((8, 10), "On-screen effects (your picks), effects only, both eyes", fill=(255, 255, 255))
for i, p in enumerate(panels):
    r_, c_ = divmod(i, cols)
    sheet.paste(p, (c_ * (pw + 10), 36 + r_ * (ph + 8)))
sheet.save(OUT)
print("saved", OUT, sheet.size)
