using RavenfieldVR.Input;
using RavenfieldVR.Util;
using UnityEngine;

namespace RavenfieldVR.Rig
{
    /// <summary>
    /// Getting into a vehicle with either hand. An empty hand (nothing in it: no weapon, magazine, grenade or wheel) that
    /// comes within reach of a vehicle and holds its grip closed on it for a moment takes the nearest free seat to that hand, as the game's own
    /// Use takes the seat under the reticle. A short tick says the hand has found an open vehicle. Locked, dead, enemy and
    /// full vehicles refuse, as the game's own entry does. The Use trigger still gets in as before.
    /// </summary>
    public sealed class VehicleTouchEnter
    {
        /// <summary>How near a palm must be to a vehicle's colliders to count as touching it.</summary>
        private const float TouchRadius = 0.10f;

        private readonly VRRig _rig;
        private readonly Collider[] _hits = new Collider[32];
        /// <summary>The vehicle each hand is touching now (null: none), so the tick plays once when a hand first reaches it.</summary>
        private readonly Vehicle[] _touching = new Vehicle[2];
        /// <summary>
        /// How long the grip must stay closed on the vehicle before the player gets in. This runs before the weapon system, so
        /// on a squeeze it took the press first: a hand behind a car's door reaching for the pouch, a holster or a magazine
        /// lying by the wheel got the player into the car (lead's review, 2026-10-19). Held, any other grip the squeeze is
        /// for takes the hand (its role is no longer Free) and the hold ends.
        /// </summary>
        private const float HoldToEnter = 0.45f;
        private readonly float[] _holdSince = { -1f, -1f };
        private readonly float[] _seenAt = new float[2]; // when each hand last had a vehicle in reach (unscaled)

        public VehicleTouchEnter(VRRig rig)
        {
            _rig = rig;
        }

        /// <summary>Called once per frame by the rig, after the cockpit (a hand on a steering wheel never reaches a door here).</summary>
        public void Update()
        {
            Step();
            for (int h = 0; h < 2; h++)
            {
                VRHand hand = _rig.Hands[h];
                float progress = _touching[h] != null && _holdSince[h] >= 0f ? Mathf.Clamp01((Time.unscaledTime - _holdSince[h]) / HoldToEnter) : -1f;
                EntryHint(h, hand, _touching[h] != null && hand != null && hand.Tracked, progress);
            }
        }

        private readonly GameObject[] _hint = new GameObject[2];

        /// <summary>The rig is gone: the entry glows with it (they outlive scenes).</summary>
        public void Destroy()
        {
            for (int h = 0; h < 2; h++)
            {
                if (_hint[h] != null)
                {
                    Object.Destroy(_hint[h].GetComponent<Renderer>().sharedMaterial);
                    Object.Destroy(_hint[h]);
                    _hint[h] = null;
                }
            }
            if (_hintMaterial != null)
            {
                Object.Destroy(_hintMaterial);
                _hintMaterial = null;
            }
        }
        private Material _hintMaterial;
        private readonly float[] _tickAt = new float[2];

        /// <summary>
        /// A hand on a vehicle it can get into: a glow at the palm, growing and turning green through the half-second hold, with light
        /// ticks as it fills (vehicles scout, 2026-11-15: a single tick said a vehicle was found, nothing showed the hold).
        /// </summary>
        private void EntryHint(int h, VRHand hand, bool touching, float progress)
        {
            if (!touching)
            {
                if (_hint[h] != null && _hint[h].activeSelf)
                {
                    _hint[h].SetActive(false);
                }
                return;
            }
            if (_hint[h] == null)
            {
                _hint[h] = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                _hint[h].name = "RavenfieldVR Entry Hint";
                Object.Destroy(_hint[h].GetComponent<Collider>());
                Object.DontDestroyOnLoad(_hint[h]);
                if (_hintMaterial == null)
                {
                    _hintMaterial = new Material(Shader.Find("Sprites/Default")) { renderQueue = 3100 };
                }
                Renderer r = _hint[h].GetComponent<Renderer>();
                r.sharedMaterial = new Material(_hintMaterial);
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            }
            if (!_hint[h].activeSelf)
            {
                _hint[h].SetActive(true);
            }
            float k = Mathf.Max(0f, progress);
            _hint[h].transform.position = hand.PalmCentre;
            _hint[h].transform.localScale = Vector3.one * (0.03f + 0.04f * k);
            _hint[h].GetComponent<Renderer>().sharedMaterial.color = Color.Lerp(new Color(0.55f, 0.85f, 1f, 0.3f), new Color(0.4f, 1f, 0.5f, 0.75f), k);
            if (progress > 0f && progress < 1f && Time.unscaledTime >= _tickAt[h])
            {
                _tickAt[h] = Time.unscaledTime + 0.1f;
                hand.Haptic(0.01f, 200f, 0.15f + 0.3f * k);
            }
        }

