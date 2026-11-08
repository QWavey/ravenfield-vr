"""showcase_all: every kept showcase clip in one video, in order, to watch everything at once (the user, 2026-11-08: "make sure we
have alot of clips but name them good but at the end always produce a full combined clip so i can see everything you did").

  python tools/showcase_all.py [--clips showcase/clips] [--out showcase/showcase-all.mp4] [--titles]

The clips are showcase/clips/NN-scene-map-what.mp4 (named by tools/trailer.py's output path), joined by their names' order; with
--titles a half-second card with each clip's name (Bahnschrift, small) goes before it. Re-encoded to one stream (H.264 CRF 16,
AAC 192k, 1920x1080 at 60 fps), so clips of any settings join cleanly. The clips themselves and the takes they were cut from
(showcase/takes) stay as they are.
"""
import argparse
import glob
import os
import subprocess
import sys
import tempfile

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import showcase_clip  # noqa: E402
import showcase_take as take  # noqa: E402

REPO = os.path.abspath(os.path.join(os.path.dirname(os.path.abspath(__file__)), '..'))


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--clips', default=os.path.join(REPO, 'showcase', 'clips'))
    ap.add_argument('--out', default=os.path.join(REPO, 'showcase', 'showcase-all.mp4'))
    ap.add_argument('--titles', action='store_true', help="a half-second card with each clip's name before it")
    a = ap.parse_args()
    clips = sorted(glob.glob(os.path.join(a.clips, '*.mp4')))
    if not clips:
        print(f'no clips in {a.clips}')
        return 1
    exe = take.ffmpeg()
    tmp = tempfile.mkdtemp(prefix='showcase-all-')
    inputs, parts = [], []
    for i, clip in enumerate(clips):
        if a.titles:
            png = os.path.join(tmp, f't{i}.png')
            name = os.path.splitext(os.path.basename(clip))[0].replace('-', ' ').upper()
            showcase_clip.card(png, name[:40], w=1920, h=1080)
            inputs += ['-loop', '1', '-t', '0.6', '-framerate', '60', '-i', png, '-f', 'lavfi', '-t', '0.6', '-i', 'anullsrc=r=48000:cl=stereo']
            n = len(parts)
            parts.append((2 * n + 0, 2 * n + 1, True))
        inputs += ['-i', clip]
        parts.append((None, None, False))
    # Build the filter: each part scaled to 1920x1080, 60 fps, yuv420p, stereo 48 kHz; then concat.
    graph, labels, idx = [], [], 0
    for kind in parts:
        if kind[2]:  # a title card: video and silent audio inputs
            v, a_ = idx, idx + 1
            idx += 2
            graph.append(f'[{v}:v]scale=1920:1080,fps=60,format=yuv420p,setsar=1[v{len(labels)}]')
            graph.append(f'[{a_}:a]aformat=sample_rates=48000:channel_layouts=stereo[a{len(labels)}]')
        else:
            graph.append(f'[{idx}:v]scale=1920:1080,fps=60,format=yuv420p,setsar=1[v{len(labels)}]')
            graph.append(f'[{idx}:a]aformat=sample_rates=48000:channel_layouts=stereo[a{len(labels)}]')
            idx += 1
        labels.append(len(labels))
    graph.append(''.join(f'[v{k}][a{k}]' for k in labels) + f'concat=n={len(labels)}:v=1:a=1[v][a]')
    cmd = [exe, '-y', '-loglevel', 'error'] + inputs + ['-filter_complex', ';'.join(graph), '-map', '[v]', '-map', '[a]',
                                                       '-c:v', 'libx264', '-crf', '16', '-preset', 'medium', '-pix_fmt', 'yuv420p',
                                                       '-c:a', 'aac', '-b:a', '192k', '-movflags', '+faststart', a.out]
    r = subprocess.run(cmd, capture_output=True, text=True)
    if r.returncode != 0:
        raise SystemExit('ffmpeg failed: ' + r.stderr[-800:])
    print(f'{a.out}: {len(clips)} clip(s):')
    for c in clips:
        print('  ' + os.path.basename(c))
    return 0


if __name__ == '__main__':
    sys.exit(main())
