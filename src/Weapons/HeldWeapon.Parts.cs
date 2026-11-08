using System.Collections.Generic;
using RavenfieldVR.Core;
using RavenfieldVR.Input;
using RavenfieldVR.Rig;
using RavenfieldVR.Runtime;
using UnityEngine;

namespace RavenfieldVR.Weapons
{
    // HeldWeapon, part: the parts the mod poses on the held gun itself. Weapon overrides, the loaded rocket, a hinge,
    // the shells in the chambers, companions moved with a part, and the magazine put back.
    // The class and the frame's work: HeldWeapon.cs.
    public sealed partial class HeldWeapon
    {
        private void ApplyPartOverrides()
        {
            WeaponRigData d = Rig.Data;
            float fraction = SlideOffset / Mathf.Max(d.SlideTravel.magnitude, 1e-4f);
            // A charging handle apart from the bolt (a separate part, or one cut out of an AR's receiver) comes back only with
            // the hand: when the bolt locks open on an empty magazine, the handle stays forward (it showed pulled back). A hand-
            // driven action left open part-way (BoltOpen, not locked) keeps its handle with it: the handle snapped home on
            // release while the pump stayed back (2026-10-19, ARS M500: handleTravel 7.5 cm, slideTravel 12.4 cm).
            bool heldOpenByHand = ActiveAmmo != null && ActiveAmmo.BoltOpen && !ActiveAmmo.SlideLocked;
            // (An operating rod, HandleCycles, is no such handle: it stays back with the bolt.)
            float handle = Reload != null && !Reload.IsRacking && !heldOpenByHand && !d.HandleCycles
                           && (Rig.Handle != null || (d.CarveHandle || d.AddedHandle) && d.SlidePath != null) ? 0f : fraction;
            // Locked open with the bolt under the racked handle: the handle home (or where the hand has it), the bolt back.
            bool boltAlone = BoltLockedAlone;
            float slideShare = boltAlone && !Reload.IsRacking ? 0f : fraction;
            // The knob or the T worked from home while the bolt or carrier stays on its catch until it is let go (from its first
            // frame: the offset starts at nothing there, and the carrier was sent home for that frame).
            bool lockRack = Reload != null && Reload.IsRacking && Reload.RackFromLock && (KnobLockedAlone || HandleLockedAlone);
            if (lockRack)
            {
                slideShare = 1f;
            }
            if (SlideOffset > 0.0005f || lockRack)
            {
                MovePart(Rig.Slide, d.SlideRestLocal, d.SlideTravel * slideShare);
                MoveCompanions(d.SlideTravel * slideShare);
                MovePart(Rig.Handle, d.HandleRestLocal, d.HandleTravel * Mathf.Clamp01(handle));
                _slideMoved = true;
            }
            else
            {
                if (_slideMoved)
                {
                    // Home once when the offset drops to nothing in one step (a bolt let go near the front, a sub weapon
                    // switched mid-slam): the slide kept its last drawn place, up to 1.5 cm back. Only once: after that the
                    // gun's own animation moves it again (its shots).
                    MovePart(Rig.Slide, d.SlideRestLocal, Vector3.zero);
                    MoveCompanions(Vector3.zero);
                    _slideMoved = false;
                }
                if (Rig.Handle != null)
                {
                    MovePart(Rig.Handle, d.HandleRestLocal, Vector3.zero);
                }
            }
            // (Its own statement: hung as an "else if" between the two above, the slide was sent home every frame it was
            // moved, and no slide, bolt or handle could be pulled back by hand: Tasks 254.)
            if (boltAlone)
            {
                // Its place under the handle: back by what the handle is short of its full travel.
                MovePart(Rig.Bolt, d.BoltRestLocal, d.SlideTravel * (1f - Mathf.Clamp01(slideShare)));
                _boltMoved = true;
            }
            else if (_boltMoved)
            {
                MovePart(Rig.Bolt, d.BoltRestLocal, Vector3.zero); // rides on the handle again
                _boltMoved = false;
            }
            _boltPiece?.SetOffset(Rig, d.SlideTravel * ((d.CarveHandle || d.AddedHandle) && d.SlidePath != null ? handle : fraction));
            ApplyBoltTurn(d, slideShare, handle);
            ApplyHinge(d);
            ApplyTrigger(d);
            TickTriggerReset();
            ApplyChamberShells(d);
            ApplySightParts();

            AmmoState ammo = ActiveAmmo ?? Ammo;
            ApplyBeltParts(ammo);
            Rig.HideSpareParts(); // the flat reload's spare magazines are not drawn while held (WeaponRigData.SparePartPaths)
            // The magazine parts are this gun's own, so they follow the parent's magazine even while a fire-mode variant is
            // active: the variant's state could say the magazine is in while it is still in the hand (task 249).
            if (Rig.MagazineParts.Length > 0 && Ammo != null && Ammo.UsesDetachableMag)
            {
                // While the magazine is out a prop represents it (sliding in or out, in the hand, on the ground).
                // Scaling hides mesh-renderer and skinned magazines alike.
                bool hide = !Ammo.MagInserted;
                if (hide)
                {
                    Vector3 tiny = Vector3.one * 1e-4f;
                    foreach (Transform part in Rig.MagazineParts)
                    {
                        if (part != null && part.localScale != tiny) // a mod may swap or destroy magazine pieces at runtime
                        {
                            part.localScale = tiny;
                        }
                    }
                    _magHidden = true;
                }
                else if (_magHidden)
                {
                    RestoreMagazine();
                }
            }
            if (Rig.Magazine != null && Ammo != null && Ammo.Participates && Ammo.UsesDetachableMag && Ammo.MagInserted && !d.SeatOnHinge
                && VRConfig.ReloadNow == ReloadMode.Manual && ReferenceEquals(ActiveRig, Rig) && !(Weapon != null && Weapon.reloading))
            {
                // The seated magazine sits on its seat on the gun body, where a fresh one slides in. On many models it hangs
                // off the armature, not the gun body, and the flat animation's poses (loaded, empty, slide locked) moved it a
                // few centimetres against the gun: a fresh magazine went in beside the well (an M1911's 4 cm, a P220's 4.4).
                Rig.GunBodyPose.Multiply(d.MagSeat).ApplyTo(Rig.Magazine);
            }
            ShowMagazineLoaded();
            bool rocketLoaded = Rig.Magazine != null && ammo != null && d.Action == ActionType.SingleShot && !ammo.UsesDetachableMag && ammo.MagRounds > 0
                                && VRConfig.ReloadNow == ReloadMode.Manual && ReferenceEquals(ActiveRig, Rig) && !(Weapon != null && Weapon.reloading)
                                && (d.MagSeat.position - d.GripPoint).sqrMagnitude < 1.5f * 1.5f;
            if (rocketLoaded)
            {
                // A launcher loaded by hand shows its rocket on its seat: the game parks a fired one away (the Panzerfaust 3's
                // warhead 30 m off) and only its own reload animation, which a hand-loaded launcher never plays, brings it
                // back; the launcher looked empty once loaded.
                Rig.GunBodyPose.Multiply(d.MagSeat).ApplyTo(Rig.Magazine);
            }
            ShowLoadedRocket(rocketLoaded);
        }

