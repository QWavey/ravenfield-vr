using System.Collections.Generic;
using RavenfieldVR.Core;
using RavenfieldVR.Rig;
using RavenfieldVR.Runtime;
using UnityEngine;
using UnityEngine.Rendering;

namespace RavenfieldVR.Weapons
{
    // MagazineProp, part: its model: made from the weapon's own magazine, shell or rocket (copied, baked, cut from the
    // arms' bones) or a stand-in.
    // The class, its shape and section, dropping and lifetime: MagazineProp.cs.
    public sealed partial class MagazineProp
    {
        /// <summary>Creates a prop for <paramref name="rig"/>'s ammunition at the magazine's seated pose.</summary>
        public static MagazineProp Create(WeaponRig rig, PropKind kind, int rounds)
        {
            // Timed on its own in a profiled run (a 300 ms call in the weapons' render step was traced to a machine gun's
            // first box; 2026-10-21).
            if (!Core.PerfProbe.Enabled)
            {
                return CreateNow(rig, kind, rounds);
            }
            Core.PerfProbe.Begin("magazine prop made");
            try
            {
                return CreateNow(rig, kind, rounds);
            }
            finally
            {
                Core.PerfProbe.End("magazine prop made");
            }
        }

        private static MagazineProp CreateNow(WeaponRig rig, PropKind kind, int rounds)
        {
            WeaponRigData d = rig.Data;
            Axes(d, kind, out Vector3 insertGun, out Vector3 frontGun);
            Quaternion seatInv = Runtime.RigidPose.Conjugate(d.MagSeat.rotation);
            Vector3 insertLocal = seatInv * insertGun;
            Vector3 frontLocal = seatInv * frontGun;

            GameObject go = null;
            Bounds bounds = d.MagBounds;
            string fitKey = null;
            if (CopiesModel(rig, kind))
            {
                go = CheckedCopy(rig, kind, rounds > 0, out bounds);
                fitKey = go != null ? "mag:" + d.Key : null;
                if (go == null)
                {
                    bounds = d.MagBounds;
                }
                else if (kind == PropKind.Magazine)
                {
                    AdoptCopyBounds(d, bounds);
                }
            }
            if (go == null && kind == PropKind.Rocket)
            {
                go = ProjectileCopy(rig, insertLocal, frontLocal, out bounds);
            }
            bool standIn = go == null;
            if (go == null)
            {
                go = new GameObject("RavenfieldVR " + kind);
                bounds = BuildProcedural(go, kind, insertLocal, frontLocal, d.MagBounds, ShotgunShells(rig));
            }
            // What the hand holds, once per weapon and kind: the gun's own part copied or a made stand-in, and which part
            // (a machine gun's box was invisible in the hand, or only its belt came out; Tasks 252 n). The message only the first
            // time: built on every magazine (every reload), with a walk of the copy's renderers, it was garbage each time.
            string kindKey = "propkind:" + kind + ":" + d.Key;
            if (!Log.Logged(kindKey))
            {
                Log.InfoOnce(kindKey, $"{kind} of {d.Key} in the hand: {(standIn ? "a made stand-in" : "a copy of the gun's own")} "
                    + $"(part '{(rig.Magazine != null ? rig.Magazine.name : "none")}', {rig.MagazineParts.Length} part(s) with it), {bounds.size.x * 100f:0} x {bounds.size.y * 100f:0} x {bounds.size.z * 100f:0} cm"
                    + $"{(standIn ? "" : $", {go.GetComponentsInChildren<Renderer>(true).Length} renderer(s)")}.");
            }
            go.name = "RavenfieldVR " + kind;
            SetLayer(go, HeldLayer);
            var prop = new MagazineProp(go, kind, d.Key, rounds, bounds, insertLocal, frontLocal, d.IsMachineGun, fitKey);
            prop.Data = d;
            prop.RockAngle = kind == PropKind.Magazine && d.MagCurved && !d.IsMachineGun && !d.IsPistol ? 14f : 0f;
            prop._pistolGun = d.IsPistol;
            if (kind == PropKind.Rocket)
            {
                // Its rail runs from the launcher's mouth to where its tail sits seated: lined up at the muzzle, in front of
                // the launcher, where a rocket is put in. It began a few tenths of the rocket's length back from the seat,
                // inside the tube, and a rocket held at the muzzle was never taken.
                // The mouth is the tube's front end (the launcher's own meshes, not its rocket): its "muzzle" point is where
                // the projectile starts, 1.1 m in front of the grip on an RPG-7, past the warhead.
                Vector3 tailSeated = d.MagSeat.TransformPoint(prop.TipLocal);
                Vector3 aim = d.AimAxis.normalized;
                bool tube = TubeFront(rig, aim, out float front);
                float mouth = tube ? front : Vector3.Dot(d.MuzzlePoint, aim);
                float depth = mouth - Vector3.Dot(tailSeated, aim);
                if (depth > 0.05f && depth < 1.8f)
                {
                    prop.RailLength = depth + 0.01f;
                }
                string railKey = "rocketrail:" + d.Key;
                if (!Log.Logged(railKey))
                {
                    Log.InfoOnce(railKey, $"Rocket rail of {d.Key}: {prop.RailLength * 100f:0} cm from the {(tube ? "tube's front" : "muzzle point")} to the seated tail "
                                          + $"({depth * 100f:0} cm measured), along the rocket's length {Vector3.Angle(insertGun, -aim):0.0} deg off the aim.");
                }
            }
            RigidPose seat = rig.GunBodyPose.Multiply(d.MagSeat);
            seat.ApplyTo(go.transform);
            Cull();
            return prop;
        }

        /// <summary>Visual-only copy of what the weapon feeds from, in magazine space (for the pouch display).</summary>
        internal static GameObject BuildDisplay(WeaponRig rig, PropKind kind, out Bounds bounds, out Vector3 insertLocal, out Vector3 frontLocal, bool loaded = true)
        {
            WeaponRigData d = rig.Data;
            Quaternion seatInv = Runtime.RigidPose.Conjugate(d.MagSeat.rotation);
            Axes(d, kind, out Vector3 insertGun, out Vector3 frontGun);
            insertLocal = (seatInv * insertGun).normalized;
            frontLocal = seatInv * frontGun;
            GameObject go = null;
            bounds = d.MagBounds;
            if (CopiesModel(rig, kind))
            {
                go = CheckedCopy(rig, kind, loaded, out bounds); // (the pouch shows full ones, empty ones when it hands those out)
                if (go == null)
                {
                    bounds = d.MagBounds;
                }
                else if (kind == PropKind.Magazine)
                {
                    AdoptCopyBounds(d, bounds); // (built as the gun is drawn: the well is right before the first magazine is taken)
                }
            }
            if (go == null && kind == PropKind.Rocket)
            {
                go = ProjectileCopy(rig, insertLocal, frontLocal, out bounds);
            }
            if (go == null)
            {
                go = new GameObject("RavenfieldVR Pouch " + kind);
                bounds = BuildProcedural(go, kind, insertLocal, frontLocal, d.MagBounds, ShotgunShells(rig));
            }
            go.name = "RavenfieldVR Pouch " + kind;
            SetLayer(go, HeldLayer);
            foreach (Collider c in go.GetComponentsInChildren<Collider>(true))
            {
                Object.Destroy(c);
            }
            return go;
        }

        internal static int DisplayLayer => HeldLayer;

