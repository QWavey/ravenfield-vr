"""Offline: the keys the weapons' own Ravenscript (Lua) checks, classified the way src/Input/ScriptKeys.cs does (its Rules,
FirstRule on the behaviour name, else the nearest rule word in the 500 characters after the key check). Lists every key with
its purpose, the words that decided it and the other rule words near it, so a broad word deciding the wrong purpose shows.
Usage: python -I tools/export_checks/script_keys_scan.py [bundle or folder ...] (default: the Steam workshop folder of Ravenfield)
"""
import glob
import os
import re
import sys

import UnityPy

sys.stdout.reconfigure(encoding='utf-8')

# Kept in step with ScriptKeys.Rules (order matters: ties and the behaviour name go to the first rule listed).
RULES = [
    ('FireMode', r'fire ?mode|firemode|isauto|semi|burst|switchauto|modechange|weaponmode|selector'),
    ('Bipod', r'bipod'),
    ('Laser', r'laser|镭射|激光'),
    ('Magnifier', r'magnif|zoom|scope|sight'),
    ('Light', r'flash ?light|torch|light|flash$'),
    ('Suppressor', r'suppress|silenc'),
    ('Inspect', r'inspect'),
    ('Skin', r'skin|camo'),
    ('Sprint', r'sprint|dash'),
    ('Hud', r'hud|feed|killcam|minimap|overlay|scoreboard|crosshair|damageindicator'),
]
RULES = [(p, re.compile(w, re.I)) for p, w in RULES]
STRONG_MAGNIFIER = re.compile(r'magnif|zoom|scope', re.I)
KEY_CHECK = re.compile(r'GetKey(?:Down|Up)?\s*\(\s*(?:KeyCode\.(\w+)|["\']([^"\']+)["\'])', re.I)


def first_rule(text):
    for purpose, words in RULES:
        if words.search(text):
            if purpose == 'Laser' and STRONG_MAGNIFIER.search(text):
                return 'Magnifier'
            return purpose
    return None


def nearest_rule(text):
    best, best_at = None, None
    for purpose, words in RULES:
        m = words.search(text)
        if m and (best_at is None or m.start() < best_at):
            best, best_at = purpose, m.start()
    return best, best_at


def scan(path):
    rows = []
    env = UnityPy.load(path)
    sources = {}
    for obj in env.objects:
        if obj.type.name == 'TextAsset':
            try:
                d = obj.read()
                text = d.m_Script if isinstance(d.m_Script, str) else bytes(d.m_Script).decode('utf-8', 'replace')
                sources[obj.path_id] = (d.m_Name, text)
            except Exception:
                pass
    if not sources:
        return rows
    for obj in env.objects:
        if obj.type.name != 'MonoBehaviour':
            continue
        try:
            tree = obj.read_typetree()
        except Exception:
            continue
        if 'behaviour' not in tree or 'source' not in tree:
            continue
        src = tree.get('source') or {}
        name, text = sources.get(src.get('m_PathID'), (None, None))
        if text is None:
            continue
        behaviour = tree.get('behaviour') or name or ''
        for m in KEY_CHECK.finditer(text):
            key = m.group(1) or m.group(2)
            around = text[m.start():m.start() + 500]
            by_name = first_rule(behaviour)
            near, at = nearest_rule(around)
            purpose = by_name or near or 'Other'
            others = sorted({p for p, w in RULES if w.search(around) and p != purpose})
            word = ''
            if not by_name and near:
                w = dict(RULES)[near].search(around)
                word = w.group(0) if w else ''
            rows.append((os.path.basename(path), behaviour, key, purpose, 'name' if by_name else f'near "{word}"' if near else '-', others))
    return rows


def main():
    targets = sys.argv[1:] or [r'C:\Program Files (x86)\Steam\steamapps\workshop\content\636480']
    files = []
    for t in targets:
        files += sorted(glob.glob(os.path.join(t, '**', '*.rfc'), recursive=True)) if os.path.isdir(t) else [t]
    seen = set()
    for f in files:
        try:
            rows = scan(f)
        except Exception as e:
            print(f'{os.path.basename(f)}: not read ({e})')
            continue
        for r in rows:
            k = (r[1], r[2], r[3])
            if k in seen:
                continue
            seen.add(k)
            print(f'{r[0][:28]:28s} | {r[1][:28]:28s} | {r[2]:10s} -> {r[3]:10s} by {r[4]:18s} | also near: {", ".join(r[5]) or "-"}')


if __name__ == '__main__':
    main()