        private sealed class RocketShown
        {
            public readonly HashSet<Renderer> RenderersOn = new HashSet<Renderer>();
            public readonly HashSet<GameObject> ObjectsOn = new HashSet<GameObject>();
            public readonly Dictionary<Transform, Vector3> ScalesRestored = new Dictionary<Transform, Vector3>();
        }

        /// <summary>
        /// What was switched on per launcher, not per hold: one holstered loaded and drawn again found its rocket already shown,
        /// recorded nothing, and the rocket stayed on it after the shot (the game hides it only once).
        /// </summary>
        private static readonly Dictionary<Weapon, RocketShown> RocketsShown = new Dictionary<Weapon, RocketShown>();
        private static readonly List<Weapon> GoneLaunchers = new List<Weapon>();

        private RocketShown Shown()
        {
            if (RocketsShown.TryGetValue(Weapon, out RocketShown s))
            {
                return s;
            }
            if (RocketsShown.Count >= 16)
            {
                GoneLaunchers.Clear();
                foreach (Weapon w in RocketsShown.Keys)
                {
                    if (w == null)
                    {
                        GoneLaunchers.Add(w);
                    }
                }
                foreach (Weapon w in GoneLaunchers)
                {
                    RocketsShown.Remove(w);
                }
                GoneLaunchers.Clear();
            }
            return RocketsShown[Weapon] = new RocketShown();
        }

