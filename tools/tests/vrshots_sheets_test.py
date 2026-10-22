"""Checks of vrshots.build_sheets on made-up pictures in a temporary folder: which tiles a gun's sheet gets.
A pump gun's shells going into the port, a break action's chambers and open breech, a magazine gun's magazines; a gun
with no rack, magazine or shell picture gets no sheet.
Run: python -I tools/tests/vrshots_sheets_test.py   (exit code 0 when all pass)"""
import json
import os
import shutil
import sys
import tempfile

sys.path.insert(0, os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__)))))
import vrshots  # noqa: E402
from PIL import Image  # noqa: E402

failed = passed = 0


def check(ok, what):
    global failed, passed
    if ok:
        passed += 1
    else:
        failed += 1
        print('FAIL', what)


def main():
    shots = tempfile.mkdtemp(prefix='vrshots-sheets-')
    try:
        names = [
            # A pump gun: its rack and a shell into the port from both sides (as AutoTest.PointCloseUps names them).
            'weapon-041-R870_BUCKSHOT_.png', 'rack-grip-041-R870_BUCKSHOT_.png', 'rack-grip-041-R870_BUCKSHOT_-left.png',
            'shell-into-port-041-R870_BUCKSHOT_-left.png', 'shell-into-port-041-R870_BUCKSHOT_-right.png',
            # A break action: no rack, a shell into a chamber and the open breech loaded.
            'weapon-044-DOUBLE-BARREL.png', 'shell-into-chamber-044-DOUBLE-BARREL-left.png',
            'shell-into-chamber-044-DOUBLE-BARREL-right.png', 'break-open-loaded-044-DOUBLE-BARREL-left.png',
            'break-open-loaded-044-DOUBLE-BARREL-right.png',
            # A magazine gun.
            'weapon-112-HK416.png', 'rack-grip-112-HK416.png', 'mag-1-seated-112-HK416.png', 'mag-6-half-in-112-HK416.png',
            # Only a picture in the hand: no sheet.
            'weapon-120-KNIFE.png',
        ]
        for n in names:
            Image.new('RGB', (8, 8), (90, 120, 150)).save(os.path.join(shots, n))
        out = os.path.join(shots, 'sheets')
        index = {e['id']: e for e in vrshots.build_sheets(shots, out)}
        check(sorted(index) == [41, 44, 112], f'sheets for the pump, the break action and the magazine gun only: {sorted(index)}')
        pump = index.get(41, {}).get('pictures', [])
        check('shell-into-port@left' in pump and 'shell-into-port@right' in pump and not any(p.startswith(('mag', 'shell-into-chamber', 'break-open')) for p in pump),
              f'the pump gun: its port close-ups, no magazine or chamber tiles: {pump}')
        brk = index.get(44, {}).get('pictures', [])
        check({'shell-into-chamber@left', 'shell-into-chamber@right', 'break-open-loaded@left', 'break-open-loaded@right'} <= set(brk)
              and not any(p.startswith(('mag', 'shell-into-port')) for p in brk), f'the break action: its chambers and open breech: {brk}')
        mag = index.get(112, {}).get('pictures', [])
        check('mag-1-seated' in mag and 'mag-6-half-in' in mag and not any(p.startswith(('shell', 'break-open')) for p in mag),
              f'the magazine gun: its magazine tiles: {mag}')
        written = json.load(open(os.path.join(out, 'index.json'), encoding='utf-8'))
        check(len(written) == 3 and all(os.path.exists(os.path.join(vrshots.REPO, e['sheet'])) for e in written), 'index.json lists the three sheets written')
    finally:
        shutil.rmtree(shots, ignore_errors=True)
    print(f'{passed} passed, {failed} failed')
    return 1 if failed else 0


if __name__ == '__main__':
    sys.exit(main())
