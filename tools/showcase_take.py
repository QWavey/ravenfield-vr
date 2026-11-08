"""showcase_take: checks a showcase take (docs/Showcase-Plan.md), lines its sound up with its picture and makes the clip.

  python tools/showcase_take.py reports/shots/showcase/showcase-pilot [--preview] [--mux]

A take is two passes of the same scene into one folder:
  picture pass: f00000.jpg ... (one picture a frame at the take's rate), shots.txt (frame, take time, mark, camera), take.txt
  sound pass:   audio.wav (the game's own mix in real time, 32-bit float), sound.txt (frame, take time, mark)

Checks and prints:
  - frames on disk against the frames filmed (a missing one: the writers lost it)
  - the sound's length against the picture's, and its peak
  - the sync: for every "shot" mark, where its sound starts in audio.wav (the first 1/240 s block that jumps over the level before
    it, near the sound pass's own mark), against where the picture pass fired it. The sound is shifted by the median of those, and
    the spread around it says how well the two passes kept together (under a frame is in sync)
  - the first frame's orientation (the sky should be at the top on an outdoor take)
--preview: preview.mp4 next to the take, the picture only, 1280x720 (OpenCV).
--mux: take.mp4 next to the take, picture and the shifted sound, its peak brought to -1 dB (H.264 CRF 18 + AAC 192k, ffmpeg).
"""
import glob
import os
import shutil
import statistics
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


def read_kv(path):
    out = {}
    for line in open(path, encoding='utf-8'):
        k, _, v = line.strip().partition('\t')
        if k:
            out[k] = v
    return out


def read_marks(path):
    """(frame, time, mark, camera or '') per line of shots.txt / sound.txt."""
    marks = []
    if not os.path.exists(path):
        return marks
    for i, line in enumerate(open(path, encoding='utf-8')):
        if i == 0:
            continue
        parts = line.rstrip('\n').split('\t')
        if len(parts) >= 3:
            marks.append((int(parts[0]), float(parts[1]), parts[2], parts[3] if len(parts) > 3 else ''))
    return marks


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
            samples = np.frombuffer(body[:len(body) - len(body) % 4], dtype='<f4')
        pos += 8 + size + (size & 1)
    if fmt is None or samples is None:
        raise SystemExit(f'{path}: no fmt or data chunk')
    channels, rate = fmt[1], fmt[2]
    samples = samples[:len(samples) - len(samples) % channels]
    return samples.reshape(-1, channels), rate