        /// <summary>
        /// While a launcher is loaded, its rocket as it shows loaded is drawn: parts the game shrank to nothing get their scale
        /// back, renderers and objects it switched off after the shot are switched on (the Panzerfaust 3's warhead stayed
        /// hidden on a launcher loaded by hand: placing it on its seat was not enough). What this switched on is switched
        /// off again once it is fired, so the game's own hiding is left as it was (it hides nothing a second time).
        /// </summary>
        private void ShowLoadedRocket(bool loaded)
        {
            if (loaded)
            {
                for (int i = 0; i < Rig.MagazineParts.Length && i < Rig.MagazinePartScales.Length; i++)
                {
                    Transform p = Rig.MagazineParts[i];
                    // (Shrunk against its own scale: the game leaves some at 3%, which a fixed 0.1% took for drawn, and the tube looked empty loaded.)
                    if (p == null || Rig.MagazineHideOnly.Contains(p) || p.localScale.sqrMagnitude > Rig.MagazinePartScales[i].sqrMagnitude * 0.25f
                        || Rig.MagazinePartScales[i].sqrMagnitude < 1e-6f)
                    {
                        continue;
                    }
                    RocketShown shown = Shown();
                    if (!shown.ScalesRestored.ContainsKey(p))
                    {
                        shown.ScalesRestored[p] = p.localScale;
                    }
                    p.localScale = Rig.MagazinePartScales[i];
                }
                foreach (Renderer r in Rig.MagShownLoaded)
                {
                    if (r == null)
                    {
                        continue;
                    }
                    if (!r.gameObject.activeSelf)
                    {
                        r.gameObject.SetActive(true);
                        Shown().ObjectsOn.Add(r.gameObject);
                    }
                    if (!r.enabled)
                    {
                        r.enabled = true;
                        Shown().RenderersOn.Add(r);
                    }
                }
                return;
            }
            if (!RocketsShown.TryGetValue(Weapon, out RocketShown was))
            {
                return;
            }
            RocketsShown.Remove(Weapon);
            foreach (Renderer r in was.RenderersOn)
            {
                if (r != null)
                {
                    r.enabled = false;
                }
            }
            foreach (GameObject g in was.ObjectsOn)
            {
                if (g != null)
                {
                    g.SetActive(false);
                }
            }
            foreach (KeyValuePair<Transform, Vector3> kv in was.ScalesRestored)
            {
                if (kv.Key != null)
                {
                    kv.Key.localScale = kv.Value;
                }
            }
        }

        private bool _slideMoved;
        private bool _boltMoved;

        /// <summary>
        /// The action is locked open and the slide part is a handle carrying the bolt: the bolt alone stays back, the handle
        /// is home (an M4's charging handle on an empty magazine).
        /// </summary>
        internal bool BoltLockedAlone => Rig.Bolt != null && Rig.Slide != null && Reload != null && (ActiveAmmo ?? Ammo) is AmmoState a && a.SlideLocked
                                         && ReferenceEquals(ActiveRig, Rig);

