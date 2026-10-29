"""Offline -> mod: the magazine lines measured better than the analysed ones, as a table the mod applies after its analysis
(src/Weapons/MagLineTable.cs, generated here). Reads mag_line_scan.py outputs ("best tip: fore/aft F deg, sideways S deg,
score B (analysed A)"), takes the guns whose best tilt is clearly better (B at most half of A, and at least 15 fewer points
out of place), looks up each gun's analysis key in its rig.json, and writes the table. The tilts are relative to the line the
analysis makes, so the export (which the scans read) records the analysed line, before this table is applied.
Usage: mag_line_table.py <scan output> [more...]
       mag_line_table.py --both <mag_line_body.py --both output>
The second is the one in use (2026-10-20): the scans alone counted a point pushed into the gun as hidden and drove lines through
the walls of the well (37 of their 70 lines went through more wall than the analysed line: the 1911s, P220s, CZ Bren 2s,
AK-12s); mag_line_body.py --both takes the tilt with the least wall and the least out of the outline together. A gun is taken
when that sum is at most 60% of the analysed line's and at least 30 points fewer."""
import json
import os
import re
import sys

import numpy as np

sys.stdout.reconfigure(encoding='utf-8')
ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))  # the repository
DATA = os.path.join(ROOT, 'reports', 'sandbox', 'data', 'weapons')
OUT = os.path.join(ROOT, 'src', 'Weapons', 'MagLineTable.cs')

# Export folders whose line on the cap was checked in a picture and looked right (along the grip's rake).
CHECKED = {'P320_p320_1', 'P320s_p320_1_s'}

# Lines set by hand from a picture (fore/aft, sideways from the export's line) where the scan's best was an artifact: a part
# of the gun the magazine is pushed into counts as hiding it, not as a wall. The PPSh-41's scanned -16.5 (179 -> 82) pulled
# its square top block back through the magazine catch; along the block's sides, straight down the gun, it comes out clear
# (0 points beside the outline at 12% out; 2026-10-20).
MANUAL = {'PPSh-41_PPSH_Stick': (6.5, 0.0)}
# --both: a least on the search's edge (fore/aft 18, sideways 6) is taken only where a picture showed it right: the P320's
# magazine sits canted in its model (seen from the front, -6 sideways follows it, -1.5 leaves its footprint; 2026-10-20).
EDGE_OK = {'P320_p320_1', 'P320s_p320_1_s'}
# --both: where the least lies on the edge unchecked, the earlier scan's line, inside it and through less wall than the
# analysed line by mag_line_body.py (the Vectors and the Glock 19 at -16.5: 224 -> 84 and 174 -> 35).
EDGE_INSIDE = ('vector_vector', 'vector_rds_vector_rds', 'mw-vector_No_script__vector_originalshaking',
               'mw-vector_rds_No_script__vector_rds_originalshaking', 'ARS_Glock_19_G19')
if len(sys.argv) > 1 and sys.argv[1] == '--both':
    MANUAL.update({f: (-16.5, 0.0) for f in EDGE_INSIDE})

rows = {}
BOTH = re.compile(r'^(\S+): analysed wall (\d+) out (\d+); least ([+-][\d.]+)/([+-][\d.]+) wall (\d+) out (\d+)')
for path in (sys.argv[2:] if sys.argv[1] == '--both' else []):
    for line in open(path, encoding='utf-8'):
        m = BOTH.match(line)
        if not m:
            continue
        folder = m.group(1)
        analysed = int(m.group(2)) + int(m.group(3))
        fore, side = float(m.group(4)), float(m.group(5))
        best = int(m.group(6)) + int(m.group(7))
        rig = json.load(open(os.path.join(DATA, folder, 'rig.json'), encoding='utf-8'))
        # A detachable magazine only: a pump's or a break action's shell, a launcher's rocket (the Panzerfaust 3's is its stock)
        # go in by other lines. Not where most of the count stays at the least (over 1000: the M417A2's uneven magwell bottom,
        # the RPG-7V's tube), nor on the search's edge unchecked.
        magazine = rig.get('action') == 'SelfLoading' or rig.get('action') == 'Manual' and rig.get('magazine')
        edge = abs(fore) >= 17.9 or abs(side) >= 5.9
        if (fore or side) and magazine and best <= analysed * 0.6 and analysed - best >= 30 and best <= 1000 and (not edge or folder in EDGE_OK):
            rows[rig['key']] = (fore, side, analysed, best, folder)