def onsets(audio, rate):
    """Times (s) where the level jumps: a 1/240 s block over 2.5x the median of the quarter second before it (the battle's own
    din: against the 4 blocks before, a pistol at the spawn's loud fight found no shot at all), over 1.6x the block just before,
    and over 0.03; one onset per 60 ms."""
    block = max(1, rate // 240)
    mono = np.abs(audio).mean(axis=1)
    n = len(mono) // block
    level = mono[:n * block].reshape(n, block).max(axis=1)
    out = []
    for i in range(4, n):
        base = float(np.median(level[max(0, i - 60):i]))
        if level[i] > 0.03 and level[i] > 2.5 * max(base, 1e-4) and level[i] > 1.6 * max(level[i - 1], 1e-4):
            t = i * block / rate
            if not out or t - out[-1] > 0.06:
                out.append(t)
    return out


def read_events(path):
    """(time, sound) per line of events.txt / sound-events.txt: each mechanical sound as it started (the recorder's log)."""
    out = []
    if os.path.exists(path):
        for i, line in enumerate(open(path, encoding='utf-8')):
            parts = line.rstrip('\n').split('\t')
            if i and len(parts) == 2:
                out.append((float(parts[0]), parts[1]))
    return out


DEFAULT_LATENCY = 0.025  # (the listener's blocks reach the WAV this long after the sound starts: measured by shots, 8-25 ms)


def anchors(d, audio=None, rate=None):
    """Points that tie the picture to the WAV: (picture second, WAV second). From the shots (the picture's shot frame, its sound's
    onset) and from the mechanical sounds both passes logged (a magazine out or in, a slide), paired by kind and order: the sound
    pass's time plus the output's latency (measured by the shots, else 25 ms). A reload has no shot: its sound was lined up by
    nothing and slid off its picture (the user: "the sounds are not synced correctly with the reloads"). Also the latency."""
    pic = read_marks(os.path.join(d, 'shots.txt'))
    snd = read_marks(os.path.join(d, 'sound.txt'))
    out, lat = [], []
    if audio is not None and len(audio):
        jumps = onsets(audio, rate)
        pic_shots = [t for _, t, m, _ in pic if m == 'shot']
        snd_shots = [t for _, t, m, _ in snd if m == 'shot']
        for tp, ts in zip(pic_shots, snd_shots):
            near = [j for j in jumps if ts - 0.03 <= j <= ts + 0.15]
            if near:
                out.append((tp, near[0]))
                lat.append(near[0] - ts)
    latency = statistics.median(lat) if lat else DEFAULT_LATENCY
    pe, se = read_events(os.path.join(d, 'events.txt')), read_events(os.path.join(d, 'sound-events.txt'))
    for tp, kind in pe:
        # The nearest of its kind in the other pass (by order, a burst's one round more or fewer paired every later one a round off).
        same = [ts for ts, k in se if k == kind]
        if same:
            ts = min(same, key=lambda x: abs(x - tp))
            if abs(ts - tp) < (0.15 if kind == 'Shot' else 0.4):
                out.append((tp, ts + latency))
    out.sort()
    return out, latency


def wav_time(points, t, latency=DEFAULT_LATENCY):
    """The WAV second under picture second t: its offset from the anchors around it (between two, in proportion; else the nearest)."""
    if not points:
        return t + latency
    if t <= points[0][0]:
        return t + points[0][1] - points[0][0]
    for (a, wa), (b, wb) in zip(points, points[1:]):
        if a <= t <= b:
            k = (t - a) / (b - a) if b > a else 0.0
            return t + (wa - a) + k * ((wb - b) - (wa - a))
    return t + points[-1][1] - points[-1][0]


def sync(pic_marks, snd_marks, jumps):
    """The sound's shift (s, audio time minus picture time) by the shots, and each shot's error around it (ms)."""
    pic_shots = [t for _, t, m, _ in pic_marks if m == 'shot']
    snd_shots = [t for _, t, m, _ in snd_marks if m == 'shot']
    pairs = []
    for k, (tp, ts) in enumerate(zip(pic_shots, snd_shots)):
        # The shot's sound: the first jump from a frame before the sound pass's mark to 150 ms after it (the mark is logged at the
        # frame's end; the first audio block after the call carries it, plus the output's own latency).
        near = [j for j in jumps if ts - 0.03 <= j <= ts + 0.15]
        if near:
            pairs.append((k, tp, near[0]))
    if not pairs:
        return None, [], len(pic_shots), len(snd_shots)
    shift = statistics.median(o - tp for _, tp, o in pairs)
    errors = [(k, 1000 * (o - tp - shift)) for k, tp, o in pairs]
    return shift, errors, len(pic_shots), len(snd_shots)


def jitter(d, jpgs, marks):
    """The picture's shake from frame to frame: the whole picture's shift between neighbours (phase correlation at 480x270), and the
    frames where it flips direction every frame by more than 8 px (a ping-pong: take 8's gun cam jumped 66 px a frame by turns while
    walking, the physics stepping 0 and 2 times a frame). A smooth pan or a shot's kick does not flip every frame."""
    try:
        import cv2
    except ImportError:
        return 'jitter: OpenCV missing'
    cams = {f: cam for f, _, _, cam in marks}
    prev, shifts = None, []
    for name in jpgs:
        im = cv2.imread(os.path.join(d, name), cv2.IMREAD_GRAYSCALE)
        im = cv2.resize(im, (480, 270)).astype(np.float32)
        if prev is not None:
            (x, y), _ = cv2.phaseCorrelate(prev, im)
            shifts.append((x * 4, y * 4))
        prev = im
    bad = {}
    for i in range(1, len(shifts) - 1):
        for axis in (0, 1):
            a, b, c = shifts[i - 1][axis], shifts[i][axis], shifts[i + 1][axis]
            if min(abs(a), abs(b), abs(c)) > 8 and a * b < 0 and b * c < 0:
                cam = cams.get(i + 1, '?')
                bad[cam] = bad.get(cam, 0) + 1
                break
    if not bad:
        return f'jitter: none (no frame-to-frame ping-pong over 8 px in {len(shifts)} steps)'
    return 'jitter: PING-PONG FRAMES ' + ', '.join(f'{n} in {cam}' for cam, n in sorted(bad.items(), key=lambda kv: -kv[1]))


def main():
    if len(sys.argv) < 2:
        print(__doc__)
        return 1
    d = sys.argv[1]
    take = read_kv(os.path.join(d, 'take.txt'))
    pic = read_marks(os.path.join(d, 'shots.txt'))
    snd = read_marks(os.path.join(d, 'sound.txt'))
    fps = int(take.get('fps', '60'))
    frames = int(take.get('frames', '0'))
    jpgs = sorted(f for f in os.listdir(d) if f.startswith('f') and f.endswith('.jpg'))
    print(f'{d}: {frames} frames filmed at {fps} fps ({frames / fps:.2f} s), {len(jpgs)} pictures on disk'
          + ('' if len(jpgs) == frames else f'  ** {frames - len(jpgs)} MISSING **'))
    wav = os.path.join(d, 'audio.wav')
    shift = None
    gain = 1.0
    if not os.path.exists(wav):
        print('sound: NO audio.wav (no sound pass)')
    else:
        audio, rate = read_wav(wav)
        if len(audio) == 0:
            print("sound: audio.wav HAS NO SAMPLES (the sound pass recorded nothing)")
        else:
            secs = len(audio) / rate
            peak = float(np.abs(audio).max())
            gain = min(1.0, 0.89 / peak) if peak > 0 else 1.0  # (the game's mix goes over full scale: the peak brought to -1 dB)
            snd_end = snd[-1][1] if snd else 0.0
            print(f'sound: {secs:.3f} s at {rate} Hz, {audio.shape[1]} channel(s), peak {np.abs(audio).max():.3f}; the sound pass ran '
                  f'{snd_end:.2f} s over {len(snd)} frames ({len(snd) / max(snd_end, 1e-3):.0f} fps); the picture is {frames / fps:.2f} s')
            jumps = onsets(audio, rate)
            shift, errors, np_, ns = sync(pic, snd, jumps)
            pts, lat = anchors(d, audio, rate)
            pe = read_events(os.path.join(d, 'events.txt'))
            if pe:
                ev = [(tp, wt) for tp, wt in pts if not any(abs(tp - t) < 1e-6 for _, t, m, _ in pic if m == 'shot')]
                offs = [1000 * (wt - tp) for tp, wt in ev]
                print(f'events: {len(pe)} mechanical sound(s) in the picture pass, {len(ev)} paired with the sound pass; their offsets '
                      + (', '.join(f'{o:+.0f}' for o in offs) if offs else '-') + f' ms (latency {1000 * lat:.0f} ms)')
            if shift is None:
                print(f'sync: NO SHOT FOUND in the sound ({np_} shot marks in the picture, {ns} in the sound pass, {len(jumps)} onsets)')
            else:
                worst = max(abs(e) for _, e in errors)
                print(f'sync: sound shifted {1000 * shift:+.0f} ms by {len(errors)} of {np_} shots; each shot around it: '
                      + ', '.join(f'{e:+.0f}' for _, e in errors) + f' ms (worst {worst:.0f} ms'
                      + (', IN SYNC' if worst <= 1000 / fps else ', OUT BY MORE THAN A FRAME') + ')')
    if jpgs:
        print(jitter(d, jpgs, pic))
    if jpgs:
        im = np.asarray(Image.open(os.path.join(d, jpgs[0])).convert('RGB')).astype(float)
        top, bottom = im[: im.shape[0] // 5], im[-im.shape[0] // 5:]
        print(f'first frame {im.shape[1]}x{im.shape[0]}: top rows brightness {top.mean():.0f} (blue {top[..., 2].mean():.0f}), '
              f'bottom rows {bottom.mean():.0f} (blue {bottom[..., 2].mean():.0f})')
    if '--preview' in sys.argv and jpgs:
        import cv2
        out = os.path.join(d, 'preview.mp4')
        vw = cv2.VideoWriter(out, cv2.VideoWriter_fourcc(*'mp4v'), fps, (1280, 720))
        for f in jpgs:
            vw.write(cv2.resize(cv2.imread(os.path.join(d, f)), (1280, 720), interpolation=cv2.INTER_AREA))
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
            s = shift or 0.0
            # Shifted onto the picture: a later sound cut at its start, an earlier one delayed; then cut or padded to the picture.
            filt = f'atrim=start={s:.4f},asetpts=PTS-STARTPTS' if s >= 0 else f'adelay={int(-s * 1000)}:all=1'
            cmd += ['-i', wav, '-filter:a', filt + f',volume={gain:.4f},apad,atrim=0:{frames / fps:.4f}', '-c:a', 'aac', '-b:a', '192k']
        cmd += ['-c:v', 'libx264', '-crf', '18', '-preset', 'medium', '-pix_fmt', 'yuv420p', '-t', f'{frames / fps:.4f}', out]
        r = subprocess.run(cmd, capture_output=True, text=True)
        print(f'mux: {out}' if r.returncode == 0 else f'mux FAILED: {r.stderr[-500:]}')
    return 0


if __name__ == '__main__':
    sys.exit(main())
