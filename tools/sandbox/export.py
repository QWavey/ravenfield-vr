"""Exports the weapons for the sandbox: runs the game once in flat mode (no headset, no SteamVR), lets the mod analyse
every weapon from the main menu and write each one's model and analysis, then copies them to reports/sandbox/data.

    python tools/sandbox/export.py                        every weapon (a few minutes)
    python tools/sandbox/export.py --weapons "GLOCK,vector"   only these (any part of the name)
    python tools/sandbox/export.py --no-build             install the DLL already built

Never starts while Ravenfield is running. Steam is started if needed (workshop weapons only load with it).
"""
import argparse
import os
import shutil
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.dirname(HERE))  # tools/
import vrshots  # noqa: E402  (its game, Steam and install helpers)

REPO = vrshots.REPO
DATA = os.path.join(REPO, 'reports', 'sandbox', 'data', 'weapons')


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument('--weapons', default='', help='comma-separated parts of weapon names (default: every weapon)')
    ap.add_argument('--no-build', action='store_true')
    ap.add_argument('--timeout', type=float, default=25, help='minutes')
    ap.add_argument('--game', default=os.path.join(vrshots.STEAM, 'steamapps', 'common', 'Ravenfield'))
    a = ap.parse_args()
    if vrshots.running('ravenfield.exe'):
        raise SystemExit('Ravenfield is running (someone is playing?): not starting.')
    vrshots.build_and_install(a.game, build=not a.no_build)
    vrshots.ensure_steam()
    out = os.path.join(a.game, 'BepInEx', 'RavenfieldVR-export')
    names = ','.join(n.strip() for n in a.weapons.split(',') if n.strip())
    # Single-threaded rendering: meshes the game does not let scripts read are read back from the graphics card, which
    # must not race the render thread.
    args = ['-novr', '-nointro', '-force-gfx-direct', '-rfvr-selftest', '-rfvr-export' + ('=' + names if names else '')]
    if names:
        args.append('-rfvr-only=' + names)
    since = time.time()
    vrshots.log('exporting ' + (names or 'every weapon') + ' (flat, no headset)')
    try:
        vrshots.run_game(a.game, args, a.timeout, vr=False)
    finally:
        # Interrupted (Ctrl+C, an error): the game this run started goes too (it was refused above when one was running),
        # else the next export or game test is refused with "someone is playing?".
        if vrshots.running('ravenfield.exe'):
            vrshots.kill('ravenfield.exe')
    index = os.path.join(out, 'index.json')
    complete = os.path.exists(index) and os.path.getmtime(index) >= since - 5
    os.makedirs(DATA, exist_ok=True)
    copied, failed = [], []
    # Every weapon written in this run is copied, also when the run stopped part way (the index is written only at its end).
    for name in (os.listdir(out) if os.path.isdir(out) else []):
        src = os.path.join(out, name)
        rig, glb = os.path.join(src, 'rig.json'), os.path.join(src, 'model.glb')
        try:
            if not (os.path.isdir(src) and os.path.exists(rig) and os.path.exists(glb) and os.path.getmtime(rig) >= since - 5):
                continue
            dst = os.path.join(DATA, name)
            shutil.rmtree(dst, ignore_errors=True)
            shutil.copytree(src, dst)
            copied.append(name)
        except OSError as e:
            failed.append(f'{name}: {e}')
    log = os.path.join(a.game, 'BepInEx', 'LogOutput.log')
    try:
        shutil.copy2(log, os.path.join(os.path.dirname(DATA), 'export-log.txt'))
    except OSError:
        pass
    vrshots.log(f'{len(copied)} weapon(s) exported to {DATA}' + ('' if complete else ' (the run did not finish: the weapons written so far)'))
    for f in failed:
        vrshots.log('not copied: ' + f)
    # Each name asked for must have produced a weapon (the game's two filters match names differently: a name only one of
    # them knew was analysed but never written, and the run said it worked).
    missing = [n for n in names.split(',') if n and not any(n.lower().replace(' ', '_') in c.lower() or n.lower() in c.lower() for c in copied)]
    if missing:
        vrshots.log('nothing exported for: ' + ', '.join(missing) + ' (a part of the weapon\'s name as the game lists it)')
    if not copied or failed or missing or not complete:
        raise SystemExit(1)


if __name__ == '__main__':
    main()