        /// <summary>
        /// Locked open with a made knob (carved out or added) on a moving part that has no bolt of its own: the knob stays forward
        /// (a non-reciprocating handle, as the CZ Bren 2's) while the part, the bolt, waits back on its catch. The grab zone and
        /// the stroke start at the knob then: they sat 11.5 cm back with the bolt, so the knob drawn at home was hardly in reach
        /// and jumped back from the hand on the grab (Bren hunt, 2026-10-19, task 302).
        /// </summary>
        internal bool KnobLockedAlone => Rig.Bolt == null && Rig.Slide != null && (Rig.Data.CarveHandle || Rig.Data.AddedHandle) && Reload != null
                                         && (ActiveAmmo ?? Ammo) is AmmoState a && a.SlideLocked && ReferenceEquals(ActiveRig, Rig);

        /// <summary>
        /// Locked open with a charging handle of its own (an M4's T): the T is drawn home (ApplyPartOverrides) while the carrier
        /// waits back on its catch, so the grab zone and the stroke start at the T. They started 7.5 cm behind it with the carrier,
        /// so after the last round the T was hardly in reach, the sight switch or the magnifier took the grab, and a rack that did
        /// start drew the T jumping back out of the hand (user, 2026-10-20: "some m4a1 racks got unrackable again"; rack hunt).
        /// </summary>
        internal bool HandleLockedAlone => Rig.Handle != null && !Rig.Data.HandleCycles && !BoltLockedAlone && !KnobLockedAlone && Reload != null
                                           && (ActiveAmmo ?? Ammo) is AmmoState a && a.SlideLocked && ReferenceEquals(ActiveRig, Rig);

        /// <summary>A shot of a gun that fires once a pull, its trigger not yet let forward.</summary>
        private bool _triggerResetDue;

        /// <summary>The trigger let forward after a shot: the sear's small click in the finger, where the gun can fire again.</summary>
        private void TickTriggerReset()
        {
            if (_triggerResetDue && Input.VRInput.GetTrigger(Input.VRInput.GunHand) < 0.45f)
            {
                _triggerResetDue = false;
                _system.Rig.Dominant.Haptic(0.004f, 240f, 0.12f);
            }
        }

        private Quaternion _triggerRest;
        private Vector3 _triggerRestPos;
        private bool _triggerTaken;

        /// <summary>The gun's trigger as far back as the finger has pulled the controller's. Written whole each frame: the fire clip moves some of them too.</summary>
        private void ApplyTrigger(WeaponRigData d)
        {
            Transform t = Rig.Trigger;
            if (t == null)
            {
                return;
            }
            if (!_triggerTaken)
            {
                _triggerRest = t.localRotation;
                _triggerRestPos = t.localPosition;
                _triggerTaken = true;
            }
            float pull = Input.VRInput.GetTrigger(Input.VRInput.GunHand);
            Quaternion turn = Quaternion.identity;
            if (d.TriggerAngle > 0f)
            {
                Vector3 axisWorld = Rig.DirToWorld(d.TriggerAxis);
                turn = Quaternion.AngleAxis(d.TriggerAngle * pull, t.parent != null ? t.parent.InverseTransformDirection(axisWorld) : axisWorld);
            }
            t.localRotation = turn * _triggerRest;
            Vector3 move = Rig.DirToWorld(d.TriggerMove) * pull;
            t.localPosition = _triggerRestPos + (t.parent != null ? t.parent.InverseTransformVector(move) : move);
        }

        /// <summary>Swings the hinged part, and a catch that turns with it, about their own pivots, on the axes the mod's reload opens them.</summary>
        private void ApplyHinge(WeaponRigData d)
        {
            Swing(Rig.Hinge, d.HingeAxis, d.HingeOpenAngle, ref _hingeRestLocal, ref _hingeOverridden);
            Swing(Rig.HingeCatch, d.HingeCatchAxis, d.HingeCatchAngle, ref _catchRestLocal, ref _catchOverridden);
        }

