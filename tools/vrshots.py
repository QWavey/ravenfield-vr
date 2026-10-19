"""vrshots: pictures of the mod in a virtual headset, for checking things by eye (and by agents).

Runs Ravenfield with the mod under SteamVR's null driver (a headset that is not there), lets the autotest take the
pictures, and puts the user's SteamVR settings back afterwards, always (a fresh backup is made before every run and its
hash is checked after the restore). Never starts while Ravenfield is already running.

Examples
  python tools/vrshots.py --weapons                     every weapon: grab pictures + one sheet per weapon
  python tools/vrshots.py --weapon "GLOCK 43X"          one weapon (any part of its name)
  python tools/vrshots.py --weapon 60 --weapon P320     by id (the number on its sheet) and by name
  python tools/vrshots.py --weapon "mod:Tactical"       every weapon of a mod (its title or folder)
  python tools/vrshots.py --weapon "=AK-47"             exactly this name
  python tools/vrshots.py --ui                          loading, pause menu + options, loadout, squad pointing,
                                                        wrist view, body/holsters, damage, dirt
  python tools/vrshots.py --hands                       glove close-ups and the pose sheet (no headset needed)
  python tools/vrshots.py --bench                       graphics bench (GPU and frame time per setting)
  python tools/vrshots.py --autotest                    the whole autotest (every check, a sample of weapons)
  python tools/vrshots.py --list                        the weapon ids and names from the last weapons run
  python tools/vrshots.py --weapon MK46 --dumprig MK46  also the weapon's whole rig with what each part does
  python tools/vrshots.py --film                        film what each eye gets while guns shoot (a pistol, a rifle, a
                                                        shotgun, a machine gun): their muzzle flash + the lens flash
  python tools/vrshots.py --film "GLOCK 43X" --film 54  the same for these guns (name, id, mod:TEXT, =NAME)

Options
  --no-build          install the DLL already built (default: build.ps1 first, then install it)
  --res 2064x2208     the virtual headset's resolution (default 1440x1584; the Quest 3 over Link is about 2064x2208)
  --timeout MIN       give up after this many minutes (default: 240 for --weapons, 30 otherwise)
  --game DIR          Ravenfield's folder (default: Steam's)

Output
  reports/shots/<mode>/            the pictures of the run (the previous run's in <mode>-previous); --weapon runs go to
                                   reports/shots/weapon/, so the full --weapons run stays where it is
  reports/shots/weapons/sheets/    one labelled sheet per weapon, and index.json (id, name, sheet, pictures)
  reports/shots/<mode>/autotest.txt  the autotest report of the run
  reports/shots/<mode>/game-log.txt  the game's log (BepInEx), weapon-report.txt and rig-dump.txt when made by the run
"""
import argparse
import glob
import hashlib
import json
import os
import re
import shutil
import subprocess
import sys
import time

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
STEAM = r'C:\Program Files (x86)\Steam'
STEAMVR_CFG = os.path.join(STEAM, 'config', 'steamvr.vrsettings')
BACKUPS = os.path.join(os.environ.get('LOCALAPPDATA', REPO), 'RavenfieldVR', 'steamvr-backups')
CHAPERONE = os.path.join(STEAM, 'config', 'chaperone_info.vrchap')
NULL_UNIVERSE = 2  # the null driver's tracking universe (the game logs it: "Tracking universe: 2")
VR_PROCESSES = ['vrmonitor.exe', 'vrserver.exe', 'vrcompositor.exe', 'vrdashboard.exe', 'vrstartup.exe', 'vrwebhelper.exe',
                'steamvr_room_setup.exe']


def log(msg):
    print(time.strftime('%H:%M:%S'), msg, flush=True)


def running(image):
    out = subprocess.run(['tasklist', '/FI', f'IMAGENAME eq {image}'], capture_output=True, text=True, errors='ignore').stdout
    return image.lower() in out.lower()


def kill(image):
    subprocess.run(['taskkill', '/F', '/IM', image], capture_output=True)


def sha(path):
    return hashlib.sha256(open(path, 'rb').read()).hexdigest()