for path in (sys.argv[1:] if sys.argv[1] != '--both' else []):
    capped_scan = 'best tip (capped)' in open(path, encoding='utf-8').read()
    cur = None
    for line in open(path, encoding='utf-8'):
        m = re.match(r'^(\S+): \d+ magazine points', line)
        if m:
            cur = m.group(1)
            continue
        m = re.search(r'best tip \(capped\): fore/aft ([+-]?\d+\.\d) deg, sideways ([+-]?\d+\.\d) deg, score (\d+) \(analysed (\d+)\)', line)
        if m and cur:
            # The best within a plausible tilt (fore/aft 18, sideways 6; mag_line_wide.py): the one taken when a scan gives it.
            fore, side, best, analysed = float(m.group(1)), float(m.group(2)), int(m.group(3)), int(m.group(4))
            # On the cap itself only when a picture (mag_line_draw.py) showed it right: pinned there, the line is usually the same
            # artifact pushing further out (the HK416-D's and F2000's slid through the receiver and the stock, 2026-10-20).
            at_cap = abs(fore) >= 17.9 or abs(side) >= 5.9
            if (fore or side) and (not at_cap or cur in CHECKED) and best <= analysed * 0.55 and analysed - best >= 15:
                rig = json.load(open(os.path.join(DATA, cur, 'rig.json'), encoding='utf-8'))
                rows[rig['key']] = (fore, side, analysed, best, cur)
            cur = None
            continue
        m = re.search(r'best tip( \(wide\))?: fore/aft ([+-]?\d+\.\d) deg, sideways ([+-]?\d+\.\d) deg, score (\d+) \(analysed (\d+)\)', line)
        if m and cur and capped_scan:
            continue  # the capped line, after this one, decides
        if m and cur:
            wide = m.group(1) is not None
            fore, side, best, analysed = float(m.group(2)), float(m.group(3)), int(m.group(4)), int(m.group(5))
            # A best on the search's edge is not taken: the line may lie further out, or the count only falls because a steep
            # line lets points out of the band it counts (the QBZ192's). A wide scan (mag_line_wide.py) marks its own edge.
            edge = '(edge)' in line if wide else abs(fore) >= 17.9 or abs(side) >= 7.4
            if (fore or side) and not edge and best <= analysed * 0.55 and analysed - best >= 15:
                rig = json.load(open(os.path.join(DATA, cur, 'rig.json'), encoding='utf-8'))
                rows[rig['key']] = (fore, side, analysed, best, cur)
            cur = None

for folder, (fore, side) in MANUAL.items():
    rig = json.load(open(os.path.join(DATA, folder, 'rig.json'), encoding='utf-8'))
    analysed, best = rows.get(rig['key'], (0, 0, -1, -1, ''))[2:4]
    rows[rig['key']] = (fore, side, analysed, best, folder)


def corrected(folder, fore, side):
    """The line as the scan turns it (about right = aim x up, then about the aim), in the gun's frame. Set as it is in game,
    it does not depend on which of the analysis's sources gave the line there (a tilt on top of a different source landed
    elsewhere; review, 2026-10-20)."""
    rig = json.load(open(os.path.join(DATA, folder, 'rig.json'), encoding='utf-8'))
    norm = lambda v: np.asarray(v, float) / np.linalg.norm(v)
    aim = norm(rig['aim'])
    up = np.asarray(rig.get('up', [0, 1, 0]), float)
    up = norm(up - aim * up.dot(aim))
    right = norm(np.cross(aim, up))

    def rot(v, axis, deg):
        r = np.radians(deg)
        return v * np.cos(r) + np.cross(axis, v) * np.sin(r) + axis * axis.dot(v) * (1 - np.cos(r))
    return norm(rot(rot(norm(rig['magEject']), right, fore), aim, side))


lines = [
    '// Generated by tools/export_checks/mag_line_table.py from mag_line_scan.py over the sandbox export: do not edit by hand.',
    'using System.Collections.Generic;',
    'using UnityEngine;',
    '',
    'namespace RavenfieldVR.Weapons',
    '{',
    '    internal static partial class MagLineTable',
    '    {',
    '        /// <summary>Analysis key -> the magazine line (out of the gun) in the gun\'s frame, as measured offline.</summary>',
    '        private static readonly Dictionary<string, Vector3> Lines = new Dictionary<string, Vector3>',
    '        {',
]
for key in sorted(rows):
    fore, side, analysed, best, folder = rows[key]
    v = corrected(folder, fore, side)
    # (Only the PPSh-41's was checked in a picture: the others are the scan's line one step inside its edge, said so; sweep.)
    how = ('one step inside the search edge (the edge unchecked; counts in reports/maglines/body_both.txt)' if folder in EDGE_INSIDE and folder in MANUAL
           else 'set by hand from a picture' if folder in MANUAL
           else f'{analysed} -> {best} points in the walls or out of the well' if sys.argv[1] == '--both'
           else f'{analysed} -> {best} points out of place')
    # Back into Unity's space: the export writes glTF's right-handed vectors (SandboxExport's Vec: x, y, -z). Written as they were,
    # every line was mirrored in z in game, 134-178 degrees off the analysed line (the game's log, 2026-10-20 night): the
    # magazine's tip and base swapped, its line up through the receiver, its grab zones there in the way of the M4A1s' rack.
    lines.append(f'            {{ {json.dumps(key, ensure_ascii=False)}, new Vector3({v[0]:.5f}f, {v[1]:.5f}f, {-v[2]:.5f}f) }}, // {folder}: {fore:+.1f} fore/aft, {side:+.1f} sideways from the export\'s line; {how}')
lines += ['        };', '    }', '}', '']
open(OUT, 'w', encoding='utf-8', newline='\r\n').write('\n'.join(lines))
print(f'{len(rows)} magazine lines written to {OUT}')
for key in sorted(rows):
    print(f'  {key}: {rows[key][0]:+.1f} fore/aft, {rows[key][1]:+.1f} sideways ({rows[key][2]} -> {rows[key][3]})')
