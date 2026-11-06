using System.Collections.Generic;
using RavenfieldVR.Core;
using RavenfieldVR.Input;
using RavenfieldVR.Rig;
using RavenfieldVR.Util;
using UnityEngine;

namespace RavenfieldVR.Weapons
{
    /// <summary>
    /// The player's hands on a soldier's gun (Tasks 346, user: "when i swing my hand against a enemy gun it should move with the
    /// arms to the side", "or grabbing the gun", "or doing the mag out of his gun so they need to reload"):
    /// <list type="bullet">
    /// <item>a hand swung into it knocks it aside: his aim is thrown off the way the hand went, for under a second, and he does not
    /// fire for half a second (<see cref="SoldierAimKnockPatch"/>, <see cref="SoldierHoldFirePatch"/>);</item>
    /// <item>a squeeze on its magazine (the gun's lower middle) pulls the magazine out: the gun is empty, so his AI reloads it
    /// (AiActorController.Reload: the active weapon empty), and the magazine is in the hand with its rounds, a magazine of his
    /// gun's kind (it fits the player's gun of that kind);</item>
    /// <item>a squeeze anywhere else on it wrests it from him: a dropped copy of it is made where it was and taken into the hand
    /// as a dropped weapon is (WeaponDrops), and he draws his other weapon. Never from a soldier with no other weapon: the game's AI
    /// reads its active weapon without a check (AiActorController), an unarmed soldier threw every frame. His gun is knocked aside
    /// instead.</item>
    /// </list>
    /// Only soldiers on their feet with a gun out (not seated, not down as a ragdoll), friend or enemy alike. Before the body grab, so
    /// a squeeze on his gun takes the gun and not him (it is consumed).
    /// </summary>
    internal sealed class SoldierGuns
    {
        /// <summary>A hand's middle this near a soldier's gun touches it (m: the gun in the fist's hollow is 0).</summary>
        private const float Reach = 0.06f;
        /// <summary>A hand this fast against the player's body swings: it knocks the gun aside (m/s).</summary>
        private const float SwingSpeed = 2f;
        /// <summary>How long the knocked aim takes to come back, and how long he holds his fire (s); the aim's greatest throw (deg).</summary>
        private const float KnockTime = 0.8f, HoldFireTime = 0.5f, KnockAngle = 55f;
        /// <summary>A squeeze this recent still takes the gun the hand arrives at (s), as a body grab's (BodyGrab.GrabWindow).</summary>
        private const float GrabWindow = 0.4f;
        /// <summary>Soldiers farther than this from the hand are not looked at (m).</summary>
        private const float LookWithin = 2.5f;

        /// <summary>Soldiers' guns a hand took the magazine out of. Dropped still empty they come back with none in (<see cref="LeftUnloaded"/>).</summary>
        private static readonly HashSet<Weapon> Unloaded = new HashSet<Weapon>();

        /// <summary>This gun's magazine was taken and he has not reloaded it since: its drop has no magazine in it.</summary>
        internal static bool LeftUnloaded(Weapon gun) => gun != null && gun.ammo <= 0 && Unloaded.Contains(gun);

        /// <summary>A new map: the guns of the last one are gone.</summary>
        internal static void ForgetUnloaded() => Unloaded.Clear();

        private readonly WeaponSystem _sys;
        private readonly VRRig _rig;
        private readonly float[] _lastKnock = { -10f, -10f };
        /// <summary>The soldier whose gun each hand holds pushed off (one with no other weapon to draw: see <see cref="Wrest"/>), or null.</summary>
        private readonly Actor[] _holdingGunOf = new Actor[2];
        private readonly float[] _nextStrain = new float[2];

        private sealed class Knock
        {
            public float At;
            public Vector3 Axis;
            public float Angle;
        }

        private static readonly Dictionary<Actor, Knock> Knocks = new Dictionary<Actor, Knock>();
        // Each gun's shape: the box round its third-person model in that model's own space (its renderers' meshes), made once.
        private static readonly Dictionary<Weapon, Bounds> Shapes = new Dictionary<Weapon, Bounds>();
        private static readonly List<Renderer> RendererScratch = new List<Renderer>();
        private static readonly List<Weapon> GoneWeapons = new List<Weapon>();
        private static readonly List<Actor> GoneActors = new List<Actor>();
        private float _nextPrune;

