"""trailer: the showcase's edit (docs/Showcase-Plan.md): shots cut from showcase takes by an edit list, hard cuts, the game's own
sound cut with its picture, cards in Bahnschrift, one graded H.264 file. No music; the only sound is the game's.

  python tools/trailer.py tools/trailer/pilot-tight.json showcase/pilot-clip-03.mp4
  python tools/trailer.py --marks reports/shots/showcase/showcase-pilot      the take's marks as frame ranges (to write an edit)
  python tools/trailer.py --check tools/trailer/trailer-v1.json               which of an edit's takes and marks exist yet

An edit list (JSON):
  {
    "fps": 60,                                   the takes' rate (all takes of one edit share it)
    "takes": {"pilot": "reports/shots/showcase/showcase-pilot",     a take's folder, or a pattern ("showcase/takes/pilot-*"):
              "reload": "showcase/takes/reload-*"},                the newest folder it matches
    "shots": [
      {"take": "pilot", "in": 75, "out": 150},   frames [in, out) of the take, its sound with it
      {"take": "pilot", "mark": "burst", "in": 0, "out": 60},   frames counted from the first frame of a mark's run
      {"take": "pilot", "mark": "walk", "nth": 0, "in": 20},    no "out": to the end of that run of the mark; "nth": its Nth run
      {"take": "pilot", "shot": 2, "pre": 8, "post": 30},       around the take's 3rd shot (its "shot" marks, from 0): the cut on it
      {"card": "RAVENFIELD VR", "sub": "full mod support", "seconds": 1.6, "tail": 1.2}
                                                 black with the title (and a small line under it); "tail": the last shot's sound rings on
    ],
    "grade": true,                               a little contrast, a touch less saturation, a soft vignette
    "beat": {"start": 6.1, "grid0": 11.36, "period": 0.46875, "div": 2}
                                                 every shot's end moved onto the music's beat grid; a card with "at" (a
                                                 second of the song) starts exactly there (the shot before it made to fit)
    "music": {"path": "...", "start": 5.9, "gain": 0.5, "game": 0.85, "fade_in": 0.3, "fade_out": 2.0}
                                                 a track under the game's sound from "start" s at the trailer's start (optional)
    "letterbox": 2.39                            black bars to this picture shape (optional)
  }

Cuts are hard (the user: "the transitioning between views is too slow"); each cut's sound is faded over 4 ms so it does not click.
The sound of each take is lined up with its picture first (tools/showcase_take.py's sync, by the shots) and brought to -1 dB.
"""
import argparse
import glob
import json
import os
import shutil
import subprocess
import sys
import tempfile
import wave

import numpy as np
from PIL import Image

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
REPO = os.path.abspath(os.path.join(os.path.dirname(os.path.abspath(__file__)), '..'))
import showcase_clip  # noqa: E402
import showcase_take as take  # noqa: E402

FADE = 0.004


def runs(marks):
    """[(mark, camera, first, last)] per run of one mark and camera."""
    out = []
    for f, _, mark, cam in marks:
        if out and out[-1][0] == mark and out[-1][1] == cam and out[-1][3] == f - 1:
            out[-1][3] = f
        else:
            out.append([mark, cam, f, f])
    return out