# ---- SteamVR: the null driver for the run, the user's settings back after ------------------------------------------

class NullHeadset:
    def __init__(self, res):
        self.res = res
        self.backup = None
        self.hash = None

    def __enter__(self):
        for p in VR_PROCESSES:
            kill(p)
        time.sleep(2)
        cfg = json.load(open(STEAMVR_CFG, encoding='utf-8'))
        if cfg.get('steamvr', {}).get('forcedDriver') == 'null':
            raise SystemExit('SteamVR is already on the null driver (a run that did not restore?): put your own settings back first.')
        os.makedirs(BACKUPS, exist_ok=True)
        self.hash = sha(STEAMVR_CFG)
        self.backup = os.path.join(BACKUPS, f'steamvr.vrsettings.{time.strftime("%Y%m%d-%H%M%S")}.{self.hash[:8]}')
        shutil.copyfile(STEAMVR_CFG, self.backup)
        w, h = self.res
        cfg.setdefault('steamvr', {})
        cfg['steamvr']['forcedDriver'] = 'null'
        cfg['steamvr']['activateMultipleDrivers'] = False
        cfg['steamvr']['enableHomeApp'] = False
        cfg['driver_null'] = {'displayFrequency': 90, 'enable': True, 'renderHeight': h, 'renderWidth': w,
                              'secondsFromVsyncToPhotons': 0.011, 'windowHeight': h, 'windowWidth': w}
        json.dump(cfg, open(STEAMVR_CFG, 'w', encoding='utf-8'), indent=3)
        log(f'SteamVR on the null driver ({w}x{h}); your settings backed up to {self.backup}')
        self.room_setup_off()
        return self

    def room_setup_off(self):
        """A play area for the null driver's tracking universe (2, as the game logs it: "Tracking universe"): without one
        SteamVR's room setup opens, asks the game to quit, and SteamVR ends it about 23 s later, so every run's first launch
        failed (task 279). The user's chaperone file is backed up and put back after the run (or removed if there was none)."""
        self.chap_existed = os.path.exists(CHAPERONE)
        self.chap_hash = sha(CHAPERONE) if self.chap_existed else None
        self.chap_backup = None
        data = {'jsonid': 'chaperone_info', 'universes': [], 'version': 5}
        if self.chap_existed:
            self.chap_backup = os.path.join(BACKUPS, f'chaperone_info.vrchap.{time.strftime("%Y%m%d-%H%M%S")}.{self.chap_hash[:8]}')
            shutil.copyfile(CHAPERONE, self.chap_backup)
            try:
                data = json.load(open(CHAPERONE, encoding='utf-8'))
            except (OSError, ValueError):
                pass
        if not any(str(u.get('universeID')) == str(NULL_UNIVERSE) for u in data.get('universes', [])):
            half, top = 1.5, 2.43
            corners = [(-half, -half), (half, -half), (half, half), (-half, half)]
            walls = [[[a[0], 0, a[1]], [a[0], top, a[1]], [b[0], top, b[1]], [b[0], 0, b[1]]]
                     for a, b in zip(corners, corners[1:] + corners[:1])]
            data.setdefault('universes', []).append({
                'collision_bounds': walls, 'play_area': [2 * half, 2 * half],
                'seated': {'translation': [0, 0, 0], 'yaw': 0}, 'standing': {'translation': [0, 0, 0], 'yaw': 0},
                'time': time.strftime('%a %b %d %H:%M:%S %Y'), 'universeID': NULL_UNIVERSE})
            json.dump(data, open(CHAPERONE, 'w', encoding='utf-8'), indent=3)
        log('a play area for the null driver (room setup stays shut)'
            + (f'; your chaperone file backed up to {self.chap_backup}' if self.chap_existed else ''))

    def room_setup_back(self):
        """The user's chaperone file as it was (or none, as there was none)."""
        for attempt in range(15):
            try:
                if self.chap_existed:
                    shutil.copyfile(self.chap_backup, CHAPERONE)
                    if sha(CHAPERONE) == self.chap_hash:
                        return True
                else:
                    if os.path.exists(CHAPERONE):
                        os.remove(CHAPERONE)
                    return True
            except OSError:
                pass
            for p in VR_PROCESSES:
                kill(p)
            time.sleep(1)
        return False

    def __exit__(self, *exc):
        # Also on Ctrl+C or an error: the game this run started (none was running before it) goes too, and SteamVR's
        # settings come back even while a just-killed SteamVR still holds the file for a moment.
        kill('ravenfield.exe')
        for p in VR_PROCESSES:
            kill(p)
        time.sleep(3)
        ok, error = False, None
        for attempt in range(15):
            try:
                shutil.copyfile(self.backup, STEAMVR_CFG)
                ok = sha(STEAMVR_CFG) == self.hash
                if ok:
                    break
            except OSError as e:
                error = e
            for p in VR_PROCESSES:
                kill(p)
            time.sleep(1)
        if ok:
            log('SteamVR settings restored (hash matches)')
        else:
            log(f'!!! SteamVR settings NOT restored ({error or "hash differs"}): copy {self.backup} over {STEAMVR_CFG} by hand !!!')
        if hasattr(self, 'chap_existed'):  # (not set when entering failed before the play area was written)
            if self.room_setup_back():
                log('chaperone file put back' + (' (hash matches)' if self.chap_existed else ' (removed: there was none)'))
            else:
                log(f'!!! chaperone file NOT put back: {"copy " + self.chap_backup + " over " if self.chap_existed else "delete "}{CHAPERONE} by hand !!!')
        return False