        internal SoldierGuns(WeaponSystem sys, VRRig rig)
        {
            _sys = sys;
            _rig = rig;
        }

        /// <summary>Once a frame, before the body grab.</summary>
        internal void Update()
        {
            if (Time.unscaledTime >= _nextPrune)
            {
                _nextPrune = Time.unscaledTime + 10f;
                Prune();
            }
            Actor player = GameContext.PlayerActor;
            if (player == null || player.dead || !GameContext.PlayerOnFoot || GameContext.GameMenuOpen || ActorManager.instance == null)
            {
                return;
            }
            GatherClose(player);
            for (int i = 0; i < 2; i++)
            {
                VRHand hand = _rig.Hands[i];
                if (hand == null || !hand.Tracked)
                {
                    _holdingGunOf[i] = null;
                    continue;
                }
                Vector3 palm = hand.PalmCentre;
                if (_holdingGunOf[i] != null)
                {
                    HoldGunOff(i, hand, palm);
                    continue;
                }
                if (!NearestGun(palm, out Actor soldier, out Weapon gun, out float distance) || distance > Reach)
                {
                    continue;
                }
                // A squeeze on his gun, not on him: his hands hold it, and a hand closed on his wrist or arm takes him (BodyGrab: "grab
                // them on the hand or arm", user, 2026-10-27). The gun only where it is clearly nearer than any of him (its box is a
                // little bigger than the gun).
                // (A squeeze made a moment before the hand got there counts, as for every other reach: GetPressedRecently is unclaimed
                // presses only, so one the gun took is not taken again.)
                // (And only a hand arriving slower than a swing: a fist closed in the wind-up and swung into the gun knocks it aside below,
                // it does not take it; round-3 skeptic, 2026-10-28.)
                if (hand.Role == HandRole.Free && hand.Held == null && !VRGameInput.GunInputBlocked
                    && hand.BodyRelativeVelocity.sqrMagnitude <= SwingSpeed * SwingSpeed
                    && VRInput.GetPressedRecently(DigitalAction.Grab, hand.Side, GrabWindow) && distance + 0.02f < BodyDistance(soldier, palm))
                {
                    bool done = OnMagazine(gun, palm) ? TakeMagazine(hand, soldier, gun) || Wrest(i, hand, soldier, gun) : Wrest(i, hand, soldier, gun);
                    if (done)
                    {
                        VRInput.Consume(DigitalAction.Grab, hand.Side); // his gun took the squeeze: not him (BodyGrab) nor anything else
                        continue;
                    }
                }
                Vector3 swing = hand.BodyRelativeVelocity;
                if (swing.sqrMagnitude > SwingSpeed * SwingSpeed && Time.unscaledTime - _lastKnock[i] > 0.4f)
                {
                    _lastKnock[i] = Time.unscaledTime; // (real time, as melee: a hand swings as fast in slow motion)
                    KnockAside(soldier, gun, swing, hand);
                }
            }
        }

        /// <summary>The soldiers on their feet near either hand this frame, with their positions (<see cref="GatherClose"/>).</summary>
        private static readonly List<Actor> Close = new List<Actor>();
        private static readonly List<Vector3> CloseAt = new List<Vector3>();

        /// <summary>
        /// One pass over the match's soldiers a frame for both hands: those within <see cref="LookWithin"/> of either palm (taken
        /// from the palms' midpoint, out to the reach plus half the palms' distance apart: no soldier near a palm is left out).
        /// Each hand went over every soldier itself, two native position reads each a frame with a hundred bots and more.
        /// </summary>
        private void GatherClose(Actor player)
        {
            Close.Clear();
            CloseAt.Clear();
            VRHand left = _rig.Hands[0], right = _rig.Hands[1];
            bool l = left != null && left.Tracked && _holdingGunOf[0] == null, r = right != null && right.Tracked && _holdingGunOf[1] == null;
            if (!l && !r)
            {
                return;
            }
            Vector3 a0 = l ? left.PalmCentre : right.PalmCentre, a1 = r ? right.PalmCentre : left.PalmCentre;
            Vector3 mid = (a0 + a1) * 0.5f;
            float within = LookWithin + (a1 - a0).magnitude * 0.5f;
            foreach (Actor a in ActorManager.instance.actors)
            {
                if (a == null || a == player || a.dead || !a.aiControlled || a.IsSeated())
                {
                    continue;
                }
                Vector3 at = a.transform.position;
                if ((at - mid).sqrMagnitude > within * within || a.ragdoll != null && a.ragdoll.IsRagdoll())
                {
                    continue; // (down: the gun is not in his hands)
                }
                Close.Add(a);
                CloseAt.Add(at);
            }
        }