        /// <summary>
        /// The gun takes its own magazine copy's box when the two clearly disagree (their middles 2 cm or more apart, or the longest
        /// side off by a third): the magazine in the hand is lined up by the copy's box, while the well's keep-out, the grab on a
        /// seated magazine and a belt's mouth went by the analysed one, so a magazine lined up in one place and seated in another
        /// (user, 2026-10-29: "the mag insertion being on the wrong place but on the real when inserted"). In every logged case the
        /// copy was the right one: the PKP's analysed box held its belt links, the G BRN180 EO SS's was 44 cm long for a 22 cm
        /// magazine, the ARS guns' skinned magazines measured 13.6 cm across for a 21 cm one. Both boxes are in the magazine part's
        /// own frame. Said once per gun.
        /// </summary>
        private static void AdoptCopyBounds(WeaponRigData d, Bounds copy)
        {
            // Once, on the first copy (the pouch's, at the draw: fully drawn): each later one compared with the last adopted box, a
            // partly drawn copy at 0.4 of it passed and shrank it, and every loaded copy after was refused as too big (review).
            if (d.MagBoundsChecked)
            {
                return;
            }
            d.MagBoundsChecked = true;
            Bounds analysed = d.MagBounds;
            float copyLong = Longest(copy), analysedLong = Longest(analysed);
            if (copyLong < 0.02f || analysedLong < 1e-4f)
            {
                return;
            }
            float apart = (copy.center - analysed.center).magnitude;
            if (apart < 0.02f && copyLong <= analysedLong * 1.33f && copyLong >= analysedLong * 0.75f)
            {
                return;
            }
            Log.InfoOnce("magbounds-adopt:" + d.Key, $"{d.Key}: its magazine is measured by the copy in the hand from now on ({copyLong * 100f:0} cm long against "
                                                   + $"the analysed {analysedLong * 100f:0}, their middles {apart * 100f:0.0} cm apart): the well, the seated grab and the "
                                                   + "belt's mouth go by what is drawn.");
            d.MagBoundsAnalysed = analysed;
            d.MagBoundsAdopted = true;
            d.MagBounds = copy;
        }

        /// <summary>
        /// The copy of the gun's own magazine (or rocket), checked against the analysis. A copy taken while the game had
        /// the part parked and shrunk for its reload (a BRN 180's magazine waits 25 m away at 1%) came out tiny and showed as
        /// nothing in the hand: scaled back to the analysed size, it is kept if it then sits where the magazine was
        /// measured; otherwise (or with nothing in it drawn, or a "rocket" too short to be one) the made one is used.
        /// </summary>
        private static GameObject CheckedCopy(WeaponRig rig, PropKind kind, bool loaded, out Bounds bounds)
        {
            WeaponRigData d = rig.Data;
            bounds = d.MagBounds;
            // A magazine with rounds in it: as the gun's looked loaded (see LoadedMags), not as it shows now; an empty one as the gun's
            // looked empty (EmptyMags), when it has been seen so.
            GameObject go = kind == PropKind.Rocket ? LoadedLook(d.Key) : kind == PropKind.Magazine ? Kept(loaded ? LoadedMags : EmptyMags, d.Key) : null;
            if (go != null && kind == PropKind.Magazine)
            {
                if (DrawsAnything(go))
                {
                    bounds = RenderedBounds(go, d.MagBounds, d.Key);
                    return go;
                }
                Object.Destroy(go); // (drawing nothing: as it shows now, below)
                go = null;
            }
            if (go != null)
            {
                if (DrawsAnything(go))
                {
                    bounds = RenderedBounds(go, d.MagBounds, d.Key);
                    return go;
                }
                // A kept copy that draws nothing is not handed out: the hand held an invisible rocket (Tasks 205). The gun's
                // own parts below, or the made one, are the choices after it.
                Log.InfoOnce("rocketlook-hidden:" + d.Key, $"Rocket of {d.Key}: the copy kept as it sits loaded draws nothing in the hand; the gun's own part or the made one is used.");
                Object.Destroy(go);
                go = null;
            }
            // A full one with no loaded look kept yet (the gun not yet seen settled with a full magazine, as one taken from a soldier
            // nearly empty): the round-sized pieces the gun has switched off now go in too (RoundSized), or a see-through magazine's
            // rounds were left out and a full magazine looked empty in the hand (the G BRN180 EO S, 2026-11-03: 32 of its 62 pieces).
            go = CopyVisuals(rig, roundsToo: kind == PropKind.Magazine && loaded);
            if (go == null)
            {
                return null;
            }
            bool drawn = false;
            foreach (Renderer r in go.GetComponentsInChildren<Renderer>(true))
            {
                drawn |= r.enabled && r.transform.lossyScale.sqrMagnitude > 1e-8f;
            }
            float expected = d.MagBounds.size.magnitude;
            Bounds raw = MeasureCopy(go, out bool any);
            if (drawn && any && expected > 0.02f && raw.size.magnitude > 1e-4f
                && (raw.size.magnitude < expected / 3f || raw.size.magnitude > expected * 3f))
            {
                float k = expected / raw.size.magnitude;
                foreach (Transform child in go.transform)
                {
                    child.localPosition *= k;
                    child.localScale *= k;
                }
                raw = MeasureCopy(go, out any);
                bool placed = (raw.center - d.MagBounds.center).magnitude < 0.03f;
                Log.InfoOnce("magrescale:" + d.Key, $"Magazine copy of {d.Key} came out x{1f / k:0.###} of the analysed size (the part was parked or shrunk): "
                                                     + $"scaled back, {(placed ? "kept" : $"but {((raw.center - d.MagBounds.center).magnitude * 100f):0.0} cm off its place: the made one is used")}.");
                if (!placed)
                {
                    drawn = false;
                }
            }
            if (!drawn || !any)
            {
                Object.Destroy(go);
                return null;
            }
            bounds = RenderedBounds(go, d.MagBounds, d.Key);
            if (kind == PropKind.Rocket && Longest(bounds) < 0.12f || kind == PropKind.Shell && !WeaponAnalyzer.ShellSized(bounds))
            {
                Object.Destroy(go); // not a rocket (a fuse, a cap), not a shell: the made one instead
                Log.InfoOnce("copytoosmall:" + kind + ":" + d.Key, $"{d.Key}: its own {kind.ToString().ToLowerInvariant()} copy measures {Longest(bounds) * 100f:0} cm, "
                                                              + "not one: a made stand-in is held instead."); // (said, as the other fallbacks are; sweep)
                return null;
            }
            return go;
        }