# ---- the game ------------------------------------------------------------------------------------------------------

def build_and_install(game, build=True):
    """Builds the mod (unless build is False) and installs build/RavenfieldVR.dll into the game: with --no-build the DLL
    already built is still installed (a --no-build run once tested the DLL of an earlier run: check-1017e)."""
    if build:
        log('building the mod')
        r = subprocess.run(['powershell', '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', os.path.join(REPO, 'build.ps1')],
                           capture_output=True, text=True, errors='ignore', cwd=REPO)
        if r.returncode != 0:
            print(r.stdout[-3000:], r.stderr[-2000:])
            raise SystemExit('build failed')
    dest = os.path.join(game, 'BepInEx', 'plugins', 'RavenfieldVR')
    for f in ('RavenfieldVR.dll', 'RavenfieldVR.pdb'):
        shutil.copy2(os.path.join(REPO, 'build', f), dest)
    log('installed into ' + dest)


def ensure_steam():
    if running('steam.exe'):
        return
    log('starting Steam (workshop weapons only load with it)')
    subprocess.Popen([os.path.join(STEAM, 'steam.exe'), '-silent'])
    time.sleep(25)


def report_since(game, start):
    """Whether the autotest wrote its report after start (AutoTest.ReportPath: written once, when the test ends)."""
    report = os.path.join(game, 'BepInEx', 'RavenfieldVR-autotest.txt')
    try:
        return os.path.getmtime(report) >= start
    except OSError:
        return False


def run_game(game, args, timeout_min, vr):
    exe = os.path.join(game, 'ravenfield.exe')
    logfile = os.path.join(game, 'BepInEx', 'LogOutput.log')
    full = args + ['-screen-fullscreen', '0', '-screen-width', '1280', '-screen-height', '720']
    for attempt in range(1, 4):
        start = time.time()
        log(f'starting Ravenfield (attempt {attempt}): {" ".join(args)}')
        subprocess.Popen([exe] + full, cwd=game)
        flat = False
        while True:
            time.sleep(4)
            if vr and running('steamvr_room_setup.exe'):
                kill('steamvr_room_setup.exe')  # it closes the game on a fresh null driver
            if vr and os.path.exists(logfile) and os.path.getmtime(logfile) >= start:
                try:
                    if 'flat mode' in open(logfile, encoding='utf-8', errors='ignore').read():
                        flat = True
                        break
                except OSError:
                    pass
            if not running('ravenfield.exe'):
                if vr and time.time() - start < 40:
                    flat = True  # taken down while SteamVR was still starting
                elif vr and '-rfvr-autotest' in args and time.time() - start < 120 and not report_since(game, start):
                    # Gone in its first two minutes with no report: SteamVR closed it (room setup asks the game to quit,
                    # the autotest ignores that, and SteamVR then ends the process; check-1018i ended so after 42 s).
                    flat = True
                break
            if time.time() - start > timeout_min * 60:
                log('timeout: stopping the game')
                kill('ravenfield.exe')
                break
        if flat:
            log('came up without the headset; trying again')
            try:  # (kept for finding out why: every run's first launch fails, the next one overwrites its log)
                shutil.copyfile(logfile, os.path.join(REPO, 'reports', 'shots', f'failed-launch-{attempt}.log'))
            except OSError:
                pass
            kill('ravenfield.exe')
            time.sleep(8)
            continue
        log(f'the game finished after {time.time() - start:.0f} s')
        return True
    return False


