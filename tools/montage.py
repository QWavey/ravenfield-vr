"""montage: one fast, smooth montage of the pictures a vrshots run took, weapon by weapon (Tasks 219).

  python tools/montage.py                       the full weapons run (reports/shots/weapons) -> reports/shots/montage/
  python tools/montage.py reports/shots/weapon  any other run's folder
  options: --per-weapon 4   key pictures per weapon (spread over what the run took)
           --size 640x400   the MP4's frame size;  --gif-size 320x200 the GIF's
           --hold 2 --fade 2 frames each picture is held, and cross-faded into the next
           --fps 30          --gif-every 2 (the GIF keeps every 2nd frame)
           --no-gif / --no-mp4

Each weapon's pictures go in the order the test took them: in the hand, the rack grip from the left and pulled, then
the magazine seated, leaving, in the hand, lined up, half way in and seated again. Each is held for a moment and
cross-faded into the next, the weapon's id and name in a corner. The run's other pictures (menus, body, damage,
dirt, lens effects) come after the weapons. Writes montage.mp4 (needs OpenCV) and montage.gif.
"""
import argparse
import os
import re
import sys

from PIL import Image, ImageDraw, ImageFont

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
ORDER = ['weapon', 'rack-grip@left', 'rack-grip-pulled@left', 'mag-1-seated', 'mag-2-leaving', 'mag-4-in-hand',
         'mag-5-lined-up', 'mag-6-half-in', 'mag-7-seated-again']
PAT = re.compile(r'^(?P<kind>[a-z0-9-]+?)-(?P<idx>\d{3})-(?P<name>.+?)(?P<side>-left|-right)?\.png$')


def font(size):
    try:
        return ImageFont.load_default(size=size)
    except TypeError:  # an old Pillow
        return ImageFont.load_default()


def collect(shots):
    """(weapons: [(id, name, [paths in test order])], others: [paths])"""
    weapons, others = {}, []
    for f in sorted(os.listdir(shots)):
        if not f.endswith('.png'):
            continue
        m = PAT.match(f)
        if not m or m.group('name').endswith('-looking-down'):
            others.append(os.path.join(shots, f))
            continue
        key = m.group('kind') + ('@' + m.group('side')[1:] if m.group('side') else '')
        if key not in ORDER:
            others.append(os.path.join(shots, f))  # a weapon's other pictures (its lens flash...) after the weapons
            continue
        weapons.setdefault((int(m.group('idx')), m.group('name')), {})[key] = os.path.join(shots, f)
    out = []
    for (idx, name), pics in sorted(weapons.items()):
        ordered = [pics[k] for k in ORDER if k in pics]
        if ordered:
            out.append((idx, name, ordered))
    return out, others


def spread(items, n):
    """n of the items, evenly spread, the first and the last kept."""
    if len(items) <= n:
        return items
    if n == 1:
        return items[:1]
    return [items[round(i * (len(items) - 1) / (n - 1))] for i in range(n)]


