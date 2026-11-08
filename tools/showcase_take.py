"""showcase_take: checks a showcase take (docs/Showcase-Plan.md) and makes a quick preview.

  python tools/showcase_take.py reports/shots/showcase/showcase-pilot [--preview]

A take folder holds f00000.jpg ... (one picture a frame at the take's rate), audio.wav (the game's own sound in lock-step, 32-bit
float), shots.txt (frame, game time, the scene's mark, the camera) and take.txt (rate, frames, size, sound format).

Checks and prints:
  - frames on disk against the frames filmed (a missing one: the writers lost it)
  - the sound's length against the picture's (frames / fps), in milliseconds
  - for every frame marked "shot": where the sound's onset is (the first 1/240 s block that jumps over the level before it),
    in frames from the marked one (0-1 is in sync; a shot's sound starts the frame it is fired or the next)
  - the first frame's orientation (the sky should be at the top: the top rows brighter or bluer than the bottom on an outdoor take)
--preview: preview.mp4 next to the take, the picture only, 1280x720 (OpenCV).
--mux: take.mp4 next to the take, picture and the game's sound (H.264 CRF 18 + AAC 192k, ffmpeg).
"""
import glob
import os
import shutil
import struct
import subprocess
import sys

import numpy as np
from PIL import Image


def ffmpeg():
    """ffmpeg on PATH, else where winget puts it (Gyan.FFmpeg)."""
    found = shutil.which('ffmpeg')
    if found:
        return found
    hits = glob.glob(os.path.join(os.environ.get('LOCALAPPDATA', ''), 'Microsoft', 'WinGet', 'Packages', 'Gyan.FFmpeg*', '*', 'bin', 'ffmpeg.exe'))
    return hits[0] if hits else None


def read_take(d):
    take = {}
    for line in open(os.path.join(d, 'take.txt'), encoding='utf-8'):
        k, _, v = line.strip().partition('\t')
        if k:
            take[k] = v
    marks = []
    for i, line in enumerate(open(os.path.join(d, 'shots.txt'), encoding='utf-8')):
        if i == 0:
            continue
        parts = line.rstrip('\n').split('\t')
        if len(parts) >= 4:
            marks.append((int(parts[0]), float(parts[1]), parts[2], parts[3]))
    return take, marks


def read_wav(path):
    with open(path, 'rb') as f:
        data = f.read()
    if data[:4] != b'RIFF' or data[8:12] != b'WAVE':
        raise SystemExit(f'{path}: not a WAV')
    pos, fmt, samples = 12, None, None
    while pos + 8 <= len(data):
        cid, size = data[pos:pos + 4], struct.unpack('<I', data[pos + 4:pos + 8])[0]
        body = data[pos + 8:pos + 8 + size]
        if cid == b'fmt ':
            fmt = struct.unpack('<HHIIHH', body[:16])
        elif cid == b'data':
            samples = np.frombuffer(body, dtype='<f4')
        pos += 8 + size + (size & 1)
    if fmt is None or samples is None:
        raise SystemExit(f'{path}: no fmt or data chunk')
    channels, rate = fmt[1], fmt[2]
    return samples.reshape(-1, channels), rate


def onsets(audio, rate, block=None):
    """Mono level per 1/240 s block and the blocks where it jumps (over 4x the level of the 4 blocks before, and over 0.02)."""
    block = block or max(1, rate // 240)
    mono = np.abs(audio).mean(axis=1)
    n = len(mono) // block
    level = mono[:n * block].reshape(n, block).max(axis=1)
    jumps = []
    for i in range(4, n):
        before = level[i - 4:i].mean()
        if level[i] > 0.02 and level[i] > 4 * max(before, 1e-4):
            jumps.append(i)
    return jumps, block


def main():
    if len(sys.argv) < 2:
        print(__doc__)
        return 1
    d = sys.argv[1]
    take, marks = read_take(d)
    fps = int(take.get('fps', '60'))
    frames = int(take.get('frames', '0'))
    jpgs = sorted(f for f in os.listdir(d) if f.startswith('f') and f.endswith('.jpg'))
    print(f'{d}: {frames} frames filmed at {fps} fps ({frames / fps:.2f} s), {len(jpgs)} pictures on disk'
          + ('' if len(jpgs) == frames else f'  ** {frames - len(jpgs)} MISSING **'))
    wav = os.path.join(d, 'audio.wav')
    if os.path.exists(wav):
        audio, rate = read_wav(wav)
        if len(audio) == 0:
            print("sound: audio.wav HAS NO SAMPLES (the game's sound was not recorded)")
            audio = None
    if os.path.exists(wav) and audio is not None:
        secs = len(audio) / rate
        print(f'sound: {secs:.3f} s at {rate} Hz, {audio.shape[1]} channel(s); against the picture {1000 * (secs - frames / fps):+.0f} ms; '
              f'peak {np.abs(audio).max():.3f}')
        jumps, block = onsets(audio, rate)
        frame_of_block = lambda b: b * block / rate * fps
        for f, _, mark, cam in marks:
            if mark != 'shot':
                continue
            near = [frame_of_block(b) - f for b in jumps if -6 <= frame_of_block(b) - f <= 12]
            print(f'  shot at frame {f} ({cam}): ' + (f'sound starts {min(near, key=abs):+.1f} frames from it' if near else 'NO SOUND ONSET within 12 frames'))
    else:
        print('sound: NO audio.wav')
    if jpgs:
        im = np.asarray(Image.open(os.path.join(d, jpgs[0])).convert('RGB')).astype(float)
        top, bottom = im[: im.shape[0] // 5], im[-im.shape[0] // 5:]
        print(f'first frame {im.shape[1]}x{im.shape[0]}: top rows brightness {top.mean():.0f} (blue {top[..., 2].mean():.0f}), '
              f'bottom rows {bottom.mean():.0f} (blue {bottom[..., 2].mean():.0f})')
    if '--preview' in sys.argv and jpgs:
        try:
            import cv2
        except ImportError:
            print('preview: OpenCV missing')
            return 0
        out = os.path.join(d, 'preview.mp4')
        vw = cv2.VideoWriter(out, cv2.VideoWriter_fourcc(*'mp4v'), fps, (1280, 720))
        for f in jpgs:
            img = cv2.imread(os.path.join(d, f))
            vw.write(cv2.resize(img, (1280, 720), interpolation=cv2.INTER_AREA))
        vw.release()
        print(f'preview: {out}')
    if '--mux' in sys.argv and jpgs:
        exe = ffmpeg()
        if not exe:
            print('mux: no ffmpeg')
            return 1
        out = os.path.join(d, 'take.mp4')
        cmd = [exe, '-y', '-loglevel', 'error', '-framerate', str(fps), '-i', os.path.join(d, 'f%05d.jpg')]
        if os.path.exists(wav):
            cmd += ['-i', wav, '-c:a', 'aac', '-b:a', '192k']
        cmd += ['-c:v', 'libx264', '-crf', '18', '-preset', 'medium', '-pix_fmt', 'yuv420p', '-shortest', out]
        r = subprocess.run(cmd, capture_output=True, text=True)
        print(f'mux: {out}' if r.returncode == 0 else f'mux FAILED: {r.stderr[-500:]}')
    return 0


if __name__ == '__main__':
    sys.exit(main())
