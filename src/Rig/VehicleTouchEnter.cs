using RavenfieldVR.Input;
using RavenfieldVR.Util;
using UnityEngine;

namespace RavenfieldVR.Rig
{
    /// <summary>
    /// Getting into a vehicle with either hand. An empty hand (nothing in it: no weapon, magazine, grenade or wheel) that
    /// comes within reach of a vehicle and squeezes its grip takes the nearest free seat to that hand, as the game's own
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

        public VehicleTouchEnter(VRRig rig)
        {
            _rig = rig;
        }

        /// <summary>Called once per frame by the rig, after the cockpit (a hand on a steering wheel never reaches a door here).</summary>
        public void Update()
        {
            Actor actor = GameContext.PlayerActor;
            // On foot only: seated, on a ladder, falling or dead the hands do what they did (the wheel, the holsters, the ladder).
            bool onFoot = actor != null && GameContext.PlayerOnFoot && !GameContext.GameMenuOpen && actor.CanEnterSeat();
            foreach (VRHand hand in _rig.Hands)
            {
                int i = (int)hand.Side;
                if (!onFoot || !hand.Tracked || hand.Role != HandRole.Free || hand.Held != null)
                {
                    _touching[i] = null;
                    continue;
                }
                Seat seat = NearestFreeSeatInReach(hand.Palm.position, actor, out Vehicle vehicle);
                if (seat == null)
                {
                    _touching[i] = null;
                    continue;
                }
                if (_touching[i] != vehicle)
                {
                    _touching[i] = vehicle;
                    hand.Haptic(0.02f, 180f, 0.35f); // the hand has found a vehicle with a seat it can take
                }
                if (!hand.GrabPressed && !hand.GrabReaching)
                {
                    continue;
                }
                if (actor.EnterSeat(seat, kickOutOccupant: false))
                {
                    VRInput.Consume(DigitalAction.Grab, hand.Side);
                    hand.Haptic(0.05f, 120f, 0.6f);
                    _touching[i] = null;
                    return; // one seat a frame: the other hand is no longer on foot
                }
                if (hand.GrabPressed)
                {
                    hand.Haptic(0.05f, 40f, 0.3f); // the game refused the seat
                }
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
            for (int k = 0; k < hits; k++)
            {
                Vehicle v = _hits[k].GetComponentInParent<Vehicle>();
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
