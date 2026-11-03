using System.Collections.Generic;
using HarmonyLib;
using RavenfieldVR.Core;
using RavenfieldVR.Input;
using RavenfieldVR.Rig;
using RavenfieldVR.Util;
using UnityEngine;

namespace RavenfieldVR.Weapons
{
    /// <summary>
    /// Magazines loaded by hand (Weapons/LoadMagazinesByHand: experimental, off by default; user, 2026-11-03: "you have like 3 mags,
    /// but the other ones are empty and need to be reloaded with ammo that you have and need to put in by hand ... when you have a
    /// mag then in the hand you can put the ammo out one by one when you press the trigger button").
    /// A weapon fed from box magazines carries three: the one in the gun, as the game loads it, and two in the pouch, empty. The
    /// pouch hands out the fullest of those as it is; one let go of at the pouch, or cleared away where it lies, goes back into it
    /// with its rounds (not into the reserve). The reserve is loose rounds: with the gun put away (both hands free), a squeeze at the
    /// pouch with a magazine that is not full in the other hand brings one round out into the hand, and a live round held to the
    /// magazine's top goes in. A magazine in a hand that is not the gun's gives its rounds out one by one on that hand's trigger.
    /// Cases and rounds lying around can be picked up with a free hand and are pushed by bullets.
    /// Not for machine guns' boxes, en-bloc clips or guns loaded through the action (their reload is round by round already),
    /// nor with the assisted reload.
    /// </summary>
    internal static class HandLoading
    {
        /// <summary>How many magazines a weapon carries, the gun's own one of them.</summary>
        internal const int MagazinesCarried = 3;

        /// <summary>How near (m) a held round's middle must come to a magazine's top to go in.</summary>
        private const float LoadReach = 0.045f;

        /// <summary>How near (m) a free hand's grip point must be to a case or round lying around to take it.</summary>
        private const float TakeReach = 0.06f;

        /// <summary>The push (N s) a bullet gives a case or round it passes through: a 12 g case flies off at about 3 m/s.</summary>
        private const float ShotPush = 0.035f;

        internal static bool Enabled => VRConfig.LoadMagazinesByHand.Value && VRConfig.ReloadNow == ReloadMode.Manual;

        /// <summary>The ammunition that holds a weapon's magazines (a fire-mode variant's are its parent's).</summary>
        private static AmmoState Root(AmmoState a) => a.MagazineOf ?? a;

        /// <summary>Whether <paramref name="a"/>'s magazines are loaded by hand: the setting on, a box magazine, not a machine gun's or a clip.</summary>
        internal static bool Applies(AmmoState a) =>
            Enabled && a != null && a.Weapon != null && a.UsesDetachableMag && !a.Rig.IsMachineGun && !a.Rig.EnBloc;

        /// <summary>The magazines of <paramref name="a"/>'s weapon in the pouch, their rounds each: two empty ones at first.</summary>
        private static List<int> BagOf(AmmoState a)
        {
            AmmoState root = Root(a);
            if (root.SpareMagazines == null)
            {
                root.SpareMagazines = new List<int>();
                for (int i = 1; i < MagazinesCarried; i++)
                {
                    root.SpareMagazines.Add(0);
                }
            }
            return root.SpareMagazines;
        }

        /// <summary>The fullest of the weapon's magazines in the pouch, taken out: its rounds; false with none there (all in the gun or out).</summary>
        internal static bool TakeMagazine(AmmoState a, out int rounds)
        {
            List<int> bag = BagOf(a);
            rounds = 0;
            if (bag.Count == 0)
            {
                return false;
            }
            int best = 0;
            for (int i = 1; i < bag.Count; i++)
            {
                if (bag[i] > bag[best])
                {
                    best = i;
                }
            }
            rounds = bag[best];
            bag.RemoveAt(best);
            return true;
        }

        /// <summary>
        /// A magazine of a weapon loaded by hand back into its pouch with its rounds (stowed there, or cleared away where it lay):
        /// false when it is not one, or the pouch already holds three (a soldier's of the same gun taken as well): its rounds then
        /// go to the reserve as any magazine's. Three, not two: with the gun's own out of it, all three can be in the pouch (at two
        /// a stowed one vanished, its rounds into the reserve, and the gun was left with two magazines).
        /// </summary>
        internal static bool PutBack(MagazineProp prop)
        {
            if (prop == null || prop.Kind != PropKind.Magazine || !Applies(prop.Owner))
            {
                return false;
            }
            List<int> bag = BagOf(prop.Owner);
            if (bag.Count >= MagazinesCarried)
            {
                return false;
            }
            bag.Add(Mathf.Clamp(prop.Rounds, 0, prop.Owner.Capacity));
            return true;
        }

