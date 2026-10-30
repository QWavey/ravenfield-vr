"""Offline: the reload helpers the analysis found (a part that moves in the reload but is hidden at rest: the M4A1 family's
second 'zZ008 MAG', the fresh magazine the flat view's hand brings in) and which of them the held gun hides
(WeaponAnalyzer.AnalyzeMagazine, 155fa20): only one named as ammunition (a magazine, a round, a shell, an ammo box), never an
optic. An export made after that change says "the reload helper 'X' hidden while held" for each one hidden; a gun whose
note disagrees with the rule here is listed as wrong (exit code 1). An older export has no such notes and is listed only.
Usage: reload_helper_rule.py [gun dir ...]"""
import glob
import json
import os
import re
import sys

sys.stdout.reconfigure(encoding='utf-8')
ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))  # the repository
DATA = os.path.join(ROOT, 'reports', 'sandbox', 'data', 'weapons')
# WeaponAnalyzer's MagName, NotMagName, RoundName, AmmoPartName and OpticName (the parts of them a helper's name meets).
MAG = re.compile(r"(^|[^a-z])(mag(azine?)?s?($|[^a-z])|clip|drum|shell|rocket|round|ammo|cartridge|belt|cargador|chargeur|patron|lenta|"
                 r"zidan|danxia)|(^|[^a-z])dx($|[^a-z])", re.I)
NOT_MAG = re.compile(r"release|catch|button|latch|controll?er|控制器|(^|[^a-z])well|magwell|housing|lever|pouch|holder|spare|switch|"
                     r"bolt ?stop|stopper|indicator|弹匣释放|卡笋|laser|镭射|激光|flash ?light|torch", re.I)
ROUND = re.compile(r"(^|[^a-z])(bullets?|rounds?|cartridges?|casings?|shells?|zidan)($|[^a-z])|子弹|弹壳|弹头", re.I)
AMMO = re.compile(r"gauge|guage|loader|follower|弹|patron", re.I)
OPTIC = re.compile(r"glass|lens|scope|sight|optic|reticle|magnif|zoom|(^|[^a-z])(clu|cmd ?unit|command ?unit)($|[^a-z])|倍镜|瞄", re.I)
FOUND = re.compile(r"'([^']*)' moves in the reload but is hidden at rest")
HIDDEN = re.compile(r"the reload helper '([^']*)' hidden while held")


def hides(name):
    """Whether the rule hides a helper of this name while held (the launcher words left out: a launcher's rockets stay drawn)."""
    unspaced = re.sub(r'\s+', '', name)  # (the P220's 'dan    xia': 弹匣's pinyin spaced out)
    named = any(MAG.search(n) and not NOT_MAG.search(n) for n in (name, unspaced)) or ROUND.search(name) or AMMO.search(name)
    return bool(named) and not OPTIC.search(name)


names = sys.argv[1:] or sorted(os.path.basename(x) for x in glob.glob(os.path.join(DATA, '*')))
rows, wrong, new_export = [], [], False
for n in names:
    try:
        rig = json.load(open(os.path.join(DATA, n, 'rig.json'), encoding='utf-8'))
    except Exception as e:
        print(n, 'failed', e)
        continue
    notes = rig.get('notes') or ''
    found = FOUND.findall(notes)
    hidden = set(HIDDEN.findall(notes))
    new_export |= bool(hidden)
    for helper in found:
        rows.append((n, helper, hides(helper), helper in hidden))
# Only an export with any such note was made after the rule: in an older one every helper reads as "not hidden".
for n, helper, rule, said in rows:
    if new_export and rule != said:
        wrong.append((n, helper, rule, said))
print(f"{len(rows)} reload helper(s) in {len(names)} gun(s); the rule hides {sum(1 for r in rows if r[2])}"
      + ("" if new_export else " (an export from before the rule: no 'hidden while held' notes to check)"))
by_name = {}
for n, helper, rule, _ in rows:
    by_name.setdefault((helper, rule), []).append(n)
for (helper, rule), guns in sorted(by_name.items(), key=lambda kv: (-len(kv[1]), kv[0][0])):
    print(f"  {'hidden' if rule else 'drawn '} {len(guns):3d}  {helper}   ({guns[0]}{', ...' if len(guns) > 1 else ''})")
for n, helper, rule, said in wrong:
    print(f"WRONG {n}: '{helper}' {'should be' if rule else 'is'} hidden while held, the note {'does not say so' if rule else 'says so'}")
print(f"{len(wrong)} wrong")
sys.exit(1 if wrong else 0)
