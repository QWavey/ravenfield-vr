"""Offline: install.ps1 upgrading a made-up earlier install in a temporary folder (never the real game).

An upgrade removes the files the earlier install added that this one no longer has. Their names come from the install record,
which is a plain text file in the game folder: an entry that climbs out of it ('..\\x') or is rooted must be skipped, never
deleted (uninstall.ps1 and the setup already refused them). Also: the mod, BepInEx and the record are written.
Needs the packaged mod in dist/RavenfieldVR (build.ps1 -Package); skipped without it.
Run: python -I tools/tests/install_ps1_test.py   (exit code 0 when all pass)
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


if not os.path.exists(os.path.join(ROOT, 'dist', 'RavenfieldVR', 'BepInEx', 'plugins', 'RavenfieldVR', 'RavenfieldVR.dll')):
    print('install.ps1 check skipped (no packaged mod in dist/RavenfieldVR)')
    sys.exit(0)

base = tempfile.mkdtemp(prefix='rfvr-install-')
try:
    game = os.path.join(base, 'game')
    plugin = os.path.join(game, 'BepInEx', 'plugins', 'RavenfieldVR')
    os.makedirs(plugin)
    open(os.path.join(game, 'ravenfield.exe'), 'w').close()
    outside = os.path.join(base, 'outside.txt')  # beside the game folder: not the install's
    with open(outside, 'w', encoding='utf-8') as f:
        f.write('not yours')
    old = os.path.join(game, 'old_part.txt')  # added by the earlier version, gone in this one
    with open(old, 'w', encoding='utf-8') as f:
        f.write('old')
    with open(os.path.join(plugin, 'install-manifest.txt'), 'w', encoding='utf-8') as f:
        f.write('version=0.9\ninstalledBepInEx=False\nbackup=\nadded=old_part.txt\nadded=..\\outside.txt\nadded=' + outside + '\n')
    r = subprocess.run(['powershell', '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', os.path.join(ROOT, 'install.ps1'), '-GameDir', game],
                       capture_output=True, text=True, errors='replace')
    out = (r.stdout + r.stderr).strip()
    check(r.returncode == 0, 'install.ps1 ran: ' + out[-300:])
    check(os.path.exists(outside), "a record entry outside the game folder ('..\\outside.txt', and a rooted one) is not deleted")
    check(not os.path.exists(old), 'a file the earlier version added and this one has not is removed')
    check(os.path.exists(os.path.join(plugin, 'RavenfieldVR.dll')) and os.path.exists(os.path.join(game, 'BepInEx', 'core', 'BepInEx.dll')),
          'the mod and BepInEx are installed')
    record = open(os.path.join(plugin, 'install-manifest.txt'), encoding='utf-8-sig').read()
    check('installedBepInEx=True' in record and 'outside.txt' not in record and 'old_part.txt' not in record,
          'the new record names what this install added (and BepInEx as installed by it)')
finally:
    shutil.rmtree(base, ignore_errors=True)

print(f'{passed} passed, {failed} failed')
sys.exit(1 if failed else 0)