def card(path, size, caption, sub):
    try:
        im = Image.open(path).convert('RGB')
    except OSError as e:  # cut short when the game was stopped
        print(f'skipped {os.path.basename(path)}: {e}')
        return None
    w, h = size
    # Fill the frame (cropped to its shape), not letterboxed.
    scale = max(w / im.width, h / im.height)
    im = im.resize((max(w, round(im.width * scale)), max(h, round(im.height * scale))), Image.LANCZOS)
    x0, y0 = (im.width - w) // 2, (im.height - h) // 2
    im = im.crop((x0, y0, x0 + w, y0 + h))
    d = ImageDraw.Draw(im, 'RGBA')
    big, small = font(max(11, h // 22)), font(max(9, h // 30))
    d.rectangle([0, 0, w, max(16, h // 13)], fill=(0, 0, 0, 150))
    d.text((6, 2), caption, fill=(255, 255, 255, 255), font=big)
    if sub:
        d.text((w - 6 - d.textlength(sub, font=small), 3), sub, fill=(255, 230, 120, 255), font=small)
    return im


def frames(cards, hold, fade):
    """Each card held, then cross-faded into the next (one card looked ahead: they are made as they are needed)."""
    prev = None
    for c in cards:
        if c is None:
            continue
        if prev is not None:
            for k in range(1, fade + 1):
                yield Image.blend(prev, c, k / (fade + 1))
        for _ in range(hold):
            yield c
        prev = c


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument('shots', nargs='?', default=os.path.join(REPO, 'reports', 'shots', 'weapons'))
    ap.add_argument('--out', default=os.path.join(REPO, 'reports', 'shots', 'montage'))
    ap.add_argument('--per-weapon', type=int, default=4)
    ap.add_argument('--size', default='640x400')
    ap.add_argument('--gif-size', default='320x200')
    ap.add_argument('--hold', type=int, default=2)
    ap.add_argument('--fade', type=int, default=2)
    ap.add_argument('--fps', type=float, default=30)
    ap.add_argument('--gif-every', type=int, default=2)
    ap.add_argument('--no-gif', action='store_true')
    ap.add_argument('--no-mp4', action='store_true')
    a = ap.parse_args()
    if not os.path.isdir(a.shots):
        sys.exit(f'no pictures in {a.shots}')
    weapons, others = collect(a.shots)
    size = tuple(int(v) for v in a.size.lower().split('x'))
    gif_size = tuple(int(v) for v in a.gif_size.lower().split('x'))
    os.makedirs(a.out, exist_ok=True)

    def all_cards(sz):
        for idx, name, pics in weapons:
            picked = spread(pics, a.per_weapon)
            for p in picked:
                kind = os.path.basename(p).split(f'-{idx:03d}-')[0]
                side = ' (left)' if p.endswith('-left.png') else ''
                yield card(p, sz, f'{idx:03d} {name}', kind.replace('-', ' ') + side)
        for p in others:
            yield card(p, sz, os.path.basename(p)[:-4].replace('-', ' '), '')

    total = 0
    if not a.no_mp4:
        try:
            import cv2
            import numpy as np
        except ImportError:
            cv2 = None
            print('OpenCV is not installed: no MP4')
        if cv2 is not None:
            path = os.path.join(a.out, 'montage.mp4')
            # H.264 when this OpenCV has it (a tenth of the size), else MPEG-4 part 2.
            vw = cv2.VideoWriter(path, cv2.VideoWriter_fourcc(*'avc1'), a.fps, size)
            if not vw.isOpened():
                vw = cv2.VideoWriter(path, cv2.VideoWriter_fourcc(*'mp4v'), a.fps, size)
            if not vw.isOpened():
                sys.exit(f'could not open a video writer for {path}')
            for f in frames(all_cards(size), a.hold, a.fade):
                vw.write(cv2.cvtColor(np.asarray(f), cv2.COLOR_RGB2BGR))
                total += 1
            vw.release()
            print(f'{path}: {total} frames, {total / a.fps:.0f} s, {os.path.getsize(path) / 1e6:.1f} MB')
    if not a.no_gif:
        path = os.path.join(a.out, 'montage.gif')
        seq = [f.quantize(colors=128, method=Image.Quantize.FASTOCTREE, dither=Image.Dither.NONE)
               for i, f in enumerate(frames(all_cards(gif_size), a.hold, a.fade)) if i % a.gif_every == 0]
        if seq:
            seq[0].save(path, save_all=True, append_images=seq[1:], duration=round(1000 * a.gif_every / a.fps), loop=0, optimize=False)
            print(f'{path}: {len(seq)} frames, {os.path.getsize(path) / 1e6:.1f} MB')
    print(f'{len(weapons)} weapons, {len(others)} other pictures')


if __name__ == '__main__':
    main()
