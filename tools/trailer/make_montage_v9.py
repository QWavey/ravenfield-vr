import json
import os
import sys

R = r'C:\Users\Flo\Downloads\NewApps\Ravenfield-VR'
sys.path.insert(0, os.path.join(R, 'tools', 'trailer'))
from make_montage import cut  # noqa: E402

T = os.path.join(R, 'tools', 'trailer')
v8 = json.load(open(os.path.join(T, 'montage-v8.json')))
takes = dict(v8['takes'])
# The two new runs (2026-11-09 evening): Coastline and Island, the beats with clean hands only.
takes.update({
    'c1': 'showcase/takes/montage-01-hk416-swap-coastline-20261109-2113',
    'c2': 'showcase/takes/montage-02-p220-rack-coastline-20261109-2113',
    'i1': 'showcase/takes/montage-01-m4a1-swap-island-20261109-2115',
    'i2': 'showcase/takes/montage-02-ars_glock_19-rack-island-20261109-2115',
    'i3': 'showcase/takes/montage-03-f2000-shotshelmet-island-20261109-2115',
    'i5': 'showcase/takes/montage-05-glock_43x-aimshots-island-20261109-2115',
})
# (Without the Canyon MP9's and the Island Glock 19's racks: their off-hand sleeves spiked; the Coastline HK416's swap instead.)
order = [('swap-full', 'd1'), ('rack', 'c2'), ('shots3', 't3'), ('swap', 'i1'), ('aim', 't5'), ('shots', 'i3'),
         ('swap', 't6'), ('aim', 'i5'), ('swap', 'c1'), ('swap', 'k6'), ('shots', 'k3'), ('aim-last', 'k5')]
shots = []
for kind, t in order:
    shots += [{"take": t, "shot": k, "pre": 0, "post": 14} for k in range(3)] if kind == 'shots3' else cut(kind, t)
used = {t for _, t in order}
shots.append({"card": "RAVENFIELD VR", "seconds": 1.6, "tail": 1.2})
json.dump({"fps": 60, "takes": {k: v for k, v in takes.items() if k in used}, "shots": shots, "grade": True},
          open(os.path.join(T, 'montage-v9.json'), 'w'), indent=1)
print('ok', len(order), 'beats')