        /// <summary>
        /// True when some renderer of the copy is on, in an active object, and has a size (a copy scaled to nothing, or
        /// switched off, draws nothing).
        /// </summary>
        private static bool DrawsAnything(GameObject go)
        {
            foreach (Renderer r in go.GetComponentsInChildren<Renderer>(true))
            {
                if (r.enabled && r.gameObject.activeInHierarchy && r.transform.lossyScale.sqrMagnitude > 1e-8f)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Each launcher's rocket as it looks seated and loaded (key: the weapon), copied while it shows so. A rocket from the
        /// pouch comes after the shot, and by then the game has shrunk the launcher's rocket to a few percent and moved it
        /// half a metre (the five RPG-7V variants, the scoped RPG-7V): that copy failed its check and the hand held the made
        /// olive stand-in instead of the launcher's own warhead (user, 2026-10-16).
        /// </summary>
        private static readonly Dictionary<string, GameObject> LoadedLooks = new Dictionary<string, GameObject>();
        private static readonly Dictionary<string, float> LoadedLookTried = new Dictionary<string, float>();
        private static readonly Dictionary<string, int> LoadedLookTries = new Dictionary<string, int>();
        private static GameObject _loadedLooksHolder;

        /// <summary>
        /// Each gun's magazine as it looks with rounds in it (key: the weapon), copied while it sits loaded in the gun. A magazine
        /// from the pouch is a copy of the gun's own as it shows when taken, and a gun shot empty shows its magazine empty (a mod's
        /// hides its top rounds in the empty pose, or swaps in an empty model): the full magazine in the hand looked empty (user,
        /// 2026-10-25). An empty one taken out of the gun is copied as it shows (empty).
        /// </summary>
        private static readonly Dictionary<string, GameObject> LoadedMags = new Dictionary<string, GameObject>();
        private static readonly Dictionary<string, float> LoadedMagTried = new Dictionary<string, float>();
        private static readonly Dictionary<string, int> LoadedMagTries = new Dictionary<string, int>();

        /// <summary>
        /// Each gun's magazine as it looks empty (key: the weapon), copied while it sits empty in the gun shot dry. An empty magazine
        /// is otherwise a copy of the gun's own as it shows when taken: with the gun still holding a full one (an empty one from the
        /// pouch with magazines loaded by hand), it showed the full one's rounds, empty as it was.
        /// </summary>
        private static readonly Dictionary<string, GameObject> EmptyMags = new Dictionary<string, GameObject>();
        private static readonly Dictionary<string, float> EmptyMagTried = new Dictionary<string, float>();
        private static readonly Dictionary<string, int> EmptyMagTries = new Dictionary<string, int>();

        /// <summary>
        /// While a gun with a loaded magazine in it is held (outside a reload): a copy of its magazine as it shows now, kept when it
        /// measures as the analysis did (half to double the size, its middle within 3 cm). Every two seconds, ten tries at most.
        /// </summary>
        internal static void KeepLoadedMagazine(WeaponRig rig) => KeepMagazineLook(rig, true);

        /// <summary>The same while the gun sits shot dry with its empty magazine in (<see cref="EmptyMags"/>).</summary>
        internal static void KeepEmptyMagazine(WeaponRig rig) => KeepMagazineLook(rig, false);

        private static void KeepMagazineLook(WeaponRig rig, bool loaded)
        {
            string key = rig.Data.Key;
            Dictionary<string, GameObject> looks = loaded ? LoadedMags : EmptyMags;
            Dictionary<string, float> triedAt = loaded ? LoadedMagTried : EmptyMagTried;
            Dictionary<string, int> triesOf = loaded ? LoadedMagTries : EmptyMagTries;
            if (looks.ContainsKey(key) || rig.Weapon == null || rig.Weapon.reloading || !CopiesModel(rig, PropKind.Magazine)
                || triesOf.TryGetValue(key, out int tries) && tries >= 10
                || triedAt.TryGetValue(key, out float tried) && Time.unscaledTime < tried + 2f)
            {
                return;
            }
            triedAt[key] = Time.unscaledTime;
            triesOf[key] = tries + 1;
            string how = loaded ? "loaded" : "empty";
            GameObject go = CopyVisuals(rig);
            if (go == null)
            {
                return;
            }
            Bounds analysed = rig.Data.MagBounds;
            Bounds b = MeasureCopy(go, out bool any);
            float size = b.size.magnitude, expected = analysed.size.magnitude;
            if (!any || expected >= 0.02f && (size < expected / 2f || size > expected * 2f || (b.center - analysed.center).magnitude > 0.03f))
            {
                Log.InfoOnce("maglook-no:" + how + ":" + key, $"Magazine of {key} not kept as it looks {how} (try {tries + 1}): {(any ? $"{size * 100f:0} cm across against {expected * 100f:0} analysed, middle {(b.center - analysed.center).magnitude * 100f:0.0} cm off" : "nothing drawn")}.");
                Object.Destroy(go);
                return;
            }
            if (_loadedLooksHolder == null)
            {
                _loadedLooksHolder = new GameObject("RavenfieldVR loaded rockets");
                _loadedLooksHolder.SetActive(false); // kept, never drawn
                Object.DontDestroyOnLoad(_loadedLooksHolder);
            }
            go.transform.SetParent(_loadedLooksHolder.transform, false);
            go.name = "magazine " + how + " " + key;
            looks[key] = go;
            Log.InfoOnce("maglook:" + how + ":" + key, $"Magazine of {key} kept as it looks {how}: {go.GetComponentsInChildren<Renderer>(true).Length} renderer(s), {size * 100f:0} cm across.");
        }

        /// <summary>Self-check: whether a loaded look of this gun's magazine is kept.</summary>
        internal static bool KeepsLoadedLook(string key) => LoadedMags.ContainsKey(key);

        /// <summary>Whether an empty look of this gun's magazine is kept (<see cref="EmptyMags"/>).</summary>
        internal static bool KeepsEmptyLook(string key) => EmptyMags.ContainsKey(key);

        /// <summary>Self-check: how many renderers of a prop's model draw (on, active, with a size).</summary>
        internal static int DrawnCount(MagazineProp prop)
        {
            int n = 0;
            if (prop != null && prop.Go != null)
            {
                foreach (Renderer r in prop.Go.GetComponentsInChildren<Renderer>(true))
                {
                    n += r.enabled && r.gameObject.activeInHierarchy && r.transform.lossyScale.sqrMagnitude > 1e-8f ? 1 : 0;
                }
            }
            return n;
        }

        /// <summary>The last look at each launcher's loaded rocket (gun space) while its far seat waits to be taken over.</summary>
        private static readonly Dictionary<string, (RigidPose pose, float at)> SeatLooks = new Dictionary<string, (RigidPose pose, float at)>();

        /// <summary>
        /// While a loaded launcher is held: a copy of its rocket as it shows now, kept when it measures as the analysis did
        /// (its size between half and double, its middle within 8 cm; rocket-sized when the analysis found no box). Tried at most
        /// twice a second for the first five tries, then every ten seconds, until one is kept; thirty tries at most (a copy
        /// is meshes made and thrown away: a launcher whose rocket never measures right would make them for as long as it is
        /// held; ten tries half a second apart were all spent in the first five seconds).
        /// </summary>
        internal static void KeepLoadedLook(WeaponRig rig)
        {
            string key = rig.Data.Key;
            WeaponRigData d = rig.Data;
            if (rig.Magazine != null && rig.GunBody != null && (d.MagSeat.position - d.GripPoint).sqrMagnitude > 1.5f * 1.5f)
            {
                // The analysed rest pose had the rocket parked away (the Panzerfaust 3's warhead waits 30 m off in its empty
                // idle, and its rocket was "seated" there: taken from the back, a 30 m rail). Loaded, it sits on the gun now.
                RigidPose live = rig.GunBodyPose.Inverse().Multiply(RigidPose.Of(rig.Magazine));
                // Taken only from a still pose outside the reload: two looks a third of a second apart within 1 cm and 1
                // degree. One frame mid-animation went into the weapon's shared data for every rig of it (review 253.13).
                bool still = false;
                if (!rig.Weapon.reloading && (live.position - d.GripPoint).sqrMagnitude < 1.5f * 1.5f)
                {
                    if (SeatLooks.TryGetValue(key, out (RigidPose pose, float at) seen) && Time.unscaledTime - seen.at >= 0.3f)
                    {
                        still = Vector3.Distance(seen.pose.position, live.position) < 0.01f && Quaternion.Angle(seen.pose.rotation, live.rotation) < 1f;
                        SeatLooks[key] = (live, Time.unscaledTime);
                    }
                    else if (!SeatLooks.ContainsKey(key))
                    {
                        SeatLooks[key] = (live, Time.unscaledTime);
                    }
                }
                if (still)
                {
                    Log.InfoOnce("rocketseat:" + key, $"Rocket of {key}: seated where it sits loaded, {(live.position - d.GripPoint).magnitude * 100f:0} cm from the grip "
                                                      + $"(the analysis had it {(d.MagSeat.position - d.GripPoint).magnitude:0.0} m off).");
                    d.MagSeat = live;
                }
            }
            if (LoadedLooks.ContainsKey(key) || !CopiesModel(rig, PropKind.Rocket)
                || LoadedLookTries.TryGetValue(key, out int tries) && tries >= 30
                || LoadedLookTried.TryGetValue(key, out float tried) && Time.unscaledTime < tried + (tries < 5 ? 0.5f : 10f))
            {
                return;
            }
            LoadedLookTried[key] = Time.unscaledTime;
            LoadedLookTries[key] = tries + 1;
            GameObject go = CopyVisuals(rig);
            if (go == null)
            {
                return;
            }
            Bounds analysed = rig.Data.MagBounds;
            Bounds b = MeasureCopy(go, out bool any);
            float size = b.size.magnitude, expected = analysed.size.magnitude;
            // Refused: a copy drawn too small or too big (12 cm to 2 m long), or shrunk or moved away from what the analysis saw
            // (after the shot the game left it at 3% and up to 60 cm off): kept between half and double the size and within
            // 8 cm (at half again and 3 cm, the RPG-7V [PG-7VR]'s and the Panzerfaust's were never kept).
            bool fits = any && Longest(b) >= 0.12f && Longest(b) < 2f
                        && (expected < 0.02f || size > expected / 2f && size < expected * 2f && (b.center - analysed.center).magnitude < 0.08f);
            if (!fits)
            {
                Log.InfoOnce("rocketlook-no:" + key, $"Rocket of {key} not copied as it sits loaded (try {tries + 1}): {(any ? $"{Longest(b) * 100f:0} cm long, {size * 100f:0} cm across against {expected * 100f:0} analysed, middle {(b.center - analysed.center).magnitude * 100f:0.0} cm off" : "nothing drawn")}.");
                Object.Destroy(go);
                return;
            }
            if (_loadedLooksHolder == null)
            {
                _loadedLooksHolder = new GameObject("RavenfieldVR loaded rockets");
                _loadedLooksHolder.SetActive(false); // kept, never drawn
                Object.DontDestroyOnLoad(_loadedLooksHolder);
            }
            go.transform.SetParent(_loadedLooksHolder.transform, false);
            go.name = "rocket " + key;
            LoadedLooks[key] = go;
            Log.InfoOnce("rocketlook:" + key, $"Rocket of {key} copied as it sits loaded: {go.GetComponentsInChildren<MeshFilter>(true).Length} mesh(es), {Longest(b) * 100f:0} cm long.");
        }

        /// <summary>
        /// A launcher that shows no rocket of its own (a missile tube: HYDRA, SCALPEL, a signal launcher): the rocket in the
        /// hand is the projectile it fires, its own meshes, seated inside the tube with its nose at the mouth. It was a
        /// made-up 75 cm olive capsule. Null when the projectile has no mesh to show or is not rocket-sized.
        /// </summary>
        private static GameObject ProjectileCopy(WeaponRig rig, Vector3 insertLocal, Vector3 frontLocal, out Bounds bounds)
        {
            WeaponRigData d = rig.Data;
            bounds = d.MagBounds;
            GameObject prefab = rig.Weapon != null && rig.Weapon.configuration != null ? rig.Weapon.configuration.projectilePrefab : null;
            if (prefab == null)
            {
                return null;
            }
            // Its meshes in its own space (it flies along its forward), without trails, flames or glow.
            Matrix4x4 toRoot = prefab.transform.worldToLocalMatrix;
            var parts = new List<(MeshRenderer r, Mesh mesh, Matrix4x4 m)>();
            bool any = false;
            var b = new Bounds();
            foreach (MeshRenderer r in prefab.GetComponentsInChildren<MeshRenderer>(true))
            {
                MeshFilter mf = r.GetComponent<MeshFilter>();
                if (!r.enabled || mf == null || mf.sharedMesh == null || GunCollider.SeeThrough(r) || !WeaponAnalyzer.ActiveUpTo(r.transform, prefab.transform))
                {
                    continue; // (switched off in the prefab: a flame or a glow it turns on in flight)
                }
                Matrix4x4 m = toRoot * r.transform.localToWorldMatrix;
                parts.Add((r, mf.sharedMesh, m));
                Bounds mb = mf.sharedMesh.bounds;
                for (int i = 0; i < 8; i++)
                {
                    Vector3 c = m.MultiplyPoint3x4(mb.center + Vector3.Scale(mb.extents, new Vector3((i & 1) == 0 ? -1f : 1f, (i & 2) == 0 ? -1f : 1f, (i & 4) == 0 ? -1f : 1f)));
                    if (!any)
                    {
                        b = new Bounds(c, Vector3.zero);
                        any = true;
                    }
                    else
                    {
                        b.Encapsulate(c);
                    }
                }
            }
            float length = b.size.z;
            if (!any || length < 0.12f || length > 2f || Mathf.Max(b.size.x, b.size.y) > length)
            {
                Log.InfoOnce("rocketprojectile:" + d.Key, $"Rocket of {d.Key}: its projectile {prefab.name} has {(any ? $"a {b.size.x * 100f:0} x {b.size.y * 100f:0} x {length * 100f:0} cm model, not a rocket's" : "no model")}: the made one is used.");
                return null;
            }
            // Into seat space: its forward along the bore (out of the tube), its up the launcher's up, its nose at the tube's
            // front on the bore's line through the seat.
            Vector3 aimLocal = -insertLocal.normalized;
            Quaternion q = Quaternion.LookRotation(aimLocal, frontLocal);
            Vector3 aimGun = d.AimAxis.normalized;
            float seatAlong = Vector3.Dot(d.MagSeat.position, aimGun);
            bool tube = TubeFront(rig, aimGun, out float front);
            float noseAlong = tube ? front - seatAlong : Vector3.Dot(d.MuzzlePoint, aimGun) - seatAlong;
            Vector3 nosePoint = new Vector3(b.center.x, b.center.y, b.max.z);
            // On the tube's own axis when the muzzle point is off it (within 15 cm).
            Vector3 across = Vector3.zero;
            if (tube && TubeCentre(rig, aimGun, front, out Vector3 tubeCentre))
            {
                Vector3 offGun = Vector3.ProjectOnPlane(tubeCentre - d.MagSeat.position, aimGun);
                if (offGun.magnitude < 0.15f)
                {
                    across = Runtime.RigidPose.Conjugate(d.MagSeat.rotation) * offGun;
                    if (offGun.magnitude > 0.01f)
                    {
                        Log.InfoOnce("rocketcentre:" + d.Key, $"Rocket of {d.Key}: on the tube's axis, {offGun.magnitude * 100f:0.0} cm off the muzzle point's line.");
                    }
                }
            }
            var root = new GameObject("rocket");
            foreach ((MeshRenderer r, Mesh mesh, Matrix4x4 m) in parts)
            {
                Vector3 p = aimLocal * noseAlong + across + q * (m.MultiplyPoint3x4(Vector3.zero) - nosePoint);
                Quaternion rot = q * m.rotation;
                AddPart(root.transform, r.name, mesh, r.sharedMaterials, p, rot, m.lossyScale);
            }
            bounds = MeasureCopy(root, out _); // its own size (the analysis had a 14 cm stand-in box for a missile tube)
            Log.InfoOnce("rocketprojectile:" + d.Key, $"Rocket of {d.Key}: its projectile's own model ({prefab.name}, {parts.Count} mesh(es), {length * 100f:0} cm), seated in the tube.");
            return root;
        }

        /// <summary>
        /// Drops the looks, seats and shapes kept by weapon name before a content reload (2026-10-19): the reloaded launcher is
        /// new, so its rocket is copied and its seat measured again. The kept rocket copies are destroyed with their keys.
        /// </summary>
        internal static void ClearLooks()
        {
            foreach (GameObject look in LoadedLooks.Values)
            {
                if (look != null)
                {
                    Object.Destroy(look);
                }
            }
            LoadedLooks.Clear();
            LoadedLookTried.Clear();
            LoadedLookTries.Clear();
            foreach (GameObject look in LoadedMags.Values)
            {
                if (look != null)
                {
                    Object.Destroy(look);
                }
            }
            LoadedMags.Clear();
            LoadedMagTried.Clear();
            LoadedMagTries.Clear();
            foreach (GameObject look in EmptyMags.Values)
            {
                if (look != null)
                {
                    Object.Destroy(look);
                }
            }
            EmptyMags.Clear();
            EmptyMagTried.Clear();
            EmptyMagTries.Clear();
            SeatLooks.Clear();
            BoneCuts.Clear(); // (kept, every content reload left its entries for the session)
            GripSections.Clear();
            // Their hull meshes retired, as the drops' are (WeaponDrops.ClearShapes): forgotten, they stayed in memory for the session,
            // one set per magazine type and reload (round 11). Destroyed at the next scene change once no collider uses them.
            foreach (ShapeFit.Piece[] pieces in RenderedShapes.Values)
            {
                ShapeFit.Retire(pieces);
            }
            RenderedShapes.Clear();
        }

        /// <summary>A new copy of the launcher's rocket as it looked loaded; null when none was kept (or its meshes are gone).</summary>
        private static GameObject LoadedLook(string key) => Kept(LoadedLooks, key);

        /// <summary>A new copy of a kept look (<see cref="LoadedLooks"/>, <see cref="LoadedMags"/>); null when none was kept (or its meshes are gone).</summary>
        private static GameObject Kept(Dictionary<string, GameObject> kept, string key)
        {
            if (!kept.TryGetValue(key, out GameObject look))
            {
                return null;
            }
            bool whole = look != null;
            if (whole)
            {
                foreach (MeshFilter mf in look.GetComponentsInChildren<MeshFilter>(true))
                {
                    whole &= mf.sharedMesh != null;
                }
            }
            if (!whole)
            {
                // Its meshes unloaded with their content pack: copied again the next time it shows loaded.
                if (look != null)
                {
                    Object.Destroy(look);
                }
                kept.Remove(key);
                return null;
            }
            GameObject go = Object.Instantiate(look);
            foreach (OwnedMesh owned in go.GetComponentsInChildren<OwnedMesh>(true))
            {
                if (owned.Mesh != null)
                {
                    // A baked mesh is the kept copy's own: this one gets its own, or dropping it would take the kept one's.
                    Mesh own = Object.Instantiate(owned.Mesh);
                    own.name = owned.Mesh.name;
                    MeshFilter mf = owned.GetComponent<MeshFilter>();
                    if (mf != null)
                    {
                        mf.sharedMesh = own;
                    }
                    owned.Mesh = own;
                }
            }
            return go;
        }

        /// <summary>The copy's meshes' bounds in its own space.</summary>
        private static Bounds MeasureCopy(GameObject root, out bool any)
        {
            Matrix4x4 toRoot = root.transform.worldToLocalMatrix;
            any = false;
            var b = new Bounds();
            foreach (MeshFilter mf in root.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf.sharedMesh == null)
                {
                    continue;
                }
                Bounds mb = mf.sharedMesh.bounds;
                Matrix4x4 m = toRoot * mf.transform.localToWorldMatrix;
                for (int i = 0; i < 8; i++)
                {
                    Vector3 corner = mb.center + Vector3.Scale(mb.extents, new Vector3((i & 1) == 0 ? -1f : 1f, (i & 2) == 0 ? -1f : 1f, (i & 4) == 0 ? -1f : 1f));
                    Vector3 p = m.MultiplyPoint3x4(corner);
                    if (!any)
                    {
                        b = new Bounds(p, Vector3.zero);
                        any = true;
                    }
                    else
                    {
                        b.Encapsulate(p);
                    }
                }
            }
            return b;
        }

        /// <summary>
        /// The bounds of what a copy actually shows, in its root's space. The analysis measures the magazine part alone at
        /// its idle pose; the copy also carries the pieces that travel with it (rounds, a follower) and skinned triangles
        /// baked from the gun's mesh, and a magazine held or displayed by the analysis box sat off its real middle (pistol
        /// magazines hung low in the pouch and in the hand). A copy measuring far from the analysis keeps the analysis.
        /// </summary>
        private static Bounds RenderedBounds(GameObject root, Bounds analysed, string key)
        {
            Matrix4x4 toRoot = root.transform.worldToLocalMatrix;
            bool any = false;
            var b = new Bounds();
            foreach (MeshFilter mf in root.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf.sharedMesh == null)
                {
                    continue;
                }
                Bounds mb = mf.sharedMesh.bounds;
                Matrix4x4 m = toRoot * mf.transform.localToWorldMatrix;
                for (int i = 0; i < 8; i++)
                {
                    Vector3 corner = mb.center + Vector3.Scale(mb.extents, new Vector3((i & 1) == 0 ? -1f : 1f, (i & 2) == 0 ? -1f : 1f, (i & 4) == 0 ? -1f : 1f));
                    Vector3 p = m.MultiplyPoint3x4(corner);
                    if (!any)
                    {
                        b = new Bounds(p, Vector3.zero);
                        any = true;
                    }
                    else
                    {
                        b.Encapsulate(p);
                    }
                }
            }
            float size = b.size.magnitude, expected = analysed.size.magnitude;
            if (!any || size < 0.02f || size > 1.5f || expected > 0.02f && (size > expected * 3f || size < expected / 3f))
            {
                Log.InfoOnce("magbounds-analysed:" + key, $"Magazine copy of {key} measures {size * 100f:0.0} cm against {expected * 100f:0.0} cm analysed; the analysed size is used.");
                return analysed;
            }
            if ((b.center - analysed.center).magnitude > 0.01f || Mathf.Abs(size - expected) > 0.01f)
            {
                // (A key of its own: sharing one with the line above, only whichever came first was ever logged for a gun.)
                Log.InfoOnce("magbounds-shown:" + key, $"Magazine copy of {key}: shown bounds centre {b.center * 100f} cm, size {size * 100f:0.0} cm (analysed {analysed.center * 100f} cm, {expected * 100f:0.0} cm).");
            }
            return b;
        }

        /// <summary>
        /// Copies the magazine's look: its own meshes, the pieces that travel with it, and for skinned guns the
        /// triangles of the gun mesh that the magazine bones drive. Everything is expressed in magazine space.
        /// </summary>
        private static GameObject CopyVisuals(WeaponRig rig, bool roundsToo = false)
        {
            // While the magazine is out of the gun its parts are scaled to nothing: copy them at their real size.
            Vector3[] hidden = rig.ShowMagazineForCopy();
            try
            {
                return CopyVisualsShown(rig, roundsToo);
            }
            finally
            {
                rig.RestoreMagazineScales(hidden);
            }
        }

        /// <summary>A piece further than this from the magazine is parked out of sight, not part of it.</summary>
        private const float ParkedAway = 1.5f;

        /// <summary>Under one of the gun model's own belt parts (one that does not hold the magazine itself): the tray's belt, not the box's.</summary>
        private static bool UnderBelt(Transform t, WeaponRig rig, Transform source)
        {
            foreach (Transform belt in rig.BeltParts)
            {
                if (belt != null && !source.IsChildOf(belt) && t.IsChildOf(belt))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// A piece the size of a round (a plain mesh 3 mm to 8 cm across as drawn): switched off on the gun, it is a round its magazine
        /// shows only while loaded; a second model of the magazine (an empty or a full one swapped in) is far bigger.
        /// </summary>
        private static bool RoundSized(Renderer r)
        {
            MeshFilter mf = r is MeshRenderer ? r.GetComponent<MeshFilter>() : null;
            if (mf == null || mf.sharedMesh == null)
            {
                return false;
            }
            float size = Vector3.Scale(mf.sharedMesh.bounds.size, r.transform.lossyScale).magnitude;
            return size > 0.003f && size < 0.08f;
        }

        private static GameObject CopyVisualsShown(WeaponRig rig, bool roundsToo = false)
        {
            int roundsAdded = 0;
            Transform source = rig.Magazine;
            var root = new GameObject("mag");
            bool any = false;
            Quaternion inv = Runtime.RigidPose.Conjugate(source.rotation);
            var magBones = new HashSet<Transform>();
            foreach (Transform part in rig.MagazineParts)
            {
                if (rig.MagazineHideOnly.Contains(part))
                {
                    continue; // a second magazine drawn in its place: hidden with it, not copied (it doubled the one in the hand)
                }
                foreach (Transform t in part.GetComponentsInChildren<Transform>(true))
                {
                    magBones.Add(t);
                }
            }

            // Only what shows on the gun: a rig's hidden spare magazine (the one its reload animation brings in) must not
            // turn up in your hand next to the real one.
            bool anyShown = false;
            foreach (Transform part in rig.MagazineParts)
            {
                foreach (Renderer r in part.GetComponentsInChildren<Renderer>(true))
                {
                    anyShown |= r.enabled && r.gameObject.activeInHierarchy && !(r is ParticleSystemRenderer);
                }
            }
            // Nothing shows (a launcher's rocket after the shot): what showed while it sat loaded, not every model under
            // it (an RPG-7V's rocket holds five warheads and shows one; the copy of all five was the rocket in the hand).
            HashSet<Renderer> loadedLook = !anyShown && rig.MagShownLoaded != null && rig.MagShownLoaded.Count > 0 ? rig.MagShownLoaded : null;
            foreach (Transform part in rig.MagazineParts)
            {
                if (rig.MagazineHideOnly.Contains(part))
                {
                    continue;
                }
                foreach (Renderer r in part.GetComponentsInChildren<Renderer>(true))
                {
                    if (anyShown && (!r.enabled || !r.gameObject.activeInHierarchy) || loadedLook != null && !loadedLook.Contains(r))
                    {
                        if (!(roundsToo && anyShown && RoundSized(r)))
                        {
                            continue;
                        }
                        roundsAdded++;
                    }
                    if ((r.transform.position - source.position).sqrMagnitude > ParkedAway * ParkedAway)
                    {
                        continue; // parked out of sight by the mod (a PKM box's belt links wait 25 m away): not on the magazine
                    }
                    if (PoseSampler.UnderAny(r.transform, rig.SpareParts))
                    {
                        continue; // hidden while held (the PKM box's belt links hanging under it): not on the magazine in the hand
                    }
                    if (UnderBelt(r.transform, rig, source))
                    {
                        // The gun's own belt, laid on the feed tray: copied with the box while it lay there, a stiff length of belt stuck
                        // out of the box in the hand, bent as it lay on the tray, and was gone once the box was in again until the belt was
                        // laid (user, 2026-11-03: "magbelt is getting split ... sometimes dissapearing when inserted"). The box's belt
                        // end hangs from it once it is in (ReloadInteractions' belt strip).
                        continue;
                    }
                    Mesh mesh = null;
                    if (r is MeshRenderer)
                    {
                        MeshFilter mf = r.GetComponent<MeshFilter>();
                        mesh = mf != null ? mf.sharedMesh : null;
                    }
                    else if (r is SkinnedMeshRenderer smr && smr.sharedMesh != null)
                    {
                        mesh = new Mesh { name = smr.sharedMesh.name + " (baked)" };
                        smr.BakeMesh(mesh);
                    }
                    if (mesh == null)
                    {
                        continue;
                    }
                    AddPart(root.transform, r.name, mesh, r.sharedMaterials, inv * (r.transform.position - source.position), inv * r.transform.rotation,
                        r is SkinnedMeshRenderer ? Vector3.one : r.transform.lossyScale, owned: r is SkinnedMeshRenderer);
                    any = true;
                }
            }

            if (roundsAdded > 0)
            {
                Log.InfoOnce("maglook-rounds:" + rig.Data.Key, $"Magazine of {rig.Data.Key}: {roundsAdded} round-sized piece(s) switched off on the gun drawn in a full one "
                                                           + "(no loaded look kept yet).");
            }
            // Skinned guns: the magazine is a bone; cut its triangles out of the gun mesh.
            if (!any && rig.Data.MagazineIsSkinned)
            {
                foreach (SkinnedMeshRenderer smr in rig.Weapon.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                {
                    Mesh cut = ExtractBoneTriangles(smr, magBones);
                    if (cut != null)
                    {
                        AddPart(root.transform, smr.name + " (magazine)", cut, smr.sharedMaterials,
                            inv * (smr.transform.position - source.position), inv * smr.transform.rotation, Vector3.one, owned: true);
                        any = true;
                    }
                }
            }

            if (!any)
            {
                Object.Destroy(root);
                return null;
            }
            return root;
        }

        /// <param name="root">The copy the part goes under.</param>
        /// <param name="name">The part's name.</param>
        /// <param name="mesh">Its mesh.</param>
        /// <param name="materials">Its materials (fixed for the mod's shaders).</param>
        /// <param name="localPos">Where it sits under the root.</param>
        /// <param name="localRot">How it is turned under the root.</param>
        /// <param name="scale">Its scale.</param>
        /// <param name="owned">The mesh was made for this copy (baked, cut out): it goes with the copy.</param>
        private static void AddPart(Transform root, string name, Mesh mesh, Material[] materials, Vector3 localPos, Quaternion localRot, Vector3 scale, bool owned = false)
        {
            var part = new GameObject(name);
            part.transform.SetParent(root, false);
            part.transform.localPosition = localPos;
            part.transform.localRotation = localRot;
            part.transform.localScale = scale;
            part.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = part.AddComponent<MeshRenderer>();
            mr.sharedMaterials = Util.MaterialFix.Fix(materials);
            mr.shadowCastingMode = ShadowCastingMode.Off; // (a magazine's shadow is hardly seen; its pieces were drawn into the shadow maps per eye)
            if (owned)
            {
                part.AddComponent<OwnedMesh>().Mesh = mesh;
            }
        }

        /// <summary>
        /// Which of a skinned mesh's vertices and triangles a magazine's bones drive (per mesh and bone set): found once. Every
        /// magazine let go, pulled, taken from the pouch or kicked out on such a gun walked the whole gun mesh's triangles and
        /// bone weights again (255.P2-1); only the bake (the magazine as posed now) is done each time.
        /// </summary>
        private sealed class BoneCut
        {
            public int[] Sources;
            public int[][] Triangles;
            public Vector2[] Uv;
        }

        private static readonly Dictionary<string, BoneCut> BoneCuts = new Dictionary<string, BoneCut>();

        /// <summary>Triangles whose vertices are driven mostly by the given bones, baked in the renderer's space.</summary>
        private static Mesh ExtractBoneTriangles(SkinnedMeshRenderer smr, HashSet<Transform> bones)
        {
            Mesh shared = smr.sharedMesh;
            if (shared == null || !shared.isReadable || smr.bones == null)
            {
                return null;
            }
            Transform[] smrBones = smr.bones;
            var boneIsMag = new bool[smrBones.Length];
            bool anyBone = false;
            var key = new System.Text.StringBuilder().Append(shared.GetInstanceID()).Append(':');
            for (int i = 0; i < smrBones.Length; i++)
            {
                boneIsMag[i] = smrBones[i] != null && bones.Contains(smrBones[i]);
                anyBone |= boneIsMag[i];
                if (boneIsMag[i])
                {
                    key.Append(i).Append(',');
                }
            }
            if (!anyBone)
            {
                return null;
            }
            if (!BoneCuts.TryGetValue(key.ToString(), out BoneCut cut))
            {
                cut = FindBoneCut(shared, boneIsMag);
                BoneCuts[key.ToString()] = cut; // a null (nothing driven by them) is kept too
            }
            if (cut == null)
            {
                return null;
            }
            var baked = new Mesh();
            smr.BakeMesh(baked);
            Vector3[] verts = baked.vertices;
            Vector3[] normals = baked.normals;
            Object.Destroy(baked);
            int n = cut.Sources.Length;
            var newVerts = new Vector3[n];
            var newNormals = new Vector3[n];
            for (int i = 0; i < n; i++)
            {
                int source = cut.Sources[i];
                newVerts[i] = verts[source];
                newNormals[i] = normals != null && normals.Length > source ? normals[source] : Vector3.up;
            }
            var result = new Mesh { name = shared.name + " (magazine)" };
            result.indexFormat = n > 65000 ? IndexFormat.UInt32 : IndexFormat.UInt16;
            result.vertices = newVerts;
            result.normals = newNormals;
            result.uv = cut.Uv;
            result.subMeshCount = cut.Triangles.Length;
            for (int sub = 0; sub < cut.Triangles.Length; sub++)
            {
                result.SetTriangles(cut.Triangles[sub], sub);
            }
            result.RecalculateBounds();
            return result;
        }

        private static BoneCut FindBoneCut(Mesh shared, bool[] boneIsMag)
        {
            BoneWeight[] weights = shared.boneWeights;
            if (weights == null || weights.Length != shared.vertexCount)
            {
                return null;
            }
            Vector2[] uv = shared.uv;
            var map = new Dictionary<int, int>();
            var sources = new List<int>();
            var newUv = new List<Vector2>();
            var subMeshes = new List<int[]>();
            for (int sub = 0; sub < shared.subMeshCount; sub++)
            {
                int[] tris = shared.GetTriangles(sub);
                var keep = new List<int>();
                for (int t = 0; t + 2 < tris.Length; t += 3)
                {
                    if (!DrivenBy(weights[tris[t]], boneIsMag) || !DrivenBy(weights[tris[t + 1]], boneIsMag) || !DrivenBy(weights[tris[t + 2]], boneIsMag))
                    {
                        continue;
                    }
                    for (int k = 0; k < 3; k++)
                    {
                        int source = tris[t + k];
                        if (!map.TryGetValue(source, out int idx))
                        {
                            idx = sources.Count;
                            map[source] = idx;
                            sources.Add(source);
                            newUv.Add(uv != null && uv.Length > source ? uv[source] : Vector2.zero);
                        }
                        keep.Add(idx);
                    }
                }
                subMeshes.Add(keep.ToArray());
            }
            return sources.Count == 0 ? null : new BoneCut { Sources = sources.ToArray(), Triangles = subMeshes.ToArray(), Uv = newUv.ToArray() };
        }

        private static bool DrivenBy(BoneWeight w, bool[] boneIsMag)
        {
            float share = 0f;
            if (w.boneIndex0 < boneIsMag.Length && boneIsMag[w.boneIndex0]) share += w.weight0;
            if (w.boneIndex1 < boneIsMag.Length && boneIsMag[w.boneIndex1]) share += w.weight1;
            if (w.boneIndex2 < boneIsMag.Length && boneIsMag[w.boneIndex2]) share += w.weight2;
            if (w.boneIndex3 < boneIsMag.Length && boneIsMag[w.boneIndex3]) share += w.weight3;
            return share >= 0.5f;
        }

        /// <summary>
        /// A magazine, and a launcher's rocket, are copied from the weapon's own model. The rocket only when the analysis
        /// found one of a rocket's size (a launcher showed a made-up capsule instead of its rocket).
        /// </summary>
        private static bool CopiesModel(WeaponRig rig, PropKind kind)
        {
            if (rig.Magazine == null)
            {
                return false;
            }
            if (kind == PropKind.Magazine)
            {
                return true;
            }
            if (kind == PropKind.Shell)
            {
                // The gun's own shell (its reload's), when its part is a shell's size: the hand held the mod's made 12-gauge
                // shell on every shotgun (user, 2026-10-22: "shotguns are not displayed with their real shells").
                return WeaponAnalyzer.ShellSized(rig.Data.MagBounds);
            }
            // A rocket whose analysed box came out empty (the scoped RPG-7V's sits under a material-less container) is copied
            // too: the copy measures itself, and one that is not rocket-sized still falls back to the made one.
            float length = Longest(rig.Data.MagBounds);
            // Up to 2 m: the RPG-7V's tandem PG-7VR is 1.7 m with its booster, and at 1.4 m it got the flying projectile.
            return kind == PropKind.Rocket && (length < 0.001f || length > 0.15f && length < 2f);
        }

        private static float Longest(Bounds b) => Mathf.Max(b.size.x, Mathf.Max(b.size.y, b.size.z));

        /// <summary>
        /// How far forward (gun-body space, along <paramref name="aim"/>) the launcher's own meshes reach: its tube's front
        /// end. Not its rocket (the magazine parts), not see-through lenses or sight overlays (queue 3000 and up), nothing
        /// over 2.5 m (a sight's far plane). False when it has no mesh of its own to measure (a skinned tube).
        /// </summary>
        internal static bool TubeFront(WeaponRig rig, Vector3 aim, out float front)
        {
            front = float.MinValue;
            if (rig.GunBody == null)
            {
                return false;
            }
            bool any = false;
            foreach (MeshFilter mf in rig.GunBody.GetComponentsInChildren<MeshFilter>(false))
            {
                Renderer r = mf.GetComponent<Renderer>();
                if (mf.sharedMesh == null || r == null || !r.enabled || r.sharedMaterial != null && r.sharedMaterial.renderQueue >= 3000)
                {
                    continue;
                }
                bool rocket = false;
                foreach (Transform part in rig.MagazineParts)
                {
                    rocket |= part != null && mf.transform.IsChildOf(part);
                }
                if (rocket || Vector3.Scale(mf.sharedMesh.bounds.size, mf.transform.lossyScale).magnitude > 2.5f)
                {
                    continue;
                }
                Bounds mb = mf.sharedMesh.bounds;
                for (int i = 0; i < 8; i++)
                {
                    Vector3 corner = mb.center + Vector3.Scale(mb.extents, new Vector3((i & 1) == 0 ? -1f : 1f, (i & 2) == 0 ? -1f : 1f, (i & 4) == 0 ? -1f : 1f));
                    front = Mathf.Max(front, Vector3.Dot(rig.ToGun(mf.transform.TransformPoint(corner)), aim));
                    any = true;
                }
            }
            return any;
        }

        /// <summary>
        /// The middle, across the bore, of the launcher's own meshes that reach its front end (within 12 cm of it), in gun-body
        /// space: where a missile goes into a tube whose muzzle point is not on the tube's axis (the HYDRA's missile slid in on
        /// the muzzle point's line, over the side of its box). False when nothing reaches the front.
        /// </summary>
        internal static bool TubeCentre(WeaponRig rig, Vector3 aim, float front, out Vector3 centre)
        {
            centre = Vector3.zero;
            if (rig.GunBody == null)
            {
                return false;
            }
            Vector3 up = Vector3.ProjectOnPlane(rig.Data.Up, aim).normalized, right = Vector3.Cross(up, aim).normalized;
            float upMin = float.MaxValue, upMax = float.MinValue, rightMin = float.MaxValue, rightMax = float.MinValue;
            var corners = new Vector3[8];
            foreach (MeshFilter mf in rig.GunBody.GetComponentsInChildren<MeshFilter>(false))
            {
                Renderer r = mf.GetComponent<Renderer>();
                if (mf.sharedMesh == null || r == null || !r.enabled || r.sharedMaterial != null && r.sharedMaterial.renderQueue >= 3000)
                {
                    continue;
                }
                bool rocket = false;
                foreach (Transform part in rig.MagazineParts)
                {
                    rocket |= part != null && mf.transform.IsChildOf(part);
                }
                if (rocket || Vector3.Scale(mf.sharedMesh.bounds.size, mf.transform.lossyScale).magnitude > 2.5f)
                {
                    continue;
                }
                Bounds mb = mf.sharedMesh.bounds;
                float reach = float.MinValue, back = float.MaxValue;
                for (int i = 0; i < 8; i++)
                {
                    Vector3 corner = mb.center + Vector3.Scale(mb.extents, new Vector3((i & 1) == 0 ? -1f : 1f, (i & 2) == 0 ? -1f : 1f, (i & 4) == 0 ? -1f : 1f));
                    corners[i] = rig.ToGun(mf.transform.TransformPoint(corner));
                    float along = Vector3.Dot(corners[i], aim);
                    reach = Mathf.Max(reach, along);
                    back = Mathf.Min(back, along);
                }
                // A tube is long along the bore: a sight post, a grip or a handle that reaches the mouth (shorter than 20 cm)
                // pulled the middle toward itself, and the missile with it (review 253.11).
                if (reach < front - 0.12f || reach - back < 0.2f || rig.Handle != null && mf.transform.IsChildOf(rig.Handle))
                {
                    continue;
                }
                foreach (Vector3 c in corners)
                {
                    upMin = Mathf.Min(upMin, Vector3.Dot(c, up));
                    upMax = Mathf.Max(upMax, Vector3.Dot(c, up));
                    rightMin = Mathf.Min(rightMin, Vector3.Dot(c, right));
                    rightMax = Mathf.Max(rightMax, Vector3.Dot(c, right));
                }
            }
            if (upMin > upMax)
            {
                return false;
            }
            centre = up * ((upMin + upMax) * 0.5f) + right * ((rightMin + rightMax) * 0.5f) + aim * front;
            return true;
        }

        /// <summary>A shotgun's rounds are shells; any other gun loaded round by round takes rifle rounds.</summary>
        private static bool ShotgunShells(WeaponRig rig) =>
            rig.Weapon == null || rig.Weapon.configuration == null || rig.Weapon.configuration.projectilesPerShot > 1 || rig.Data.Action == ActionType.BreakOpen;

        private static Bounds BuildProcedural(GameObject go, PropKind kind, Vector3 insertLocal, Vector3 frontLocal, Bounds magBounds, bool shotgunShell)
        {
            EnsureMaterials();
            if (kind == PropKind.Shell)
            {
                // The round itself (a shell with its brass head, or a live rifle round), tip first into the gun. It was a
                // brass capsule: "a yellow thing" instead of a shell.
                GameObject round = Casings.MakeRound(shotgunShell, go.transform, out float length, out float radius);
                Vector3 up = Mathf.Abs(Vector3.Dot(insertLocal.normalized, frontLocal.normalized)) > 0.95f ? Vector3.up : frontLocal;
                Quaternion along = Quaternion.LookRotation(insertLocal, up);
                round.transform.localRotation = along;
                round.transform.localPosition = -(along * new Vector3(0f, 0f, length * 0.5f));
                return new Bounds(Vector3.zero, Abs(along * new Vector3(radius * 2f, radius * 2f, length)));
            }
            Vector3 size;
            Material mat;
            PrimitiveType shape;
            switch (kind)
            {
                case PropKind.Shell:
                    size = new Vector3(0.019f, 0.065f, 0.019f);
                    mat = _brass;
                    shape = PrimitiveType.Capsule;
                    break;
                case PropKind.Rocket:
                    size = new Vector3(0.085f, 0.75f, 0.085f);
                    mat = _olive;
                    shape = PrimitiveType.Capsule;
                    break;
                default:
                    size = magBounds.size.sqrMagnitude > 1e-6f ? magBounds.size : new Vector3(0.025f, 0.12f, 0.06f);
                    mat = _metal;
                    shape = PrimitiveType.Cube;
                    break;
            }
            GameObject tmp = GameObject.CreatePrimitive(shape);
            Object.DestroyImmediate(tmp.GetComponent<Collider>());
            tmp.transform.SetParent(go.transform, false);
            // Primitive's long axis (Y) follows the insertion direction; Z follows the barrel.
            Quaternion orient = Quaternion.LookRotation(frontLocal, insertLocal);
            tmp.transform.localRotation = orient;
            tmp.transform.localPosition = kind == PropKind.Magazine ? magBounds.center : Vector3.zero;
            tmp.transform.localScale = kind == PropKind.Magazine ? new Vector3(size.x, size.y, size.z) : new Vector3(size.x, size.y * 0.5f, size.z);
            tmp.GetComponent<Renderer>().sharedMaterial = mat;
            Vector3 worldishSize = orient * new Vector3(size.x, size.y, size.z);
            return new Bounds(tmp.transform.localPosition, Abs(worldishSize));
        }

        private static void EnsureMaterials()
        {
            if (_metal != null)
            {
                return;
            }
            Shader s = Shader.Find("Standard") ?? Shader.Find("Legacy Shaders/Diffuse");
            _metal = new Material(s) { color = new Color(0.12f, 0.12f, 0.13f) };
            _brass = new Material(s) { color = new Color(0.72f, 0.55f, 0.22f) };
            _olive = new Material(s) { color = new Color(0.28f, 0.31f, 0.2f) };
            if (_metal.HasProperty("_Metallic"))
            {
                _metal.SetFloat("_Metallic", 0.6f);
                _brass.SetFloat("_Metallic", 0.9f);
            }
        }

        private static void SetLayer(GameObject go, int layer)
        {
            foreach (Transform t in go.GetComponentsInChildren<Transform>(true))
            {
                t.gameObject.layer = layer;
            }
        }
    }
}