# ---- pictures -----------------------------------------------------------------------------------------------------

TILES = [
    ('weapon', 'weapon in hand'),
    ('rack-grip', 'rack grip'), ('rack-grip@left', 'rack grip, left'), ('rack-grip@right', 'rack grip, right'),
    ('rack-grip-pulled', 'rack pulled'), ('rack-grip-pulled@left', 'rack pulled, left'), ('rack-grip-pulled@right', 'rack pulled, right'),
    ('mag-1-seated', 'mag 1 seated'), ('mag-2-leaving', 'mag 2 dropped, leaving'), ('mag-3-out', 'mag 3 out (gun empty)'),
    ('mag-4-in-hand', 'mag 4 fresh one in hand'), ('mag-5-lined-up', 'mag 5 lined up under well'),
    ('mag-6-half-in', 'mag 6 half way in'), ('mag-7-seated-again', 'mag 7 seated again'),
]


def build_sheets(shots, out):
    from PIL import Image, ImageDraw
    os.makedirs(out, exist_ok=True)
    pat = re.compile(r'^(?P<kind>[a-z0-9-]+?)-(?P<idx>\d{3})-(?P<name>.+?)(?P<side>-left|-right)?\.png$')
    weapons = {}
    for f in os.listdir(shots):
        m = pat.match(f)
        if not m or m.group('name').endswith('-looking-down'):
            continue
        key = m.group('kind') + ('@' + m.group('side')[1:] if m.group('side') else '')
        weapons.setdefault((int(m.group('idx')), m.group('name')), {})[key] = f
    tw, th, cols = 400, 250, 4
    index = []
    for (idx, name), tiles in sorted(weapons.items()):
        have = [k for k, _ in TILES if k in tiles]
        if not any(k.startswith(('rack', 'mag')) for k in have):
            continue
        rows = (len(TILES) + cols - 1) // cols
        sheet = Image.new('RGB', (tw * cols, th * rows + 30), (40, 40, 40))
        d = ImageDraw.Draw(sheet)
        d.text((8, 8), f'{idx:03d} {name}', fill=(255, 255, 255))
        for i, (key, label) in enumerate(TILES):
            x, y = (i % cols) * tw, 30 + (i // cols) * th
            if key in tiles:
                sheet.paste(Image.open(os.path.join(shots, tiles[key])).convert('RGB').resize((tw, th)), (x, y))
            else:
                d.rectangle([x, y, x + tw - 1, y + th - 1], fill=(90, 90, 90))
                d.text((x + tw // 2 - 15, y + th // 2), 'none', fill=(200, 200, 200))
            d.rectangle([x, y, x + tw - 1, y + 16], fill=(0, 0, 0))
            d.text((x + 4, y + 2), f'{i + 1}. {label}', fill=(255, 230, 120))
        path = os.path.join(out, f'{idx:03d}-{re.sub(r"[^A-Za-z0-9_.-]+", "_", name)}.jpg')
        sheet.save(path, quality=82)
        index.append({'id': idx, 'name': name, 'sheet': os.path.relpath(path, REPO), 'pictures': have})
    json.dump(index, open(os.path.join(out, 'index.json'), 'w', encoding='utf-8'), indent=1)
    return index


def collect(game, mode, since):
    """The run's pictures and reports into reports/shots/<mode> (the run before kept as <mode>-previous)."""
    src = os.path.join(game, 'BepInEx', 'RavenfieldVR-shots')
    dest = os.path.join(REPO, 'reports', 'shots', mode)
    if os.path.isdir(dest):
        prev = dest + '-previous'
        shutil.rmtree(prev, ignore_errors=True)
        if os.path.exists(prev):  # a file in it still open (a viewer): keep it under another name
            prev = dest + '-previous-' + time.strftime('%H%M%S')
        os.replace(dest, prev)
    os.makedirs(dest, exist_ok=True)
    for f in glob.glob(os.path.join(src, '*.png')):
        shutil.copy2(f, dest)
    for f in glob.glob(os.path.join(src, 'film-*')) + glob.glob(os.path.join(src, 'reload-*')):  # the films (--film): a folder of frames per gun
        if os.path.isdir(f):
            shutil.copytree(f, os.path.join(dest, os.path.basename(f)))
    for name, to in (('RavenfieldVR-autotest.txt', 'autotest.txt'), ('RavenfieldVR-hand-closeups.png', 'hand-closeups.png'),
                     ('RavenfieldVR-hand-poses.png', 'hand-poses.png'), ('RavenfieldVR-hands.png', 'hands.png'),
                     ('RavenfieldVR-hands-back.png', 'hands-back.png'), ('LogOutput.log', 'game-log.txt'),
                     ('RavenfieldVR-weapon-report.txt', 'weapon-report.txt'), ('RavenfieldVR-rig-dump.txt', 'rig-dump.txt')):
        p = os.path.join(game, 'BepInEx', name)
        if os.path.exists(p) and os.path.getmtime(p) >= since:  # made by this run (the game rewrites them)
            shutil.copy2(p, os.path.join(dest, to))
    return dest


def summary(dest):
    report = os.path.join(dest, 'autotest.txt')
    if not os.path.exists(report):
        return
    lines = open(report, encoding='utf-8', errors='ignore').read().splitlines()
    problems = [l for l in lines if l.startswith('  ! ')]
    log(f'autotest: {len(problems)} problem(s)')
    for p in problems[:25]:
        print('   ', p.strip()[:220])


def main():
    # (Part names in the summary are often Chinese: printed to a cp1252 console or a redirected log they ended the run with
    # a UnicodeEncodeError after the pictures were taken.)
    for stream in (sys.stdout, sys.stderr):
        if stream is not None and hasattr(stream, 'reconfigure'):
            stream.reconfigure(encoding='utf-8', errors='replace')
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument('--weapons', action='store_true', help='every weapon: grab pictures and sheets')
    ap.add_argument('--weapon', action='append', default=[], metavar='NAME|ID|mod:TEXT|=NAME', help='one weapon (repeatable)')
    ap.add_argument('--ui', action='store_true', help='menus, wrist view, loadout, squad pointing, body, damage, dirt')
    ap.add_argument('--hands', action='store_true', help='glove close-ups and the pose sheet (flat)')
    ap.add_argument('--bench', action='store_true', help='graphics bench')
    ap.add_argument('--autotest', action='store_true', help='the whole autotest (quick: a sample of weapons)')
    ap.add_argument('--list', action='store_true', help='weapon ids and names from the last weapons run')
    ap.add_argument('--film', action='append', nargs='?', const='', metavar='NAME|ID',
                    help='film both eyes while guns shoot (repeatable; alone: four sample guns)')
    ap.add_argument('--flashcheck', action='store_true', help='every weapon fired: lens flashes counted against shots (no pictures)')
    ap.add_argument('--one-mag', action='store_true', help='with --film: aimed, full auto until one magazine is empty, nothing else')
    ap.add_argument('--dumprig', action='append', default=[], metavar='NAME', help='write the rig of matching weapons (repeatable)')
    ap.add_argument('--out', metavar='NAME', help='collect into reports/shots/NAME instead of the mode folder (a run that must not move the last one away)')
    ap.add_argument('--no-build', action='store_true')
    ap.add_argument('--res', default='1440x1584')
    ap.add_argument('--timeout', type=float, default=0)
    ap.add_argument('--game', default=os.path.join(STEAM, 'steamapps', 'common', 'Ravenfield'))
    a = ap.parse_args()

    if a.list:
        idx = os.path.join(REPO, 'reports', 'shots', 'weapons', 'sheets', 'index.json')
        for w in json.load(open(idx, encoding='utf-8')) if os.path.exists(idx) else []:
            print(f'{w["id"]:4d}  {w["name"]}')
        return
    modes = [m for m, on in (('weapons', a.weapons and not a.weapon), ('weapon', bool(a.weapon)), ('ui', a.ui), ('hands', a.hands), ('bench', a.bench),
                             ('autotest', a.autotest), ('film', a.film is not None), ('flashcheck', a.flashcheck)) if on]
    if not modes:
        ap.print_help()
        return
    if running('ravenfield.exe'):
        raise SystemExit('Ravenfield is running (someone is playing?): not starting.')
    failed = False
    w, h = (int(v) for v in a.res.lower().split('x'))
    build_and_install(a.game, build=not a.no_build)
    ensure_steam()
    for mode in modes:
        shots = os.path.join(a.game, 'BepInEx', 'RavenfieldVR-shots')
        shutil.rmtree(shots, ignore_errors=True)  # the run starts with an empty folder (copied to reports/shots after)
        base = ['-vr', '-map', 'island2023', '-nointro', '-rfvr-autotest']
        if mode in ('weapons', 'weapon'):
            only = ','.join(x.replace(',', ' ') for x in a.weapon)
            args = base + ['-rfvr-deep', '-rfvr-grabshots', f'-rfvr-only={only}']
            if a.dumprig:
                args.append('-rfvr-dumprig=' + ','.join(x.replace(',', ' ') for x in a.dumprig))
            timeout = a.timeout or (240 if not a.weapon else 30)
        elif mode == 'ui':
            args, timeout = base + ['-rfvr-uishots'], a.timeout or 20
        elif mode == 'film':
            only = ','.join(x.replace(',', ' ') for x in a.film if x)
            args, timeout = base + ['-rfvr-flashfilm', f'-rfvr-only={only}'] + (['-rfvr-filmonemag'] if a.one_mag else []), a.timeout or 20
        elif mode == 'flashcheck':
            args, timeout = base + ['-rfvr-flashcheck'], a.timeout or 45
        elif mode == 'bench':
            args, timeout = base + ['-rfvr-perfbench'], a.timeout or 20
        elif mode == 'autotest':
            args, timeout = base + ['-rfvr-quick', '-rfvr-perf'], a.timeout or 40
        else:
            args, timeout = ['-novr', '-nointro', '-rfvr-handshots'], a.timeout or 10
        since = time.time()
        if mode == 'hands':
            ran = run_game(a.game, args, timeout, vr=False)
        else:
            with NullHeadset((w, h)):
                ran = run_game(a.game, args, timeout, vr=True)
        if not ran:
            failed = True
            log(f'!!! the game did not come up in the headset after three tries: the {mode} pictures are missing or partial !!!')
        dest = collect(a.game, a.out or mode, since)
        log(f'{len(glob.glob(os.path.join(dest, "*.png")))} picture(s) in {os.path.relpath(dest, REPO)}')
        if mode in ('weapons', 'weapon'):
            index = build_sheets(dest, os.path.join(dest, 'sheets'))
            log(f'{len(index)} weapon sheet(s) in {os.path.relpath(os.path.join(dest, "sheets"), REPO)}')
            for e in index[:40]:
                print(f'    {e["id"]:4d}  {e["name"]:40s} {e["sheet"]}')
        if mode == 'film':
            subprocess.run([sys.executable, '-I', os.path.join(REPO, 'tools', 'flashfilm.py'), dest])
        summary(dest)
    if failed:
        sys.exit(1)


if __name__ == '__main__':
    main()
