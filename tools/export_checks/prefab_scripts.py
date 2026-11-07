"""Which script classes the game's own prefabs carry (resources.assets): the vanilla weapons, projectiles and props by name.

Found the vanilla C4 to be a RemoteDetonatorWeapon (its charges never went off in VR) and the Air Horn an Airhorn (a blast on the
trigger, where the mod only swung it): a game class with a Fire or a hit of its own that the mod never names is worth reading.

    python -I tools/export_checks/prefab_scripts.py                    every prefab with a weapon or projectile script
    python -I tools/export_checks/prefab_scripts.py Airhorn Wrench     prefabs carrying any of these classes
    python -I tools/export_checks/prefab_scripts.py --name C4          prefabs whose name contains this

Reads by hand (no type trees): a MonoBehaviour's GameObject PPtr at byte 0, its script PPtr at 16. Needs UnityPy.
"""
import argparse
import os
import struct
import sys

import UnityPy

sys.stdout.reconfigure(encoding="utf-8")
DATA = r"C:\Program Files (x86)\Steam\steamapps\common\Ravenfield\ravenfield_Data"
WEAPONISH = ("Weapon", "Projectile", "Throwable", "Melee", "Airhorn", "Wrench", "RavenSword", "Mortar", "Turret", "Medipack", "Ammobox",
             "NightVision", "ToggleableItem", "SquadLeaderKit", "FishingRod")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("classes", nargs="*", help="script class names to look for (default: weapon and projectile classes)")
    ap.add_argument("--name", help="only prefabs whose name contains this (any case)")
    ap.add_argument("--data", default=DATA, help="the game's ravenfield_Data folder")
    a = ap.parse_args()
    env = UnityPy.load(os.path.join(a.data, "globalgamemanagers.assets"), os.path.join(a.data, "resources.assets"))
    scripts = {}
    for obj in env.objects:
        if obj.type.name == "MonoScript":
            try:
                scripts[obj.path_id] = obj.read().m_ClassName
            except Exception:
                pass
    by_go = {}
    for obj in env.objects:
        if obj.type.name != "MonoBehaviour" or not obj.assets_file.name.endswith("resources.assets"):
            continue
        raw = obj.get_raw_data()
        _, go = struct.unpack_from("<iq", raw, 0)
        _, script = struct.unpack_from("<iq", raw, 16)
        by_go.setdefault(go, []).append(scripts.get(script, "?"))
    wanted = set(a.classes)
    for obj in env.objects:
        if obj.type.name != "GameObject" or not obj.assets_file.name.endswith("resources.assets"):
            continue
        classes = by_go.get(obj.path_id)
        if not classes:
            continue
        try:
            name = obj.read().m_Name
        except Exception:
            continue
        if a.name and a.name.lower() not in name.lower():
            continue
        if wanted and not wanted.intersection(classes):
            continue
        if not wanted and not a.name and not any(any(w in c for w in WEAPONISH) for c in classes):
            continue
        print(f"{name}: {', '.join(classes)}")


if __name__ == "__main__":
    main()
