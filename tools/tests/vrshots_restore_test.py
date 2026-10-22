"""Checks of vrshots.restore_left_over on temporary copies (no SteamVR file of the user's is touched, no process is stopped).
Run: python -I tools/tests/vrshots_restore_test.py   (exit code 0 when all pass)"""
import hashlib
import json
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


def sha(p):
    return hashlib.sha256(open(p, 'rb').read()).hexdigest()


def scene(own_cfg, chap, backup_ok=True):
    d = tempfile.mkdtemp(prefix='vrshots-restore-')
    cfg, chap_path, backups = os.path.join(d, 'steamvr.vrsettings'), os.path.join(d, 'chaperone_info.vrchap'), os.path.join(d, 'backups')
    os.makedirs(backups)
    mine = {'steamvr': {'forcedDriver': '', 'mirrorViewGeometry': '0 0 1080 600'}, 'driver_oculus': {'enable': True}}
    json.dump(mine, open(cfg, 'w', encoding='utf-8'), indent=3)
    h = sha(cfg)
    backup = os.path.join(backups, f'steamvr.vrsettings.20261022-001547.{h[:8]}')
    shutil.copyfile(cfg, backup)
    if not backup_ok:
        with open(backup, 'a', encoding='utf-8') as f:
            f.write(' ')
    if not own_cfg:
        null = dict(mine)
        null['steamvr'] = {'forcedDriver': 'null'}
        json.dump(null, open(cfg, 'w', encoding='utf-8'), indent=3)
    if chap is not None:
        json.dump({'jsonid': 'chaperone_info', 'universes': chap, 'version': 5}, open(chap_path, 'w', encoding='utf-8'))
    return d, cfg, chap_path, backups, h


TEST_AREA = {'universeID': str(vrshots.NULL_UNIVERSE)}
USER_AREA = {'universeID': '1599753190'}

d, cfg, chap, backups, h = scene(own_cfg=True, chap=None)
before = sha(cfg)
done = vrshots.restore_left_over(cfg, chap, backups, stop=False)
check(sha(cfg) == before and not os.path.exists(chap) and 'left as they are' in done[0], f'own settings left alone: {done}')
shutil.rmtree(d)

d, cfg, chap, backups, h = scene(own_cfg=False, chap=[TEST_AREA])
done = vrshots.restore_left_over(cfg, chap, backups, stop=False)
check(sha(cfg) == h, f'null driver: the backup is back byte for byte: {done}')
check(not os.path.exists(chap), 'the run\'s own chaperone file removed (there was none)')
shutil.rmtree(d)

d, cfg, chap, backups, h = scene(own_cfg=False, chap=[USER_AREA, TEST_AREA])
done = vrshots.restore_left_over(cfg, chap, backups, stop=False)
left = json.load(open(chap, encoding='utf-8'))['universes']
check(sha(cfg) == h and left == [USER_AREA], f'the user\'s play area kept, the run\'s taken out: {left}')
shutil.rmtree(d)

d, cfg, chap, backups, h = scene(own_cfg=False, chap=None, backup_ok=False)
null_hash = sha(cfg)
done = vrshots.restore_left_over(cfg, chap, backups, stop=False)
check(sha(cfg) == null_hash and done[0].startswith('!!!'), f'a backup that does not match its hash is not used: {done}')
shutil.rmtree(d)

print(f'{passed} passed, {failed} failed')
sys.exit(1 if failed else 0)
