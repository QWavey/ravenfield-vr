"""Checks of the fast test mode's mod picking (vrshots.fast_mods) on a made-up weapon-to-mod list in a temporary game folder:
a gun's own mod, a gun of the game's own (no mod), a mod named by its title, a filter that matches nothing (every mod loads).
Run: python -I tools/tests/vrshots_fast_test.py   (exit code 0 when all pass)"""
import os
import shutil
import sys
import tempfile

sys.path.insert(0, os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__)))))
import vrshots  # noqa: E402

failed = passed = 0


def check(ok, what):
    global failed, passed
    if ok:
        passed += 1
    else:
        failed += 1
        print('FAIL', what)


def main():
    vrshots.log = lambda msg: None  # quiet
    game = tempfile.mkdtemp(prefix='vrshots-fast-')
    try:
        mods, kept = vrshots.fast_mods(game, ['=M4A1'])
        check(mods is None and kept == ['=M4A1'], f'no list yet: every mod loads ({mods}, {kept})')

        os.makedirs(os.path.join(game, 'BepInEx', 'cache'))
        with open(os.path.join(game, 'BepInEx', 'cache', 'RavenfieldVR-weapon-mods.tsv'), 'w', encoding='utf-8') as f:
            f.write('# weapon\tmod\ttitle\n'
                    'M4A1\t2900000001\tModern Rifles Pack\n'
                    'M4A1 EOT\t2900000001\tModern Rifles Pack\n'
                    'R870\t2900000002\tShotguns Expansion\n'
                    'Rk-44\tgame\tRavenfield\n')
        mods, kept = vrshots.fast_mods(game, ['=M4A1'])
        check(mods == ['2900000001'] and kept == ['=M4A1'], f'exact name: its own mod ({mods})')
        mods, kept = vrshots.fast_mods(game, ['=Rk-44'])
        check(mods == ['none'], f"the game's own gun: no mod at all ({mods})")
        mods, kept = vrshots.fast_mods(game, ['mod:shotguns', 'm4a1'])
        check(mods == ['2900000002', '2900000001'], f'a mod by title and a name part: both mods, once each ({mods})')
        mods, kept = vrshots.fast_mods(game, ['=Nothing Like It'])
        check(mods is None, f'no match: every mod loads, as before ({mods})')
    finally:
        shutil.rmtree(game, ignore_errors=True)
    print(f'{passed} passed, {failed} failed')
    return 1 if failed else 0


if __name__ == '__main__':
    sys.exit(main())