        /// <summary>The hands' search for a seat in reach and the hold to get in (as before).</summary>
        private void Step()
        {
            Actor actor = GameContext.PlayerActor;
            // On foot only: seated, on a ladder, falling or dead the hands do what they did (the wheel, the holsters, the ladder).
            // Not while the game is paused either (the headset off, the dashboard open: no cursor menu, so GameMenuOpen is false):
            // a squeeze on a door then seated the player in a frozen match (rig hunt, 2026-10-21).
            bool onFoot = actor != null && GameContext.PlayerOnFoot && !GameContext.GameMenuOpen && !GameManager.IsPaused() && actor.CanEnterSeat();
            foreach (VRHand hand in _rig.Hands)
            {
                int i = (int)hand.Side;
                if (!onFoot || !hand.Tracked || hand.Role != HandRole.Free || hand.Held != null)
                {
                    _touching[i] = null;
                    _holdSince[i] = -1f;
                    continue;
                }
                Seat seat = NearestFreeSeatInReach(hand.Palm.position, actor, out Vehicle vehicle);
                if (seat == null)
                {
                    // A frame or two off the 10 cm reach (the hand on a moving door, tracking's tremor) keeps the hold: one frame out
                    // reset it, the squeeze had to be made again and the tick of finding the vehicle sounded again (vehicle entry
                    // scout, 2026-11-12). Its seat is found again on the way back; out for longer, it ends.
                    if (_touching[i] != null && Time.unscaledTime - _seenAt[i] < 0.15f)
                    {
                        continue;
                    }
                    _touching[i] = null;
                    _holdSince[i] = -1f;
                    continue;
                }
                _seenAt[i] = Time.unscaledTime;
                if (_touching[i] != vehicle)
                {
                    _touching[i] = vehicle;
                    _holdSince[i] = -1f;
                    hand.Haptic(0.02f, 180f, 0.35f); // the hand has found a vehicle with a seat it can take
                }
                // Unclaimed: a squeeze something else took (a grenade dud, the goggles, a dropped weapon refused) ends the hold, so
                // a fist closed on a dud against a car door does not get in (review, 2026-10-20).
                if (!VRInput.GetHeldUnclaimed(DigitalAction.Grab, hand.Side))
                {
                    _holdSince[i] = -1f;
                    continue;
                }
                float now = Time.unscaledTime;
                if (_holdSince[i] < 0f)
                {
                    // Only a squeeze made on the vehicle starts the hold (not a grip already closed when the hand arrived).
                    if (!hand.GrabPressed && !hand.GrabReaching)
                    {
                        continue;
                    }
                    _holdSince[i] = now;
                }
                if (now - _holdSince[i] < HoldToEnter)
                {
                    continue;
                }
                _holdSince[i] = -1f;
                // The squeeze is the entry's: the wheel or stick does not take the same fist the next frame.
                VRInput.Consume(DigitalAction.Grab, hand.Side);
                if (actor.EnterSeat(seat, kickOutOccupant: false))
                {
                    // As the game's own entry does (FpsActorController.UseButtonHeld): a moment in which Use does not leave, and a
                    // Use held from before not taken as a leave.
                    if (actor.controller is FpsActorController fps)
                    {
                        fps.cannotLeaveAction.Start();
                        fps.useConsumed = true;
                    }
                    hand.Haptic(0.05f, 120f, 0.6f);
                    _touching[i] = null;
                    return; // one seat a frame: the other hand is no longer on foot
                }
                hand.Haptic(0.05f, 40f, 0.3f); // the game refused the seat
            }
        }

        /// <summary>
        /// The free seat nearest the palm, in a vehicle whose colliders the palm is within reach of, or null. Refused the way the
        /// game's EnterSeat refuses: a locked vehicle, a dead one, another team's, and a seat that is taken or not in the scene.
        /// </summary>
        private Seat NearestFreeSeatInReach(Vector3 palm, Actor actor, out Vehicle vehicle)
        {
            vehicle = null;
            Seat best = null;
            float bestSqr = float.MaxValue;
            int hits = Physics.OverlapSphereNonAlloc(palm, TouchRadius, _hits);
            Rigidbody lastBody = null;
            for (int k = 0; k < hits; k++)
            {
                // (Each of a vehicle's colliders under the palm is the same vehicle: looked up once per body, not once per collider;
                // the parent search ran for every hit of both hands each frame; vehicle entry scout.)
                Rigidbody body = _hits[k].attachedRigidbody;
                if (body != null && body == lastBody)
                {
                    continue;
                }
                lastBody = body;
                Vehicle v = body != null ? body.GetComponent<Vehicle>() : null; // (a Unity null is no ??'s null: tested plainly)
                if (v == null)
                {
                    v = _hits[k].GetComponentInParent<Vehicle>();
                }
                if (v == null || v.dead || v.isLocked || v.ownerTeam >= 0 && v.ownerTeam != actor.team)
                {
                    continue;
                }
                foreach (Seat s in v.seats)
                {
                    if (s == null || s.IsOccupied() || !s.gameObject.activeInHierarchy)
                    {
                        continue;
                    }
                    float sqr = (s.transform.position - palm).sqrMagnitude;
                    if (sqr < bestSqr)
                    {
                        bestSqr = sqr;
                        best = s;
                        vehicle = v;
                    }
                }
            }
            return best;
        }
    }
}
