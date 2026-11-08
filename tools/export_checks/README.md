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
| `mag_line_table.py` | Writes `src/Weapons/MagLineTable.cs`. In use: `--both` over `mag_line_body.py --both`: the guns with a detachable magazine whose least line (walls plus out of the outline) is at most 60% of the analysed line's and 30 points fewer, under 1000 left, not on the search's edge unless in `EDGE_OK` (looked at with `mag_line_draw.py`); `MANUAL` lines set by hand from pictures. The older mode reads scan outputs (their lines went through the well's walls: a point pushed into the gun counts as hidden there). The mod applies the table after its analysis and the export. | `python mag_line_table.py --both <body output>` |
| `mag_line_draw.py` | A side view (or `front`, across the gun): the gun's outline (grey), the magazine seated (blue) and slid out (green), its points beside the outline (red). | `python mag_line_draw.py <gun dir> <fraction 0-1> <out.png> [fore/aft] [sideways] [front]` |
| `mag_line_body.py` | A line against the gun's walls: the magazine slid out by 5-30% of its length, its points on the gun's body but off its own seated footprint (widened 1 mm), side and front views; `--both` adds the scans' out-of-the-outline count and searches the tilt (fore/aft 18, sideways 6) with the least of the two summed, for every gun of the export. | `python mag_line_body.py <gun dir> [fore/aft] [sideways]`, `--table`, `--search`, `--both [gun dirs]` |
| `mag_line_table_check.py` | Checks `src/Weapons/MagLineTable.cs` against the export in Unity's space (the export is glTF's: x, y, -z): each line a unit vector, its stated tilt off the analysed line, keyed by its folder's key. Run it after every regeneration: the table once went out mirrored in z (134-178 degrees off in game). Exit code 1 on a wrong line. | `python mag_line_table_check.py` |
| `still_bolts.py` | Self-loading guns with a part named as a bolt or carrier outside the slide's, bolt's and handle's subtrees: a bolt the rack leaves still (Tasks 307: the MG3s' bolt, the K13s' handle; the rest false alarms). | `python still_bolts.py [gun dirs]` |
| `parent_handle_rule.py` | The rule that takes a slide's holder drawing an AR charging handle of its own as the mesh the T is cut out of (the K13s), over the export: which guns it takes (the seven K13s and Suzuya's two AR-15s). | `python parent_handle_rule.py` |
| `whole_t_rule.py` | The second pass of ArStaticHandle (a whole flat handle, T and shaft, 12-22 cm) over the export: which guns it takes (the three M4A1 [SQUAD]s; the M417A2 at 1.5 cm left out by the 1.3 cm limit). | `python whole_t_rule.py` |
| `hand_points.py` | Grip and support points against the drawn model: those more than 5 cm off it (a hand in the air). Over the export only throwables, melee, sub-launchers and bot variants. | `python hand_points.py [gun dirs]` |
| `mag_axis_audit.py` | Every self-loader's long magazine: its analysed line against its own long axis and its chord, worst first. | `python mag_axis_audit.py` |
| `bolt_carrier_rule.py` | The bolt-carrier rule (WeaponAnalyzer.ArBoltCarrier) over every self-loading long gun: does it pick the known bolt, and what does it pick on the guns with nothing found to move. | `python bolt_carrier_rule.py` |
| `magazine_pieces_rule.py` | Three magazine faults (sweep of 2026-10-29): drawn copies of the magazine away from its seat (the K13's spare), pieces under the magazine apart from its body (the PKM family's and MK46's belt links), and the analysed box off the drawn magazine (the ARS guns, the G BRN180 EO SS; corrected in game from the copy). An export made after the fixes should list the first two no more. | `python magazine_pieces_rule.py [gun dirs]` |
| `reload_helper_rule.py` | The reload helpers (a part the reload moves that is hidden at rest: the M4A1 family's second 'zZ008 MAG') and which the held gun hides: only one named as ammunition, its spaces taken out too (the P220's 'dan    xia'), never an optic. On an export made after the rule each gun's note must agree (exit code 1 when one does not). | `python reload_helper_rule.py [gun dirs]` |
| `hold_points_audit.py` | How far each firearm's grip point and support point (where the hands hold it) lie from its drawn surface, worst first. The grip point is the palm's centre: 1-3 cm off is normal, over 4 cm is worth a look. | `python hold_points_audit.py [gun dir ...]` |

`<gun dir>` is a folder name under `reports/sandbox/data/weapons` (for example `vector_vector`). The audits over the whole
export take a few seconds.

What they found (task 293, 269 and 270 in Tasks.md): the 1911's line is clean offline; 108 of 287 guns keep their meshes
only on the graphics card, so the analysis takes a magazine's upright box for its axis (the Vector family's raked magazine,
18 degrees off; the runtime fit covers it); the bolt-carrier rule picks the known bolt on 6 of 6 guns where it picks
anything; the three "Bot Friendly" ARS guns' bore and estimated grip ran 5-7 cm beside the gun (their hand held air 7-12 cm
off the grip; fixed in WeaponAnalyzer.PullMuzzleOntoBarrel).

## Source checks (no export needed)

- `stacked_docs.py --brief`: a doc comment pushed onto the wrong member by an insertion.
- `unused_private.py`: private members nothing uses.
- `enum_switches.py [--all]`: switches and tables over the mod's enums that name at least half of an enum's values but not all, with no `default` (a value added without its row fell through: `MechSound.HandGrab` had no cut table). Known and meant: the Game preset has no profile (`GraphicsPresets.Profiles`), the intro's `Done` phase has nothing to do.
