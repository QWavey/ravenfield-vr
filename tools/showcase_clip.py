"""showcase_clip: one finished clip from a showcase take (docs/Showcase-Plan.md): the take's picture and its own game sound lined up
(tools/showcase_take.py's sync), a light grade, and an end card. No music; the only sound is the game's.

  python tools/showcase_clip.py reports/shots/showcase/showcase-pilot OUT.mp4 [--title "RAVENFIELD VR"] [--card 1.8]

The card: black, the title in Bahnschrift Bold Condensed (Windows' own DIN-style face), wide-tracked, faded in; the take's sound
rings out under it. H.264 CRF 16 + AAC 192k, 1920x1080 at the take's rate.
"""
import argparse
import os
import subprocess
import sys
import tempfile

import numpy as np
from PIL import Image, ImageDraw, ImageFont

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import showcase_take as take  # noqa: E402


def card(path, title, w=1920, h=1080):
    im = Image.new('RGB', (w, h), (0, 0, 0))
    d = ImageDraw.Draw(im)
    font = ImageFont.truetype('bahnschrift.ttf', 118)
    try:
        font.set_variation_by_name('Bold Condensed')
    except (OSError, ValueError):
        pass
    # Tracked out by hand: each letter placed with 0.14 em between.
    track = int(118 * 0.14)
    widths = [d.textlength(c, font=font) for c in title]
    total = sum(widths) + track * (len(title) - 1)
    x = (w - total) / 2
    for c, cw in zip(title, widths):
        d.text((x, h / 2), c, font=font, fill=(236, 236, 232), anchor='lm')
        x += cw + track
    im.save(path)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('take')
    ap.add_argument('out')
    ap.add_argument('--title', default='RAVENFIELD VR')
    ap.add_argument('--card', type=float, default=1.8)
    a = ap.parse_args()
    exe = take.ffmpeg()
    if not exe:
        raise SystemExit('no ffmpeg')
    info = take.read_kv(os.path.join(a.take, 'take.txt'))
    fps, frames = int(info.get('fps', '60')), int(info.get('frames', '0'))
    pic = take.read_marks(os.path.join(a.take, 'shots.txt'))
    snd = take.read_marks(os.path.join(a.take, 'sound.txt'))
    wav = os.path.join(a.take, 'audio.wav')
    audio, rate = take.read_wav(wav)
    shift, errors, _, _ = take.sync(pic, snd, take.onsets(audio, rate))
    shift = shift or 0.0
    gain = min(1.0, 0.89 / max(float(np.abs(audio).max()), 1e-6))
    secs = frames / fps
    total = secs + a.card
    tmp = tempfile.mkdtemp(prefix='showcase-clip-')
    title_png = os.path.join(tmp, 'card.png')
    card(title_png, a.title)
    afilt = f'atrim=start={shift:.4f},asetpts=PTS-STARTPTS' if shift >= 0 else f'adelay={int(-shift * 1000)}:all=1'
    graph = (
        # The take, graded a little: more contrast, a touch less saturation, a soft vignette (the game's flat light, not a filter look).
        f'[0:v]eq=contrast=1.10:saturation=0.92:gamma=0.97,vignette=angle=PI/6,scale=in_range=full:out_range=limited,format=yuv420p,setsar=1[take];'
        f'[1:v]loop=loop=-1:size=1,trim=duration={a.card:.3f},fps={fps},fade=t=in:st=0.15:d=0.5,scale=in_range=full:out_range=limited,format=yuv420p,setsar=1[card];'
        f'[take][card]concat=n=2:v=1:a=0[v];'
        f'[2:a]{afilt},volume={gain:.4f},apad,atrim=0:{total:.4f},afade=t=out:st={total - 1.2:.3f}:d=1.2[a]'
    )
    cmd = [exe, '-y', '-loglevel', 'error', '-framerate', str(fps), '-i', os.path.join(a.take, 'f%05d.jpg'), '-i', title_png, '-i', wav,
           '-filter_complex', graph, '-map', '[v]', '-map', '[a]', '-c:v', 'libx264', '-crf', '16', '-preset', 'slow', '-pix_fmt', 'yuv420p',
           '-r', str(fps), '-c:a', 'aac', '-b:a', '192k', '-movflags', '+faststart', a.out]
    r = subprocess.run(cmd, capture_output=True, text=True)
    if r.returncode != 0:
        raise SystemExit('ffmpeg failed: ' + r.stderr[-800:])
    print(f'clip: {a.out} ({total:.1f} s: {secs:.1f} s of take, {a.card:.1f} s card; sound shifted {1000 * shift:+.0f} ms, gain {gain:.2f})')
    return 0


if __name__ == '__main__':
    sys.exit(main())