class Take:
    """A take's frames, its sound lined up with them (seconds of picture -> samples) and its marks."""

    def __init__(self, path):
        self.path = path
        info = take.read_kv(os.path.join(path, 'take.txt'))
        self.fps = int(info.get('fps', '60'))
        self.frames = int(info.get('frames', '0'))
        self.marks = take.read_marks(os.path.join(path, 'shots.txt'))
        wav = os.path.join(path, 'audio.wav')
        self.audio, self.rate = (None, 48000)
        self.shift = 0.0
        self.points, self.latency = [], take.DEFAULT_LATENCY
        if os.path.exists(wav):
            audio, rate = take.read_wav(wav)
            if len(audio):
                snd = take.read_marks(os.path.join(path, 'sound.txt'))
                shift, _, _, _ = take.sync(self.marks, snd, take.onsets(audio, rate))
                self.shift = shift or 0.0
                self.points, self.latency = take.anchors(path, audio, rate)
                peak = float(np.abs(audio).max())
                self.audio = audio * (min(1.0, 0.89 / peak) if peak > 0 else 1.0)
                self.rate = rate

    def mark_run(self, mark, nth=0):
        """(first, last + 1) of the Nth run of a mark (a run: neighbouring frames with the mark and one camera)."""
        found = [(first, last + 1) for m, _, first, last in runs(self.marks) if m == mark]
        if nth >= len(found):
            raise SystemExit(f'{self.path}: no run {nth} of mark "{mark}" ({len(found)} found; marks: '
                             f'{", ".join(sorted({r[0] for r in runs(self.marks)}))})')
        return found[nth]

    def mark_start(self, mark):
        return self.mark_run(mark)[0]

    def sound(self, first, last, rate, channels):
        """The sound under frames [first, last) at the given rate and channels (silence where the take has none)."""
        n = int(round((last - first) / self.fps * rate))
        out = np.zeros((n, channels), dtype=np.float32)
        if self.audio is None:
            return out
        a = self.audio
        if self.rate != rate:
            raise SystemExit(f'{self.path}: sound at {self.rate} Hz, the edit at {rate} Hz')
        # The cut's own anchor (its shots' onsets, its reload sounds paired between the passes), not one shift for the whole take.
        start = int(round(take.wav_time(self.points, first / self.fps, self.latency) * rate)) if self.points else int(round((first / self.fps + self.shift) * rate))
        lo, hi = max(0, start), min(len(a), start + n)
        if hi > lo:
            src = a[lo:hi]
            if src.shape[1] != channels:
                src = np.repeat(src.mean(axis=1, keepdims=True), channels, axis=1)
            out[lo - start:hi - start] = src
        return out


