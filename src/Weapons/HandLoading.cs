using HarmonyLib;
using RavenfieldVR.Core;
using RavenfieldVR.Input;
using RavenfieldVR.Rig;
using RavenfieldVR.Util;
using UnityEngine;

namespace RavenfieldVR.Weapons
{
    /// <summary>
    /// Magazines loaded by hand (Weapons/LoadMagazinesByHand: experimental, off by default). As the user put it after trying the first
    /// version (2026-11-03): "when this setting is on everything is normal but I have infinite bullets, so when i dont have any mags left
    /// that are full, I can take infinite empty mags and fill them up with bullets myself". The pouch hands out magazines from the
    /// reserve as without the option (full while it lasts, then what is left), and once the reserve gives none, empty ones, as many as
    /// wanted. Rounds from the pouch are without end: with the gun put away, a squeeze at the pouch takes one of its magazines, and with
    /// a magazine that is not full in the other hand, one round into the hand; a live round held to the magazine's top goes in. A
    /// magazine in a hand that is not the gun's gives its rounds out one by one on that hand's trigger. Cases and rounds lying around
    /// can be picked up with a free hand and are pushed by bullets. (The first version carried three magazines, two empty in the pouch,
    /// and the reserve as loose rounds.)
    /// Not for machine guns' boxes, en-bloc clips or guns loaded through the action (their reload is round by round already),
    /// nor with the assisted reload.
    /// </summary>
    internal static class HandLoading
    {
        /// <summary>How near (m) a held round's middle must come to a magazine's top to go in.</summary>
        private const float LoadReach = 0.045f;

        /// <summary>How near (m) a free hand's grip point must be to a case or round lying around to take it.</summary>
        private const float TakeReach = 0.06f;

        /// <summary>The push (N s) a bullet gives a case or round it passes through: a 12 g case flies off at about 3 m/s.</summary>
        private const float ShotPush = 0.035f;

        internal static bool Enabled => VRConfig.LoadsMagazinesByHand && VRConfig.ReloadNow == ReloadMode.Manual;

        /// <summary>A single round goes into an open chamber by hand (Weapons/ChamberByHand, with manual reloading).</summary>
        internal static bool ChamberEnabled => VRConfig.ChamberByHand != null && VRConfig.ChamberByHand.Value && VRConfig.ReloadNow == ReloadMode.Manual;

        /// <summary>How near (m) a held round's middle must come to the gun's ejection port to go into the open chamber.</summary>
        private const float ChamberReach = 0.06f;

        private static float _chamberSaidAt = -10f;

        /// <summary>A round made for a hand (the reload's single round from the pouch) is that hand's from here: followed, let go, loaded.</summary>
        internal static void HoldRound(VRHand hand, Casings.Case round) => Hold(hand, round);

        /// <summary>Where the drawn gun's chamber is reached from: its ejection port, or where its slide or bolt is taken.</summary>
        private static Vector3 ChamberWorld(HeldWeapon gun)
        {
            if (Casings.TryPort(gun.ActiveWeapon, out Vector3 port))
            {
                return port;
            }
            return gun.ActiveRig.ToWorld(gun.ActiveRig.Data.RackPoint);
        }

        /// <summary>
        /// A live round of the drawn gun held to its ejection port with the chamber open and empty: it goes in (the user, 2026-11-11:
        /// "make me able to load bullets into chambers of guns"). True when it went in (the round is gone from the hand).
        /// </summary>
        private static bool TryChamber(WeaponSystem sys, VRHand h, Casings.Case c)
        {
            HeldWeapon gun = sys.Held;
            if (!ChamberEnabled || gun == null || !c.Live || c.Key == null || h == sys.Rig.Dominant || gun.ActiveAmmo == null || gun.ActiveRig == null
                || gun.ActiveWeapon == null || gun.ActiveRig.Data.Key != c.Key)
            {
                return false;
            }
            AmmoState ammo = gun.ActiveAmmo;
            Vector3 port = ChamberWorld(gun);
            if (Vector3.Distance(Casings.CentreOf(c), port) > ChamberReach)
            {
                return false;
            }
            if (!ammo.ChamberOpenEmpty || !ammo.ChamberHasRoom)
            {
                // At the port and it cannot go in: said why, now and then (a round held to a shut gun did nothing and said nothing).
                if (Time.unscaledTime - _chamberSaidAt > 1.5f)
                {
                    _chamberSaidAt = Time.unscaledTime;
                    sys.FlashMessage(ammo.Chambered ? "CHAMBER FULL" : !ammo.ChamberHasRoom ? "FULL" : "OPEN THE ACTION");
                    h.Haptic(0.08f, 40f, 0.5f);
                }
                return false;
            }
            if (!ammo.ChamberByHand())
            {
                return false;
            }
            // (A round that came out of no reserve, the pouch's without end when magazines are loaded by hand, is simply in the gun now;
            // one out of the reserve was taken from it when it came into the hand.)
            Casings.LoadedAway(c);
            Free(h);
            sys.Sounds.Play(MechSound.ShellIn, port, 0.6f);
            gun.Nudge(-gun.ActiveRig.UpWorld * 0.03f);
            h.Haptic(0.03f, 150f, 0.55f);
            sys.Rig.Dominant.Haptic(0.02f, 150f, 0.35f);
            Log.InfoOnce("chamber-hand:" + c.Key, $"{c.Key}: a round put into the open chamber by hand ({(ammo.SlideLocked ? "the slide caught back" : "the bolt open")}).");
            return true;
        }

