"""Generate a clean blue VR-headset app icon for Ravenfield VR.

Renders each ICO size individually with supersampling (crisp small sizes),
then packs them into a single multi-resolution .ico. Also writes a 256px PNG
preview for eyeballing.
"""
import os
import sys
from PIL import Image, ImageDraw, ImageFilter

# Default: write installer/app.ico next to app.manifest (this file lives in tools/).
_REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUT_ICO = sys.argv[1] if len(sys.argv) > 1 else os.path.join(_REPO, "installer", "app.ico")
OUT_PNG = sys.argv[2] if len(sys.argv) > 2 else os.path.join(os.path.dirname(OUT_ICO), "app_preview.png")

# Palette — deep navy to vivid blue.
TOP = (22, 52, 110)      # top of gradient
BOT = (45, 120, 255)     # bottom of gradient
VISOR = (247, 250, 255)  # headset body
LENS = (16, 30, 68)      # dark lens cavity
GLOW = (90, 170, 255)    # soft ring around lenses
STRAP = (255, 255, 255, 120)


def lerp(a, b, t):
    return tuple(round(a[i] + (b[i] - a[i]) * t) for i in range(3))


def rounded_mask(size, radius):
    m = Image.new("L", (size, size), 0)
    d = ImageDraw.Draw(m)
    d.rounded_rectangle([0, 0, size - 1, size - 1], radius=radius, fill=255)
    return m


def render(S):
    """Render the icon at resolution S (already the supersampled size)."""
    img = Image.new("RGBA", (S, S), (0, 0, 0, 0))

    # Vertical gradient background.
    grad = Image.new("RGB", (1, S))
    gd = grad.load()
    for y in range(S):
        gd[0, y] = lerp(TOP, BOT, y / (S - 1))
    grad = grad.resize((S, S))
    # Subtle diagonal sheen for depth (very light, top-left).
    sheen = Image.new("L", (S, S), 0)
    sd = ImageDraw.Draw(sheen)
    sd.ellipse([-S * 0.4, -S * 0.5, S * 0.7, S * 0.6], fill=38)
    sheen = sheen.filter(ImageFilter.GaussianBlur(S * 0.08))
    bg = Image.composite(Image.new("RGB", (S, S), (255, 255, 255)), grad, sheen)
    bg = Image.blend(grad, bg, 0.5)
    bg = bg.convert("RGBA")
    bg.putalpha(rounded_mask(S, int(S * 0.225)))
    img = Image.alpha_composite(img, bg)

    # --- Head straps: thin bands running off the visor sides (wrap-around) ---
    strap = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    strd = ImageDraw.Draw(strap)
    strd.rounded_rectangle([S * 0.015, S * 0.445, S * 0.22, S * 0.545],
                           radius=S * 0.05, fill=STRAP)
    strd.rounded_rectangle([S * 0.78, S * 0.445, S * 0.985, S * 0.545],
                           radius=S * 0.05, fill=STRAP)
    img = Image.alpha_composite(img, strap)

    # --- Visor body (white, rounded) with a carved nose notch ---
    visor = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    vd = ImageDraw.Draw(visor)
    vx0, vy0, vx1, vy1 = S * 0.135, S * 0.335, S * 0.865, S * 0.665
    vd.rounded_rectangle([vx0, vy0, vx1, vy1], radius=S * 0.115, fill=VISOR)
    # Nose notch: carve a pronounced upward arc at the bottom centre.
    vd.ellipse([S * 0.405, vy1 - S * 0.14, S * 0.595, vy1 + S * 0.10],
               fill=(0, 0, 0, 0))
    img = Image.alpha_composite(img, visor)

    # --- Lenses: rounded-rectangle optics with an inner screen glow ---
    lens = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    ld = ImageDraw.Draw(lens)
    ly0, ly1 = S * 0.40, S * 0.575
    lrad = S * 0.05
    boxes = [(S * 0.195, S * 0.455), (S * 0.545, S * 0.805)]
    for lx0, lx1 in boxes:
        ld.rounded_rectangle([lx0, ly0, lx1, ly1], radius=lrad, fill=LENS + (255,))
    # inner screen glow (soft blue) centred in each lens
    glow = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    gd2 = ImageDraw.Draw(glow)
    for lx0, lx1 in boxes:
        cx, cy = (lx0 + lx1) / 2, (ly0 + ly1) / 2
        gr = (lx1 - lx0) * 0.33
        gd2.ellipse([cx - gr, cy - gr, cx + gr, cy + gr], fill=GLOW + (210,))
    glow = glow.filter(ImageFilter.GaussianBlur(S * 0.02))
    lens = Image.alpha_composite(lens, glow)
    # thin specular streak near the top of each lens
    ld = ImageDraw.Draw(lens)
    for lx0, lx1 in boxes:
        ld.rounded_rectangle([lx0 + (lx1 - lx0) * 0.16, ly0 + S * 0.022,
                              lx1 - (lx1 - lx0) * 0.16, ly0 + S * 0.05],
                             radius=S * 0.014, fill=(190, 220, 255, 150))
    # keep lenses within the visor only
    vmask = Image.new("L", (S, S), 0)
    ImageDraw.Draw(vmask).rounded_rectangle([vx0, vy0, vx1, vy1], radius=S * 0.115, fill=255)
    img.paste(lens, (0, 0), Image.composite(lens.split()[3], Image.new("L", (S, S), 0), vmask))

    return img


SIZES = [256, 128, 64, 48, 32, 16]
frames = []
for s in SIZES:
    ss = min(1024, s * (4 if s >= 128 else 16))
    frames.append(render(ss).resize((s, s), Image.LANCZOS))

# Pack all sizes into one .ico
frames[0].save(OUT_ICO, format="ICO",
               sizes=[(s, s) for s in SIZES],
               append_images=frames[1:])
print("Wrote", OUT_ICO, "sizes:", SIZES)
if len(sys.argv) > 2:          # optional 256px preview only when a path is given
    frames[0].save(OUT_PNG)
    print("Wrote preview", OUT_PNG)