        private void Swing(Transform part, Vector3 axis, float angle, ref Quaternion rest, ref bool overridden)
        {
            if (part == null)
            {
                return;
            }
            if (HingeOpenness <= 0.001f)
            {
                if (overridden)
                {
                    part.localRotation = rest;
                    overridden = false;
                }
                return;
            }
            if (!overridden)
            {
                rest = part.localRotation;
                overridden = true;
            }
            Vector3 axisWorld = Rig.DirToWorld(axis);
            Vector3 axisLocal = part.parent != null ? part.parent.InverseTransformDirection(axisWorld) : axisWorld;
            part.localRotation = Quaternion.AngleAxis(angle * HingeOpenness, axisLocal) * rest;
        }

        /// <summary>
        /// A break action's chamber shells: drawn where a chamber holds a shell, hidden where it is empty, and carried with the
        /// barrels as they open. The DOUBLE-BARREL's hang from the gun's root: they were always drawn, and stayed in the air
        /// where the shut chambers were when the barrels swung down (user, 2026-10-22). Left to the flat reload outside manual
        /// reloading.
        /// </summary>
        private void ApplyChamberShells(WeaponRigData d)
        {
            Transform[] shells = Rig.ChamberShells;
            if (shells.Length == 0)
            {
                return;
            }
            if (ChamberFull.Length != shells.Length)
            {
                ChamberFull = new bool[shells.Length];
            }
            if (Ammo == null || !Ammo.Participates || VRConfig.ReloadNow != ReloadMode.Manual || Weapon != null && Weapon.reloading)
            {
                RestoreChamberShells();
                return;
            }
            // As many chambers hold a shell as there are live and fired ones in the gun: the ones the reload filled first.
            int want = Mathf.Min(shells.Length, Ammo.MagRounds + Ammo.SpentCases);
            int full = 0;
            foreach (bool f in ChamberFull)
            {
                full += f ? 1 : 0;
            }
            for (int i = 0; i < ChamberFull.Length && full < want; i++)
            {
                if (!ChamberFull[i])
                {
                    ChamberFull[i] = true;
                    full++;
                }
            }
            for (int i = ChamberFull.Length - 1; i >= 0 && full > want; i--)
            {
                if (ChamberFull[i])
                {
                    ChamberFull[i] = false;
                    full--;
                }
            }
            if (!_chambersApplied)
            {
                _chamberLocalPos = new Vector3[shells.Length];
                _chamberLocalRot = new Quaternion[shells.Length];
                for (int i = 0; i < shells.Length; i++)
                {
                    if (shells[i] != null)
                    {
                        _chamberLocalPos[i] = shells[i].localPosition;
                        _chamberLocalRot[i] = shells[i].localRotation;
                    }
                }
                _chambersApplied = true;
            }
            Vector3 tiny = Vector3.one * 1e-4f;
            for (int i = 0; i < shells.Length; i++)
            {
                Transform t = shells[i];
                if (t == null)
                {
                    continue; // a mod may swap or destroy its pieces at runtime
                }
                Vector3 scale = ChamberFull[i] ? Rig.ChamberShellScales[i] : tiny;
                if (t.localScale != scale)
                {
                    t.localScale = scale;
                }
                if (ChamberFull[i] && Rig.Hinge != null && !t.IsChildOf(Rig.Hinge) && i < d.ChamberShellRest.Count)
                {
                    Rig.GunBodyPose.Multiply(d.HingeRotated(d.ChamberShellRest[i], HingeOpenness)).ApplyTo(t);
                }
            }
        }

        /// <summary>The chamber shells back as the gun had them: let go, or reloaded by the flat animation.</summary>
        private void RestoreChamberShells()
        {
            if (!_chambersApplied)
            {
                return;
            }
            Transform[] shells = Rig.ChamberShells;
            for (int i = 0; i < shells.Length; i++)
            {
                if (shells[i] != null)
                {
                    shells[i].localScale = Rig.ChamberShellScales[i];
                    shells[i].localPosition = _chamberLocalPos[i];
                    shells[i].localRotation = _chamberLocalRot[i];
                }
            }
            _chambersApplied = false;
        }

