"""Sorts the weapon sheets of a vrshots run into category folders (for reviewing them by kind).

  python tools/shots_categories.py [sheets dir] [weapon report]

Reads the sheets (reports/shots/weapons/sheets/*.jpg with index.json) and the mod's weapon report (BepInEx/
RavenfieldVR-weapon-report.txt from the self-test: each weapon's action, pistol grip, machine gun) and copies every
sheet into reports/shots/weapons/categories/<category>/: pistols, smgs, assault-rifles, battle-rifles-dmrs,
bolt-action-snipers, shotguns, machine-guns, launchers, other. Writes categories.json (category -> sheets).
"""
import json
import os
import re
import shutil
import sys

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SHEETS = sys.argv[1] if len(sys.argv) > 1 else os.path.join(REPO, 'reports', 'shots', 'weapons', 'sheets')
REPORT = sys.argv[2] if len(sys.argv) > 2 else r'C:\Program Files (x86)\Steam\steamapps\common\Ravenfield\BepInEx\RavenfieldVR-weapon-report.txt'
OUT = os.path.join(os.path.dirname(SHEETS), 'categories')

SMG = re.compile(r'(?<![a-z])(mp ?5|mp ?7|mp ?9|mp ?40|ump|vector|kriss|p ?90|uzi|smg|mpx|scorpion|evo|pp-?\d|ppsh|pps|thompson|tommy|mac-?1[01]|'
                 r'tec-?9|vityaz|bizon|aug para|mp ?18|sten|grease|m3a1|cbj|pdw|mp ?1|mp ?2|kp|suomi|tmp|skorpion|ingress)', re.I)
PISTOL = re.compile(r'(?<![a-z])(glock|1911|p220|p226|p320|92g|g17|g19|fnx|beretta|deagle|desert eagle|usp|m9\b|px9|viper|revolver|magnum|'
                    r'condor|python|tt-?33|makarov|five-?seven|p250|hk45|cz ?75|pistol|handgun)', re.I)
DMR = re.compile(r'(?<![a-z])(dmr|sr-?25|m110|m14|ebr|svd|dragunov|sks|fal|g3|scar-?h|m1a|mk ?12|mk ?14|vss|m1 ?garand|garand|garret|g28|'
                 r'm39|psg|hk ?417|ar-?10|ak-?308|svt|mosin.*sv|lr-?300|marksman|br\b)', re.I)
SNIPER = re.compile(r'(?<![a-z])(sniper|awp|awm|l96|m24|m40|m200|intervention|barrett|m82|m107|mrad|cheytac|kar ?98|mosin|springfield|'
                    r'enfield|trg|ax-?50|lrr|recon|scout|sv-?98|hunting)', re.I)
SHOTGUN = re.compile(r'(?<![a-z])(shotgun|m870|r870|870|m500|m590|mossberg|spas|aa-?12|saiga|benelli|m1014|ksg|super ?90|double ?barrel|'
                     r'sawn|ithaca|striker|usas|vepr|dp-?12|supernova|nova|buckshot)', re.I)
LAUNCHER = re.compile(r'(?<![a-z])(rpg|launcher|bazooka|panzer|at-?4|smaw|javelin|stinger|law|m72|carl|gustaf|hydra|scalpel|m32|'
                      r'm79|m203|gl\b|grenade launcher|rocket|igla|at-?1k|nlaw|thumper|mgl|china lake)', re.I)


def key_name(s):
    return re.sub(r'[^A-Za-z0-9_.-]+', '_', s)


def read_report(path):
    """Entry name (sanitised as in the picture names) -> (action, flags)."""
    out = {}
    if not os.path.exists(path):
        return out
    cur = None
    for line in open(path, encoding='utf-8', errors='ignore'):
        if line.startswith('  ') and not line.startswith('    ') and '#' in line:
            cur = key_name(line.strip().split('#')[0])
        elif cur and line.startswith('    action '):
            out.setdefault(cur, line.strip()[len('action '):])
    return out


def category(name, info):
    n = name.replace('_', ' ')
    action = info.split(',')[0].strip() if info else ''
    if 'machine gun' in info or re.search(r'(?<![a-z])(lmg|mg\d|m249|m60|pkm|pkp|rpk|mg-?42|mg-?34|m240|negev|minimi|bren(?! 2)|'
                                          r'dp-?28|qjb|ultimax|stoner|mk ?46|mk ?48|hk ?21|mg ?5|ameli|l86)', n, re.I):
        return 'machine-guns'
    if action == 'SingleShot' or LAUNCHER.search(n):
        return 'launchers'
    if action in ('ShellFed', 'BreakOpen') or SHOTGUN.search(n):
        return 'shotguns'
    if ('pistol grip' in info or PISTOL.search(n)) and not SMG.search(n):
        return 'pistols'
    if action == 'Manual' or SNIPER.search(n):
        return 'bolt-action-snipers'
    if SMG.search(n):
        return 'smgs'
    if DMR.search(n):
        return 'battle-rifles-dmrs'
    if action == 'SelfLoading':
        return 'assault-rifles'
    return 'other'


def main():
    index = json.load(open(os.path.join(SHEETS, 'index.json'), encoding='utf-8'))
    report = read_report(REPORT)
    shutil.rmtree(OUT, ignore_errors=True)
    cats = {}
    for e in index:
        info = report.get(key_name(e['name']), '')
        c = category(e['name'], info)
        d = os.path.join(OUT, c)
        os.makedirs(d, exist_ok=True)
        src = os.path.join(REPO, e['sheet']) if not os.path.isabs(e['sheet']) else e['sheet']
        shutil.copy2(src, d)
        cats.setdefault(c, []).append({'id': e['id'], 'name': e['name'], 'sheet': os.path.join(d, os.path.basename(src)), 'action': info})
    json.dump(cats, open(os.path.join(OUT, 'categories.json'), 'w', encoding='utf-8'), indent=1)
    for c, items in sorted(cats.items()):
        print(f'{c:22s} {len(items):4d}')


if __name__ == '__main__':
    main()
