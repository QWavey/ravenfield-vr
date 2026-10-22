"""Offline: uninstall.ps1 on a made-up install in a temporary folder (never the real game, never the real Steam).

Checks what the setup's own uninstall already does: the VR launcher is kept while a Steam account's launch options still start
it (deleted, Ravenfield no longer started from Steam), and goes when none does; the launcher's log is removed; -RemoveConfig
removes the launcher's remembered choice with the mod's config and weapon overrides; BepInEx it did not install stays.
Run: python -I tools/tests/uninstall_ps1_test.py   (exit code 0 when all pass)
"""
import os
import shutil
import subprocess
import sys
import tempfile

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
failed = passed = 0


def check(ok, what):
    global failed, passed
    if ok:
        passed += 1
    else:
        failed += 1
        print('FAIL', what)


def make(base, launch_option):
    game = os.path.join(base, 'game')
    steam = os.path.join(base, 'steam')
    for d in ('BepInEx/plugins/RavenfieldVR', 'BepInEx/config', 'BepInEx/core', 'steam'):
        os.makedirs(os.path.join(game if d != 'steam' else base, d), exist_ok=True)
    files = {'ravenfield.exe': '', 'RavenfieldVR-Launcher.exe': 'launcher', 'BepInEx/plugins/RavenfieldVR/RavenfieldVR.dll': 'mod',
             'BepInEx/core/BepInEx.dll': 'loader', 'BepInEx/RavenfieldVR-launcher.log': 'log',
             'BepInEx/config/com.qwavey.ravenfieldvr.cfg': 'cfg', 'BepInEx/config/RavenfieldVR.weapons.json': '{}',
             'BepInEx/config/RavenfieldVR.launcher.ini': 'mode=VR', 'BepInEx/config/other.plugin.cfg': 'keep'}
    for rel, text in files.items():
        with open(os.path.join(game, rel), 'w', encoding='utf-8') as f:
            f.write(text)
    with open(os.path.join(game, 'BepInEx/plugins/RavenfieldVR/install-manifest.txt'), 'w', encoding='utf-8') as f:
        f.write('version=test\ninstalledBepInEx=False\nbackup=\nadded=RavenfieldVR-Launcher.exe\n'
                'added=BepInEx\\plugins\\RavenfieldVR\\RavenfieldVR.dll\n')
    cfg = os.path.join(steam, 'userdata', '12345', 'config')
    os.makedirs(cfg, exist_ok=True)
    option = '"\\"' + game.replace('\\', '\\\\') + '\\\\RavenfieldVR-Launcher.exe\\" %command%"' if launch_option else '""'
    with open(os.path.join(cfg, 'localconfig.vdf'), 'w', encoding='utf-8') as f:
        f.write('"UserLocalConfigStore"\n{\n\t"Software"\n\t{\n\t\t"Valve"\n\t\t{\n\t\t\t"Steam"\n\t\t\t{\n\t\t\t\t"apps"\n\t\t\t\t{\n'
                '\t\t\t\t\t"636480"\n\t\t\t\t\t{\n\t\t\t\t\t\t"LaunchOptions"\t\t' + option + '\n\t\t\t\t\t}\n\t\t\t\t}\n\t\t\t}\n\t\t}\n\t}\n}\n')
    return game, steam


def run(game, steam, *extra):
    r = subprocess.run(['powershell', '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', os.path.join(ROOT, 'uninstall.ps1'),
                        '-GameDir', game, '-SteamDir', steam] + list(extra), capture_output=True, text=True, errors='replace')
    return r.returncode, r.stdout + r.stderr


def exists(game, rel):
    return os.path.exists(os.path.join(game, rel))


for in_use in (True, False):
    base = tempfile.mkdtemp(prefix='rfvr-uninstall-')
    try:
        game, steam = make(base, in_use)
        code, out = run(game, steam, '-RemoveConfig')
        check(code == 0, f'uninstall.ps1 ran (launch option {"set" if in_use else "not set"}): {out.strip()[-300:]}')
        check(exists(game, 'RavenfieldVR-Launcher.exe') == in_use,
              f'the launcher {"kept: Steam still starts it" if in_use else "removed: nothing starts it"}')
        check(not exists(game, 'BepInEx/plugins/RavenfieldVR'), 'the plugin folder removed')
        check(not exists(game, 'BepInEx/RavenfieldVR-launcher.log'), "the launcher's log removed")
        check(not any(exists(game, 'BepInEx/config/' + c) for c in ('com.qwavey.ravenfieldvr.cfg', 'RavenfieldVR.weapons.json', 'RavenfieldVR.launcher.ini')),
              "-RemoveConfig: the config, the weapon overrides and the launcher's choice removed")
        check(exists(game, 'BepInEx/config/other.plugin.cfg') and exists(game, 'BepInEx/core/BepInEx.dll'),
              "another plugin's config and BepInEx it did not install kept")
    finally:
        shutil.rmtree(base, ignore_errors=True)

print(f'{passed} passed, {failed} failed')
sys.exit(1 if failed else 0)