        /// <summary>The magazines in the pouch, for the wrist and the log: "2 in the pouch (0, 12 rounds)".</summary>
        internal static string PouchText(AmmoState a)
        {
            List<int> bag = BagOf(a);
            return bag.Count == 0 ? "none in the pouch" : $"{bag.Count} in the pouch ({string.Join(", ", bag)} rounds)";
        }

        /// <summary>
        /// Each frame before the held gun's update: a hand holding a magazine that is not the gun hand gives one round out of its top
        /// on its trigger (a squeeze each).
        /// </summary>
        internal static void Update(WeaponSystem sys)
        {
            if (!Enabled || sys.Rig == null || GameContext.GameMenuOpen || GameManager.IsPaused())
            {
                return;
            }
            foreach (VRHand h in sys.Rig.Hands)
            {
                if (sys.Held != null && h == sys.Rig.Dominant || !h.Tracked || !(h.Held is MagazineProp mag) || mag.Go == null
                    || mag.Kind != PropKind.Magazine || !VRInput.GetPressed(DigitalAction.Fire, h.Side))
                {
                    continue;
                }
                VRInput.Consume(DigitalAction.Fire, h.Side);
                if (mag.Rounds <= 0)
                {
                    h.Haptic(0.05f, 40f, 0.4f); // empty: a dull tap
                    continue;
                }
                mag.Rounds--;
                Transform t = mag.Go.transform;
                Vector3 top = t.TransformPoint(mag.TipLocal), up = t.TransformDirection(mag.InsertDirLocal), front = t.TransformDirection(mag.FrontLocal);
                // Out over the top and forward off the feed lips, as a thumb pushes it; at the hand's own speed besides.
                Casings.RoundOut(mag.Owner != null ? mag.Owner.Rig : null, mag.WeaponKey, mag.Owner, top + up * 0.012f,
                    Quaternion.LookRotation(front, up), h.Velocity + up * 0.7f + front * 0.35f);
                sys.Sounds.Play(MechSound.ShellIn, top, 0.35f);
                h.Haptic(0.015f, 180f, 0.3f);
            }
        }

        /// <summary>
        /// A free hand's squeeze at the pouch (no gun in it) with a magazine of a weapon loaded by hand in the other hand, not full:
        /// one round of it out of the reserve into the hand. True when the squeeze was this (the round taken, or "FULL" / "NO AMMO").
        /// </summary>
        internal static bool RoundFromPouch(WeaponSystem sys, VRHand hand)
        {
            if (!Enabled || hand.Held != null || !hand.GrabPressed)
            {
                return false;
            }
            VRHand other = sys.Rig.Hands[0] == hand ? sys.Rig.Hands[1] : sys.Rig.Hands[0];
            if (!(other.Held is MagazineProp mag) || mag.Kind != PropKind.Magazine || !Applies(mag.Owner) || !sys.Zones.InPouch(hand.Palm.position))
            {
                return false;
            }
            AmmoState a = mag.Owner;
            if (mag.Rounds >= a.Capacity)
            {
                sys.FlashMessage("FULL");
                hand.Haptic(0.12f, 35f, 0.6f);
                return true;
            }
            if (!a.InfiniteSpare && a.SpareAmmo <= 0)
            {
                sys.FlashMessage("NO AMMO");
                hand.Haptic(0.15f, 35f, 0.7f);
                return true;
            }
            a.TakeSpare(1);
            Hold(hand, Casings.RoundInHand(a.Rig, mag.WeaponKey, a, hand));
            sys.Sounds.Play(MechSound.Pouch, hand.Palm.position, 0.4f);
            hand.Haptic(0.02f, 160f, 0.35f);
            return true;
        }

