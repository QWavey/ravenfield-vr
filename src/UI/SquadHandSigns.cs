using RavenfieldVR.Core;
using RavenfieldVR.Input;
using RavenfieldVR.Rig;
using RavenfieldVR.Util;
using UnityEngine;

namespace RavenfieldVR.UI
{
    /// <summary>
    /// Squad hand signs with the free hand (task 298). A flat hand, all four fingers and the thumb open, held out in front and
    /// pointing forward, up or down (within 45 degrees of level), sends the squad where the hand points: to the capture point
    /// the line is on, as the pointing does (<see cref="SquadObjectiveMarkers"/>), or to the ground the line meets. A fist, the
    /// four fingers curled, held the same way tells the squad to stay: it stops following the leader and its members stop
    /// where they stand, which the game shows as SQUAD - HOLD. A sign counts once it has been held for 0.5 seconds, with a short
    /// buzz in that hand and the order sound. The same sign is not taken again until the hand shows something else, or nothing
    /// for a moment. No sign while the hand holds something, while a menu is open, while the squad button is held or the order
    /// screen is open, on a ladder, while the wrist is turned to the eyes, or when the player is dead.
    /// </summary>
    internal static class SquadHandSigns
    {
        private enum HandSign { None, Flat, Fist }

        private const float HoldSeconds = 0.5f;
        private const float ReleaseSeconds = 0.25f;  // the hand shows no sign this long before the same sign counts again
        private const float OpenCurl = 0.3f;         // the four fingers at most this curled: open
        private const float ThumbOpenCurl = 0.5f;    // the thumb is looser on a controller, it rests on the buttons
        private const float ClosedCurl = 0.7f;       // the four fingers at least this curled: a fist
        private const float MaxPitch = 45f;          // the flat hand's pointing direction within this of level, up or down
        private const float MaxYaw = 60f;            // ... and within this of the way the head faces
        private const float InFront = 70f;           // the palm within this of the way the head faces
        private const float MinReach = 0.25f;        // the palm at least this far from the eyes
        // The palm no lower than this under the eyes: raised to the shoulder or above, as a hand signal is given. At chest
        // height (0.4 m) an open hand reaching out for a door, a weapon on a table or a magazine on the ground for half a
        // second ordered the squad (lead's review, 2026-10-19).
        private const float MaxBelowEyes = 0.25f;

        private static HandSign _candidate;
        private static float _candidateSince;
        private static HandSign _given;

        /// <summary>
        /// Each frame of the game's update, after the pointing (<see cref="SquadPointing.Compute"/>): reads the free hand's sign and
        /// gives the order when a sign has been held long enough.
        /// </summary>
        internal static void Update()
        {
            VRRig rig = VRRig.Instance;
            if (rig == null || !VRManager.Active)
            {
                return;
            }
            float now = Time.unscaledTime;
            if (!CanSign())
            {
                // No signs here (a menu, the squad button or the order screen, a ladder, dead). The shape the hand shows now is
                // taken as given, so the pointing's order is not followed by the same hand's sign when the block lifts.
                _given = Detect(rig.Off, rig);
                _candidate = HandSign.None;
                _candidateSince = now;
                return;
            }
            HandSign sign = Detect(rig.Off, rig);
            if (sign != _candidate)
            {
                _candidate = sign;
                _candidateSince = now;
            }
            if (sign == HandSign.None)
            {
                if (_given != HandSign.None && now - _candidateSince >= ReleaseSeconds)
                {
                    _given = HandSign.None; // nothing shown for a moment: the same sign may count again
                }
                return;
            }
            if (sign == _given || now - _candidateSince < HoldSeconds)
            {
                return;
            }
            _given = sign; // taken: no repeat until the hand changes sign (an order that found no ground still counts as taken)
            Give(sign, rig.Off);
        }

        /// <summary>A new scene: no sign is in progress and the next one counts from the start.</summary>
        internal static void Reset()
        {
            _candidate = HandSign.None;
            _candidateSince = 0f;
            _given = HandSign.None;
        }

        /// <summary>
        /// Signs are read only in a match, with the player alive, and when no menu, squad button, order screen, ladder or wrist view is
        /// up, nor the match paused (the SteamVR dashboard, the headset off: no menu is up then, and a hand held still ordered the
        /// squad behind the pause; round-7 scout).
        /// </summary>
        private static bool CanSign()
        {
            if (!GameContext.InGame || !GameContext.PlayerAlive || GameContext.GameMenuOpen || GameManager.IsPaused())
            {
                return false;
            }
            // (Not from a seat: a driver's or gunner's open hand resting level for half a second was read as "go there" and sent the
            // squad off; scout, 2026-11-11. The hands are the vehicle's there.)
            Actor seated = FpsActorController.instance != null ? FpsActorController.instance.actor : null;
            if (seated != null && seated.IsSeated())
            {
                return false;
            }
            if (UISystem.Instance == null || UISystem.Instance.MenuMode)
            {
                return false;
            }
            if (VRInput.GetHeld(DigitalAction.Squad))
            {
                return false; // the squad button is held: its pointing order is being given
            }
            if (VRGameInput.WristLooking || VRGameInput.WristLookGesture)
            {
                return false; // the wrist is turned to the eyes (the wrist view): an open hand there is no sign
            }
            Actor actor = GameContext.PlayerActor;
            if (actor == null || actor.IsOnLadder())
            {
                return false; // on a ladder the hands grip the rungs, curled, in front of the chest: that is no sign
            }
            SquadPointUi ui = SquadPointUi.instance;
            return ui == null || ui.canvas == null || !ui.canvas.enabled;
        }