        /// <summary>The soldier's gun nearest a point, on his feet with it out: its distance from the point (m, 0 inside it).</summary>
        private static bool NearestGun(Vector3 point, out Actor soldier, out Weapon gun, out float distance)
        {
            soldier = null;
            gun = null;
            distance = float.MaxValue;
            for (int n = 0; n < Close.Count; n++)
            {
                Actor a = Close[n];
                if ((CloseAt[n] - point).sqrMagnitude > LookWithin * LookWithin)
                {
                    continue;
                }
                Weapon w = a.activeWeapon;
                if (w == null || w.thirdPersonTransform == null || !ShapeOf(w, out Bounds local))
                {
                    continue;
                }
                Transform tp = w.thirdPersonTransform;
                Vector3 closest = tp.TransformPoint(local.ClosestPoint(tp.InverseTransformPoint(point)));
                float d = (closest - point).magnitude;
                if (d < distance)
                {
                    distance = d;
                    soldier = a;
                    gun = w;
                }
            }
            return gun != null;
        }

        private static readonly Collider[] Near = new Collider[32];

        /// <summary>How far a point is from the nearest of a soldier's own colliders (his body, arms, hands), or infinity past the reach.</summary>
        private static float BodyDistance(Actor soldier, Vector3 point)
        {
            float best = float.PositiveInfinity;
            int n = Physics.OverlapSphereNonAlloc(point, Reach + 0.05f, Near, Hitbox.ALL_HITBOX_LAYER_MASK, QueryTriggerInteraction.Collide);
            for (int k = 0; k < n; k++)
            {
                Collider c = Near[k];
                if (c != null && BodyGrab.HurtableOf(c) == soldier)
                {
                    best = Mathf.Min(best, (c.ClosestPoint(point) - point).magnitude);
                }
            }
            return best;
        }

        /// <summary>The box round a gun's third-person model in that model's space, from its renderers' meshes (made once a gun).</summary>
        private static bool ShapeOf(Weapon w, out Bounds local)
        {
            if (Shapes.TryGetValue(w, out local))
            {
                return local.size.sqrMagnitude > 0f;
            }
            Transform tp = w.thirdPersonTransform;
            RendererScratch.Clear();
            tp.GetComponentsInChildren(false, RendererScratch);
            bool any = false;
            Matrix4x4 toModel = tp.worldToLocalMatrix;
            foreach (Renderer r in RendererScratch)
            {
                Mesh mesh = r is SkinnedMeshRenderer skinned ? skinned.sharedMesh : r is MeshRenderer ? r.GetComponent<MeshFilter>()?.sharedMesh : null;
                if (mesh == null)
                {
                    continue; // (particles, trails, lines: no part of the gun's body)
                }
                Bounds mb = mesh.bounds;
                Matrix4x4 m = toModel * r.transform.localToWorldMatrix;
                for (int c = 0; c < 8; c++)
                {
                    Vector3 corner = mb.center + Vector3.Scale(mb.extents, new Vector3((c & 1) == 0 ? -1f : 1f, (c & 2) == 0 ? -1f : 1f, (c & 4) == 0 ? -1f : 1f));
                    Vector3 p = m.MultiplyPoint3x4(corner);
                    if (!any)
                    {
                        local = new Bounds(p, Vector3.zero);
                        any = true;
                    }
                    else
                    {
                        local.Encapsulate(p);
                    }
                }
            }
            RendererScratch.Clear();
            if (!any)
            {
                // Not kept: a soldier not drawn has his gun hidden (Actor: isRendered), and an empty box kept from then left his gun
                // untouchable once he came into view.
                local = new Bounds();
                return false;
            }
            Shapes[w] = local;
            return true;
        }

