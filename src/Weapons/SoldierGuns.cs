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
        /// <summary>Soldiers farther than this from the hand are not looked at (m).</summary>
        private const float LookWithin = 2.5f;

        private readonly WeaponSystem _sys;
        private readonly VRRig _rig;
        private readonly float[] _lastKnock = { -10f, -10f };

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
            for (int i = 0; i < 2; i++)
            {
                VRHand hand = _rig.Hands[i];
                if (hand == null || !hand.Tracked)
                {
                    continue;
                }
                Vector3 palm = hand.PalmCentre;
                if (!NearestGun(palm, player, out Actor soldier, out Weapon gun, out float distance) || distance > Reach)
                {
                    continue;
                }
                // A squeeze on his gun, not on him: his hands hold it, and a hand closed on his wrist or arm takes him (BodyGrab: "grab
                // them on the hand or arm", user, 2026-10-27). The gun only where it is clearly nearer than any of him (its box is a
                // little bigger than the gun).
                if (hand.Role == HandRole.Free && hand.Held == null && !VRGameInput.GunInputBlocked && VRInput.GetPressed(DigitalAction.Grab, hand.Side)
                    && distance + 0.02f < BodyDistance(soldier, palm))
                {
                    bool done = OnMagazine(gun, palm) ? TakeMagazine(hand, soldier, gun) || Wrest(hand, soldier, gun) : Wrest(hand, soldier, gun);
                    if (done)
                    {
                        VRInput.Consume(DigitalAction.Grab, hand.Side); // his gun took the squeeze: not him (BodyGrab) nor anything else
                        continue;
                    }
                }
                Vector3 swing = hand.BodyRelativeVelocity;
                if (swing.sqrMagnitude > SwingSpeed * SwingSpeed && Time.time - _lastKnock[i] > 0.4f)
                {
                    _lastKnock[i] = Time.time;
                    KnockAside(soldier, gun, swing, hand);
                }
            }
        }

        /// <summary>The soldier's gun nearest a point, on his feet with it out: its distance from the point (m, 0 inside it).</summary>
        private static bool NearestGun(Vector3 point, Actor player, out Actor soldier, out Weapon gun, out float distance)
        {
            soldier = null;
            gun = null;
            distance = float.MaxValue;
            foreach (Actor a in ActorManager.instance.actors)
            {
                if (a == null || a == player || a.dead || !a.aiControlled || a.IsSeated() || (a.transform.position - point).sqrMagnitude > LookWithin * LookWithin)
                {
                    continue;
                }
                if (a.ragdoll != null && a.ragdoll.IsRagdoll())
                {
                    continue; // down: the gun is not in his hands
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
        /// The magazine pulled out of his gun: the gun empty (his AI reloads it), the magazine in the hand with its rounds when his gun's
        /// kind is analysed (a magazine of that kind is made from it), else only taken out. False for a gun with no magazine to pull (a
        /// tube, a break action, a launcher, a blade, already empty or reloading).
        /// </summary>
        private bool TakeMagazine(VRHand hand, Actor soldier, Weapon gun)
        {
            if (gun.ammo <= 0 || gun.reloading || gun.configuration == null || gun.configuration.ammo <= 1 || gun is MeleeWeapon || gun is ThrowableWeapon)
            {
                return false;
            }
            // (Only an analysis already made: made now, it is tens to hundreds of milliseconds on this frame.)
            WeaponRig rig = WeaponAnalyzer.IsCached(gun, gun.weaponEntry) ? _sys.RigFor(gun) : null;
            if (rig != null && !(rig.Data.Action == ActionType.SelfLoading || rig.Data.Action == ActionType.Manual && rig.Data.MagazinePath != null))
            {
                return false; // a tube, a break action, a single shot: nothing comes out under it
            }
            int rounds = gun.ammo;
            gun.ammo = 0;
            if (rig != null)
            {
                try
                {
                    MagazineProp prop = MagazineProp.Create(rig, PropKind.Magazine, rounds);
                    prop.Attach(hand);
                    prop.FollowHand();
                }
                catch (System.Exception e)
                {
                    Log.ExceptionOnce("SoldierGuns.TakeMagazine", e);
                }
            }
            _sys.Sounds.Play(MechSound.MagOut, hand.PalmCentre, 0.8f);
            hand.Haptic(0.06f, 120f, 0.7f);
            Log.InfoOnce("soldiermag:" + gun.name, $"Pulled the magazine ({rounds} rounds) out of {soldier.name}'s {gun.name}: he reloads.{(rig == null ? " (His gun's kind not analysed yet: no magazine in the hand.)" : "")}");
            return true;
        }

        /// <summary>
        /// His gun wrested from him: a dropped copy made where it was and taken into the hand as a dropped weapon is, and he draws his
        /// other weapon. False (knocked aside instead) when he has no other weapon to draw, or the copy could not be made.
        /// </summary>
        private bool Wrest(VRHand hand, Actor soldier, Weapon gun)
        {
            int slot = System.Array.IndexOf(soldier.weapons, gun);
            int other = -1;
            for (int s = 0; s < soldier.weapons.Length && other < 0; s++)
            {
                if (s != slot && soldier.weapons[s] != null && !(soldier.weapons[s] is ThrowableWeapon))
                {
                    other = s;
                }
            }
            if (slot < 0 || other < 0 || !soldier.CanUnholsterWeapon() || _sys.Drops == null)
            {
                KnockAside(soldier, gun, hand.BodyRelativeVelocity.sqrMagnitude > 0.01f ? hand.BodyRelativeVelocity : -soldier.transform.right, hand);
                return true; // (the squeeze is still his gun's: it moved it, it did not take him)
            }
            DroppedWeapon drop = _sys.Drops.DropFromHands(soldier, gun, hand.Velocity * 0.3f);
            if (drop == null)
            {
                return false;
            }
            soldier.DropWeapon(slot); // (the game destroys it)
            soldier.SwitchWeapon(other); // armed again at once (Unholster sets his active weapon in the call)
            _sys.Drops.TakeInto(drop, hand);
            hand.Haptic(0.08f, 90f, 0.9f);
            Log.InfoOnce("soldierwrest", $"Wrested {gun.name} from {soldier.name}: he draws his {(soldier.activeWeapon != null ? soldier.activeWeapon.name : "nothing")}.");
            return true;
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