        /// <summary>A free hand's squeeze that found no magazine or grenade to pick up: a case or round lying within reach of its fingers, taken.</summary>
        internal static bool TakeLying(VRHand hand)
        {
            if (!Enabled || hand.Held != null)
            {
                return false;
            }
            Casings.Case lying = Casings.Take(hand, hand.Palm.position + hand.Palm.rotation * new Vector3(0f, 0.012f, 0.05f), TakeReach);
            if (lying == null)
            {
                return false;
            }
            Hold(hand, lying);
            hand.Haptic(0.015f, 170f, 0.3f);
            return true;
        }

        private static void Hold(VRHand hand, Casings.Case c)
        {
            hand.Held = c;
            hand.Role = HandRole.Magazine; // (a small thing in the fingers: no weapon drawn into this hand meanwhile)
        }

        /// <summary>
        /// Before rendering: a case or round in a hand follows its fingers; let go it flies with the hand's throw; a live round held to
        /// the top of a magazine of its own weapon in the other hand, not full, goes into it.
        /// </summary>
        internal static void BeforeRender(WeaponSystem sys)
        {
            if (sys.Rig == null)
            {
                return;
            }
            foreach (VRHand h in sys.Rig.Hands)
            {
                if (!(h.Held is Casings.Case c))
                {
                    continue;
                }
                if (c.Go == null || !c.Go.activeSelf || !c.Held)
                {
                    Free(h); // gone with its scene, or put away
                    continue;
                }
                Casings.Follow(c, h);
                if (!h.GrabHeld || !Enabled)
                {
                    Casings.LetGo(c, h.ThrowVelocity(), h.AngularVelocity);
                    Free(h);
                    continue;
                }
                VRHand other = sys.Rig.Hands[0] == h ? sys.Rig.Hands[1] : sys.Rig.Hands[0];
                if (c.Live && c.Key != null && other.Held is MagazineProp mag && mag.Go != null && mag.Kind == PropKind.Magazine
                    && mag.WeaponKey == c.Key && mag.Owner != null && mag.Owner.Weapon != null && mag.Rounds < mag.Owner.Capacity)
                {
                    Vector3 top = mag.Go.transform.TransformPoint(mag.TipLocal);
                    if (Vector3.Distance(Casings.CentreOf(c), top) < LoadReach)
                    {
                        mag.Rounds++;
                        Casings.LoadedAway(c);
                        Free(h);
                        sys.Sounds.Play(MechSound.ShellIn, top, 0.5f);
                        h.Haptic(0.02f, 160f, 0.45f);
                        other.Haptic(0.02f, 160f, 0.45f);
                    }
                }
            }
        }

        private static void Free(VRHand h)
        {
            h.Held = null;
            if (h.Role == HandRole.Magazine)
            {
                h.Role = HandRole.Free;
            }
        }

        private static int _layer = -1, _layerFrame = -1;

        /// <summary>The loose props' layer, looked up once a frame (bullets ask it every step of every flight).</summary>
        private static int PropLayer
        {
            get
            {
                if (_layerFrame != Time.frameCount)
                {
                    _layerFrame = Time.frameCount;
                    _layer = PropPhysics.Layer;
                }
                return _layer;
            }
        }

        /// <summary>
        /// A bullet's step through the world (Projectile.ProjectileRaycast): a case or round lying on the props' layer (which bullets
        /// otherwise pass through) along it, up to what it hits, is pushed along the bullet's line.
        /// </summary>
        internal static void ShotThrough(Ray ray, float distance)
        {
            int layer = PropLayer;
            if (layer < 0 || !(distance > 0f))
            {
                return;
            }
            if (Physics.Raycast(ray, out RaycastHit hit, distance, 1 << layer, QueryTriggerInteraction.Ignore)
                && hit.rigidbody != null && !hit.rigidbody.isKinematic && Casings.IsCase(hit.collider))
            {
                hit.rigidbody.AddForceAtPosition(ray.direction * ShotPush, hit.point, ForceMode.Impulse);
            }
        }
    }

    /// <summary>Bullets push the cases and rounds lying around (magazines loaded by hand: <see cref="HandLoading.ShotThrough"/>).</summary>
    [HarmonyPatch(typeof(Projectile), nameof(Projectile.ProjectileRaycast))]
    internal static class LooseRoundsShotPatch
    {
        private static void Postfix(Ray ray, float distance, bool __result, ref RaycastHit hitInfo)
        {
            if (HandLoading.Enabled)
            {
                HandLoading.ShotThrough(ray, __result ? hitInfo.distance : distance);
            }
        }
    }
}