        /// <summary>
        /// The hand is on the gun's magazine: under the gun's middle (the middle 45% of its length, below its centre in the world:
        /// a soldier holds his gun upright, the magazine under the receiver).
        /// </summary>
        private static bool OnMagazine(Weapon w, Vector3 palm)
        {
            if (!Shapes.TryGetValue(w, out Bounds local))
            {
                return false;
            }
            Transform tp = w.thirdPersonTransform;
            Vector3 p = tp.InverseTransformPoint(palm) - local.center;
            Vector3 e = local.extents;
            int axis = e.x >= e.y && e.x >= e.z ? 0 : e.y >= e.z ? 1 : 2; // its length
            float along = p[axis] / Mathf.Max(1e-4f, e[axis]);
            return Mathf.Abs(along) < 0.45f && palm.y < tp.TransformPoint(local.center).y - 0.015f;
        }

        /// <summary>
        /// The magazine pulled out of his gun: the gun empty (his AI reloads it), the magazine in the hand with its rounds (a magazine of
        /// the gun's analysed kind is made from it). False for a gun with no magazine to pull (a tube, a break action, a launcher, a
        /// blade, already empty or reloading), and for one whose kind is not analysed yet (whether it has one is not known: a tube
        /// shotgun was emptied with nothing in the hand; the squeeze wrests the gun instead).
        /// </summary>
        private bool TakeMagazine(VRHand hand, Actor soldier, Weapon gun)
        {
            if (gun.ammo <= 0 || gun.reloading || gun.configuration == null || gun.configuration.ammo <= 1 || gun is MeleeWeapon || gun is ThrowableWeapon)
            {
                return false;
            }
            // (Only an analysis already made: made now, it is tens to hundreds of milliseconds on this frame.)
            WeaponRig rig = WeaponAnalyzer.IsCached(gun, gun.weaponEntry) ? _sys.RigFor(gun) : null;
            if (rig == null || !(rig.Data.Action == ActionType.SelfLoading || rig.Data.Action == ActionType.Manual && rig.Data.MagazinePath != null))
            {
                return false; // a tube, a break action, a single shot: nothing comes out under it (or not known yet)
            }
            int rounds = gun.ammo;
            MagazineProp prop = null;
            try
            {
                // Made while his gun still has its rounds: emptied first, a mod whose model shows its magazine's rounds by the count
                // (on the ammo change) was copied empty, a full magazine looking empty in the hand.
                prop = MagazineProp.Create(rig, PropKind.Magazine, rounds);
                gun.ammo = 0;
                Unloaded.Add(gun);
                // As the game's own changes of it: its cached "has any ammo" was left true, and a soldier with no rounds left to reload
                // stood on his empty gun and never drew his sidearm (the AI's HasAnyAmmo checks; round-3 scout, 2026-10-28).
                gun.OnAmmoChanged();
                prop.Attach(hand);
                prop.FollowHand();
            }
            catch (System.Exception e)
            {
                // No magazine in the hand: his rounds back and the squeeze not spent (it wrests the gun instead). Kept emptied, the
                // rounds were gone with nothing in the hand (round 11).
                Log.ExceptionOnce("SoldierGuns.TakeMagazine", e);
                prop?.Destroy();
                gun.ammo = rounds;
                Unloaded.Remove(gun);
                gun.OnAmmoChanged();
                return false;
            }
            _sys.Sounds.Play(MechSound.MagOut, hand.PalmCentre, 0.8f, 0f, rig.Data); // (his gun's own)
            hand.Haptic(0.06f, 120f, 0.7f);
            Log.InfoOnce("soldiermag:" + gun.name, $"Pulled the magazine ({rounds} rounds) out of {soldier.name}'s {gun.name}: he reloads.");
            return true;
        }

