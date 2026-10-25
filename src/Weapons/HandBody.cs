using RavenfieldVR.Core;
using RavenfieldVR.Input;
using RavenfieldVR.Rig;
using RavenfieldVR.Util;
using UnityEngine;

namespace RavenfieldVR.Weapons
{
    /// <summary>
    /// A fist's physics: a small sphere round each empty hand's fist that pushes ragdolls (a held soldier, a body on the ground)
    /// as a gun's stand-in does (user, 2026-10-25: "the enemies/friendlies don't have collisions with my gun or hand"). Chased to
    /// the fist by its velocity each physics step, so the physics knows how fast it moves. On a layer of its own that meets ragdoll
    /// bones and nothing else: not the player's character controller (which pushes itself out of whatever it overlaps, the hands
    /// sit inside it), not the level, not a magazine the hand reaches for. Only while the hand is empty and closed into a fist or
    /// swung (an open hand reaching to take hold of a body does not push it away first), and never switched on inside a body
    /// (it would shove it out at the physics' depenetration speed). The blow's damage is the punch's (MeleeHandler.HandBlows).
    /// </summary>
    internal sealed class HandBody : MonoBehaviour
    {
        /// <summary>The fist's radius (m) and mass (kg: a fist with a forearm behind it; a limb gives, a torso barely).</summary>
        private const float Radius = 0.045f, Mass = 1.5f;
        /// <summary>A hand this fast against the body (m/s) swings: an open hand pushes too then.</summary>
        private const float SwingSpeed = 2f;
        /// <summary>Further than this from the fist (m, held back or after a jump): put back on it.</summary>
        private const float SnapDistance = 0.3f;
        private const float MaxSpeed = 25f;
        /// <summary>
        /// Against a body a hand holds the fist follows no faster than this (m/s) after its first touch: it presses (a held body
        /// cannot give way, and a fist chased into it at full speed shoved its limbs out of their joints, as the gun's did).
        /// </summary>
        private const float PressSpeed = 2f;
        private float _pressUntil;

        private static readonly HandBody[] Bodies = new HandBody[2];
        private static int _layer = -2;

        private VRHand _hand;
        private Rigidbody _body;
        private SphereCollider _sphere;
        private bool _want;

        /// <summary>
        /// The layer the fists use: an unnamed one (no game object is on it), set to meet ragdoll bones only. -1 when none is free
        /// (no fist physics then).
        /// </summary>
        private static int Layer
        {
            get
            {
                if (_layer != -2)
                {
                    return _layer;
                }
                _layer = -1;
                for (int l = 31; l >= 23; l--)
                {
                    if (string.IsNullOrEmpty(LayerMask.LayerToName(l)) && l != PropPhysics.HiddenFromPlayerLayer)
                    {
                        _layer = l;
                        break;
                    }
                }
                if (_layer >= 0)
                {
                    for (int other = 0; other < 32; other++)
                    {
                        Physics.IgnoreLayerCollision(_layer, other, other != Hitbox.RAGDOLL_LAYER);
                    }
                }
                Log.Info(_layer >= 0 ? $"Fist physics on layer {_layer} (meets ragdoll bones only)." : "Fist physics: no free layer, fists push nothing.");
                return _layer;
            }
        }

        /// <summary>Whether <paramref name="c"/> is one of the fists (a held body's impact with it is the punch's, not a slam).</summary>
        internal static bool Is(Collider c) => c != null && c.GetComponent<HandBody>() != null;

        /// <summary>Once a frame: each hand's fist on or off. <paramref name="allowed"/>: on foot, alive, no menu.</summary>
        internal static void Follow(VRRig rig, bool allowed)
        {
            if (rig == null || Layer < 0)
            {
                return;
            }
            for (int i = 0; i < 2; i++)
            {
                VRHand hand = rig.Hands[i];
                if (hand == null)
                {
                    continue;
                }
                HandBody b = Bodies[i];
                bool want = allowed && hand.Tracked && hand.Role == HandRole.Free && hand.Held == null && !VRGameInput.GunInputBlocked
                            && (VRInput.GetGrip(hand.Side) > 0.6f || hand.BodyRelativeVelocity.sqrMagnitude > SwingSpeed * SwingSpeed);
                if (b == null)
                {
                    if (!want)
                    {
                        continue;
                    }
                    b = Bodies[i] = Create(hand);
                }
                b._hand = hand;
                b._want = want;
            }
        }

        private static HandBody Create(VRHand hand)
        {
            var go = new GameObject("RavenfieldVR Fist " + hand.Side) { layer = Layer };
            Object.DontDestroyOnLoad(go);
            HandBody b = go.AddComponent<HandBody>();
            b._sphere = go.AddComponent<SphereCollider>();
            b._sphere.radius = Radius;
            b._sphere.enabled = false;
            b._body = go.AddComponent<Rigidbody>();
            b._body.mass = Mass;
            b._body.useGravity = false;
            b._body.drag = b._body.angularDrag = 0f;
            b._body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            b._body.constraints = RigidbodyConstraints.FreezeRotation;
            go.transform.position = hand.PalmCentre;
            return b;
        }

        private void FixedUpdate()
        {
            float dt = Time.fixedDeltaTime;
            if (_hand == null || _body == null || dt < 1e-5f)
            {
                return;
            }
            Vector3 fist = _hand.PalmCentre;
            if (!_want)
            {
                if (_sphere.enabled)
                {
                    _sphere.enabled = false;
                }
                _body.velocity = Vector3.zero;
                _body.position = fist;
                return;
            }
            if (!_sphere.enabled)
            {
                // On only where it overlaps no bone (the fist closed inside a body it is about to take, a body just let go of).
                if (Physics.CheckSphere(fist, Radius, 1 << Hitbox.RAGDOLL_LAYER, QueryTriggerInteraction.Ignore))
                {
                    _body.position = fist;
                    _body.velocity = Vector3.zero;
                    return;
                }
                _body.position = fist;
                _body.velocity = Vector3.zero;
                _sphere.enabled = true;
                return;
            }
            Vector3 delta = fist - _body.position;
            if (delta.sqrMagnitude > SnapDistance * SnapDistance)
            {
                _sphere.enabled = false; // (back on from the fist, where it overlaps nothing)
                _body.position = fist;
                _body.velocity = Vector3.zero;
                return;
            }
            _body.velocity = Vector3.ClampMagnitude(delta / dt, Time.time < _pressUntil ? PressSpeed : MaxSpeed);
        }

        private void OnCollisionStay(Collision c)
        {
            if (BodyGrab.IsHeldBone(c.rigidbody))
            {
                _pressUntil = Time.time + Mathf.Max(0.1f, Time.fixedDeltaTime * 3f);
            }
        }

        private void OnDestroy()
        {
            for (int i = 0; i < Bodies.Length; i++)
            {
                if (Bodies[i] == this)
                {
                    Bodies[i] = null;
                }
            }
        }
    }
}
