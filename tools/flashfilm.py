"""flashfilm: the eye films of a vrshots --film run (autotest -rfvr-flashfilm) as videos, per-shot strips and numbers.

Each film-<id>-<gun>/ folder holds what each eye of the headset got, frame by frame (L-0000.png, R-0000.png) and the
frame log (frames.txt: frame, eye, time, what happened, the lens flash and glow strength in that eye's picture).

For every gun this writes, next to its folder:
  <gun>.mp4          both eyes side by side, real time
  <gun>-slow.gif     the same four times slower, so each flash can be seen
  <gun>-shots.png    every shot: the frame before it and the five after it, both eyes, with the numbers
and one film-summary.txt for the run:
  - each eye's brightness standing still (the two eyes must match: one brighter than the other is a bug)
  - for each shot, frame by frame, the lens flash strength drawn in each eye and how much brighter the view got
    (the 99th percentile of the brightening against the frame before the shot)

  python tools/flashfilm.py reports/shots/film
"""
import glob
import os
import re
import sys

import numpy as np
from PIL import Image, ImageDraw

try:
    import cv2
except ImportError:  # the MP4 is skipped without OpenCV
    cv2 = None


def luma(img):
    a = np.asarray(img, dtype=np.float32) / 255.0
    return 0.2126 * a[..., 0] + 0.7152 * a[..., 1] + 0.0722 * a[..., 2]


def read_film(d):
    rows = []
    for line in open(os.path.join(d, 'frames.txt'), encoding='utf-8').read().splitlines()[1:]:
        p = line.split('\t')
        if len(p) >= 5:
            rows.append({'frame': int(p[0]), 'eye': p[1], 'time': float(p[2]), 'note': p[3], 'lens': p[4]})
    eyes = {'L': [r for r in rows if r['eye'] == 'L'], 'R': [r for r in rows if r['eye'] == 'R']}
    n = min(len(eyes['L']), len(eyes['R']))
    frames = []
    for i in range(n):
        lp, rp = os.path.join(d, f'L-{i:04d}.png'), os.path.join(d, f'R-{i:04d}.png')
        if os.path.exists(lp) and os.path.exists(rp):
            frames.append((i, eyes['L'][i], eyes['R'][i], lp, rp))
    return frames


def lens_num(s, key):
    m = re.search(key + r' ([0-9.]+)', s)
    return float(m.group(1)) if m else 0.0


def pair(lp, rp, label, scale=1.0):
    L, R = Image.open(lp).convert('RGB'), Image.open(rp).convert('RGB')
    if scale != 1.0:
        L = L.resize((int(L.width * scale), int(L.height * scale)), Image.LANCZOS)
        R = R.resize((int(R.width * scale), int(R.height * scale)), Image.LANCZOS)
    out = Image.new('RGB', (L.width + R.width + 4, L.height + 18), (20, 20, 20))
    out.paste(L, (0, 0))
    out.paste(R, (L.width + 4, 0))
    ImageDraw.Draw(out).text((4, L.height + 3), label, fill=(255, 230, 120))
    return out


