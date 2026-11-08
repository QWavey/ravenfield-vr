"""showcase_sheet: a contact sheet of a showcase take, to check it by eye: frames from each marked step of the scene.

  python tools/showcase_sheet.py reports/shots/showcase/showcase-pilot [--per 3] [--width 480]

Reads shots.txt (frame, time, mark, camera) and takes --per frames from each run of one mark (its first, middle and last by
default), each labelled with its frame, mark and camera, in a grid of four columns. Writes sheet.jpg in the take's folder.
"""
import argparse
import os
import sys

from PIL import Image, ImageDraw, ImageFont


def runs(marks):
    """[(mark, camera, [frames])] for each run of one mark and camera, in order."""
    out = []
    for frame, mark, cam in marks:
        if out and out[-1][0] == mark and out[-1][1] == cam:
            out[-1][2].append(frame)
        else:
            out.append((mark, cam, [frame]))
    return out


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('take')
    ap.add_argument('--per', type=int, default=3)
    ap.add_argument('--width', type=int, default=480)
    a = ap.parse_args()
    marks = []
    for i, line in enumerate(open(os.path.join(a.take, 'shots.txt'), encoding='utf-8')):
        parts = line.rstrip('\n').split('\t')
        if i and len(parts) >= 4:
            marks.append((int(parts[0]), parts[2], parts[3]))
    picks = []
    for mark, cam, frames in runs(marks):
        n = min(a.per, len(frames))
        for k in range(n):
            f = frames[round(k * (len(frames) - 1) / max(1, n - 1))] if n > 1 else frames[0]
            picks.append((f, mark, cam))
    if not picks:
        print('no frames marked')
        return 1
    w = a.width
    h = w * 9 // 16
    cols = 4
    gap = 6
    rows = (len(picks) + cols - 1) // cols
    sheet = Image.new('RGB', (cols * w + (cols - 1) * gap, rows * h + (rows - 1) * gap), (16, 16, 16))
    draw = ImageDraw.Draw(sheet)
    try:
        font = ImageFont.truetype('bahnschrift.ttf', 15)
    except OSError:
        font = ImageFont.load_default()
    for i, (f, mark, cam) in enumerate(picks):
        path = os.path.join(a.take, f'f{f:05d}.jpg')
        x, y = (i % cols) * (w + gap), (i // cols) * (h + gap)
        if os.path.exists(path):
            sheet.paste(Image.open(path).convert('RGB').resize((w, h), Image.LANCZOS), (x, y))
        # The label inside its own picture, on a dark band at its top (between the pictures it read as the one above's).
        draw.rectangle((x, y, x + w - 1, y + 20), fill=(0, 0, 0))
        draw.text((x + 6, y + 2), f'{f}  {mark}  {cam}', fill=(235, 235, 235), font=font)
    out = os.path.join(a.take, 'sheet.jpg')
    sheet.save(out, quality=88)
    print(f'sheet: {out} ({len(picks)} frames)')
    return 0


if __name__ == '__main__':
    sys.exit(main())
