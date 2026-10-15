"""Muzzle flash on the lens: a shooting simulation and its tests.

Standalone. Not part of the Ravenfield VR mod (it only follows the look approved for task 209 in its Tasks2.md:
assets/suggestions/screen-effects in the mod's repo).

A right-handed player fires different guns at 90 frames a second (a VR headset's refresh rate). Every shot puts a
see-through flash on each eye's lens:
- a white-hot core fading to orange, with a ragged edge (the gas plume seen end-on);
- a short, faint starburst;
- lens ghosts on the line through the lens centre, and a thin halo;
- a soft warm wash from the gun's side.

Its strength per eye comes from the gun's flash size, how far the muzzle is and how much it faces the eye. The eye on
the gun's side (looking along it) gets more. The muzzle climbs with recoil between shots, so the flash moves.

Flash limiter (photosensitivity, on by default): bright flashes repeating faster than 3 a second are a seizure risk
(WCAG 2.3.1 / the broadcast flash rule). After 3 full flashes inside any second, further shots get a soft flash below
the flash threshold, and sustained fire gives a steady muzzle glow instead of a strobe. The test checks it, using a 10
degree visual field, which in a headset is a small patch of the view, not a quarter of a TV.

Run:
  python flash_sim.py            all guns: GIFs, 90 fps MP4s, a timeline chart and the test report, in ./out
  python flash_sim.py --live     a window: Space (hold for automatic guns) or the mouse fires, 1-8 pick the gun,
                                 L toggles the limiter, Esc quits
"""
import argparse
import math
import os
import sys
import time

import numpy as np
from PIL import Image, ImageDraw, ImageFont

try:
    import cv2
except ImportError:  # the MP4s and the fast box filter need OpenCV; everything else works without it
    cv2 = None

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(HERE, "out")

FPS = 90                       # headset refresh rate (frames per second)
PX = 1.0                       # render scale (set_scale): 1 = the tested 300 x 250 eye view
W, H = 300, 250                # one eye's view, pixels
HFOV = math.radians(104.0)     # horizontal field of view of one eye
FOCAL = (W / 2) / math.tan(HFOV / 2)
IPD = 0.064                    # metres between the eyes
EYES = (("L", -IPD / 2), ("R", +IPD / 2))
FIELD10 = int(round(W * 10.0 / math.degrees(HFOV)))   # a 10 degree visual field, in pixels (about 29)

yy, xx = np.mgrid[0:H, 0:W].astype(np.float32)
CX, CY = W / 2, H / 2
HOT = np.array([1.0, 0.93, 0.78], np.float32)
FIRE = np.array([1.0, 0.62, 0.22], np.float32)
EMBER = np.array([0.95, 0.38, 0.12], np.float32)

# Flash life: the muzzle flash itself lasts a millisecond or two; on the lens it shows on the shot's frame and fades
# like an after-image: x0.5 a frame (at 90 fps), gone after 60 ms.
DECAY = 0.016
LIFE = 0.06

# ---- the guns (right-handed; metres from the point between the eyes: x right, y up, z forward) -----------------
GUNS = [
    dict(key="pistol", sway=5.0, label="Pistol, arms out", muzzle=(0.05, -0.13, 0.55), dir=(0.0, 0.05, 1.0), size=1.0,
         rate=3.0, auto=False, climb=0.025),
    dict(key="pistol_close", sway=6.0, label="Pistol close to the face (B)", muzzle=(0.12, -0.12, 0.28), dir=(-0.45, 0.25, 0.86),
         size=1.0, rate=2.0, auto=False, climb=0.03),
    dict(key="rifle", sway=2.5, label="Rifle at the shoulder (A), semi", muzzle=(0.045, -0.05, 0.78), dir=(0.0, 0.0, 1.0),
         size=1.7, rate=4.0, auto=False, climb=0.01),
    dict(key="rifle_auto", sway=2.5, label="Rifle, full auto (750 rpm)", muzzle=(0.045, -0.05, 0.78), dir=(0.0, 0.0, 1.0),
         size=1.7, rate=750 / 60.0, auto=True, climb=0.006),
    dict(key="smg", sway=6.0, label="SMG at arm's length (900 rpm)", muzzle=(0.15, -0.2, 0.45), dir=(0.0, 0.1, 1.0), size=1.1,
         rate=900 / 60.0, auto=True, climb=0.008),
    dict(key="shotgun", sway=2.5, label="Shotgun at the cheek (pump)", muzzle=(0.045, -0.06, 0.8), dir=(0.0, 0.0, 1.0),
         size=2.6, rate=1.2, auto=False, climb=0.04),
    dict(key="suppressed", sway=2.5, label="Rifle, suppressed", muzzle=(0.045, -0.05, 0.85), dir=(0.0, 0.0, 1.0), size=0.15,
         rate=4.0, auto=False, climb=0.008),
    dict(key="mg", sway=3.0, label="Machine gun, full auto (600 rpm)", muzzle=(0.06, -0.12, 0.95), dir=(0.0, 0.0, 1.0),
         size=2.0, rate=10.0, auto=True, climb=0.007),
]


