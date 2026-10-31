"""The once-a-minute frame rate lines of a session log as a table: per minute the preset, the share of frames on time, the frame
and GPU medians, the eyes' and the UI camera's render on the main thread, the mod's own work, and the costliest player loop
steps (FramePhases). Read a headset session's log with it to see where its frames go (Tasks 348). The 'bound' column says what
held a minute under 85% on time back: GPU when its median is 70% of the refresh interval or more, else CPU (the mod's frame rate
hold uses the same line: GraphicsPresets.RenderScaleHelps).

Run: python -I tools/frame_report.py [LOG]   (default: the newer of the game's BepInEx/LogOutput.player.log and LogOutput.log)"""
import os
import re
import sys

GAME = r'C:\Program Files (x86)\Steam\steamapps\common\Ravenfield\BepInEx'


def number(pattern, text, default=''):
    m = re.search(pattern, text)
    return m.group(1) if m else default


def bound(text):
    """'CPU' or 'GPU' for a minute under 85% on time, by the GPU's median against the refresh interval; '' when on time or untimed."""
    try:
        on_time = int(number(r'^\s*(\d+)% of', text, '100'))
        interval = float(number(r'Hz \(([\d.]+) ms\)', text, '0'))
        gpu = float(number(r'GPU median ([\d.]+) ms', text, '-1'))
    except ValueError:
        return ''
    if on_time >= 85 or interval <= 0 or gpu < 0:
        return ''
    return 'GPU' if gpu >= 0.7 * interval else 'CPU'


def gc_slice(text):
    """The collector's slice when it is not the game's 3 ms (GarbageCollectionSliceMs), as ['gc 1 ms'], else []."""
    m = re.search(r'incremental, ([\d.,]+) ms a frame', text)
    if not m:
        return []
    ms = m.group(1).replace(',', '.')
    return [] if ms in ('3', '3.0') else [f'gc {ms} ms']


def rows(path):
    for line in open(path, encoding='utf-8', errors='replace'):
        if 'Frame rate over the last minute of play:' not in line:
            continue
        text = line.split('of play:', 1)[1]
        yield {
            'bound': bound(text),
            'on time': number(r'^\s*(\d+)% of', text) + '%',
            'median': number(r'median ([\d.,]+) ms', text),
            'GPU': number(r'GPU median ([\d.,]+) ms', text),
            'preset': number(r'; (\w+), \d+x\d+ per eye', text),
            'eyes': number(r'VR Head ([\d.,]+) ms', text),
            'UI cam': number(r'VR UI Camera ([\d.,]+) ms', text),
            'mod': number(r"the mod's own work ([\d.,]+) ms", text),
            'cpu': (number(r'CPU speed (\d+)% of', text) + '%') if 'CPU speed' in text else '',
            # (a phase's own time is named 'Update (own)': the name is everything up to the number)
            'steps': ', '.join(s.strip() for s in re.findall(r'([^,]+? \d+(?:\.\d+)?),', number(r'costliest steps:([^;]*)', text))[:3]),
            'mod sections': ', '.join(s.strip() for s in re.findall(r'([\w.: ]+? [\d.]+),', number(r"the mod's costliest sections:([^;]*)", text))[:3]),
            'awake': number(r'\((\d+) awake\)', text),
            'faded': number(r'(\d+) faded out', text),
            'depth': 'yes' if 'the eyes draw a' in text else '',
            # The settings to try (GameCamerasOnePixel, ScopeHalfRate), said in the line when on: their minutes against the others.
            'try': ', '.join([short for short, said in (('cams 1px', 'game cameras into 1 px'), ('scope 1/2', 'scope lens every other frame'),
                                                        ('no small shadows', 'small objects without shadows'),
                                                        ('tiny far hidden', 'tiny distant objects hidden')) if said in text] + gc_slice(text)),
        }


def newest_session():
    """The newer of the kept session (vrshots keeps the user's play as LogOutput.player.log before a test run) and the current
    log, which a session played since has rewritten; a vrshots test log is never the user's session."""
    found = []
    for p in (os.path.join(GAME, 'LogOutput.player.log'), os.path.join(GAME, 'LogOutput.log')):
        if not os.path.exists(p):
            continue
        if p.endswith('LogOutput.log'):
            try:
                if 'Test run: -rfvr-' in open(p, encoding='utf-8', errors='ignore').read():
                    continue
            except OSError:
                continue
        found.append(p)
    return max(found, key=os.path.getmtime, default=None)


def main():
    path = sys.argv[1] if len(sys.argv) > 1 else newest_session()
    if not path or not os.path.exists(path):
        print('no log found (give its path)')
        return 1
    table = list(rows(path))
    if not table:
        print(f'{path}: no frame rate lines (under a minute of play?)')
        return 1
    print(path)
    cols = ['on time', 'bound', 'median', 'GPU', 'preset', 'cpu', 'try', 'eyes', 'UI cam', 'mod', 'awake', 'faded', 'depth', 'steps', 'mod sections']
    widths = {c: max(len(c), *(len(r[c]) for r in table)) for c in cols}
    print('min  ' + '  '.join(c.ljust(widths[c]) for c in cols))
    for i, r in enumerate(table, 1):
        print(f'{i:<3}  ' + '  '.join(r[c].ljust(widths[c]) for c in cols))
    return 0


if __name__ == '__main__':
    sys.exit(main())
