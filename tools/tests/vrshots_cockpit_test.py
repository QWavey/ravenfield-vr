"""Checks of vrshots.build_cockpit_sheets on made-up pictures: a closed seat with inside and periscope pictures gets a sheet
and a row in the overview; an open seat (only ahead and down) gets none; the views are read whole (periscope-zoom is not
periscope, inside-combat-light is not inside-left).
Run: python -I tools/tests/vrshots_cockpit_test.py   (exit code 0 when all pass)"""
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
    shots = tempfile.mkdtemp(prefix='vrshots-cockpit-')
    try:
        names = ['vehicle-01-car-Tank-seat1-ahead.png', 'vehicle-01-car-Tank-seat1-down.png', 'vehicle-01-car-Tank-seat1-inside-left.png',
                 'vehicle-01-car-Tank-seat1-periscope.png', 'vehicle-01-car-Tank-seat1-periscope-zoom.png',
                 'vehicle-01-car-Tank-seat1-inside-combat-light.png', 'vehicle-01-car-Tank-seat1-inside-alarm2.png',
                 'vehicle-02-car-Jeep-seat1-ahead.png', 'vehicle-02-car-Jeep-seat1-down.png', 'load-00-loading.png']
        for n in names:
            Image.new('RGB', (64, 40), (100, 120, 90)).save(os.path.join(shots, n))
        out = os.path.join(shots, 'cockpits')
        index = vrshots.build_cockpit_sheets(shots, out)
        check(len(index) == 1, f'one cockpit sheet (the closed seat only), got {len(index)}')
        e = index[0] if index else {'pictures': [], 'seat': ''}
        check(e['seat'] == 'vehicle-01-car-Tank-seat1', 'the sheet is the tank seat\'s: ' + e['seat'])
        check('periscope' in e['pictures'] and 'periscope-zoom' in e['pictures'], 'both periscope views read apart: ' + str(e['pictures']))
        check('inside-combat-light' in e['pictures'] and 'inside-left' in e['pictures'], 'the combat light read apart from inside-left')
        check('inside-alarm' not in e['pictures'], 'the second alarm picture (alarm2) is not taken for the first')
        check(os.path.exists(os.path.join(out, 'cockpits.jpg')), 'the overview made')
        check(os.path.exists(os.path.join(out, 'index.json')) and len(json.load(open(os.path.join(out, 'index.json')))) == 1, 'the index written')
        empty = tempfile.mkdtemp(prefix='vrshots-cockpit-none-')
        try:
            check(vrshots.build_cockpit_sheets(empty, os.path.join(empty, 'cockpits')) == [], 'no pictures: no sheets')
            check(not os.path.exists(os.path.join(empty, 'cockpits')), 'no pictures: no folder made')
        finally:
            shutil.rmtree(empty, ignore_errors=True)
    finally:
        shutil.rmtree(shots, ignore_errors=True)
    print(f'{passed} passed, {failed} failed')
    sys.exit(1 if failed else 0)


if __name__ == '__main__':
    main()