def eye_power(gun, muzzle, eye_x):
    """Flash strength for one eye: flash size x facing x distance x how much this eye looks along the gun."""
    m = np.asarray(muzzle, float)
    to_eye = np.array([eye_x, 0.0, 0.0]) - m
    d = float(np.linalg.norm(to_eye))
    to_eye /= d
    bdir = np.asarray(gun["dir"], float)
    bdir /= np.linalg.norm(bdir)
    facing = 0.5 + 0.5 * max(0.0, float(np.dot(bdir, to_eye)))      # seen from behind: half; toward the face: full
    dist = min(2.5, max(0.5, 0.75 / d))
    near = min(abs(m[0] - ex) for _, ex in EYES)
    side = 1.0 - 0.5 * min(1.0, max(0.0, (abs(m[0] - eye_x) - near) / IPD))
    return min(1.6, gun["size"] * facing * dist * side)


def project(p, eye_x):
    x, y, z = p
    return CX + FOCAL * (x - eye_x) / z, CY - FOCAL * y / z


# ---- the flash on the lens (the approved look) ------------------------------------------------------------------
def lens_flash(img, mx, my, power, phase=0.0, side=(1.0, 1.0), extras=True):
    """Adds one flash (additive, see-through) at (mx, my) with strength `power` (0..1.6)."""
    if power <= 1e-4:
        return img
    dx, dy = xx - mx, yy - my
    r = np.sqrt(dx * dx + dy * dy) + 1e-3
    ang = np.arctan2(dy, dx) + phase
    petals = 0.72 + 0.18 * np.sin(5 * ang + 0.7) * np.sin(3 * ang + 2.1) + 0.1 * np.sin(11 * ang + 0.3)
    img += (np.exp(-(r / (PX * (2.5 + 3.5 * power))) ** 2) * 2.4 * power)[..., None] * HOT
    img += (np.exp(-(r / (PX * (8 + 15 * power) * petals)) ** 2) * 1.0 * power)[..., None] * FIRE
    img += (np.exp(-(r / (PX * (16 + 26 * power) * petals)) ** 2) * 0.35 * power)[..., None] * EMBER
    img += (power * 0.10 / (1 + (r / (PX * (35 + 45 * power))) ** 2))[..., None] * EMBER
    if not extras:
        return img
    star = np.abs(np.cos(3 * (ang + 0.26))) ** 90
    img += (star * np.exp(-r / (PX * (9 + 22 * power))) * 0.35 * power)[..., None] * HOT
    vx, vy = CX - mx, CY - my
    for t, rad, col, a in ((0.55, 9, (1.0, 0.7, 0.35), 0.10), (1.15, 18, (0.45, 0.85, 0.7), 0.06),
                           (1.55, 6, (0.9, 0.5, 0.9), 0.09), (1.9, 30, (1.0, 0.75, 0.45), 0.035)):
        gx, gy = mx + vx * t, my + vy * t
        d = np.sqrt((xx - gx) ** 2 + (yy - gy) ** 2)
        rad = rad * PX
        disc = np.clip((rad - d) / (2.5 * PX), 0, 1) * (0.6 + 0.4 * (d / rad))
        img += (disc * a * power * 3)[..., None] * np.asarray(col, np.float32)
    ring = np.exp(-((r - PX * (55 + 30 * power)) / (4.0 * PX)) ** 2)
    img += (ring * 0.05 * power)[..., None] * np.array([1.0, 0.8, 0.55], np.float32)
    sx, sy = side
    wash = np.exp(-(((xx - (W * 0.5 + sx * W * 0.62)) / 1.05) ** 2 + ((yy - (H * 0.5 + sy * H * 0.42)) / 1.25) ** 2) / (2 * (100.0 * PX) ** 2))
    img += wash[..., None] * np.array([1.0, 0.7, 0.38], np.float32) * 0.42 * power
    if power > 1.0:                     # very close and facing: a faint veil over the whole lens
        img += np.array([1.0, 0.75, 0.45], np.float32) * 0.05 * (power - 1.0) / 0.6
    return img


CEILING = 0.94   # the effect alone never goes past 94% of white (a soft roll-off, no clipping)


def tone(img):
    return CEILING * (1.0 - np.exp(-np.maximum(img, 0) * 1.32))


def lens_mask():
    m = Image.new("L", (W * 4, H * 4), 0)
    ImageDraw.Draw(m).rounded_rectangle([4, 4, W * 4 - 5, H * 4 - 5], radius=int(46 * PX * 4), fill=255)
    return np.asarray(m.resize((W, H), Image.LANCZOS)).astype(np.float32) / 255.0


MASK = lens_mask()


def set_scale(k):
    """Renders everything k times bigger (the effect's sizes follow); the tests and calibration use k = 1."""
    global PX, W, H, FOCAL, FIELD10, yy, xx, CX, CY, MASK
    PX = float(k)
    W, H = int(round(300 * k)), int(round(250 * k))
    FOCAL = (W / 2) / math.tan(HFOV / 2)
    FIELD10 = int(round(W * 10.0 / math.degrees(HFOV)))
    yy, xx = np.mgrid[0:H, 0:W].astype(np.float32)
    CX, CY = W / 2, H / 2
    MASK = lens_mask()


def srgb_to_linear(c):
    return np.where(c <= 0.04045, c / 12.92, ((c + 0.055) / 1.055) ** 2.4)


def luminance(display):
    """Relative luminance of display values (sRGB-encoded 0..1)."""
    lin = srgb_to_linear(np.clip(display, 0, 1))
    return lin @ np.array([0.2126, 0.7152, 0.0722], np.float32)


