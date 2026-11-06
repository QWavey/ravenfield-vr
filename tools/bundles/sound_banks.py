"""Every weapon's sound bank, read from the bundles: which clips a gun ships, by name and length.

Mods' bundles carry no type trees, so a SoundBank is read from its raw bytes: PPtr to the object, enabled, PPtr to the script, name, then
`clips`, an array of PPtrs. Anything whose array mostly points at AudioClips of the same file counts as one.

    python -I tools/bundles/sound_banks.py out.json            all bundles of the workshop folder (a few minutes)
    python -I tools/bundles/sound_banks.py out.json mk46       bundles whose file name holds the word
    python -I tools/bundles/sound_banks.py --show out.json mk46

CueNames (src/Weapons/WeaponAnalyzer.Sounds.cs) was written from these names. Needs UnityPy.
"""
import glob
import json
import os
import struct
import sys

import UnityPy

sys.stdout.reconfigure(encoding="utf-8", errors="replace")
WORKSHOP = r"C:\Program Files (x86)\Steam\steamapps\workshop\content\636480"


def banks_of(path):
    env = UnityPy.load(path)
    clips = {o.path_id: o for o in env.objects if o.type.name == "AudioClip"}
    rows = []
    for o in env.objects:
        if o.type.name != "MonoBehaviour":
            continue
        raw = o.get_raw_data()
        if len(raw) < 44:
            continue
        go_id = struct.unpack_from("<iq", raw, 0)[1]
        pos = 28  # past m_GameObject (12), m_Enabled (4, aligned) and m_Script (12)
        length = struct.unpack_from("<i", raw, pos)[0]
        if length < 0 or length > 300:
            continue
        pos = (pos + 4 + length + 3) & ~3
        if pos + 4 > len(raw):
            continue
        count = struct.unpack_from("<i", raw, pos)[0]
        pos += 4
        if count < 1 or count > 80 or pos + count * 12 > len(raw):
            continue
        ids = [struct.unpack_from("<iq", raw, pos + i * 12)[1] for i in range(count)]
        if sum(i in clips for i in ids) < max(1, count // 2):
            continue
        names = []
        for i in ids:
            if i in clips:
                clip = clips[i].read()
                names.append([clip.m_Name, round(float(getattr(clip, "m_Length", 0) or 0), 2)])
            else:
                names.append(["?", 0])
        try:
            go = o.assets_file.objects[go_id].read().m_Name
        except Exception:
            go = None
        rows.append({"go": go, "clips": names})
    return rows


def main():
    args = sys.argv[1:]
    if args and args[0] == "--show":
        banks = json.load(open(args[1], encoding="utf-8"))
        word = args[2].lower() if len(args) > 2 else ""
        for name, rows in banks.items():
            if word in name.lower():
                for row in rows:
                    print(name[:20], row["go"], [f"{i}:{c[0]} ({c[1]})" for i, c in enumerate(row["clips"])])
        return
    word = args[1].lower() if len(args) > 1 else ""
    found = {}
    files = sorted(glob.glob(os.path.join(WORKSHOP, "*", "*.rfc")), key=os.path.getsize)
    for path in files:
        if word not in os.path.basename(path).lower() or os.path.getsize(path) > 400_000_000:
            continue
        try:
            rows = banks_of(path)
            if rows:
                found[os.path.basename(path)] = rows
        except Exception as e:
            print("failed:", os.path.basename(path), str(e)[:70], flush=True)
    json.dump(found, open(args[0], "w", encoding="utf-8"), ensure_ascii=False, indent=0)
    print(len(found), "bundles,", sum(len(v) for v in found.values()), "banks")


if __name__ == "__main__":
    main()