        /// <summary>
        /// His gun wrested from him: a dropped copy made where it was and taken into the hand as a dropped weapon is, and he draws his
        /// other weapon. Knocked aside and held pushed off instead when he has no other weapon to draw, or the copy could not be made.
        /// </summary>
        private bool Wrest(int i, VRHand hand, Actor soldier, Weapon gun)
        {
            int slot = System.Array.IndexOf(soldier.weapons, gun);
            int other = -1;
            for (int s = 0; s < soldier.weapons.Length && other < 0; s++)
            {
                // (Not a grenade, nor a toggled item such as goggles: switched to, it only toggles, and he was left with no gun out.)
                if (s != slot && soldier.weapons[s] != null && !(soldier.weapons[s] is ThrowableWeapon) && !soldier.weapons[s].IsToggleable())
                {
                    other = s;
                }
            }
            if (slot < 0 || other < 0 || !soldier.CanUnholsterWeapon() || _sys.Drops == null)
            {
                return KnockAndHold(i, hand, soldier, gun);
            }
            DroppedWeapon drop = _sys.Drops.DropFromHands(soldier, gun, hand.Velocity * 0.3f / BodyGrab.SlowMotion()); // (game units)
            if (drop == null)
            {
                // (No copy of it can be made (a kind with no model): returned false, the squeeze went on to take the soldier himself.)
                return KnockAndHold(i, hand, soldier, gun);
            }
            soldier.DropWeapon(slot); // (the game destroys it)
            soldier.SwitchWeapon(other); // armed again at once (Unholster sets his active weapon in the call)
            _sys.Drops.TakeInto(drop, hand);
            hand.Haptic(0.08f, 90f, 0.9f);
            Log.InfoOnce("soldierwrest", $"Wrested {gun.name} from {soldier.name}: he draws his {(soldier.activeWeapon != null ? soldier.activeWeapon.name : "nothing")}.");
            return true;
        }

        /// <summary>
        /// Held pushed off instead, for as long as the hand keeps its grip on it (HoldGunOff): "or grabbing the gun" (Tasks 346) where it
        /// cannot be taken from him. True: the squeeze is still his gun's (it moved it, it did not take him).
        /// </summary>
        private bool KnockAndHold(int i, VRHand hand, Actor soldier, Weapon gun)
        {
            KnockAside(soldier, gun, hand.BodyRelativeVelocity.sqrMagnitude > 0.01f ? hand.BodyRelativeVelocity : -soldier.transform.right, hand);
            _holdingGunOf[i] = soldier;
            return true;
        }

        /// <summary>
        /// A hand holding a soldier's gun pushed off: while its grip stays closed and it stays on the gun (20 cm), his aim is kept
        /// thrown off as at the knock and he does not fire; let go (or he goes down, or the gun leaves his hands) and his aim eases
        /// back as after a knock.
        /// </summary>
        private void HoldGunOff(int i, VRHand hand, Vector3 palm)
        {
            Actor soldier = _holdingGunOf[i];
            Weapon gun = soldier != null ? soldier.activeWeapon : null;
            Bounds local = default;
            bool still = hand.GrabHeld && soldier != null && !soldier.dead && gun != null && gun.thirdPersonTransform != null
                         && !(soldier.ragdoll != null && soldier.ragdoll.IsRagdoll());
            still = still && ShapeOf(gun, out local);
            if (still)
            {
                Transform tp = gun.thirdPersonTransform;
                Vector3 closest = tp.TransformPoint(local.ClosestPoint(tp.InverseTransformPoint(palm)));
                still = (closest - palm).sqrMagnitude < 0.2f * 0.2f;
            }
            if (!still)
            {
                _holdingGunOf[i] = null;
                return;
            }
            if (Knocks.TryGetValue(soldier, out Knock k))
            {
                k.At = Time.time; // (the knock kept at its start: full throw, fire held)
            }
            if (Time.unscaledTime >= _nextStrain[i])
            {
                _nextStrain[i] = Time.unscaledTime + 0.25f;
                hand.Haptic(0.02f, 60f, 0.3f); // he pulls against the hand: felt as a light strain while it holds his gun off
            }
        }

        /// <summary>His aim thrown off the way the hand went (its part across his aim), and his fire held a moment.</summary>
        private void KnockAside(Actor soldier, Weapon gun, Vector3 push, VRHand hand)
        {
            Vector3 aim = soldier.controller != null ? soldier.controller.FacingDirection() : soldier.transform.forward;
            Vector3 across = Vector3.ProjectOnPlane(push, aim);
            if (across.sqrMagnitude < 1e-4f)
            {
                return;
            }
            Vector3 axis = Vector3.Cross(aim, across).normalized;
            float strength = Mathf.Clamp01(across.magnitude / 4f);
            Knocks[soldier] = new Knock { At = Time.time, Axis = axis, Angle = KnockAngle * (0.5f + 0.5f * strength) };
            _sys.Sounds.Play(MechSound.MagKnock, gun.thirdPersonTransform.position, 0.6f);
            hand.Haptic(0.04f, 90f, 0.6f);
        }