# ---- flash limiter -----------------------------------------------------------------------------------------------
class Limiter:
    """Allows at most 3 full flashes in any second; later shots get a soft flash (below the flash threshold), and
    sustained fire a steady glow that rises and falls slowly. The window is 1.1 s: a flash takes a few frames to fade,
    so a full flash allowed exactly a second after the first one still ended inside that second."""

    WINDOW = 1.1
    BUDGET = 6          # transitions (3 flashes) in any second

    def __init__(self, enabled, soft):
        self.enabled = enabled
        self.soft, self.one_step = soft          # soft flash per glow; the strongest flash that fades in one step
        self.full = []                            # (time, transitions it costs)
        self.glow = 0.0

    def shot(self, t, power):
        if not self.enabled:
            return power, True
        # A flash costs 2 transitions (on, off); a very bright one fades in two counted steps and costs 3.
        self.full = [(s, c) for s, c in self.full if t - s < self.WINDOW]
        left = self.BUDGET - sum(c for _, c in self.full)
        one = float(np.interp(self.glow, *self.one_step))
        cost = 2 if power <= one else 3
        if left >= cost:
            self.full.append((t, cost))
            return power, True
        if left >= 2:
            self.full.append((t, 2))
            return min(power, one), True
        return min(power, float(np.interp(self.glow, *self.soft))), False

    def step(self, dt, firing_rate, power):
        """The steady glow while firing fast: follows the rate, eased (never more than 4% a frame)."""
        want = min(0.35, 0.025 * firing_rate) * power if self.enabled else 0.0
        self.glow += float(np.clip(want - self.glow, -0.04 * power - 1e-4, 0.04 * power + 1e-4)) * min(1.0, dt * FPS)
        return self.glow


