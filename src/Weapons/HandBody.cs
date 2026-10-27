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
        /// <summary>The frame <see cref="Follow"/> last set it: no longer followed (VR stopped, the grab system gone), it switches off.</summary>
        private int _followedFrame;

        /// <summary>
        /// The layer the fists use, set to meet ragdoll bones only: one with no name that no collider of the scene is on (unnamed was
        /// not enough: a map or a mod may still use one, and its colliders would have stopped colliding with anything but ragdolls),
        /// not the shape camera's (GunVolume) nor the held gun's hiding place. Renderers do not count: a layer's collisions are
        /// nothing to them, and with them counted the full mod list's island2023 left no layer at all (final test, 2026-10-27). Chosen as a scene loads
        /// (<see cref="OnSceneLoaded"/>, a scene-wide search) or on the first fist; a layer given up gets back the collisions it had.
        /// -1 when none is free (no fist physics then).
        /// </summary>
        private static int Layer
        {
            get
            {
                int scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().handle;
                if (_layer != -2 && _layerScene == scene)
                {
                    return _layer;
                }
                _layerScene = scene;
                var used = new bool[32];
                foreach (Collider c in Object.FindObjectsOfType<Collider>())
                {
                    if (c.GetComponent<HandBody>() == null)
                    {
                        used[c.gameObject.layer] = true;
                    }
                }
                int shapeLayer = GunVolume.ShapeLayer;
                int chosen = -1;
                for (int l = 31; l >= 23; l--)
                {
                    if (!used[l] && string.IsNullOrEmpty(LayerMask.LayerToName(l)) && l != PropPhysics.HiddenFromPlayerLayer && l != shapeLayer)
                    {
                        chosen = l;
                        break;
                    }
                }
                if (chosen == _layer)
                {
                    return _layer;
                }
                if (_layer >= 0 && _savedRow != null)
                {
                    for (int other = 0; other < 32; other++)
                    {
                        Physics.IgnoreLayerCollision(_layer, other, _savedRow[other]); // (as it was before the fists took it)
                    }
                }
                _layer = chosen;
                _savedRow = null;
                if (_layer >= 0)
                {
                    _savedRow = new bool[32];
                    for (int other = 0; other < 32; other++)
                    {
                        _savedRow[other] = Physics.GetIgnoreLayerCollision(_layer, other);
                    }
                    for (int other = 0; other < 32; other++)
                    {
                        Physics.IgnoreLayerCollision(_layer, other, other != Hitbox.RAGDOLL_LAYER);
                    }
                    foreach (HandBody b in Bodies)
                    {
                        if (b != null)
                        {
                            b.gameObject.layer = _layer;
                        }
                    }
                }
                if (_layer >= 0)
                {
                    Log.Info($"Fist physics on layer {_layer} (meets ragdoll bones only).");
                }
                else
                {
                    var taken = new System.Text.StringBuilder();
                    for (int l = 23; l < 32; l++)
                    {
                        string name = LayerMask.LayerToName(l);
                        taken.Append($" {l}:{(used[l] ? "collider" : !string.IsNullOrEmpty(name) ? name : l == PropPhysics.HiddenFromPlayerLayer ? "hidden gun" : l == shapeLayer ? "shapes" : "?")}");
                    }
                    Log.Info($"Fist physics: no free layer in this scene, fists push nothing (layers{taken}).");
                }
                return _layer;
            }
        }

        private static int _layerScene = -1;
        private static bool[] _savedRow;

        /// <summary>A new scene: the fists' layer chosen now, in the load (its scene-wide search not on the first punch).</summary>
        internal static void OnSceneLoaded() => _ = Layer;

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
                b._followedFrame = Time.frameCount;
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
            if (!_want || Time.frameCount - _followedFrame > 2)
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
