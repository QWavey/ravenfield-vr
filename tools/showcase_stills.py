"""showcase_stills: the showcase's screenshots in one place (docs/Showcase-Plan.md): every kept take's 4K stills
(showcase/takes/*/still-*.png, taken by the recorder at the scenes' beats) gathered into showcase/stills/, named by take and
beat, with a contact sheet to pick from.

  python tools/showcase_stills.py [--takes showcase/takes] [--out showcase/stills] [--width 640]

Writes showcase/stills/<take>--<beat>.png (hard links: no second copy), and showcase/stills/sheet.jpg (each still small, its
name under it, four across). A still that is mostly one flat colour (a camera inside a wall, a black frame) is listed as
suspect and left off the sheet.
"""
import argparse
import glob
import os
import shutil
import sys

import numpy as np
from PIL import Image, ImageDraw, ImageFont

REPO = os.path.abspath(os.path.join(os.path.dirname(os.path.abspath(__file__)), '..'))


def flat(img):
    """True when the picture is nearly one colour (its brightness spread under 6 of 255)."""
    a = np.asarray(img.convert('L').resize((96, 54)), dtype=np.float32)
    return float(a.std()) < 6.0


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--takes', default=os.path.join(REPO, 'showcase', 'takes'))
    ap.add_argument('--out', default=os.path.join(REPO, 'showcase', 'stills'))
    ap.add_argument('--width', type=int, default=640)
    a = ap.parse_args()
    os.makedirs(a.out, exist_ok=True)
    found = sorted(glob.glob(os.path.join(a.takes, '*', 'still-*.png')))
    if not found:
        print(f'no stills under {a.takes} (the recorder takes them in the picture pass; takes before 2026-11-08 night have none)')
        return 0
    kept, suspect = [], []
    for src in found:
        take = os.path.basename(os.path.dirname(src))
        beat = os.path.basename(src)[len('still-'):-len('.png')]
        dst = os.path.join(a.out, f'{take}--{beat}.png')
        if not os.path.exists(dst):
            try:
                os.link(src, dst)
            except OSError:
                shutil.copy2(src, dst)
        img = Image.open(dst)
        (suspect if flat(img) else kept).append((f'{take} / {beat}', img.size, dst))
    w = a.width
    h = w * 9 // 16
    cols, label = 4, 22
    rows = (len(kept) + cols - 1) // cols
    sheet = Image.new('RGB', (cols * w, max(1, rows) * (h + label)), (16, 16, 16))
    d = ImageDraw.Draw(sheet)
    try:
        font = ImageFont.truetype('bahnschrift.ttf', 15)
    except OSError:
        font = ImageFont.load_default()
    for i, (name, _, path) in enumerate(kept):
        x, y = (i % cols) * w, (i // cols) * (h + label)
        sheet.paste(Image.open(path).convert('RGB').resize((w, h), Image.LANCZOS), (x, y))
        d.text((x + 6, y + h + 3), name, fill=(230, 230, 230), font=font)
    out = os.path.join(a.out, 'sheet.jpg')
    sheet.save(out, quality=88)
    print(f'{len(kept)} still(s) in {os.path.relpath(a.out, REPO)}, sheet {os.path.relpath(out, REPO)}')
    for name, size, _ in kept:
        print(f'  {size[0]}x{size[1]}  {name}')
    for name, _, _ in suspect:
        print(f'  SUSPECT (nearly one colour): {name}')
    return 0


if __name__ == '__main__':
    sys.exit(main())
