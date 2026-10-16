"""GIFs from the weapon run's still pictures: the magazine sequence and racking, per gun.

  python tools/seqgif.py SHOTS_DIR OUT_DIR [--ids 58,60,211] [--width 640]

For each gun (the NNN in the picture names; all guns without --ids):
  seat-NNN-NAME.gif  the magazine pulled out, in the hand, lined up and pushed back in (mag-1 ... mag-7), labelled
  rack-NNN-NAME.gif  racking: the close-ups at rest and pulled half way, left and right side, flipping
Only local files are read; Pillow is the only dependency.
"""
import argparse
import os
import re
import sys

from PIL import Image, ImageDraw, ImageFont

SEAT = [
    ('mag-1-seated', 'seated'),
    ('mag-2-leaving', 'pulled out'),
    ('mag-3-out', 'out'),
    ('mag-4-in-hand', 'in the hand'),
    ('mag-5-lined-up', 'lined up under the well'),
    ('mag-6a-30-in', '30% in'),
    ('mag-6-half-in', 'half in'),
    ('mag-6b-60-in', '60% in'),
    ('mag-6c-85-in', '85% in'),
    ('mag-7-seated-again', 'seated again'),
]


def font(size):
    for name in ('segoeui.ttf', 'arial.ttf', 'DejaVuSans.ttf'):
        try:
            return ImageFont.truetype(name, size)
        except OSError:
            continue
    return ImageFont.load_default()


def frame(path, label, width, f):
    im = Image.open(path).convert('RGB')
    h = int(im.height * width / im.width)
    im = im.resize((width, h), Image.LANCZOS)
    out = Image.new('RGB', (width, h + 28), (18, 20, 24))
    out.paste(im, (0, 0))
    ImageDraw.Draw(out).text((8, h + 4), label, fill=(230, 230, 230), font=f)
    return out


def save(frames, durations, path):
    # One shared palette keeps the colours steady from frame to frame.
    pal = frames[0].quantize(colors=255, method=Image.MEDIANCUT)
    q = [fr.quantize(palette=pal, dither=Image.FLOYDSTEINBERG) for fr in frames]
    q[0].save(path, save_all=True, append_images=q[1:], duration=durations, loop=0, optimize=True)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('shots')
    ap.add_argument('out')
    ap.add_argument('--ids', default='')
    ap.add_argument('--width', type=int, default=640)
    a = ap.parse_args()
    os.makedirs(a.out, exist_ok=True)
    want = {int(x) for x in a.ids.split(',') if x.strip()} if a.ids else None
    f = font(16)
    names = {}
    for fn in os.listdir(a.shots):
        m = re.match(r'(?:mag-\S+?|rack-grip(?:-pulled)?)-(\d{3})-(.+?)(-left|-right)?\.png$', fn)
        if m and (want is None or int(m.group(1)) in want):
            names[int(m.group(1))] = m.group(2)
    made = 0
    for gid, name in sorted(names.items()):
        tag = f'{gid:03d}-{name}'
        frames, durations = [], []
        for prefix, label in SEAT:
            p = os.path.join(a.shots, f'{prefix}-{tag}.png')
            if os.path.exists(p):
                frames.append(frame(p, f'{name}: {label}', a.width, f))
                durations.append(900)
        if len(frames) >= 3:
            durations[-1] = 1600
            save(frames, durations, os.path.join(a.out, f'seat-{tag}.gif'))
            made += 1
        frames, durations = [], []
        for side in ('-left', '-right'):
            rest = os.path.join(a.shots, f'rack-grip-{tag}{side}.png')
            pulled = os.path.join(a.shots, f'rack-grip-pulled-{tag}{side}.png')
            if os.path.exists(rest) and os.path.exists(pulled):
                for _ in range(2):
                    frames.append(frame(rest, f'{name}: rack, at rest ({side[1:]} side)', a.width, f))
                    frames.append(frame(pulled, f'{name}: rack, pulled half way ({side[1:]} side)', a.width, f))
                    durations += [700, 700]
        if frames:
            save(frames, durations, os.path.join(a.out, f'rack-{tag}.gif'))
            made += 1
    print(f'{made} GIF(s) in {a.out}')


if __name__ == '__main__':
    sys.exit(main())