        /// <summary>The slide's companions (a knob beside it in the model) moved by the slide's travel, each from its own rest.</summary>
        private void MoveCompanions(Vector3 travelGun)
        {
            Transform[] parts = Rig.SlideCompanions;
            for (int i = 0; i < parts.Length && i < Rig.Data.SlideCompanionRest.Count; i++)
            {
                MovePart(parts[i], Rig.Data.SlideCompanionRest[i], travelGun);
            }
        }

        private void MovePart(Transform part, Vector3 restLocal, Vector3 travelGun)
        {
            if (part == null)
            {
                return;
            }
            Vector3 worldTravel = Rig.DirToWorld(travelGun);
            Transform parent = part.parent;
            Vector3 target = restLocal + (parent != null ? parent.InverseTransformVector(worldTravel) : worldTravel);
            if (part.localPosition != target) // a slide or handle at rest holds the same value every frame
            {
                part.localPosition = target;
            }
        }

        /// <summary>One piece of the magazine as it is drawn sitting full in the gun's loaded pose (<see cref="ShowMagazineLoaded"/>).</summary>
        private struct LoadedPiece
        {
            public Transform T;
            public Vector3 Position, Scale;
            public Quaternion Rotation;
            public bool Active;
        }

        private LoadedPiece[] _magLoaded;
        private Renderer[] _magLoadedRenderers;
        private bool[] _magLoadedEnabled;

        /// <summary>
        /// A magazine with rounds in a gun its animator holds in the empty pose: its pieces drawn as they are with it full in the
        /// loaded pose. Many mods' empty pose hides the magazine's rounds (and the bolt back is the same pose), so a full magazine
        /// pushed gently into a gun locked open on empty showed empty until the bolt went forward (a see-through magazine's rounds,
        /// the BRN180 EO's; user, 2026-11-03: "the full mag glitches, becomes empty and then full again when inside the gun").
        /// The pieces under the magazine's parts are taken once while it sits full in the loaded rest pose, and laid over the
        /// animator's pose only while the empty pose is drawn with rounds in (the bolt, another part, stays back).
        /// </summary>
        private void ShowMagazineLoaded()
        {
            AmmoState ammo = Ammo;
            Weapon w = Weapon;
            if (Rig.MagazineParts.Length == 0 || ammo == null || !ammo.Participates || !ammo.UsesDetachableMag || !ammo.MagInserted
                || w == null || w.reloading || !ReferenceEquals(ActiveRig, Rig) || !w.HasActiveAnimator() || w.animator.layerCount == 0)
            {
                return;
            }
            Animator a = w.animator;
            int now = a.GetCurrentAnimatorStateInfo(0).fullPathHash;
            if (_magLoaded == null)
            {
                if (_loadedRestState != 0 && now == _loadedRestState && now != _emptyState && !a.IsInTransition(0) && !ammo.SlideLocked
                    && ammo.MagRounds >= Mathf.Max(1, ammo.Capacity - 1) && Time.unscaledTime - _lastShotAt > 0.6f && SettledLoaded)
                {
                    TakeMagazineLoaded();
                }
                return;
            }
            if (_emptyState == 0 || now != _emptyState || ammo.MagRounds <= 0)
            {
                return;
            }
            foreach (LoadedPiece p in _magLoaded)
            {
                if (p.T == null)
                {
                    continue;
                }
                if (p.T.gameObject.activeSelf != p.Active)
                {
                    p.T.gameObject.SetActive(p.Active);
                }
                p.T.localPosition = p.Position;
                p.T.localRotation = p.Rotation;
                p.T.localScale = p.Scale;
            }
            for (int i = 0; i < _magLoadedRenderers.Length; i++)
            {
                Renderer r = _magLoadedRenderers[i];
                if (r != null && r.enabled != _magLoadedEnabled[i])
                {
                    r.enabled = _magLoadedEnabled[i];
                }
            }
            if (!_magLoadedSaid)
            {
                // (Once a draw: built every frame for as long as the gun stays in its empty pose, the key and the line were garbage each frame
                // before InfoOnce threw them away; per-frame scout, 2026-11-08.)
                _magLoadedSaid = true;
                Log.InfoOnce("magloaded:" + Rig.Data.Key, $"{Rig.Data.Key}: a magazine with rounds in a gun in its empty pose: drawn as it sits full ({_magLoaded.Length} piece(s)).");
            }
        }

