"""The once-a-minute frame rate lines of a session log as a table: per minute the preset, the share of frames on time, the frame
and GPU medians, the eyes' and the UI camera's render on the main thread, the mod's own work, and the costliest player loop
steps (FramePhases). Read a headset session's log with it to see where its frames go (Tasks 348).

Run: python -I tools/frame_report.py [LOG]   (default: the game's BepInEx/LogOutput.player.log, else LogOutput.log)"""
import os
import re
import sys

GAME = r'C:\Program Files (x86)\Steam\steamapps\common\Ravenfield\BepInEx'


def number(pattern, text, default=''):
    m = re.search(pattern, text)
    return m.group(1) if m else default


def rows(path):
    for line in open(path, encoding='utf-8', errors='replace'):
        if 'Frame rate over the last minute of play:' not in line:
            continue
        text = line.split('of play:', 1)[1]
        yield {
            'on time': number(r'^\s*(\d+)% of', text) + '%',
            'median': number(r'median ([\d.,]+) ms', text),
            'GPU': number(r'GPU median ([\d.,]+) ms', text),
            'preset': number(r'; (\w+), \d+x\d+ per eye', text),
            'eyes': number(r'VR Head ([\d.,]+) ms', text),
            'UI cam': number(r'VR UI Camera ([\d.,]+) ms', text),
            'mod': number(r"the mod's own work ([\d.,]+) ms", text),
            'steps': ', '.join(re.findall(r'(\w+ [\d.]+),', number(r'costliest steps:([^;]*)', text))[:3]),
            'mod sections': ', '.join(re.findall(r'([\w.: ]+? [\d.]+),', number(r"the mod's costliest sections:([^;]*)", text))[:3]),
        }


def main():
    path = sys.argv[1] if len(sys.argv) > 1 else next(
        (p for p in (os.path.join(GAME, 'LogOutput.player.log'), os.path.join(GAME, 'LogOutput.log')) if os.path.exists(p)), None)
    if not path or not os.path.exists(path):
        print('no log found (give its path)')
        return 1
    table = list(rows(path))
    if not table:
        print(f'{path}: no frame rate lines (under a minute of play?)')
        return 1
    print(path)
    cols = ['on time', 'median', 'GPU', 'preset', 'eyes', 'UI cam', 'mod', 'steps', 'mod sections']
    widths = {c: max(len(c), *(len(r[c]) for r in table)) for c in cols}
    print('min  ' + '  '.join(c.ljust(widths[c]) for c in cols))
    for i, r in enumerate(table, 1):
        print(f'{i:<3}  ' + '  '.join(r[c].ljust(widths[c]) for c in cols))
    return 0


if __name__ == '__main__':
    sys.exit(main())