def faded(clip, rate):
    k = min(len(clip) // 2, int(FADE * rate))
    if k > 0:
        ramp = np.linspace(0.0, 1.0, k, dtype=np.float32)[:, None]
        clip[:k] *= ramp
        clip[-k:] *= ramp[::-1]
    return clip


def with_music(audio, rate, m):
    """The edit's music under the game's sound: the track from m["start"] s at the trailer's start, m["gain"] (0.5), faded in over
    m["fade_in"] (0.3 s) and out over m["fade_out"] s (2.0) at the end, the game's own sound kept on top; the sum held under -1 dB."""
    exe = take.ffmpeg()
    r = subprocess.run([exe, '-loglevel', 'error', '-ss', str(m['start']), '-i', m['path'], '-f', 'f32le', '-ac', str(audio.shape[1]),
                        '-ar', str(rate), '-'], capture_output=True)
    if r.returncode != 0:
        raise SystemExit('music: ffmpeg failed: ' + r.stderr.decode(errors='replace')[-400:])
    music = np.frombuffer(r.stdout, dtype=np.float32).reshape(-1, audio.shape[1]).copy()
    n = len(audio)
    if len(music) < n:
        music = np.concatenate([music, np.zeros((n - len(music), audio.shape[1]), dtype=np.float32)])
    music = music[:n] * float(m.get('gain', 0.5))
    fi, fo = int(rate * float(m.get('fade_in', 0.3))), int(rate * float(m.get('fade_out', 2.0)))
    if fi > 0:
        music[:fi] *= np.linspace(0.0, 1.0, fi)[:, None]
    if fo > 0:
        music[-fo:] *= np.linspace(1.0, 0.0, fo)[:, None]
    mixed = audio * float(m.get('game', 0.85)) + music
    peak = float(np.abs(mixed).max())
    return (mixed * (min(1.0, 0.89 / peak) if peak > 0 else 1.0)).astype(np.float32)


def write_wav(path, audio, rate):
    pcm = (np.clip(audio, -1.0, 1.0) * 32767.0).astype('<i2')
    with wave.open(path, 'wb') as w:
        w.setnchannels(audio.shape[1])
        w.setsampwidth(2)
        w.setframerate(rate)
        w.writeframes(pcm.tobytes())


def link(src, dst):
    try:
        os.link(src, dst)
    except OSError:
        shutil.copyfile(src, dst)


def resolve(edit):
    """The edit's takes as folders: a pattern is the newest folder it matches (None: none yet)."""
    base = os.path.dirname(os.path.abspath(edit['_path']))
    root = os.path.abspath(os.path.join(base, edit.get('root', '../..')))
    out = {}
    for name, p in edit['takes'].items():
        p = p if os.path.isabs(p) else os.path.join(root, p)
        hits = [h for h in glob.glob(p) if os.path.isfile(os.path.join(h, 'take.txt'))]
        out[name] = max(hits, key=os.path.getmtime) if hits else None
    return out


def check(edit):
    """Which takes exist, and for each shot whether its take and mark are there."""
    paths = resolve(edit)
    takes = {}
    for name, p in paths.items():
        print(f'  take {name}: {os.path.relpath(p, REPO) if p else "MISSING (" + edit["takes"][name] + ")"}')
        if p:
            takes[name] = Take(p)
    ok = True
    for i, shot in enumerate(edit['shots']):
        if 'card' in shot or 'video' in shot:
            continue
        t = takes.get(shot['take'])
        if t is None:
            ok = False
            continue
        try:
            if 'mark' in shot:
                t.mark_run(shot['mark'], int(shot.get('nth', 0)))
            elif 'shot' in shot and int(shot['shot']) >= sum(1 for _, _, m, _ in t.marks if m == 'shot'):
                raise SystemExit(f'only {sum(1 for _, _, m, _ in t.marks if m == "shot")} shot(s)')
        except SystemExit as e:
            ok = False
            print(f'  shot {i} ({shot["take"]}): {e}')
    print('  ready to build' if ok else '  NOT READY')
    return ok


def snapped_length(edit, i, n, length, most, fps):
    """A shot's length in frames moved so it ends on the music (the user, 2026-11-09: "it also needs to match the song ... more"): with
    the edit's "beat" block ({"start": the song's second at the trailer's start, "grid0": a beat's second in the song, "period": seconds
    a beat, "div": cuts per beat}) the shot ends on the nearest grid point; when the next shot is a card with "at" (a second of the song),
    on that second exactly. Never under 6 frames, never past "most" frames (what the take or video has)."""
    beat = edit.get('beat')
    if not beat:
        return length
    start = float(beat['start'])
    nxt = edit['shots'][i + 1] if i + 1 < len(edit['shots']) else None
    if nxt is not None and 'card' in nxt and nxt.get('at') is not None:
        want = int(round((float(nxt['at']) - start) * fps)) - n
        return max(6, min(most, want))
    step = float(beat['period']) / int(beat.get('div', 2))
    grid0 = float(beat['grid0'])
    t_end = start + (n + length) / fps
    k = round((t_end - grid0) / step)
    for kk in (k, k - 1, k + 1, k - 2):
        want = int(round((grid0 + kk * step - start) * fps)) - n
        if 6 <= want <= most:
            return want
    return length


def build(edit, out, keep=False):
    fps = int(edit.get('fps', 60))
    paths = resolve(edit)
    missing = [n for n, p in paths.items() if p is None]
    if missing:
        raise SystemExit(f'takes not filmed yet: {", ".join(missing)} (python tools/trailer.py --check EDIT)')
    takes = {name: Take(p) for name, p in paths.items()}
    rate = next((t.rate for t in takes.values() if t.audio is not None), 48000)
    channels = next((t.audio.shape[1] for t in takes.values() if t.audio is not None), 2)
    tmp = tempfile.mkdtemp(prefix='trailer-')
    frames_dir = os.path.join(tmp, 'frames')
    os.makedirs(frames_dir)
    n = 0
    sound = []
    last = None  # (take, frame after the last shot) for a card's ring-out
    lines = []
    exe = take.ffmpeg()
    for i, shot in enumerate(edit['shots']):
        if 'video' in shot:
            # A piece of a recorded video (the user's own play, 2026-11-09): seconds "in" to "out", cropped to 16:9 ("crop", ffmpeg's
            # w:h:x:y) and scaled to 1920x1080 at the edit's rate, its own sound with it ("gain").
            a, b = float(shot['in']), float(shot['out'])
            b = a + snapped_length(edit, i, n, int(round((b - a) * fps)), int(round((b - a + float(shot.get('spare', 1.0))) * fps)), fps) / fps
            vdir = os.path.join(tmp, f'video{i}')
            os.makedirs(vdir)
            vf = (f"crop={shot['crop']}," if shot.get('crop') else '') + f'scale=1920:1080,fps={fps}'
            r = subprocess.run([exe, '-loglevel', 'error', '-ss', str(a), '-t', str(b - a), '-i', shot['video'], '-vf', vf, '-q:v', '2',
                                os.path.join(vdir, 'f%05d.jpg')], capture_output=True, text=True)
            files = sorted(os.listdir(vdir))[:int(round((b - a) * fps))]
            if r.returncode != 0 or not files:
                raise SystemExit(f'shot {i}: the video gave no frames ({r.stderr[-300:]})')
            for f in files:
                link(os.path.join(vdir, f), os.path.join(frames_dir, f'f{n:05d}.jpg'))
                n += 1
            raw = subprocess.run([exe, '-loglevel', 'error', '-ss', str(a), '-t', str(len(files) / fps), '-i', shot['video'], '-vn', '-f', 'f32le',
                                  '-ac', str(channels), '-ar', str(rate), '-'], capture_output=True).stdout
            clip = np.frombuffer(raw, dtype=np.float32).reshape(-1, channels).copy() * float(shot.get('gain', 1.0))
            need = int(round(len(files) / fps * rate))
            clip = np.concatenate([clip, np.zeros((max(0, need - len(clip)), channels), dtype=np.float32)])[:need]
            sound.append(faded(clip, rate))
            lines.append(f'  video {os.path.basename(shot["video"])} {a:.2f}-{b:.2f} ({len(files) / fps:.2f} s)')
            last = None
            continue
        if 'card' in shot:
            secs = float(shot.get('seconds', 1.6))
            png = os.path.join(tmp, f'card{i}.png')
            showcase_clip.card(png, shot['card'], sub=shot.get('sub'))
            jpg = os.path.join(tmp, f'card{i}.jpg')
            Image.open(png).convert('RGB').save(jpg, quality=95)
            count = int(round(secs * fps))
            for _ in range(count):
                link(jpg, os.path.join(frames_dir, f'f{n:05d}.jpg'))
                n += 1
            tail = float(shot.get('tail', 0.0))
            clip = np.zeros((int(round(count / fps * rate)), channels), dtype=np.float32)
            if last is not None and tail > 0:
                t, after = last
                ring = t.sound(after, after + int(round(tail * fps)), rate, channels)
                ring = ring[:len(clip)]
                ring *= np.linspace(1.0, 0.0, len(ring), dtype=np.float32)[:, None]
                clip[:len(ring)] += ring
            sound.append(clip)
            lines.append(f'  card "{shot["card"]}" {secs:.2f} s')
            last = None
            continue
        t = takes[shot['take']]
        if 'shot' in shot:
            fired = [f for f, _, mark, _ in t.marks if mark == 'shot']
            k = int(shot['shot'])
            if not 0 <= k < len(fired):
                raise SystemExit(f'shot {i}: the take has {len(fired)} shot(s), not a shot {k}')
            first, end = fired[k] - int(shot.get('pre', 6)), fired[k] + int(shot.get('post', 24))
            # Inside the shot's own camera: frames of the camera before it made a tenth of a second's flash of another view.
            cams = {f: cam for f, _, _, cam in t.marks}
            cam = cams.get(fired[k])
            while first < fired[k] and cams.get(first) != cam:
                first += 1
            while end > fired[k] + 1 and cams.get(end - 1) != cam:
                end -= 1
        elif 'mark' in shot:
            start, stop = t.mark_run(shot['mark'], int(shot.get('nth', 0)))
            first = start + int(shot.get('in', 0))
            end = start + int(shot['out']) if shot.get('out') is not None else stop
        else:
            first, end = int(shot['in']), int(shot['out'])
        if not 0 <= first < end <= t.frames:
            raise SystemExit(f'shot {i}: frames {first}-{end} outside the take ({t.frames} frames)')
        end = first + snapped_length(edit, i, n, end - first, t.frames - first, fps)
        for f in range(first, end):
            src = os.path.join(t.path, f'f{f:05d}.jpg')
            if not os.path.exists(src):
                raise SystemExit(f'shot {i}: frame {f} missing in {t.path}')
            link(src, os.path.join(frames_dir, f'f{n:05d}.jpg'))
            n += 1
        sound.append(faded(t.sound(first, end, rate, channels), rate))
        cams = sorted({cam for f, _, _, cam in t.marks if first <= f < end})
        lines.append(f'  {shot["take"]} {first}-{end} ({(end - first) / fps:.2f} s, {", ".join(cams)})')
        last = (t, end)
    audio = np.concatenate(sound) if sound else np.zeros((1, channels), dtype=np.float32)
    if edit.get('music'):
        audio = with_music(audio, rate, edit['music'])
    wav = os.path.join(tmp, 'sound.wav')
    write_wav(wav, audio, rate)
    exe = take.ffmpeg()
    if not exe:
        raise SystemExit('no ffmpeg')
    vf = ('eq=contrast=1.10:saturation=0.92:gamma=0.97,vignette=angle=PI/6,' if edit.get('grade', True) else '')
    if edit.get('letterbox'):
        bar = int(round((1080 - 1920 / float(edit['letterbox'])) / 2))
        vf += f'drawbox=x=0:y=0:w=iw:h={bar}:color=black:t=fill,drawbox=x=0:y=ih-{bar}:w=iw:h={bar}:color=black:t=fill,'
    vf += 'scale=in_range=full:out_range=limited,format=yuv420p,setsar=1'
    cmd = [exe, '-y', '-loglevel', 'error', '-framerate', str(fps), '-i', os.path.join(frames_dir, 'f%05d.jpg'), '-i', wav,
           '-vf', vf, '-c:v', 'libx264', '-crf', '16', '-preset', 'slow', '-pix_fmt', 'yuv420p', '-r', str(fps),
           '-c:a', 'aac', '-b:a', '192k', '-shortest', '-movflags', '+faststart', out]
    r = subprocess.run(cmd, capture_output=True, text=True)
    if not keep:
        shutil.rmtree(tmp, ignore_errors=True)
    if r.returncode != 0:
        raise SystemExit('ffmpeg failed: ' + r.stderr[-800:])
    print(f'trailer: {out}, {n / fps:.2f} s, {len(edit["shots"])} shots:')
    print('\n'.join(lines))


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('edit', nargs='?')
    ap.add_argument('out', nargs='?')
    ap.add_argument('--marks', metavar='TAKE')
    ap.add_argument('--check', metavar='EDIT')
    ap.add_argument('--keep', action='store_true', help='keep the temporary frames and sound')
    a = ap.parse_args()
    if a.marks:
        t = Take(a.marks)
        print(f'{a.marks}: {t.frames} frames at {t.fps} fps, sound shifted {1000 * t.shift:+.0f} ms')
        for mark, cam, first, last in runs(t.marks):
            print(f'  {first:5d}-{last + 1:5d}  {(last + 1 - first) / t.fps:5.2f} s  {mark:10s} {cam}')
        return 0
    if a.check:
        edit = json.load(open(a.check, encoding='utf-8'))
        edit['_path'] = a.check
        return 0 if check(edit) else 1
    if not a.edit or not a.out:
        print(__doc__)
        return 1
    edit = json.load(open(a.edit, encoding='utf-8'))
    edit['_path'] = a.edit
    build(edit, a.out, a.keep)
    return 0


if __name__ == '__main__':
    sys.exit(main())
