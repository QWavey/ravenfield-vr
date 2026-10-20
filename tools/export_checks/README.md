# Export checks

Offline checks over the sandbox export (`reports/sandbox/data/weapons/<gun>/rig.json` + `model.glb`, written by
`tools/sandbox/export.py`). They run on the CPU, with Python and numpy (PIL for the drawing), and never start the game,
so they work while the game is in use. Each reads the export through `tools/sandbox/app/glb.py`.

The export's `rig.json` is the analysis as it stands before anything the game does at runtime (the magazine line fitted
to the drawn gun, task 270, comes after it), so a fault here can still be fixed in game. Check the game's log for the
runtime line before changing the analysis.

| Script | What it checks | Usage |
|---|---|---|
| `mag_line_scan.py` | A magazine slid out along its line to five depths against the gun's side outline and thickness: points that show beside the well and points through the grip's panels, then the best tilt fore/aft and sideways. | `python mag_line_scan.py <gun dir> [more...]` |
| `mag_line_wide.py` | `mag_line_scan.py` over a wider search (fore/aft to 30 degrees, sideways to 12), with the best within a plausible tilt (fore/aft 18, sideways 6) as `best tip (capped)`. The smallest tilt among the near-best is taken: the side outline cannot see a magazine hidden inside the receiver, so steep lines look perfect. | `python mag_line_wide.py <gun dir> [more...]` |
| `mag_line_table.py` | Writes `src/Weapons/MagLineTable.cs` from scan outputs: the guns whose capped best line is clearly better (at most 55% of the points out of place, 15 fewer), lines on the cap only for folders listed in `CHECKED` (looked at with `mag_line_draw.py`). The mod applies the table after its analysis and the export. | `python mag_line_table.py <scan output> [more...]` |
| `mag_line_draw.py` | A side view: the gun's outline (grey), the magazine seated (blue) and slid out (green), its points beside the outline (red). | `python mag_line_draw.py <gun dir> <fraction 0-1> <out.png> [fore/aft] [sideways]` (a tilted line) |
| `mag_axis_audit.py` | Every self-loader's long magazine: its analysed line against its own long axis and its chord, worst first. | `python mag_axis_audit.py` |
| `bolt_carrier_rule.py` | The bolt-carrier rule (WeaponAnalyzer.ArBoltCarrier) over every self-loading long gun: does it pick the known bolt, and what does it pick on the guns with nothing found to move. | `python bolt_carrier_rule.py` |
| `hold_points_audit.py` | How far each firearm's grip point and support point (where the hands hold it) lie from its drawn surface, worst first. The grip point is the palm's centre: 1-3 cm off is normal, over 4 cm is worth a look. | `python hold_points_audit.py [gun dir ...]` |

`<gun dir>` is a folder name under `reports/sandbox/data/weapons` (for example `vector_vector`). The audits over the whole
export take a few seconds.

What they found (task 293, 269 and 270 in Tasks.md): the 1911's line is clean offline; 108 of 287 guns keep their meshes
only on the graphics card, so the analysis takes a magazine's upright box for its axis (the Vector family's raked magazine,
18 degrees off; the runtime fit covers it); the bolt-carrier rule picks the known bolt on 6 of 6 guns where it picks
anything; the three "Bot Friendly" ARS guns' bore and estimated grip ran 5-7 cm beside the gun (their hand held air 7-12 cm
off the grip; fixed in WeaponAnalyzer.PullMuzzleOntoBarrel).