        private bool _magLoadedSaid;

        /// <summary>The magazine's pieces (under its parts, not the parts themselves: those the seat and the reload place) as drawn now.</summary>
        private void TakeMagazineLoaded()
        {
            var pieces = new List<LoadedPiece>();
            var renderers = new List<Renderer>();
            foreach (Transform part in Rig.MagazineParts)
            {
                if (part == null)
                {
                    continue;
                }
                foreach (Transform t in part.GetComponentsInChildren<Transform>(true))
                {
                    if (t != part)
                    {
                        pieces.Add(new LoadedPiece { T = t, Position = t.localPosition, Rotation = t.localRotation, Scale = t.localScale, Active = t.gameObject.activeSelf });
                    }
                }
                renderers.AddRange(part.GetComponentsInChildren<Renderer>(true));
            }
            _magLoaded = pieces.ToArray();
            _magLoadedRenderers = renderers.ToArray();
            _magLoadedEnabled = new bool[_magLoadedRenderers.Length];
            for (int i = 0; i < _magLoadedRenderers.Length; i++)
            {
                _magLoadedEnabled[i] = _magLoadedRenderers[i].enabled;
            }
        }

        /// <summary>How far a bolt action's handle is turned up (0 shut, 1 up: ReloadInteractions' lift).</summary>
        internal float BoltTurn;
        private Quaternion _slideTurnRest = Quaternion.identity, _handleTurnRest = Quaternion.identity;
        private bool _turnRestTaken, _boltTurned;

        /// <summary>
        /// A bolt action's bolt and handle turned about the bore by <see cref="BoltTurn"/> (idea 9): put back where they are drawn this
        /// frame, then swung about the bore's axis, so the handle comes up and over as the real one does. Their turn at rest is taken
        /// the first time (as the animation has them), and given back when it is down again.
        /// </summary>
        private void ApplyBoltTurn(WeaponRigData d, float slideShare, float handle)
        {
            float angle = Reload != null ? BoltTurn * ReloadInteractions.BoltLiftAngle * Reload.BoltLiftSign : 0f;
            bool turned = Mathf.Abs(angle) > 0.05f;
            if (!turned && !_boltTurned)
            {
                return;
            }
            Transform slide = Rig.Slide, knob = Rig.Handle;
            if (!_turnRestTaken)
            {
                _turnRestTaken = true;
                _slideTurnRest = slide != null ? slide.localRotation : Quaternion.identity;
                _handleTurnRest = knob != null ? knob.localRotation : Quaternion.identity;
            }
            Vector3 pivot = Rig.ToWorld(d.MuzzlePoint), axis = Rig.DirToWorld(d.AimAxis);
            if (slide != null && !(knob != null && slide.IsChildOf(knob) && slide != knob))
            {
                MovePart(slide, d.SlideRestLocal, d.SlideTravel * slideShare);
                slide.localRotation = _slideTurnRest;
                if (turned)
                {
                    slide.RotateAround(pivot, axis, angle);
                }
            }
            if (knob != null && knob != slide && !(slide != null && knob.IsChildOf(slide)))
            {
                MovePart(knob, d.HandleRestLocal, d.HandleTravel * Mathf.Clamp01(handle));
                knob.localRotation = _handleTurnRest;
                if (turned)
                {
                    knob.RotateAround(pivot, axis, angle);
                }
            }
            _boltTurned = turned;
        }

        private void RestoreMagazine()
        {
            for (int i = 0; i < Rig.MagazineParts.Length; i++)
            {
                if (Rig.MagazineParts[i] != null)
                {
                    Rig.MagazineParts[i].localScale = _magOriginalScales[i];
                }
            }
            _magHidden = false;
        }
    }
}