def film(d, summary):
    name = os.path.basename(d)
    frames = read_film(d)
    if len(frames) < 3:
        summary.append(f'{name}: only {len(frames)} frame(s)')
        return
    t0 = frames[0][1]['time']
    dur = frames[-1][1]['time'] - t0
    fps = (len(frames) - 1) / dur if dur > 0 else 45.0
    summary.append(f'== {name}: {len(frames)} frames per eye over {dur:.2f} s ({fps:.0f} fps)')

    # Standing still: each eye's brightness (they must match).
    still = [f for f in frames if f[1]['note'] == 'still'][2:]
    if still:
        lm = np.mean([luma(Image.open(f[3])).mean() for f in still])
        rm = np.mean([luma(Image.open(f[4])).mean() for f in still])
        ratio = max(lm, rm) / max(1e-6, min(lm, rm))
        summary.append(f'  standing still: left eye {lm:.3f}, right eye {rm:.3f} mean brightness'
                       + (f'  ONE EYE {ratio:.2f}x BRIGHTER' if ratio > 1.06 else '  (match)'))

    # Each shot: the frame before and five after, with the lens numbers and the brightening.
    shots = []
    for k, f in enumerate(frames):
        note = f[1]['note']
        if 'shot' in note and (k == 0 or frames[k - 1][1]['note'] != note):
            shots.append(k)
    strip_rows = []
    for k in shots:
        note = frames[k][1]['note']
        before = max(0, k - 1)
        bl, br = luma(Image.open(frames[before][3])), luma(Image.open(frames[before][4]))
        summary.append(f'  {note}:')
        for j in range(before, min(len(frames), k + 6)):
            i, lrow, rrow, lp, rp = frames[j]
            dl = np.percentile(np.maximum(0, luma(Image.open(lp)) - bl), 99)
            dr = np.percentile(np.maximum(0, luma(Image.open(rp)) - br), 99)
            fl, fr = lens_num(lrow['lens'], 'flash'), lens_num(rrow['lens'], 'flash')
            gl, gr = lens_num(lrow['lens'], 'glow'), lens_num(rrow['lens'], 'glow')
            dt = (lrow['time'] - frames[k][1]['time']) * 1000
            summary.append(f'    {dt:+6.0f} ms  lens flash L {fl:.2f} R {fr:.2f}  glow L {gl:.2f} R {gr:.2f}  '
                           f'brighter (99%) L {dl:.2f} R {dr:.2f}')
            label = f'{note}  {dt:+.0f} ms   L flash {fl:.2f} glow {gl:.2f}   R flash {fr:.2f} glow {gr:.2f}'
            strip_rows.append(pair(lp, rp, label, 0.75))
        worker = re.search(r'worker ([0-9.]+) ms \(most ([0-9.]+)\)', frames[k][1]['lens'])
        if worker:
            summary.append(f'    lens worker {worker.group(1)} ms a frame, most {worker.group(2)} ms')
    if strip_rows:
        w = strip_rows[0].width
        sheet = Image.new('RGB', (w, sum(r.height for r in strip_rows)), (0, 0, 0))
        y = 0
        for r in strip_rows:
            sheet.paste(r, (0, y))
            y += r.height
        sheet.save(os.path.join(os.path.dirname(d), name + '-shots.png'))

    # The films: real time (MP4) and four times slower (GIF).
    pics = [pair(f[3], f[4], f"{f[1]['note']}  {1000 * (f[1]['time'] - t0):.0f} ms  |  L {f[1]['lens'][:22]}  R {f[2]['lens'][:22]}")
            for f in frames]
    if cv2 is not None:
        h, w = pics[0].height, pics[0].width
        vw = cv2.VideoWriter(os.path.join(os.path.dirname(d), name + '.mp4'), cv2.VideoWriter_fourcc(*'mp4v'), fps, (w, h))
        for p in pics:
            vw.write(cv2.cvtColor(np.asarray(p), cv2.COLOR_RGB2BGR))
        vw.release()
    small = [p.resize((p.width * 2 // 3, p.height * 2 // 3), Image.LANCZOS) for p in pics]
    durations = []
    for j in range(len(frames)):
        nxt = frames[j + 1][1]['time'] if j + 1 < len(frames) else frames[j][1]['time'] + 1 / fps
        durations.append(max(20, int(round(4000 * (nxt - frames[j][1]['time'])))))
    small[0].save(os.path.join(os.path.dirname(d), name + '-slow.gif'), save_all=True, append_images=small[1:],
                  duration=durations, loop=0, optimize=False)


def main(root):
    summary = []
    dirs = sorted(d for d in glob.glob(os.path.join(root, 'film-*')) if os.path.isdir(d))
    for d in dirs:
        film(d, summary)
    text = '\n'.join(summary) + '\n'
    open(os.path.join(root, 'film-summary.txt'), 'w', encoding='utf-8').write(text)
    print(text)


if __name__ == '__main__':
    main(sys.argv[1] if len(sys.argv) > 1 else os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), 'reports', 'shots', 'film'))