        /// <summary>How far a soldier's aim is turned right now (the knock eases back over <see cref="KnockTime"/>), or false.</summary>
        internal static bool Knocked(Actor a, out Quaternion turn, out bool holdFire)
        {
            turn = Quaternion.identity;
            holdFire = false;
            if (a == null || Knocks.Count == 0 || !Knocks.TryGetValue(a, out Knock k))
            {
                return false;
            }
            float t = Time.time - k.At;
            if (t >= KnockTime || t < 0f)
            {
                return false;
            }
            float u = 1f - t / KnockTime;
            turn = Quaternion.AngleAxis(k.Angle * u * u, k.Axis);
            holdFire = t < HoldFireTime;
            return true;
        }

        private static void Prune()
        {
            GoneWeapons.Clear();
            foreach (KeyValuePair<Weapon, Bounds> kv in Shapes)
            {
                if (kv.Key == null)
                {
                    GoneWeapons.Add(kv.Key);
                }
            }
            foreach (Weapon w in GoneWeapons)
            {
                Shapes.Remove(w);
            }
            GoneActors.Clear();
            foreach (KeyValuePair<Actor, Knock> kv in Knocks)
            {
                if (kv.Key == null || Time.time - kv.Value.At > KnockTime)
                {
                    GoneActors.Add(kv.Key);
                }
            }
            foreach (Actor a in GoneActors)
            {
                Knocks.Remove(a);
            }
        }

        /// <summary>
        /// Autotest: a point on a soldier's gun: under its middle (its magazine) or toward one end of its length (elsewhere on it),
        /// in the world. False with no gun out.
        /// </summary>
        internal static bool GunPoint(Actor a, bool magazine, out Vector3 point)
        {
            point = default;
            Weapon w = a != null ? a.activeWeapon : null;
            if (w == null || w.thirdPersonTransform == null || !ShapeOf(w, out Bounds local))
            {
                return false;
            }
            Transform tp = w.thirdPersonTransform;
            Vector3 e = local.extents;
            int axis = e.x >= e.y && e.x >= e.z ? 0 : e.y >= e.z ? 1 : 2;
            Vector3 centre = tp.TransformPoint(local.center);
            if (magazine)
            {
                point = centre + Vector3.down * 0.03f; // under its middle (the magazine side for a gun held upright)
                return true;
            }
            Vector3 end = local.center;
            end[axis] += e[axis] * 0.75f;
            point = tp.TransformPoint(end);
            return true;
        }

        /// <summary>A scene change: what was kept for its soldiers and guns forgotten.</summary>
        internal static void Clear()
        {
            Shapes.Clear();
            Knocks.Clear();
            Close.Clear(); // (no soldier of the old scene held)
            CloseAt.Clear();
        }
    }

    /// <summary>A soldier whose gun a hand knocked aside aims off the way it went for a moment (<see cref="SoldierGuns"/>).</summary>
    [HarmonyLib.HarmonyPatch(typeof(AiActorController), nameof(AiActorController.FacingDirection))]
    internal static class SoldierAimKnockPatch
    {
        private static void Postfix(AiActorController __instance, ref Vector3 __result)
        {
            if (SoldierGuns.Knocked(__instance.actor, out Quaternion turn, out _))
            {
                __result = turn * __result;
            }
        }
    }

    /// <summary>... and does not fire for half a second of it.</summary>
    [HarmonyLib.HarmonyPatch(typeof(AiActorController), nameof(AiActorController.WantsToFire))]
    internal static class SoldierHoldFirePatch
    {
        private static void Postfix(AiActorController __instance, ref bool __result)
        {
            if (__result && SoldierGuns.Knocked(__instance.actor, out _, out bool holdFire) && holdFire)
            {
                __result = false;
            }
        }
    }
}
