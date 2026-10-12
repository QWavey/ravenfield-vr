"""Inspect Ravenfield weapon content bundles (.rfc) and report how guns are rigged.

For every prefab that carries a Weapon script it prints the transform hierarchy,
flags the parts the VR reload system cares about (magazine, slide/bolt/charging
handle, muzzle, arms) and lists which transforms each reload animation moves.

Usage:
    python -I tools/inspect_weapon_bundles.py <bundle-or-folder> [...] [--json out.json]
"""
from __future__ import annotations

import argparse
import json
import re
import sys
import zlib
from pathlib import Path

import UnityPy

PART_PATTERNS = {
    "magazine": re.compile(r"(^|[^a-z])(mag|magazine|clip|drum|box ?mag|ammo ?box)([^a-z]|$)|magazine|^mag", re.I),
    "slide": re.compile(r"slide|bolt|charg|handle|carrier|pump|lever|hammer|cock", re.I),
    "muzzle": re.compile(r"muzzle|barrel ?end|flash", re.I),
    "arms": re.compile(r"arm|hand|finger|thumb|wrist|elbow|clavicle|shoulder", re.I),
}


def transform_path(tr, root_tr) -> str:
    parts = []
    node = tr
    while node is not None:
        go = node.m_GameObject.read()
        if node.object_reader.path_id == root_tr.object_reader.path_id:
            break
        parts.append(go.m_Name)
        father = node.m_Father
        node = father.read() if father is not None and father.path_id else None
    return "/".join(reversed(parts))


def walk(tr, depth, root_tr, out):
    go = tr.m_GameObject.read()
    path = transform_path(tr, root_tr) if depth else ""
    tags = [k for k, rx in PART_PATTERNS.items() if rx.search(go.m_Name)]
    out.append({"depth": depth, "name": go.m_Name, "path": path, "tags": tags})
    for child in tr.m_Children:
        walk(child.read(), depth + 1, root_tr, out)


def script_name(mono) -> str:
    try:
        return mono.m_Script.read().m_ClassName
    except Exception:
        return "?"


def clip_paths(clip, crc_to_path):
    """Return the set of transform paths an AnimationClip animates."""
    paths = set()
    for attr in ("m_PositionCurves", "m_RotationCurves", "m_EulerCurves", "m_ScaleCurves", "m_FloatCurves"):
        for curve in getattr(clip, attr, []) or []:
            p = getattr(curve, "path", None)
            if p:
                paths.add(p)
    binding = getattr(clip, "m_ClipBindingConstant", None)
    if binding is not None:
        for gb in binding.genericBindings:
            h = gb.path
            if h in crc_to_path:
                paths.add(crc_to_path[h])
            elif h:
                paths.add(f"#crc:{h}")
    return paths


def inspect_bundle(path: Path):
    env = UnityPy.load(str(path))
    report = []
    weapons = []
    for obj in env.objects:
        if obj.type.name != "MonoBehaviour":
            continue
        try:
            mono = obj.read()
        except Exception:
            continue
        if script_name(mono) in ("Weapon", "ThrowableWeapon", "MeleeWeapon", "RocketLauncher", "ShellLoadedWeapon"):
            weapons.append(mono)

    clips = {}
    for obj in env.objects:
        if obj.type.name == "AnimationClip":
            try:
                c = obj.read()
                clips[c.m_Name] = c
            except Exception:
                pass

    for mono in weapons:
        go = mono.m_GameObject.read()
        root_tr = None
        for comp in go.m_Component:
            pptr = comp.component if hasattr(comp, "component") else comp
            if pptr.type.name == "Transform":
                root_tr = pptr.read()
                break
        if root_tr is None:
            continue
        nodes = []
        walk(root_tr, 0, root_tr, nodes)
        crc_to_path = {zlib.crc32(n["path"].encode("utf-8")): n["path"] for n in nodes if n["path"]}
        reload_clips = {}
        for name, clip in clips.items():
            if re.search(r"reload|mag|chamber|cock|rack|bolt|pump", name, re.I):
                touched = sorted(clip_paths(clip, crc_to_path))
                if touched:
                    reload_clips[name] = touched
        report.append({
            "bundle": path.name,
            "weapon": go.m_Name,
            "script": script_name(mono),
            "nodes": nodes,
            "reload_clips": reload_clips,
        })
    return report


def main(argv):
    ap = argparse.ArgumentParser()
    ap.add_argument("paths", nargs="+")
    ap.add_argument("--json")
    ap.add_argument("--tree", action="store_true", help="print the full hierarchy, not only tagged parts")
    args = ap.parse_args(argv)

    files = []
    for p in map(Path, args.paths):
        files.extend(sorted(p.rglob("*.rfc")) if p.is_dir() else [p])

    all_reports = []
    for f in files:
        try:
            reports = inspect_bundle(f)
        except Exception as exc:  # corrupt or unsupported bundle: report and move on
            print(f"!! {f.name}: {exc}", file=sys.stderr)
            continue
        for r in reports:
            all_reports.append(r)
            print(f"\n=== {r['bundle']} :: {r['weapon']} ({r['script']})")
            for n in r["nodes"]:
                if args.tree or n["tags"]:
                    tag = f"  [{','.join(n['tags'])}]" if n["tags"] else ""
                    print(f"{'  ' * n['depth']}{n['name']}{tag}")
            for clip, paths in r["reload_clips"].items():
                moving = [p for p in paths if any(rx.search(p.rsplit('/', 1)[-1]) for k, rx in PART_PATTERNS.items() if k != 'arms')]
                print(f"  clip '{clip}': {len(paths)} paths; gun parts moved: {moving[:12]}")
    if args.json:
        Path(args.json).write_text(json.dumps(all_reports, indent=1), encoding="utf-8")


if __name__ == "__main__":
    main(sys.argv[1:])