        /// <summary>Whether <paramref name="a"/>'s magazines are loaded by hand: the setting on, a box magazine, not a machine gun's or a clip.</summary>
        internal static bool Applies(AmmoState a) =>
            Enabled && a != null && a.Weapon != null && a.UsesDetachableMag && !a.Rig.IsMachineGun && !a.Rig.EnBloc;

        /// <summary>
        /// The rounds of a magazine from the pouch, loaded by hand: what the reserve gives a fresh one (<paramref name="fresh"/>,
        /// AmmoState.RoundsForFreshMagazine: full while it lasts, as without the option), and none once it gives none: an empty one, to
        /// be filled by hand, as many as wanted. Pure: the offline tests check it.
        /// </summary>
        internal static int PouchMagazineRounds(int fresh) => Mathf.Max(0, fresh);

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
                // Each hand's own trigger: the gun hand's is Fire, the other's Use (VRInput.Slot); read as Fire alone, the off
                // hand's trigger never let a round out.
                DigitalAction trigger = h.Side == VRInput.GunHand ? DigitalAction.Fire : DigitalAction.Use;
                if (sys.Held != null && h == sys.Rig.Dominant || !h.Tracked || !(h.Held is MagazineProp mag) || mag.Go == null
                    || mag.Kind != PropKind.Magazine)
                {
                    continue;
                }
                if (trigger == DigitalAction.Use)
                {
                    // The trigger is the magazine's while it holds one: not the game's Use too (a vehicle beside entered as a round came out).
                    VRGameInput.SupportTriggerOwned = true;
                    VRGameInput.SupportTriggerLightsOnly = false;
                }
                // (Its top is inside the well on the rail: no round out through the gun. Nor with the laser on a panel: the click let one out.)
                if (mag.OnRail || VRGameInput.GunInputBlocked || !VRInput.GetPressed(trigger, h.Side))
                {
                    continue;
                }
                VRInput.Consume(trigger, h.Side);
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
                // (The start of its gun's magazine-out sound, as the round leaves the lips: game audio only, GameSounds.)
                float fullness = mag.Owner != null && mag.Owner.Weapon != null ? (float)mag.Rounds / mag.Owner.Capacity : 0.5f;
                GameSounds.PlayPart(mag.Data, MechSound.MagOut, top, 0f, 0.25f, 0.25f, 1f + 0.3f * fullness);
                h.Haptic(0.015f, 180f, 0.3f);
                if (mag.Rounds == 0)
                {
                    LookAgain(sys, h, mag); // its last round out: drawn as the gun's magazine looks empty
                }
            }
        }

        /// <summary>
        /// A free hand's squeeze at the pouch (no gun in it) with a magazine of a weapon loaded by hand in the other hand, not full:
        /// one round of it into the hand, without end (the reserve is not touched, and the round is no one's: put away unused it goes
        /// to no reserve). True when the squeeze was this (the round taken, or "FULL").
        /// </summary>
        internal static bool RoundFromPouch(WeaponSystem sys, VRHand hand)
        {
            if (!Enabled || hand.Held != null || !hand.GrabPressed)
            {
                return false;
            }
            VRHand other = sys.Rig.Hands[0] == hand ? sys.Rig.Hands[1] : sys.Rig.Hands[0];
            if (!(other.Held is MagazineProp mag) || mag.Kind != PropKind.Magazine || !Applies(mag.Owner) || !sys.Zones.InPouch(hand.Palm.position)
                || sys.Zones.HolsterNearerThanPouch(hand.Palm.position, -1))
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
            Casings.Case round = Casings.RoundInHand(a.Rig, mag.WeaponKey, null, hand);
            Hold(hand, round);
            LogHeldRound(hand, round);
            _roundSeenCheckAt = _roundSeenChecked ? -1f : Time.unscaledTime + 0.6f;
            sys.Sounds.Play(MechSound.Pouch, hand.Palm.position, 0.4f);
            hand.Haptic(0.02f, 160f, 0.35f);
            return true;
        }

        /// <summary>The gun last held is put away and still carried: the pouch works for it (<see cref="MagazineFromPouch"/>, the pouch shown).</summary>
        internal static bool CarriedStill(Weapon last) => Enabled && Carried(last);

        /// <summary>The player still has this weapon (put away, not dropped or swapped).</summary>
        internal static bool Carried(Weapon last)
        {
            Actor player = GameContext.PlayerActor;
            return last != null && player != null && player.weapons != null && System.Array.IndexOf(player.weapons, last) >= 0;
        }

        /// <summary>
        /// With the gun put away, a free hand's squeeze at the pouch, the other hand holding no magazine: a magazine of the gun last held
        /// (<paramref name="last"/>, still carried) out of the pouch into the hand, as the drawn gun's reload hands one out
        /// (<see cref="PouchMagazineRounds"/>: from the reserve, or empty), to be loaded there. Only a drawn gun's reload handed them out,
        /// so loading by hand meant drawing the gun for a magazine and putting it away again with it held. True when it was this.
        /// </summary>
        internal static bool MagazineFromPouch(WeaponSystem sys, VRHand hand, Weapon last)
        {
            if (!Enabled || sys.Held != null || hand.Held != null || !hand.GrabPressed || last == null || !GameContext.PlayerOnFoot || !sys.Zones.InPouch(hand.Palm.position)
                || sys.Zones.HolsterNearerThanPouch(hand.Palm.position, -1)) // (the off hip's pistol: a squeeze for it took a magazine)
            {
                return false;
            }
            VRHand other = sys.Rig.Hands[0] == hand ? sys.Rig.Hands[1] : sys.Rig.Hands[0];
            if (other.Held is MagazineProp || !CarriedStill(last))
            {
                return false;
            }
            AmmoState a = sys.StateFor(last, create: false);
            if (!Applies(a))
            {
                return false;
            }
            int rounds = PouchMagazineRounds(a.RoundsForFreshMagazine());
            WeaponRig rig = sys.RigFor(last);
            MagazineProp prop = rig != null ? MagazineProp.Create(rig, PropKind.Magazine, rounds) : null;
            if (prop == null || prop.Go == null)
            {
                prop?.Destroy();
                return true;
            }
            a.TakeSpare(rounds);
            if (rounds <= 0)
            {
                sys.FlashMessage("EMPTY MAG"); // (the reserve is out: to be filled by hand, not a fault of the pouch)
            }
            prop.Owner = a;
            prop.Attach(hand);
            prop.FollowHand();
            sys.Sounds.Play(MechSound.Pouch, hand.Palm.position, 0.6f);
            hand.Haptic(0.03f, 140f, 0.45f);
            return true;
        }

        /// <summary>A free hand's squeeze that found no magazine or grenade to pick up: a case or round lying within reach of its fingers, taken.</summary>
        internal static bool TakeLying(VRHand hand)
        {
            if (!Enabled && !ChamberEnabled || hand.Held != null)
            {
                return false;
            }
            Casings.Case lying = Casings.Take(hand, hand.Palm.position + hand.Palm.rotation * new Vector3(0f, 0.012f, 0.05f), TakeReach);
            if (lying == null)
            {
                return false;
            }
            Hold(hand, lying);
            WeaponSystem.Instance?.Sounds?.Play(MechSound.ShellIn, hand.Palm.position, 0.3f);
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
                if (_roundSeenCheckAt >= 0f && Time.unscaledTime >= _roundSeenCheckAt)
                {
                    // Once a session: whether a camera really drew it (the user saw none; the line made at the take could not tell).
                    _roundSeenCheckAt = -1f;
                    _roundSeenChecked = true;
                    Log.Info($"Round in the {h.Side} hand a moment on: {(c.Renderer.isVisible ? "drawn by a camera" : "NOT drawn by any camera")}, "
                             + $"layer {c.Go.layer}, {Vector3.Distance(sys.Rig.Head.position, c.Go.transform.position):0.00} m from the eye.");
                }
                if (!h.GrabHeld || !Enabled && !ChamberEnabled)
                {
                    if (c.Live && sys.Zones.InPouch(h.Palm.position) && h.BodyThrowVelocity().magnitude < 1.2f)
                    {
                        // A live round let go at the pouch goes back into it, as a magazine does (it fell to the ground): to its weapon's
                        // reserve if it came out of a magazine, to none if it came from the pouch (those are without end).
                        if (c.Owner != null && c.Owner.Weapon != null)
                        {
                            c.Owner.ReturnSpareCapped(1);
                        }
                        else if (c.Owner != null)
                        {
                            sys.Drops?.ReturnRoundsToOwnDrop(c.Key, 1); // (its rifle let fall since: to that drop, as a magazine's rounds go)
                        }
                        Casings.LoadedAway(c);
                        sys.Sounds.Play(MechSound.Pouch, h.Palm.position, 0.3f);
                        h.Haptic(0.015f, 150f, 0.25f);
                    }
                    else
                    {
                        Casings.LetGo(c, h.ThrowVelocity(), h.AngularVelocity);
                    }
                    Free(h);
                    continue;
                }
                if (TryChamber(sys, h, c))
                {
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
                        // Clicked in a little higher with each round, as the spring under them is pressed down (idea 7: counted by ear):
                        // the end of the gun's own magazine-in sound, the latch's click, game audio only.
                        GameSounds.PlayPart(mag.Data, MechSound.MagIn, top, 0.6f, 0.4f, 0.3f, 0.95f + 0.35f * mag.Rounds / mag.Owner.Capacity);
                        h.Haptic(0.02f, 160f, 0.45f);
                        other.Haptic(0.02f, 160f, 0.45f);
                        if (mag.Rounds == 1)
                        {
                            LookAgain(sys, other, mag);
                        }
                    }
                }
            }
        }

        /// <summary>
        /// An empty magazine given its first round: drawn as the gun's magazine looks loaded from now on. Made empty, it was a copy of
        /// the gun's magazine as it showed then, and a see-through one (the BRN180 EO's) went on showing no rounds as it was filled.
        /// Its last round let out: drawn as the gun's magazine looks empty (MagazineProp.KeepsEmptyLook), not with the rounds it showed.
        /// The same magazine made again with its rounds, in the same hand, where it was; with no such look kept, it stays as it is.
        /// </summary>
        private static void LookAgain(WeaponSystem sys, VRHand hand, MagazineProp mag)
        {
            bool kept = mag.Rounds > 0 ? MagazineProp.KeepsLoadedLook(mag.WeaponKey) : MagazineProp.KeepsEmptyLook(mag.WeaponKey);
            // (With the gun drawn too, but not on the well's line: the reload has it there. Only with the gun away, one emptied by its
            // trigger with the gun drawn kept its rounds' look; scout, 2026-11-03.)
            if (mag.OnRail || mag.Owner == null || mag.Owner.Weapon == null || !kept || !ReferenceEquals(hand.Held, mag))
            {
                return;
            }
            WeaponRig rig = sys.RigFor(mag.Owner.Weapon);
            if (rig == null || rig.Data.Key != mag.WeaponKey)
            {
                return;
            }
            MagazineProp loaded = MagazineProp.Create(rig, PropKind.Magazine, mag.Rounds);
            if (loaded == null || loaded.Go == null)
            {
                loaded?.Destroy();
                return;
            }
            loaded.Owner = mag.Owner;
            mag.Rounds = 0; // (its rounds are the new one's)
            mag.Destroy(); // frees the hand
            loaded.Attach(hand);
            loaded.FollowHand();
        }

        private static bool _loggedRound, _roundSeenChecked;
        private static float _roundSeenCheckAt = -1f;

        /// <summary>Once a session: the round made in a hand, where it is drawn and how (the user saw none; Tasks 359).</summary>
        private static void LogHeldRound(VRHand hand, Casings.Case c)
        {
            if (_loggedRound || c == null || c.Go == null)
            {
                return;
            }
            _loggedRound = true;
            Transform head = WeaponSystem.Instance != null && WeaponSystem.Instance.Rig != null ? WeaponSystem.Instance.Rig.Head : null;
            Renderer r = c.Go.GetComponent<Renderer>();
            Log.Info($"Round from the pouch in the {hand.Side} hand: active {c.Go.activeInHierarchy}, renderer {(r != null && r.enabled ? "on" : "OFF")}, "
                     + $"layer {c.Go.layer}, {(r != null ? r.bounds.size.magnitude * 100f : 0f):0.0} cm across, "
                     + $"{(head != null ? Vector3.Distance(head.position, c.Go.transform.position) : -1f):0.00} m from the eye, glove {(hand.Model != null && hand.Model.Suppressed ? "hidden (weapon's arms: held out at the fingertips)" : "shown")}.");
        }

        /// <summary>A dead hand lets go of a round too: held through the death screen it stayed in the next life's fist, and that hand took nothing else.</summary>
        internal static void LetGoOfRound(VRHand h)
        {
            if (h.Held is Casings.Case c)
            {
                Casings.LetGo(c, h.Velocity, Vector3.zero);
                Free(h);
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