        /// <summary>What the hand shows right now: a flat hand held pointing forward, a fist held in front, or nothing.</summary>
        private static HandSign Detect(VRHand hand, VRRig rig)
        {
            if (!hand.Tracked || !VRInput.HasSkeleton(hand.Side) || hand.Role != HandRole.Free || hand.Held != null)
            {
                return HandSign.None; // no finger tracking, or the hand holds something
            }
            Transform head = rig.Head;
            Vector3 up = rig.Root.up;
            Vector3 palm = hand.PalmCentre;
            Vector3 fromEye = palm - head.position;
            if (fromEye.magnitude < MinReach || Vector3.Angle(fromEye, head.forward) > InFront || Vector3.Dot(head.position - palm, up) > MaxBelowEyes)
            {
                return HandSign.None; // not held out in front, at chest height
            }
            float thumb = VRInput.GetCurl(hand.Side, 0);
            float index = VRInput.GetCurl(hand.Side, 1);
            float middle = VRInput.GetCurl(hand.Side, 2);
            float ring = VRInput.GetCurl(hand.Side, 3);
            float little = VRInput.GetCurl(hand.Side, 4);
            float most = Mathf.Max(Mathf.Max(index, middle), Mathf.Max(ring, little));
            float least = Mathf.Min(Mathf.Min(index, middle), Mathf.Min(ring, little));
            if (most <= OpenCurl && thumb <= ThumbOpenCurl)
            {
                // Flat: the hand's own pointing direction, level within 45 degrees (up or down), and roughly the way the head faces.
                Vector3 forward = hand.Pointer.Forward;
                float pitch = Mathf.Asin(Mathf.Clamp(Vector3.Dot(forward, up), -1f, 1f)) * Mathf.Rad2Deg;
                Vector3 pointing = Vector3.ProjectOnPlane(forward, up);
                Vector3 facing = Vector3.ProjectOnPlane(head.forward, up);
                bool forwardish = pointing.sqrMagnitude > 1e-6f && facing.sqrMagnitude > 1e-6f && Vector3.Angle(pointing, facing) <= MaxYaw;
                return Mathf.Abs(pitch) <= MaxPitch && forwardish ? HandSign.Flat : HandSign.None;
            }
            // A fist: the four fingers curled. The thumb is not counted, since a controller reads it only from its thumb touch.
            return least >= ClosedCurl ? HandSign.Fist : HandSign.None;
        }

        /// <summary>The sign's order and its feedback: a short buzz in the hand, the game's order sound and tally when an order went out.</summary>
        private static void Give(HandSign sign, VRHand hand)
        {
            FpsActorController controller = LocalPlayer.controller;
            bool ordered = false;
            if (controller != null)
            {
                ordered = sign == HandSign.Flat ? OrderAlongHand(controller, hand) : OrderStay(controller);
            }
            if (ordered)
            {
                if (SquadPointUi.instance != null)
                {
                    SquadPointUi.PlayCommitOrder();
                }
                if (GameManager.gameResultsTracker != null)
                {
                    GameManager.gameResultsTracker.PlayerCommandedGrunt();
                }
            }
            hand.Haptic(0.09f, 150f, 0.6f);
        }

        /// <summary>Soldiers of the player's squad to take an order (a player alone got the order's sound and grunt for nothing).</summary>
        internal static bool HasSquadmates()
        {
            Squad squad = LocalPlayer.squad;
            return squad != null && squad.aiMembers != null && squad.aiMembers.Count > 0;
        }

        /// <summary>
        /// The flat hand's order, the same as the pointing's: the capture point the line is on goes to its own order (attack or
        /// defend, as the game gives it for that flag), otherwise the squad goes to the ground the line meets.
        /// </summary>
        private static bool OrderAlongHand(FpsActorController controller, VRHand hand)
        {
            if (!HasSquadmates())
            {
                return false;
            }
            Vector3 eye = VRRig.Instance.Head.position;
            Ray line = new Ray(eye, (hand.PalmCentre - eye).normalized);
            SpawnPoint flag = SquadObjectiveMarkers.Aimed(line, eye);
            if (flag != null)
            {
                if (flag.squadOrderPoint.type == SquadOrderPoint.ObjectiveType.Defend)
                {
                    controller.SquadOrderDefend(flag);
                }
                else
                {
                    controller.SquadOrderAttack(flag);
                }
                return true;
            }
            // The ground, as the game's own ground order finds it: water first when it is nearer, then solid ground at any range.
            bool hitWater = WaterLevel.Raycast(line, out RaycastHit water);
            bool hitGround = Physics.Raycast(line, out RaycastHit ground, 999999f, 1);
            if (hitWater && (!hitGround || water.distance < ground.distance))
            {
                ground = water;
                hitGround = true;
            }
            if (!hitGround)
            {
                return false; // the line meets nothing: no order
            }
            controller.SquadOrderGoto(ground.point);
            return true;
        }

        /// <summary>
        /// The fist's order: the squad stops following its leader (the game's ungroup) and each member's path is cancelled, so
        /// they stand where they are; the game then shows SQUAD - HOLD. The leader's halt gesture and the order marker's removal
        /// are what the game does when a squad is held (Squad.DefendSpawn, StrategyUi.HideSquadOrderMarker).
        /// </summary>
        private static bool OrderStay(FpsActorController controller)
        {
            Squad squad = LocalPlayer.squad;
            if (squad == null || !HasSquadmates())
            {
                return false;
            }
            squad.Ungroup();
            foreach (AiActorController member in squad.aiMembers)
            {
                if (member != null)
                {
                    member.CancelPath(); // (one destroyed this frame threw past the order's marker, halt and sound; squad scout)
                }
            }
            StrategyUi.HideSquadOrderMarker();
            ActorController leader = squad.Leader();
            if (leader != null && leader.actor != null)
            {
                leader.actor.EmoteHalt();
            }
            return true;
        }
    }
}