# ---- the photosensitivity check (WCAG 2.3.1 / ITU-R BT.1702 style, for a headset) -------------------------------
def box_mean(mask, k):
    if cv2 is not None:
        return cv2.blur(mask.astype(np.float32), (k, k), borderType=cv2.BORDER_CONSTANT)
    pad = np.pad(mask.astype(np.float32), k // 2)
    c = pad.cumsum(0).cumsum(1)
    c = np.pad(c, ((1, 0), (1, 0)))
    s = c[k:, k:] - c[:-k, k:] - c[k:, :-k] + c[:-k, :-k]
    return s[:mask.shape[0], :mask.shape[1]] / (k * k)


def transitions(prev_y, y):
    """+1 / -1 when luminance rises / falls by 10% or more (the darker state below 0.8) over a quarter or more of any
    10 degree field; else 0."""
    d = y - prev_y
    darker = np.minimum(y, prev_y) <= 0.8
    inc = box_mean((d >= 0.1) & darker, FIELD10).max() >= 0.25
    dec = box_mean((d <= -0.1) & darker, FIELD10).max() >= 0.25
    return 1 if inc and not dec else -1 if dec and not inc else 0


def max_flashes_per_second(signs):
    """Flashes in the worst second: the transitions inside any one second, halved (a flash is a pair of opposing
    transitions; the rule allows 3 flashes, so 6 transitions)."""
    idx = [i for i, s in enumerate(signs) if s != 0]
    best = 0
    for i, f in enumerate(idx):
        best = max(best, sum(1 for g in idx[i:] if g - f < FPS))
    return best / 2.0


GLOWS = (0.0, 0.1, 0.2, 0.3, 0.4, 0.6)


def calibrate_soft():
    """The strongest soft flash that never counts as a flash transition (rising or fading), for each glow level under
    it: the glow raises the light already there, and the same step in the picture is a bigger change in light on a lit
    base. Worst of the lens centre and off to the side. Returns (glows, softs) for interpolation."""
    softs = []
    for gl in GLOWS:
        worst = 1.0
        for (fx, fy) in ((CX, CY), (CX + 60, CY + 30)):
            base = np.zeros((H, W, 3), np.float32)
            if gl > 0:
                lens_flash(base, fx, fy, gl, extras=False)
            y0 = luminance(tone(base) * MASK[..., None])
            lo, hi = 0.0, 0.8
            for _ in range(13):
                mid = (lo + hi) / 2
                a = lens_flash(base.copy(), fx, fy, mid)
                b = lens_flash(base.copy(), fx, fy, mid * math.exp(-1 / FPS / DECAY))
                y1, y2 = luminance(tone(a) * MASK[..., None]), luminance(tone(b) * MASK[..., None])
                if transitions(y0, y1) == 0 and transitions(y1, y2) == 0 and transitions(y2, y0) == 0:
                    lo = mid
                else:
                    hi = mid
            worst = min(worst, lo)
        softs.append(worst * 0.85)
    # per glow too: the strongest flash whose fade crosses the threshold only once (its later fade steps stay under it)
    e1 = math.exp(-1 / FPS / DECAY)
    ones = []
    for gl in GLOWS:
        lo, hi = 0.0, 1.6
        for _ in range(12):
            mid = (lo + hi) / 2
            ok = True
            for (fx, fy) in ((CX, CY), (CX + 60, CY + 30)):
                base = np.zeros((H, W, 3), np.float32)
                if gl > 0:
                    lens_flash(base, fx, fy, gl, extras=False)
                ys = [luminance(tone(lens_flash(base.copy(), fx, fy, mid * e1 ** k)) * MASK[..., None]) for k in (1, 2, 3)]
                ok &= transitions(ys[0], ys[1]) == 0 and transitions(ys[1], ys[2]) == 0
            lo, hi = (mid, hi) if ok else (lo, mid)
        ones.append(lo * 0.95)
    return (GLOWS, tuple(softs)), (GLOWS, tuple(ones))


# ---- the simulation ------------------------------------------------------------------------------------------------
class Sim:
    """One gun being fired. The muzzle is never in the same place twice: the player looks around while aiming (the
    head turns against the gun: 2.5 degrees with a stock at the shoulder, up to 6 for an SMG from the hip), breathes and
    has a slight tremor, and every shot kicks the muzzle up
    and a little sideways. A semi-automatic gun recovers before the next shot, and a burst climbs because full auto
    recovers slowly. Each flash also differs: its centre sits a little ahead of the muzzle (the plume), it varies in
    size, and its starburst turns."""

    def __init__(self, gun, limiter_on, soft, seed=1):
        self.gun = gun
        self.rng = np.random.default_rng(seed)
        self.lim = {e: Limiter(limiter_on, soft) for e, _ in EYES}
        self.flashes = []                    # (time, eye, x, y, power, phase)
        self.rec = np.zeros(2)               # recoil: (yaw, pitch) in degrees
        self.rec_v = np.zeros(2)
        self.ph = self.rng.uniform(0, 6.28, 6)
        self.next_shot = 0.0
        self.shots = []
        self.positions = []                  # right-eye flash centre per shot (pixels)
        self.nearer = []                     # per shot: the eye nearer the muzzle sideways ("L", "R" or "=")

    def aim(self, t):
        """Where the gun points against the head now (degrees, + right / up), recoil included."""
        p, tau = self.ph, 2 * math.pi
        a = self.gun["sway"]   # a gun at the shoulder moves little against the head, a pistol held out or an SMG more
        yaw = a * math.sin(tau * 0.32 * t + p[0]) + 0.15 * a * math.sin(tau * 0.9 * t + p[1]) + 0.15 * math.sin(tau * 4.3 * t + p[2])
        pitch = 0.45 * a * math.sin(tau * 0.45 * t + p[3]) + 0.1 * a * math.sin(tau * 0.27 * t + p[4]) + 0.15 * math.sin(tau * 3.7 * t + p[5])
        return yaw + self.rec[0], pitch + self.rec[1]

    def muzzle(self, t, ahead=0.0, jitter=(0.0, 0.0)):
        mx, my, mz = self.gun["muzzle"]
        yaw, pitch = self.aim(t)
        yaw, pitch = math.radians(yaw + jitter[0]), math.radians(pitch + jitter[1])
        bdir = np.asarray(self.gun["dir"], float)
        bdir /= np.linalg.norm(bdir)
        z = mz + ahead * bdir[2]
        return (mx + ahead * bdir[0] + z * math.tan(yaw), my + ahead * bdir[1] + z * math.tan(pitch), z)

    def fire(self, t):
        g = self.gun
        ahead = float(self.rng.uniform(0.02, 0.07)) * math.sqrt(g["size"])          # the plume's centre
        jitter = (float(self.rng.normal(0, 0.8)), float(self.rng.normal(0, 0.8)))   # its uneven spread, degrees
        size = float(self.rng.uniform(0.85, 1.15))
        phase = float(self.rng.uniform(0, 6.28))
        m = self.muzzle(t, ahead, jitter)
        for e, ex in EYES:
            p = eye_power(g, m, ex) * size
            p, _ = self.lim[e].shot(t, p)
            sx, sy = project(m, ex)
            self.flashes.append((t, e, sx, sy, p, phase))
            if e == "R":
                self.positions.append((sx, sy))
        dl, dr = abs(m[0] - EYES[0][1]), abs(m[0] - EYES[1][1])
        self.nearer.append("=" if abs(dl - dr) < 0.004 else "L" if dl < dr else "R")
        self.shots.append(t)
        # recoil: the muzzle kicks up (and a little sideways)
        kick = math.degrees(g["climb"] / g["muzzle"][2])
        self.rec += np.array([float(self.rng.uniform(-0.35, 0.35)) * kick, kick])

    def step(self, t, dt, trigger):
        g = self.gun
        if trigger and t >= self.next_shot:
            self.fire(t)
            self.next_shot = t + 1.0 / g["rate"]
        k = 40.0 if g["auto"] else 260.0                        # full auto recovers slowly: a burst climbs
        c = 2 * math.sqrt(k)
        self.rec_v += (-k * self.rec - c * self.rec_v) * dt
        self.rec += self.rec_v * dt
        rate = sum(1 for s in self.shots if t - s < 0.5) / 0.5 if g["auto"] else 0.0
        return rate

    def render(self, t, rate):
        views = {}
        m = self.muzzle(t)
        for e, ex in EYES:
            img = np.zeros((H, W, 3), np.float32)
            sx, sy = project(m, ex)
            side = (1.0 if sx >= CX else -1.0, 1.0)
            glow = self.lim[e].step(1 / FPS, rate, eye_power(self.gun, m, ex))
            if glow > 1e-3:
                lens_flash(img, sx, sy, glow, extras=False, side=side)
            for (ts, fe, fx, fy, p, ph) in self.flashes:
                age = t - ts
                if fe == e and 0 <= age <= LIFE:
                    lens_flash(img, fx, fy, p * math.exp(-age / DECAY), phase=ph, side=side)
            views[e] = tone(img) * MASK[..., None]
        self.flashes = [f for f in self.flashes if t - f[0] <= LIFE]
        return views


def run(gun, limiter_on, soft, fire_for=2.0, total=2.6):
    sim = Sim(gun, limiter_on, soft)
    frames, lum = [], {e: [] for e, _ in EYES}
    n = int(total * FPS)
    for i in range(n):
        t = i / FPS
        rate = sim.step(t, 1 / FPS, t < fire_for)
        views = sim.render(t, rate)
        frames.append(views)
        for e, _ in EYES:
            lum[e].append(luminance(views[e]))
    return frames, lum, sim.shots, sim.positions, sim.nearer


def analyse(lum, shots):
    res = {}
    for e, _ in EYES:
        ys = lum[e]
        signs = [0] + [transitions(ys[i - 1], ys[i]) for i in range(1, len(ys))]
        field = [float(box_mean(y, FIELD10).max()) for y in ys]   # brightest 10 degree field per frame
        res[e] = dict(flashes=max_flashes_per_second(signs), field=field, peak=max(field),
                      max_px=max(float(y.max()) for y in ys))
    # flash length: frames per shot whose brightest field is >= 10% of that shot's peak (first shot, right eye)
    f = res["R"]["field"]
    s0 = int(round(shots[0] * FPS)) if shots else 0
    seg = f[s0:s0 + 12]
    pk = max(seg) if seg else 0
    res["length_ms"] = 1000.0 / FPS * sum(1 for v in seg if v >= 0.1 * pk) if pk > 0 else 0.0
    return res


# ---- pictures -----------------------------------------------------------------------------------------------------
def stereo_image(views, title, sub=""):
    pad = int(6 * PX)
    im = Image.new("RGB", (2 * W + pad, H + 40), (0, 0, 0))
    for i, (e, _) in enumerate(EYES):
        im.paste(Image.fromarray((np.clip(views[e], 0, 1) * 255).astype(np.uint8)), (i * (W + pad), 20))
        d = ImageDraw.Draw(im)
        d.rounded_rectangle([i * (W + pad) + 1, 21, i * (W + pad) + W - 2, 20 + H - 2], radius=int(46 * PX), outline=(38, 40, 46))
        d.text((i * (W + pad) + 8, 26), e, fill=(110, 110, 115))
    d = ImageDraw.Draw(im)
    d.text((6, 4), title, fill=(255, 255, 255))
    d.text((6, H + 24), sub, fill=(170, 180, 190))
    return im


def save_gif(path, images, fps=30):
    images[0].save(path, save_all=True, append_images=images[1:], duration=int(1000 / fps), loop=0, optimize=True)


def save_mp4(path, images, fps=FPS):
    if cv2 is None:
        return False
    w, h = images[0].size
    vw = cv2.VideoWriter(path, cv2.VideoWriter_fourcc(*"mp4v"), fps, (w, h))
    for im in images:
        vw.write(cv2.cvtColor(np.asarray(im), cv2.COLOR_RGB2BGR))
    vw.release()
    return True


def to_fps(frames, k):
    """Every k headset frames averaged into one picture (90 / k fps; what the eye takes in), so no flash is skipped."""
    return [{e: sum(frames[i + j][e] for j in range(k)) / k for e, _ in EYES} for i in range(0, len(frames) - k + 1, k)]


def _font(size):
    try:
        return ImageFont.load_default(size=size)
    except TypeError:          # an old Pillow: the small bitmap font
        return ImageFont.load_default()


def quality_tile(views, title, eye_w=148, eye_h=123, pad=4, bar=15):
    """One gun at its final size, made for quality, not size: each eye's full render is shrunk with a high-quality
    filter (so it stays smooth and sharp), then the lens outline and the text are drawn at this size (crisp, where
    shrinking the whole picture had blurred them)."""
    w, h = 2 * eye_w + pad, bar + eye_h
    im = Image.new("RGB", (w, h), (0, 0, 0))
    k = 4
    mask = Image.new("L", (eye_w * k, eye_h * k), 0)
    rad = int(round(46 * eye_w / 300))
    ImageDraw.Draw(mask).rounded_rectangle([0, 0, eye_w * k - 1, eye_h * k - 1], radius=rad * k, fill=255)
    mask = mask.resize((eye_w, eye_h), Image.LANCZOS)
    d = ImageDraw.Draw(im)
    small = _font(9)
    for i, (e, _) in enumerate(EYES):
        eye = Image.fromarray((np.clip(views[e], 0, 1) * 255).astype(np.uint8)).resize((eye_w, eye_h), Image.LANCZOS)
        x = i * (eye_w + pad)
        im.paste(eye, (x, bar), mask)
        d.rounded_rectangle([x, bar, x + eye_w - 1, bar + eye_h - 1], radius=rad, outline=(48, 50, 56))
        d.text((x + 6, bar + 4), e, fill=(120, 120, 126), font=small)
    d.text((3, 1), title, fill=(236, 236, 236), font=_font(11))
    return im


def save_gif_hq(path, images, fps):
    """A GIF without banding or palette flicker: one palette for the whole animation, made from the colours the
    flashes really use, every brightness level alike (plus black and the greys of the text), and each frame dithered
    onto it."""
    pix = []
    for im in images[::max(1, len(images) // 30)]:
        a = np.asarray(im).reshape(-1, 3)
        pix.append(a[a.max(axis=1) > 6])
    pix = np.concatenate(pix) if pix else np.zeros((1, 3), np.uint8)
    # Every brightness level equally (the dim glow is most of the pixels, but the bright core needs its colours too,
    # or it dithers into sparkles): up to 3000 samples from each of 16 brightness bands.
    rng = np.random.default_rng(0)
    level = pix.max(axis=1).astype(int) // 16
    keep = []
    for band in range(16):
        idx = np.flatnonzero(level == band)
        if len(idx):
            keep.append(pix[rng.choice(idx, size=min(3000, len(idx)), replace=False)])
    pix = np.concatenate(keep) if keep else pix
    fixed = [(0, 0, 0), (255, 255, 255), (236, 236, 236), (120, 120, 126), (48, 50, 56)] + [(v, v, v) for v in range(16, 256, 24)]
    n = 256 - len(fixed)
    if len(pix) > 1:
        side = int(math.ceil(math.sqrt(len(pix))))
        flat = np.zeros((side * side, 3), np.uint8)
        flat[:len(pix)] = pix
        flat[len(pix):] = pix[0]
        q = Image.fromarray(flat.reshape(side, side, 3)).quantize(colors=n, method=Image.Quantize.MEDIANCUT, dither=Image.Dither.NONE)
        cols = np.asarray(q.getpalette()[:n * 3], np.uint8).reshape(-1, 3).tolist()
    else:
        cols = []
    palette = [c for rgb in fixed for c in rgb] + [c for rgb in cols for c in rgb]
    palette += [0] * (768 - len(palette))
    pal_im = Image.new("P", (1, 1))
    pal_im.putpalette(palette)
    frames = [im.quantize(palette=pal_im, dither=Image.Dither.FLOYDSTEINBERG) for im in images]
    frames[0].save(path, save_all=True, append_images=frames[1:], duration=int(round(1000 / fps)), loop=0,
                   optimize=False, disposal=1)


def save_webp_lossless(path, images, fps):
    """The same animation in full colour and lossless (animated WebP; opens in any browser)."""
    images[0].save(path, save_all=True, append_images=images[1:], duration=int(round(1000 / fps)), loop=0,
                   lossless=True, quality=100, method=4)


def all_guns_quality():
    """All guns at once, limiter on, at the usual size but in high quality: all_guns.gif (45 fps, one dithered
    palette, crisp text) and all_guns.webp (lossless)."""
    os.makedirs(OUT, exist_ok=True)
    soft = calibrate_soft()
    t0 = time.time()
    grid = []
    for gun in GUNS:
        frames, _, _, _, _ = run(gun, True, soft)
        grid.append([quality_tile(v, gun["label"]) for v in to_fps(frames, 2)])
        print(f"  {gun['label']} rendered")
    sheets = grid_frames(grid, gap=6)
    save_gif_hq(os.path.join(OUT, "all_guns.gif"), sheets, 45)
    save_webp_lossless(os.path.join(OUT, "all_guns.webp"), sheets, 45)
    print(f"all_guns: {sheets[0].size[0]} x {sheets[0].size[1]} px, {len(sheets)} frames at 45 fps, {time.time() - t0:.0f} s")


def to_30fps(frames):
    """Each 30 fps picture is the average of three headset frames (what the eye takes in), so no flash is skipped."""
    out = []
    for i in range(0, len(frames) - 2, 3):
        out.append({e: (frames[i][e] + frames[i + 1][e] + frames[i + 2][e]) / 3 for e, _ in EYES})
    return out


def timeline_chart(rows, path):
    """Per gun: the brightest 10 degree field of the right eye over time, limiter off (grey) and on (orange)."""
    cw, rh, left = 900, 110, 210
    im = Image.new("RGB", (cw, rh * len(rows) + 40), (12, 13, 16))
    d = ImageDraw.Draw(im)
    d.text((8, 8), "Right eye: brightest 10-degree field per frame (grey: no limiter, orange: limiter). Ticks: shots.", fill=(230, 230, 230))
    for k, (gun, off, on, shots) in enumerate(rows):
        y0 = 40 + k * rh
        d.text((8, y0 + 8), gun["label"], fill=(235, 235, 235))
        d.text((8, y0 + 26), f"flashes/s: {off['R']['flashes']:g} -> {on['R']['flashes']:g} (max 3)", fill=(190, 190, 195))
        plot_w = cw - left - 10
        n = len(on["R"]["field"])
        scale = max(1e-3, max(off["R"]["peak"], on["R"]["peak"]))
        for res, col in ((off, (120, 120, 125)), (on, (255, 160, 60))):
            pts = [(left + plot_w * i / (n - 1), y0 + rh - 22 - (rh - 34) * v / scale) for i, v in enumerate(res["R"]["field"])]
            d.line(pts, fill=col, width=1)
        for s in shots:
            x = left + plot_w * (s * FPS) / (n - 1)
            d.line([(x, y0 + rh - 18), (x, y0 + rh - 12)], fill=(200, 200, 200))
        d.line([(left, y0 + rh - 20), (left + plot_w, y0 + rh - 20)], fill=(50, 50, 55))
    im.save(path)


def grid_frames(grid, cols=2, gap=8):
    """All guns at once: one sheet per frame, guns in rows of `cols`."""
    tw, th = grid[0][0].size
    rows = (len(grid) + cols - 1) // cols
    frames = []
    for i in range(min(len(g) for g in grid)):
        sheet = Image.new("RGB", (cols * tw + (cols - 1) * gap, rows * th + (rows - 1) * gap), (0, 0, 0))
        for k, g in enumerate(grid):
            r, c = divmod(k, cols)
            sheet.paste(g[i], (c * (tw + gap), r * (th + gap)))
        frames.append(sheet)
    return frames


def all_guns_hd(scale):
    """All guns, limiter on, rendered `scale` times bigger: all_guns_hd.gif (30 fps) and all_guns_hd.mp4 (90 fps)."""
    os.makedirs(OUT, exist_ok=True)
    soft = calibrate_soft()          # at the tested scale
    set_scale(scale)
    t0 = time.time()
    grid30, grid90 = [], []
    for gun in GUNS:
        frames, _, _, _, _ = run(gun, True, soft)
        grid30.append([stereo_image(v, gun["label"]) for v in to_30fps(frames)])
        grid90.append([stereo_image(v, gun["label"]) for v in frames])
        print(f"  {gun['label']} rendered")
    gif = grid_frames(grid30)
    save_gif(os.path.join(OUT, "all_guns_hd.gif"), gif)
    save_mp4(os.path.join(OUT, "all_guns_hd.mp4"), grid_frames(grid90))
    print(f"all_guns_hd: {gif[0].size[0]} x {gif[0].size[1]} px, {time.time() - t0:.0f} s")


# ---- the test run -------------------------------------------------------------------------------------------------
def test_all():
    os.makedirs(OUT, exist_ok=True)
    t0 = time.time()
    soft = calibrate_soft()
    print("soft flash (below the flash threshold) per glow: " + ", ".join(f"{g:.1f}: {v:.3f}" for g, v in zip(*soft[0]))
          + "; one-step fade up to " + ", ".join(f"{g:.1f}: {v:.3f}" for g, v in zip(*soft[1])) + f"; 10-degree field = {FIELD10} px")
    rows, results, grid = [], {}, []
    for gun in GUNS:
        off_frames, off_lum, shots, _, _ = run(gun, False, soft)
        on_frames, on_lum, _, positions, nearer = run(gun, True, soft)
        if len(positions) > 1:
            pos = np.asarray(positions)
            step = float(np.median(np.linalg.norm(np.diff(pos, axis=0), axis=1)))
            spread = float(np.ptp(pos[:, 0])), float(np.ptp(pos[:, 1]))
        else:
            step, spread = 0.0, (0.0, 0.0)
        off, on = analyse(off_lum, shots), analyse(on_lum, shots)
        on["move"] = (step, spread)
        pos = np.asarray(positions) if positions else np.zeros((0, 2))
        on["min_move"] = float(np.min(np.linalg.norm(np.diff(pos, axis=0), axis=1))) if len(pos) > 1 else 0.0
        # the eye nearer the muzzle (sideways) shows the brighter flash on that shot's frame (the no-limiter run: full flashes)
        agree = total = 0
        for st, ne in zip(shots, nearer):
            f = int(round(st * FPS))
            if ne == "=" or f >= len(off["L"]["field"]):
                continue
            total += 1
            brighter = "L" if off["L"]["field"][f] > off["R"]["field"][f] else "R"
            agree += brighter == ne
        on["nearer"] = (agree, total)
        results[gun["key"]] = (off, on)
        rows.append((gun, off, on, shots))
        # GIF: limiter off above, on below (30 fps, three headset frames averaged per picture); MP4: on, 90 fps
        imgs = []
        for a, b in zip(to_30fps(off_frames), to_30fps(on_frames)):
            top = stereo_image(a, gun["label"] + "  (no limiter)")
            bot = stereo_image(b, gun["label"] + "  (limiter on)")
            both = Image.new("RGB", (top.width, top.height * 2), (0, 0, 0))
            both.paste(top, (0, 0))
            both.paste(bot, (0, top.height))
            imgs.append(both)
        save_gif(os.path.join(OUT, f"{gun['key']}.gif"), imgs)
        save_mp4(os.path.join(OUT, f"{gun['key']}_90fps.mp4"), [stereo_image(v, gun["label"] + "  (limiter on, 90 fps)") for v in on_frames])
        grid.append([quality_tile(v, gun["label"]) for v in to_fps(on_frames, 2)])
        print(f"  {gun['label']:36s} peak L {on['L']['peak']:.3f} R {on['R']['peak']:.3f} | flashes/s no limiter "
              f"{off['R']['flashes']:g}/{off['L']['flashes']:g} (R/L), limiter {on['R']['flashes']:g}/{on['L']['flashes']:g} | "
              f"flash {on['length_ms']:.0f} ms | brightest pixel {max(on['L']['max_px'], on['R']['max_px']):.3f} | "
              f"moves {step:.0f} px/shot, spread {spread[0]:.0f}x{spread[1]:.0f} px")
    # all guns in one GIF (limiter on)
    sheets = grid_frames(grid, gap=6)
    save_gif_hq(os.path.join(OUT, "all_guns.gif"), sheets, 45)
    save_webp_lossless(os.path.join(OUT, "all_guns.webp"), sheets, 45)
    timeline_chart(rows, os.path.join(OUT, "timeline.png"))

    # tests
    checks = []
    def check(name, ok, detail):
        checks.append((name, ok, detail))
    semi = [g for g in GUNS if not g["auto"]]
    worst_len = max(results[g["key"]][1]["length_ms"] for g in semi)
    check("each flash is short (<= 67 ms, 6 frames)", worst_len <= 67, f"longest {worst_len:.0f} ms")
    brightest = max(max(r[1]["L"]["max_px"], r[1]["R"]["max_px"]) for r in results.values())
    check("never full white (brightest pixel luminance < 0.98)", brightest < 0.98, f"brightest {brightest:.3f}")
    agree = sum(r[1]["nearer"][0] for r in results.values())
    total = sum(r[1]["nearer"][1] for r in results.values())
    check("the eye nearer the muzzle sees the stronger flash, shot by shot", agree == total,
          f"{agree} of {total} shots (shots with the muzzle midway between the eyes left out)")
    sup = results["suppressed"][1]["R"]["peak"] / max(1e-6, results["rifle"][1]["R"]["peak"])
    check("a suppressed rifle is faint (< 15% of the rifle's flash)", sup < 0.15, f"{sup * 100:.0f}%")
    spread_ok = all(max(r[1]["move"][1]) >= 5 for r in results.values())
    moves_ok = all(r[1]["move"][0] >= 2 for r in results.values())
    check("the flash lands somewhere new each shot (spread >= 5 px, typical move between shots >= 2 px)", spread_ok and moves_ok,
          ", ".join(f"{k} {r[1]['move'][1][0]:.0f}x{r[1]['move'][1][1]:.0f} px, {r[1]['move'][0]:.1f} px/shot" for k, r in results.items()))
    worst_on = max(max(r[1]["L"]["flashes"], r[1]["R"]["flashes"]) for r in results.values())
    check("photosensitivity: at most 3 flashes (6 transitions) in any second, limiter on (every gun, both eyes)", worst_on <= 3,
          f"worst {worst_on:g}/s")
    worst_off = max((max(r[0]["L"]["flashes"], r[0]["R"]["flashes"]), k) for k, r in results.items())
    check("(info) without the limiter", True, f"worst {worst_off[0]:g} flashes/s ({worst_off[1]}): the limiter is needed")
    lines = ["Muzzle flash on the lens: simulation test", f"{FPS} fps, eye view {W}x{H} px, {math.degrees(HFOV):.0f} deg wide, 10-degree field {FIELD10} px", ""]
    for name, ok, detail in checks:
        lines.append(f"[{'PASS' if ok else 'FAIL'}] {name}: {detail}")
    lines.append("")
    lines.append(f"Run time {time.time() - t0:.0f} s. Pictures and videos in {OUT}")
    text = "\n".join(lines)
    open(os.path.join(OUT, "report.txt"), "w", encoding="utf-8").write(text + "\n")
    print()
    print(text)
    return all(ok for _, ok, _ in checks)


# ---- live -----------------------------------------------------------------------------------------------------------
def live():
    import tkinter as tk
    from PIL import ImageTk
    soft = calibrate_soft()
    root = tk.Tk()
    root.title("Muzzle flash on the lens - Space/mouse fires, 1-8 gun, L limiter, Esc quits")
    root.configure(bg="black")
    label = tk.Label(root, bg="black")
    label.pack()
    state = dict(gun=2, lim=True, trigger=False, sim=Sim(GUNS[2], True, soft), t=0.0, last=time.perf_counter(), photo=None)

    def pick(i):
        state["gun"] = i
        state["sim"] = Sim(GUNS[i], state["lim"], soft)

    def key_down(ev):
        if ev.keysym == "space":
            state["trigger"] = True
        elif ev.keysym in [str(i + 1) for i in range(len(GUNS))]:
            pick(int(ev.keysym) - 1)
        elif ev.keysym.lower() == "l":
            state["lim"] = not state["lim"]
            pick(state["gun"])
        elif ev.keysym == "Escape":
            root.destroy()

    def key_up(ev):
        if ev.keysym == "space":
            state["trigger"] = False

    root.bind("<KeyPress>", key_down)
    root.bind("<KeyRelease>", key_up)
    root.bind("<ButtonPress-1>", lambda e: state.update(trigger=True))
    root.bind("<ButtonRelease-1>", lambda e: state.update(trigger=False))

    def tick():
        now = time.perf_counter()
        dt = min(0.05, now - state["last"])
        state["last"] = now
        state["t"] += dt
        sim = state["sim"]
        trig = state["trigger"]
        if not GUNS[state["gun"]]["auto"] and trig:
            state["trigger"] = False   # semi-automatic: one shot per press
        rate = sim.step(state["t"], dt, trig)
        views = sim.render(state["t"], rate)
        g = GUNS[state["gun"]]
        im = stereo_image(views, f"{state['gun'] + 1}. {g['label']}  |  limiter {'on' if state['lim'] else 'off'}",
                          "Space / mouse: fire (hold for automatic)   1-8: gun   L: limiter   Esc: quit")
        im = im.resize((int(im.width * 1.5), int(im.height * 1.5)), Image.BILINEAR)
        state["photo"] = ImageTk.PhotoImage(im)
        label.configure(image=state["photo"])
        root.after(1, tick)

    tick()
    root.mainloop()


if __name__ == "__main__":
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--live", action="store_true", help="open a window and fire yourself")
    ap.add_argument("--all-guns", action="store_true", help="only render the all-guns animation (quality GIF and lossless WebP)")
    ap.add_argument("--hd", type=float, default=0.0, metavar="SCALE",
                    help="only render all guns at SCALE x the eye view (e.g. 1.6): all_guns_hd.gif and .mp4")
    args = ap.parse_args()
    if args.live:
        live()
    elif args.all_guns:
        all_guns_quality()
    elif args.hd:
        all_guns_hd(args.hd)
    else:
        sys.exit(0 if test_all() else 1)
