using System.Collections.Generic;
using HarmonyLib;
using RavenfieldVR.Core;
using RavenfieldVR.Input;
using RavenfieldVR.Rig;
using RavenfieldVR.UI;
using RavenfieldVR.Util;
using UnityEngine;

namespace RavenfieldVR.Weapons
{
    /// <summary>
    /// An empty hand takes hold of a soldier, living or dead, and can drag, swing and throw them (user, 2026-10-24: "I cannot grab
    /// people with my hand and throw them around with them taking damage"). As Boneworks does it (Tasks 334): its enemies are
    /// ragdolls (PuppetMaster) the hand takes hold of, and an impact past a threshold is blunt damage scaled by its speed
    /// (Enemy_Health's damageFromImpact / thr_Impact, ImpactSFX's bluntAttack). Here: the grip near a soldier knocks them over
    /// (the game's own ragdoll, Actor.FallOver), the hand holds the nearest ragdoll bone through a joint (fixed in place, its turn
    /// damped and torque-limited) to a body that follows the hand, and while held and for a few seconds after a throw, every hard impact of that ragdoll (a wall, the
    /// ground, another soldier) hurts it (<see cref="BodyImpacts"/>). Held, the soldier does not get up.
    /// Only a hand that holds nothing takes hold: a hand on a gun, a tool, a grenade or a magazine never does (its role is not
    /// Free), the other, empty hand does (user, 2026-10-24).
    /// Also (user, 2026-10-24): a soldier held in front is a human shield (bullets hit him, his own team's too:
    /// <see cref="HumanShieldPatch"/>); taken by the head from behind, a living soldier is held still and a twist of the wrist
    /// breaks his neck (<see cref="NeckTwist"/>); a live grenade let go on a held body is strapped to it (<see cref="StrappedGrenade"/>).
    /// </summary>
    internal sealed class BodyGrab
    {
        /// <summary>
        /// How far from the fist's hollow a soldier's collider may be to be taken (13 cm, and a hand reaching for a walking soldier
        /// "grabs the air many times": user, 2026-10-25).
        /// </summary>
        private const float Reach = 0.16f;
        /// <summary>
        /// The drawn hand's middle (the fist's hollow) sits this far off the taken bone's surface: the palm on a chest, the fingers
        /// round an arm or a leg (m).
        /// </summary>
        private const float HandSkin = 0.02f;
        /// <summary>How long a squeeze of the grip still takes a soldier the hand reaches (s; the other grabs' window is 0.25).</summary>
        private const float GrabWindow = 0.4f;
        /// <summary>A hand faster than this (m/s, against the body) punches rather than takes hold (the melee swing speed is 2.6).</summary>
        private const float GrabMaxSpeed = 2.6f;
        /// <summary>
        /// A squeeze takes hold only when it began with a soldier this near (m): a punch closes the fist before its swing, away from
        /// whom it hits, and with the longer reach and window above a fist closed and swung took hold of the soldier instead of
        /// punching him ("punching enemies doesn't work": user, 2026-10-25).
        /// </summary>
        private const float SqueezeNear = 0.35f;
        private readonly bool[] _squeezedNear = new bool[2];
        /// <summary>How fast each hand moved as its squeeze began (m/s, against the body).</summary>
        private readonly float[] _squeezeSpeed = new float[2];

        /// <summary>
        /// A hand faster than this after its squeeze swings (a punch): faster than a reach, and faster than it was as it squeezed,
        /// by half a metre a second. A punch closes the fist and then speeds up; a reach for a moving soldier squeezes still moving
        /// fast and slows down to him (spent by the speed alone, a quick reach would grab the air again).
        /// </summary>
        private float SwungSpeed(int i) => Mathf.Max(GrabMaxSpeed, _squeezeSpeed[i] + 0.5f);

        /// <summary>How long after the throw the body's impacts still hurt.</summary>
        private const float ImpactsAfterThrow = 3f;
        /// <summary>Stretched this far off the hand (wedged against a wall, the arm pulled on): let go.</summary>
        private const float MaxStretch = 0.6f;
        /// <summary>
        /// A held body's bones never move faster than the hand by more than this (m/s), and a throw leaves at most
        /// <see cref="MaxThrowSpeed"/>: a ragdoll pushed into the ground or a wall was shot out of it by the physics (a throw
        /// left at 49.6 m/s from a 6 m/s swing and one impact read 62 m/s in the autotest, 2026-10-24).
        /// </summary>
        private const float MaxOverHand = 15f, MaxThrowSpeed = 22f;
        /// <summary>
        /// The hand moved this far in one frame (a snap turn, a teleport, a long frame): the body is carried with it, turned as
        /// the hand turned, instead of dragged after it (dragged at the speed limit above, the stretch let go of it).
        /// </summary>
        private const float JumpDistance = 0.25f;
        /// <summary>
        /// Or turned this far in one frame (degrees): a snap turn with the hand near the head moves it less than
        /// <see cref="JumpDistance"/>, and the body whipped round after it at 15 m/s, lifting off (soldier check, 2026-10-25).
        /// </summary>
        private const float JumpAngle = 20f;
        /// <summary>
        /// The hold's turn, per kilogram of the body held (see <see cref="Attach"/>): its spring (N m per radian), damper (N m s
        /// per radian) and the most torque it gives (N m). The damper about critical for a body hung by a bone.
        /// </summary>
        private const float HoldTwist = 30f, HoldTwistDamper = 8f, HoldTwistTorque = 20f;
        /// <summary>
        /// A held body crushed against something that does not give (one contact of a bone over this, N s) is let go: run into a
        /// wall with a soldier held, the hand pressed him into it with no limit, 3000 N s in a step, and he was flung (the soldier
        /// check's run, 2026-10-25). Dragged over rocks and slammed down, a contact stays under about 300. (Not a break force on the
        /// hold: its force spikes past 50 g for a step in plain holding, and broken there it flung the body.)
        /// </summary>
        internal const float CrushImpulse = 1000f;

        /// <summary>The ragdoll's whole mass (kg), at least 1.</summary>
        private static float BodyMass(Actor a)
        {
            float mass = 0f;
            if (a.ragdoll.rigidbodies != null)
            {
                foreach (Rigidbody rb in a.ragdoll.rigidbodies)
                {
                    mass += rb != null ? rb.mass : 0f;
                }
            }
            return Mathf.Max(1f, mass);
        }

        /// <summary>
        /// The head within this of the fist's hollow, taken from behind: a neck hold, not a knock-down (22 cm missed a hand on the
        /// back of the neck: "neck snapping doesn't work", user, 2026-10-25).
        /// </summary>
        private const float NeckReach = 0.3f;
        /// <summary>How far behind: the way the soldier looks within this of the way from the player to him (degrees; behind his shoulders).</summary>
        private const float BehindDegrees = 90f;
        /// <summary>The wrist turned this far from where it took hold (degrees): the neck breaks (70 was near the end of a wrist's roll).</summary>
        internal const float NeckTwist = 55f;
        /// <summary>How near to a held body's bone a live grenade must be let go to be strapped to it (m).</summary>
        internal const float StrapReach = 0.3f;

        private readonly VRRig _rig;
        private readonly Hold[] _holds = new Hold[2];
        private Actor _carriedActor;
        private int _carriedFrame = -1;
        private readonly Rigidbody[] _anchors = new Rigidbody[2];
        private static readonly Collider[] Near = new Collider[48];

        private sealed class Hold
        {
            public Actor Actor;
            public Rigidbody Bone;
            public ConfigurableJoint Joint;
            /// <summary>How far the bone's pivot was from the palm when taken (its stretch is measured from there).</summary>
            public float Reach;
            /// <summary>Where the drawn hand's middle sits on the bone (its own space): a hand's skin off where the palm met its surface.</summary>
            public Vector3 SeatAt;
            /// <summary>A living soldier held by the head from behind, standing (no joint, his AI paused).</summary>
            public bool Neck;
            public Quaternion NeckStart;
            /// <summary>Where the hand was last frame (to carry the body over a jump of the hand).</summary>
            public Vector3 LastPalm;
            public Quaternion LastRotation;
            /// <summary>The gun stand-in this body does not collide with until the two are apart (null: none), and the last one seen.</summary>
            public GunCollider IgnoredGun, SeenGun;
            /// <summary>The body's colliders (its own pairs ignored while held).</summary>
            public Collider[] Colliders;
            /// <summary>The <see cref="PropPhysics.PlayerGeneration"/> the player's colliders were last ignored for.</summary>
            public int PlayerGeneration = -1;
        }

        public BodyGrab(VRRig rig)
        {
            _rig = rig;
            _instance = this;
        }

        private static BodyGrab _instance;

        /// <summary>
        /// A held soldier slammed into something: each hand holding them feels it, harder the more it hurt (Boneworks' hands
        /// feel every impact of what they hold).
        /// </summary>
        internal static void FeelImpact(Actor a, float damage)
        {
            BodyGrab g = _instance;
            if (g == null || a == null)
            {
                return;
            }
            for (int i = 0; i < 2; i++)
            {
                if (g._holds[i] != null && g._holds[i].Actor == a && g._rig.Hands[i] != null)
                {
                    g._rig.Hands[i].Haptic(0.06f, 70f, Mathf.Clamp(0.3f + damage / BodyImpacts.Cap, 0.3f, 1f));
                }
            }
        }

        /// <summary>
        /// Who a collider on a hitbox layer belongs to: a standing soldier by his hitboxes; one already down (thrown, knocked
        /// over, dead) has only his ragdoll's colliders enabled, on a hitbox layer too, which may carry no Hitbox: the soldier
        /// above them then. (The grab and the gun bash both.)
        /// </summary>
        internal static Hurtable HurtableOf(Collider c)
        {
            Hitbox hb = c.GetComponent<Hitbox>();
            return hb != null ? hb.parent : c.GetComponentInParent<Actor>();
        }

        /// <summary>
        /// Whether either hand holds a soldier at all: plain fields, no Unity object compared (the human shield asks it for
        /// every bullet tested against a hitbox in the match).
        /// </summary>
        internal static bool AnyHeld
        {
            get
            {
                BodyGrab g = _instance;
                return g != null && (g._holds[0] != null || g._holds[1] != null);
            }
        }

        /// <summary>Whether the player holds this soldier (in either hand, alive or dead): bullets hit him, whoever fired.</summary>
        internal static bool IsHeld(Actor a)
        {
            BodyGrab g = _instance;
            if (g == null)
            {
                return false;
            }
            Hold h0 = g._holds[0], h1 = g._holds[1];
            return (h0 != null || h1 != null) && a != null && (h0 != null && h0.Actor == a || h1 != null && h1.Actor == a);
        }

        /// <summary>A body crushed against something that does not give is let go by every hand holding it (see <see cref="CrushImpulse"/>).</summary>
        internal static void LetGoCrushed(Actor a, Collider against, float impulse)
        {
            BodyGrab g = _instance;
            if (g == null || a == null)
            {
                return;
            }
            for (int i = 0; i < 2; i++)
            {
                Hold h = g._holds[i];
                if (h != null && !h.Neck && h.Actor == a)
                {
                    if (!Log.Logged("bodygrab-crushed"))
                    {
                        Log.InfoOnce("bodygrab-crushed", $"Let go of {a.name}: crushed against {against.name} ({impulse:0} N s in a step).");
                    }
                    g.Release(i, g._rig.Hands[i], thrown: false);
                }
            }
        }

        /// <summary>Whether <paramref name="rb"/> is a bone of a body a hand holds as a ragdoll.</summary>
        internal static bool IsHeldBone(Rigidbody rb)
        {
            BodyGrab g = _instance;
            if (g == null || rb == null)
            {
                return false;
            }
            foreach (Hold h in g._holds)
            {
                if (h != null && !h.Neck && h.Actor != null && h.Actor.ragdoll != null && h.Actor.ragdoll.rigidbodies != null
                    && System.Array.IndexOf(h.Actor.ragdoll.rigidbodies, rb) >= 0)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>The bone a hand holds a body by (the autotest's), or null.</summary>
        internal static Rigidbody HeldBone(VRHand hand)
        {
            BodyGrab g = _instance;
            Hold h = g != null && hand != null ? g._holds[(int)hand.Side] : null;
            return h != null ? h.Bone : null;
        }

        /// <summary>Whether this hand holds a soldier by the neck (the autotest's execution check).</summary>
        internal static bool HoldsNeck(VRHand hand)
        {
            BodyGrab g = _instance;
            Hold h = g != null && hand != null ? g._holds[(int)hand.Side] : null;
            return h != null && h.Neck;
        }

        /// <summary>
        /// The bone of a body held as a ragdoll (either hand) nearest <paramref name="at"/> within <paramref name="within"/>, or
        /// null: where a live grenade let go there is strapped (<see cref="StrappedGrenade"/>).
        /// </summary>
        internal static Rigidbody StrapBone(Vector3 at, float within) => StrapBone(at, within, out _);

        /// <summary>The same, with the soldier whose bone it is.</summary>
        internal static Rigidbody StrapBone(Vector3 at, float within, out Actor carrier)
        {
            carrier = null;
            BodyGrab g = _instance;
            if (g == null)
            {
                return null;
            }
            Rigidbody best = null;
            float bestDistance = within * within;
            foreach (Hold h in g._holds)
            {
                if (h == null || h.Neck || h.Actor == null || h.Actor.ragdoll == null || h.Actor.ragdoll.rigidbodies == null)
                {
                    continue;
                }
                foreach (Rigidbody rb in h.Actor.ragdoll.rigidbodies)
                {
                    if (rb == null)
                    {
                        continue;
                    }
                    float d = BoneDistanceSq(rb, at);
                    if (d <= bestDistance)
                    {
                        bestDistance = d;
                        best = rb;
                        carrier = h.Actor;
                    }
                }
            }
            return best;
        }

        /// <summary>Once a frame, after every other grab has had the press: free hands take hold of a soldier, held ones follow.</summary>
        public void Update()
        {
            BodyImpacts.DisarmExpired();
            Actor player = GameContext.PlayerActor;
            bool canHold = player != null && !player.dead && GameContext.PlayerOnFoot;
            for (int i = 0; i < 2; i++)
            {
                VRHand hand = _rig.Hands[i];
                if (hand == null)
                {
                    continue;
                }
                Hold hold = _holds[i];
                if (hold != null)
                {
                    Follow(i, hand, hold, player, canHold);
                }
                else
                {
                    if (hand.GrabPressed)
                    {
                        _squeezedNear[i] = SoldierNear(hand.PalmCentre, player); // (where this squeeze began)
                        _squeezeSpeed[i] = hand.BodyRelativeVelocity.magnitude;
                    }
                    else if (_squeezedNear[i] && hand.BodyRelativeVelocity.sqrMagnitude > SwungSpeed(i) * SwungSpeed(i))
                    {
                        // Swung after the squeeze: a punch, not a reach. A fist closed near a soldier and thrown took hold as the punch
                        // slowed on him (within the grab window) and knocked him over instead of punching (user, 2026-10-25: "I
                        // cannot punch enemies with my fists"; his no-knockdown mutator stood him straight up again).
                        _squeezedNear[i] = false;
                    }
                    // (Nor a fist that just landed a blow: its squeeze was the punch's, and it stopped on him after it.)
                    if (canHold && _squeezedNear[i] && Time.unscaledTime - MeleeHandler.LastHandBlow[i] > 0.6f
                        && hand.Role == HandRole.Free && hand.Held == null && hand.Tracked && !VRGameInput.GunInputBlocked
                        && VRInput.GetPressedRecently(DigitalAction.Grab, hand.Side, GrabWindow))
                    {
                        TryTake(i, hand, player);
                    }
                }
            }
            HandBody.Follow(_rig, canHold);
            HandBody.PushProps(_rig, canHold);
            RestoreSelfPairs();
        }

        private sealed class SelfPairs
        {
            public Actor Actor;
            public Collider[] Colliders;
            public float At;

            /// <summary>The pairs still ignored (x &lt;&lt; 8 | y; null before the first pass), each tried again until they part.</summary>
            public List<int> Pending;

            public float Next;
        }

        /// <summary>How long after its time a body's pairs still overlapping are tried again (then left ignored: a joint's pair).</summary>
        private const float SelfPairsRetry = 2f;

        /// <summary>
        /// Bodies let go whose bones still ignore one another (they do while held: <see cref="TryTake"/>), and from when they collide
        /// again: kept ignored after the let-go, a thrown body's limbs passed through its own trunk for as long as it lay there.
        /// </summary>
        private readonly List<SelfPairs> _selfPairs = new List<SelfPairs>();

        /// <summary>
        /// Once a let-go body's time armed after the throw is over, its bones collide with one another again, pair by pair, but for
        /// pairs overlapping just then (made to collide while inside each other, PhysX would push them apart at once: a jolt). A body
        /// taken again meanwhile keeps them ignored (that hold's own). Bones joined by a joint still do not meet: their joint says so.
        /// </summary>
        private void RestoreSelfPairs(bool now = false)
        {
            for (int n = _selfPairs.Count - 1; n >= 0; n--)
            {
                SelfPairs s = _selfPairs[n];
                if (!now && s.Actor != null && (Time.time < s.At || Time.time < s.Next))
                {
                    continue;
                }
                if (s.Actor == null || s.Actor.ragdoll == null || !s.Actor.ragdoll.IsRagdoll() || IsHeld(s.Actor))
                {
                    _selfPairs.RemoveAt(n);
                    continue; // (gone, stood up (the pairs went with it), or held again)
                }
                // Pairs overlapping just then are tried again every 0.1 s until they part (restored once only, a limb resting in
                // its own trunk at that moment stayed through it for as long as he lay there), for two seconds at most.
                s.Next = Time.time + 0.1f;
                if (TryPairs(s) || now || Time.time > s.At + SelfPairsRetry)
                {
                    _selfPairs.RemoveAt(n);
                }
            }
        }

        /// <summary>Collides again each pending pair not overlapping now; true when none is left.</summary>
        private static bool TryPairs(SelfPairs s)
        {
            Collider[] own = s.Colliders;
            if (s.Pending == null)
            {
                s.Pending = new List<int>(own.Length * own.Length / 2);
                for (int x = 0; x < own.Length && x < 256; x++)
                {
                    for (int y = x + 1; y < own.Length && y < 256; y++)
                    {
                        s.Pending.Add(x << 8 | y);
                    }
                }
            }
            for (int k = s.Pending.Count - 1; k >= 0; k--)
            {
                Collider a = own[s.Pending[k] >> 8], b = own[s.Pending[k] & 0xFF];
                if (a == null || b == null)
                {
                    s.Pending.RemoveAt(k); // (gone with its bone)
                    continue;
                }
                if (!a.enabled || !b.enabled || !a.gameObject.activeInHierarchy || !b.gameObject.activeInHierarchy)
                {
                    continue; // (Unity refuses the pair while either is off: kept for a later pass)
                }
                Transform ta = a.transform, tb = b.transform;
                if (!a.bounds.Intersects(b.bounds) || !Physics.ComputePenetration(a, ta.position, ta.rotation, b, tb.position, tb.rotation, out _, out _))
                {
                    Physics.IgnoreCollision(a, b, false);
                    s.Pending.RemoveAt(k);
                }
            }
            return s.Pending.Count == 0;
        }

        /// <summary>
        /// Whether hand <paramref name="i"/>'s squeeze is a reach for a soldier: made near one, in the grab window, and the hand not
        /// sped up past its speed at the squeeze since (a punch speeds up after the squeeze, a reach slows into him). Its fist does not
        /// punch him then (MeleeHandler.HandBlows): a quick reach squeezed on the way met his arm at 3 m/s, punched it, and the blow
        /// kept the hand from taking hold for 0.6 s ("my hand grabs the air many times": user, 2026-10-25; final test, 2026-10-27).
        /// </summary>
        internal static bool Reaching(int i)
        {
            BodyGrab g = _instance;
            return g != null && i >= 0 && i < 2 && g._holds[i] == null && g._squeezedNear[i] && g._rig.Hands[i] != null
                   && VRInput.GetPressedRecently(DigitalAction.Grab, g._rig.Hands[i].Side, GrabWindow);
        }

        /// <summary>Autotest: why the last squeeze near a soldier took no one.</summary>
        internal static string WhyNot = "";

        /// <summary>Whether a soldier (not the player) has a collider within <see cref="SqueezeNear"/> of <paramref name="palm"/>.</summary>
        private static bool SoldierNear(Vector3 palm, Actor player)
        {
            int n = Physics.OverlapSphereNonAlloc(palm, SqueezeNear, Near, Hitbox.ALL_HITBOX_LAYER_MASK, QueryTriggerInteraction.Collide);
            for (int k = 0; k < n; k++)
            {
                if (Near[k] != null && HurtableOf(Near[k]) is Actor a && a != player && a.ragdoll != null)
                {
                    return true;
                }
            }
            return false;
        }

        private void TryTake(int i, VRHand hand, Actor player)
        {
            // A fist swung at punching speed punches (MeleeHandler's blows), even with its grip just closed: a grab is a reach. The
            // punch's speed is the squeeze's own measure here as in the blows (SwungSpeed, Reaching): a reach squeezed at 3 m/s and
            // kept at it was too fast to take and, a reach, no blow either (round-3 scout, 2026-10-28).
            float punch = Mathf.Max(Mathf.Max(MeleeHandler.BlowSpeed, GrabMaxSpeed), SwungSpeed(i));
            if (hand.BodyRelativeVelocity.sqrMagnitude > punch * punch)
            {
                WhyNot = $"too fast {hand.BodyRelativeVelocity.magnitude:0.0}";
                return;
            }
            Vector3 palm = hand.PalmCentre;
            int n = Physics.OverlapSphereNonAlloc(palm, Reach, Near, Hitbox.ALL_HITBOX_LAYER_MASK, QueryTriggerInteraction.Collide);
            Actor best = null;
            float bestDistance = float.MaxValue;
            for (int k = 0; k < n; k++)
            {
                Collider c = Near[k];
                if (c == null)
                {
                    continue;
                }
                float d = (c.ClosestPoint(palm) - palm).sqrMagnitude;
                if (d >= bestDistance)
                {
                    continue; // (whose it is asked only of a nearer one)
                }
                Actor a = HurtableOf(c) as Actor;
                if (a == null || a == player || a.ragdoll == null)
                {
                    continue;
                }
                bestDistance = d;
                best = a;
            }
            Hold other = _holds[1 - i];
            if (best == null || other != null && other.Neck && other.Actor == best)
            {
                WhyNot = $"no one within reach ({n} colliders)";
                return; // (held by the neck in the other hand: knocked over by this one, that hold would hang on a ragdoll)
            }
            try
            {
                // Any soldier, a friend as well as an enemy: the game lets the player's bullets and knife hurt his own side, and the neck
                // was the one hold a friend escaped ("things work on enemies more than on friendlies": user, 2026-10-27).
                if (!best.dead && !best.ragdoll.IsRagdoll() && !best.IsSeated()
                    && other == null && FromBehind(best, player) && NearHead(best, palm))
                {
                    TakeNeck(i, hand, best);
                    return;
                }
                if (!best.dead && !best.ragdoll.IsRagdoll() && (HeadOf(best) - palm).sqrMagnitude < 0.45f * 0.45f && !Log.Logged("neck-missed"))
                {
                    Log.InfoOnce("neck-missed", $"No neck hold on {best.name}: {BehindAngle(best, player):0} degrees from behind him (under {BehindDegrees:0} needed), "
                                                + $"the head {(HeadOf(best) - palm).magnitude * 100f:0} cm from the fist (under {NeckReach * 100f:0}), {(best.team == player.team ? "a friend" : "an enemy")}"
                                                + $"{(best.IsSeated() ? ", seated" : "")}{(other != null ? ", the other hand holding someone" : "")}: knocked down instead.");
                }
                if (!best.ragdoll.IsRagdoll())
                {
                    best.FallOver(); // the game's own: off a seat or ladder, weapon put away, ragdoll from the animated pose
                }
                Attach(i, hand, best, palm);
            }
            catch (System.Exception e)
            {
                Log.ExceptionOnce("BodyGrab.Take", e);
                Release(i, hand, thrown: false);
            }
        }

        /// <summary>The hand holds the ragdoll's nearest bone through a joint (fixed in place, its turn damped and limited) to a body that follows the hand.</summary>
        private void Attach(int i, VRHand hand, Actor best, Vector3 palm)
        {
            // Not the bone the other hand holds: two locked joints on one body pulled it between them every step.
            Rigidbody bone = NearestBone(best, palm, _holds[1 - i]?.Bone);
            if (bone == null)
            {
                return;
            }
            Rigidbody anchor = Anchor(i);
            anchor.isKinematic = false; // (driven by its velocity while it holds: see GrabAnchor)
            if (BodyImpacts.RecordContacts && anchor.GetComponent<AnchorProbe>() == null)
            {
                anchor.gameObject.AddComponent<AnchorProbe>(); // (the autotest's: how evenly the anchor moves)
            }
            anchor.transform.SetPositionAndRotation(palm, hand.Palm.rotation);
            GrabAnchor driver = anchor.GetComponent<GrabAnchor>();
            driver.Follow(palm, hand.Palm.rotation, _rig.PlacedBody, _rig.PlacedBodyAt, _rig.PlacedSmoothing); // (this grab's, from its first step)
            driver.Bones = _holds[1 - i] != null && _holds[1 - i].Actor == best ? null : best.ragdoll.rigidbodies; // (one hand carries a body)
            // The game syncs transforms to the physics only on a step (autoSyncTransforms off): the joint below takes the
            // anchor's physics pose, which was still where it was last let go (the world's origin on a first grab).
            Physics.SyncTransforms();
            anchor.velocity = anchor.angularVelocity = Vector3.zero;
            var joint = bone.gameObject.AddComponent<ConfigurableJoint>();
            // Held at the bone's centre of mass, not its pivot or the touched point (the hips' pivot is 30 cm from its middle): the
            // lock's pull on a heavy body hanging from it turns the bone no more, only the drive below does (held at the touched
            // point, its weight levered the bone round it: 15-68 m/s bones in the soldier check, 2026-10-27; and the body pulled onto
            // a hand in the ground went into the terrain). Turning the hand still swings the body round the palm.
            joint.anchor = bone.centerOfMass;
            joint.connectedBody = anchor;
            joint.autoConfigureConnectedAnchor = true; // held where it was taken: no snap of the bone into the fist
            // The drawn hand goes on the body instead: where the palm met the bone's surface (any point of any bone), a hand's skin
            // off it. Drawn at the tracked hand, it was inside a soldier (a reach goes through bodies, the hands have no collision on
            // the way) or in the air beside him ("my hand should actually go on the body parts, not inside them or in the air":
            // user, 2026-10-27). The glove follows that point as the body moves (Follow), as on a gun's grip.
            Collider boneCollider = bone.GetComponent<Collider>();
            Vector3 grabPoint = bone.transform.TransformPoint(bone.centerOfMass);
            Vector3 outward = (palm - grabPoint).sqrMagnitude > 1e-8f ? (palm - grabPoint).normalized : Vector3.up;
            if (boneCollider != null && boneCollider.enabled)
            {
                grabPoint = SurfaceAt(boneCollider, palm, out outward);
            }
            Vector3 seatAt = bone.transform.InverseTransformPoint(grabPoint + outward * HandSkin);
            joint.xMotion = joint.yMotion = joint.zMotion = ConfigurableJointMotion.Locked;
            // The bone's turn held by a damped, torque-limited drive, not locked: locked to the hand, every turn of the
            // wrist was levered through the whole body (a quick turn whipped it at the speed cap and lifted it), and wherever the
            // body could not turn with it the lock pushed with whatever it took: "they go crazy and fling in the air" (user,
            // 2026-10-25). Scaled by the body's mass: about 9 degrees of sag under its weight, damped, and a capped torque.
            float mass = BodyMass(best);
            joint.angularXMotion = joint.angularYMotion = joint.angularZMotion = ConfigurableJointMotion.Free;
            joint.rotationDriveMode = RotationDriveMode.Slerp;
            joint.slerpDrive = new JointDrive { positionSpring = HoldTwist * mass, positionDamper = HoldTwistDamper * mass, maximumForce = HoldTwistTorque * mass };
            joint.targetRotation = Quaternion.identity;
            joint.projectionMode = JointProjectionMode.PositionAndRotation; // a heavy body hanging from it does not stretch the joint
            joint.projectionDistance = 0.02f;
            joint.projectionAngle = 5f;
            joint.enablePreprocessing = false;
            _holds[i] = new Hold { Actor = best, Bone = bone, Joint = joint, Reach = (bone.position - palm).magnitude, LastPalm = palm, LastRotation = hand.Palm.rotation, SeatAt = seatAt };
            // Taken as he runs, or as he tumbles: every bone moves with the hand from the first step. The game ragdolls a running
            // soldier with his speed in every bone (Actor.FallOver: Ragdoll(cachedVelocity)); the hold stopped the bone it took at
            // once, and the rest of him (80 kg at 6 m/s) pulled his joints apart and flung him: "all the npcs i grabbed were walking
            // or running and then flinged around in my hand and formed themselves long bodys" (user, 2026-10-25).
            // (in game units: in slow motion the real-time hand's velocity is so much faster in the slowed world)
            Vector3 handVelocity = hand.Velocity / SlowMotion();
            // Caught again in his flight (thrown in slow motion, flying as in real time until he touches something): back at the
            // world's pace before he is held, as a magazine caught in the air is. The flight's lift went on in the hand, 24 g a bone
            // at 0.2, and he sagged out of his joints (bug hunt, 2026-10-30). And one the other hand already holds keeps its motion:
            // set to this hand's, a body carried at 3 m/s stopped in one step and the first hand's joint yanked it back.
            bool heldByOther = _holds[1 - i] != null && _holds[1 - i].Actor == best;
            foreach (Rigidbody rb in best.ragdoll.rigidbodies)
            {
                if (rb != null)
                {
                    RealTimeFlight.Stop(rb);
                    if (!heldByOther)
                    {
                        rb.velocity = handVelocity;
                        rb.angularVelocity = Vector3.zero;
                    }
                }
            }
            BodyImpacts.Arm(best, float.PositiveInfinity, GameContext.PlayerActor); // (its bones' speed kept in check while armed)
            if (best.ragdoll.ragdollObject != null)
            {
                Collider[] own = best.ragdoll.ragdollObject.GetComponentsInChildren<Collider>();
                _holds[i].Colliders = own;
                DrawAhead(best);
                PropPhysics.IgnorePlayer(own);
                _holds[i].PlayerGeneration = PropPhysics.PlayerGeneration;
                // Its bones no longer collide with one another while held: a limp leg swung into its own hips as the player walked (its
                // Leg.L.002 on its Bone at 1442 N s, the soldier check, 2026-10-25), and the hand kept the body from giving way. Unity
                // forgets these pairs when the ragdoll is switched off (the soldier stands up or respawns): he is whole again then.
                for (int x = 0; x < own.Length; x++)
                {
                    for (int y = x + 1; y < own.Length; y++)
                    {
                        PropPhysics.Ignore(own[x], own[y]);
                    }
                }
                MeetGun(_holds[i]);
            }
            if (!Log.Logged("bodygrab-mass"))
            {
                Log.InfoOnce("bodygrab-mass", $"Held body: {mass:0.0} kg over {best.ragdoll.rigidbodies.Length} bones, taken by {bone.name} ({bone.mass:0.0} kg); "
                                              + $"its turn: spring {joint.slerpDrive.positionSpring:0} N m/rad, damper {joint.slerpDrive.positionDamper:0}, at most {joint.slerpDrive.maximumForce:0} N m.");
            }
            TakeHand(hand, best);
            if (hand.Model != null && boneCollider != null)
            {
                // The palm rests on the bone and every finger closes until it touches it (as on a gun's grip): round a wrist or an
                // arm, flat on a chest, not a fist inside him.
                Collider held = boneCollider;
                hand.Model.Solid = p => held != null && (held.ClosestPoint(p) - p).sqrMagnitude < 0.009f * 0.009f; // (gone with his body before the hold ends)
                hand.Model.SolidAnchor = bone.transform;
                hand.Model.SolidVersion++;
            }
            hand.Haptic(0.08f, 80f, 0.8f);
            Log.InfoOnce("bodygrab", $"Took hold of {best.name} by {bone.name} ({(best.dead ? "dead" : "alive")}), "
                                     + $"the palm {(palm - grabPoint).magnitude * 100f:0} cm {(Vector3.Dot(palm - grabPoint, outward) >= 0f ? "off" : "inside")} its surface.");
        }

        /// <summary>
        /// The other hand's gun meets the held body's trunk and head (its physics stand-in pushes them as anything: "guns go through
        /// them", user, 2026-10-25), except from a grab or a draw beside him until the two are apart: overlapping, the body was shoved
        /// out of the gun at the physics' depenetration speed. Never his arms and legs: light and dangling, a foot caught between the
        /// gun and his held hips was flung at the speed cap (the soldier check's sweep, 312 N s). (The body was ignored for the whole
        /// hold before, as it was shoved and hurt: the shoving was the hand's broken anchor, see GrabAnchor; the stand-in's touch
        /// does no damage, see BodyImpacts.)
        /// </summary>
        private static void MeetGun(Hold hold)
        {
            GameObject body = hold.Actor != null && hold.Actor.ragdoll != null ? hold.Actor.ragdoll.ragdollObject : null;
            GunCollider gun = GunCollider.Current;
            if (body == null)
            {
                return;
            }
            if (!ReferenceEquals(gun, hold.SeenGun))
            {
                hold.SeenGun = gun;
                gun?.IgnoreColliders(Limbs(hold.Actor));
                if (hold.IgnoredGun != null && !ReferenceEquals(hold.IgnoredGun, gun))
                {
                    hold.IgnoredGun.StopIgnoring(body);
                    hold.IgnoredGun = null;
                }
                if (gun != null && gun.Overlaps(body))
                {
                    gun.IgnoreCollisionsWith(body);
                    hold.IgnoredGun = gun;
                }
            }
            else if (hold.IgnoredGun != null && !(hold.Colliders != null ? hold.IgnoredGun.Overlaps(hold.Colliders) : hold.IgnoredGun.Overlaps(body)))
            {
                hold.IgnoredGun.StopIgnoring(body);
                hold.IgnoredGun = null;
            }
        }

        /// <summary>A body's arm and leg colliders: those of its bones lighter than <see cref="LimbMass"/>.</summary>
        private static List<Collider> Limbs(Actor a)
        {
            LimbScratch.Clear();
            if (a != null && a.ragdoll != null && a.ragdoll.rigidbodies != null)
            {
                foreach (Rigidbody rb in a.ragdoll.rigidbodies)
                {
                    if (rb != null && rb.mass < LimbMass)
                    {
                        foreach (Collider c in rb.GetComponents<Collider>())
                        {
                            LimbScratch.Add(c);
                        }
                    }
                }
            }
            return LimbScratch;
        }

        private static readonly List<Collider> LimbScratch = new List<Collider>();
        /// <summary>A bone lighter than this (kg) is a limb (a soldier's arms and legs weigh 4, his hips and chest 16, his head 8).</summary>
        private const float LimbMass = 6f;

        private static void TakeHand(VRHand hand, Actor a)
        {
            hand.Role = HandRole.Body;
            hand.Held = a;
            VRInput.Consume(DigitalAction.Grab, hand.Side);
        }

        /// <summary>
        /// A hand's rotation against the player's body (the rig): the neck's twist is the wrist's, and a snap or smooth turn
        /// turned the hand in the world by as much (two 30 degree snaps read as 60 of the 70 that break the neck; review, 2026-10-24).
        /// </summary>
        private Quaternion InRig(Quaternion world) => Quaternion.Inverse(_rig.Root.rotation) * world;

        /// <summary>The player stands behind the soldier: the soldier faces away from him.</summary>
        private static bool FromBehind(Actor a, Actor player) => BehindAngle(a, player) < BehindDegrees;

        /// <summary>
        /// The angle between the soldier's facing and the way from the player to him (0: straight behind him), by his body or by
        /// where he looks, whichever is more behind: the game turns his body only once he looks more than 50 degrees off it
        /// (Actor.Update), so his body's facing alone could read a hold from straight behind as from the side; and a soldier
        /// turning his head to look round at the player is still held from behind.
        /// </summary>
        private static float BehindAngle(Actor a, Actor player)
        {
            Vector3 away = Vector3.ProjectOnPlane(a.transform.position - player.transform.position, Vector3.up);
            if (away.sqrMagnitude < 1e-4f)
            {
                return 180f;
            }
            Vector3 body = Vector3.ProjectOnPlane(a.transform.forward, Vector3.up);
            float angle = body.sqrMagnitude > 1e-4f ? Vector3.Angle(away, body) : 180f;
            Vector3 looks = a.controller != null ? Vector3.ProjectOnPlane(a.controller.FacingDirection(), Vector3.up) : Vector3.zero;
            return looks.sqrMagnitude > 1e-4f ? Mathf.Min(angle, Vector3.Angle(away, looks)) : angle;
        }

        private static Vector3 HeadOf(Actor a)
        {
            try
            {
                Transform head = a.ragdoll.HumanBoneTransformAnimated(HumanBodyBones.Head) ?? a.ragdoll.HumanBoneTransformAnimated(HumanBodyBones.Neck);
                if (head != null)
                {
                    return head.position;
                }
            }
            catch (System.Exception)
            {
                // a rig without those bones mapped: the centre's height below
            }
            return a.CenterPosition() + Vector3.up * 0.45f;
        }

        private static bool NearHead(Actor a, Vector3 palm) => (HeadOf(a) - palm).sqrMagnitude < NeckReach * NeckReach;

        /// <summary>
        /// Taken by the head from behind (an enemy: as every blow and throw of the mod, never the player's own team): the soldier
        /// stands held, his AI paused (he neither turns nor shoots); a twist of the wrist breaks his neck (<see cref="FollowNeck"/>),
        /// letting go lets him go.
        /// </summary>
        private void TakeNeck(int i, VRHand hand, Actor a)
        {
            if (a.controller != null)
            {
                a.controller.enabled = false;
            }
            _holds[i] = new Hold { Actor = a, Neck = true, NeckStart = InRig(hand.Palm.rotation) };
            TakeHand(hand, a);
            hand.Haptic(0.06f, 60f, 0.6f);
            Log.InfoOnce("neckhold", $"Took {a.name} by the neck from behind: a twist of the wrist breaks it.");
        }

        private void FollowNeck(int i, VRHand hand, Hold hold, Actor player, bool canHold)
        {
            Actor a = hold.Actor;
            if (a == null || !canHold || !hand.GrabHeld || (HeadOf(a) - hand.PalmCentre).sqrMagnitude > 0.45f * 0.45f)
            {
                Release(i, hand, thrown: false); // let go (or pulled away): his AI back
                return;
            }
            if (a.dead || a.ragdoll != null && a.ragdoll.IsRagdoll())
            {
                // Killed by something else meanwhile, or knocked down by the other fist: a body to hold (kept by the neck, he lay flat
                // under a grip with nothing in it, and could not get up while it lasted).
                _holds[i] = null;
                ResumeAi(a);
                Attach(i, hand, a, hand.PalmCentre);
                if (_holds[i] == null)
                {
                    Release(i, hand, thrown: false); // (no bone to hold: the hand stayed on him for good, with no hold to end it; soldier scout)
                }
                return;
            }
            float twist = Quaternion.Angle(hold.NeckStart, InRig(hand.Palm.rotation));
            if (twist < NeckTwist)
            {
                return;
            }
            // The neck breaks: a kill by the player (not the game's silent kill, which hides the body: this one ragdolls it), and
            // the body stays in the hand.
            a.Kill(new DamageInfo(DamageInfo.DamageSourceType.Melee, player, null)
            {
                healthDamage = a.health + 100f,
                balanceDamage = 0f,
                point = HeadOf(a),
                direction = Vector3.ProjectOnPlane(a.transform.forward, Vector3.up).normalized
            });
            ResumeAi(a);
            hand.Haptic(0.12f, 30f, 1f);
            Log.InfoOnce("necksnap", $"Broke the neck of {a.name} (wrist turned {twist:0} degrees).");
            _holds[i] = null;
            if (a.ragdoll != null && a.ragdoll.IsRagdoll())
            {
                Attach(i, hand, a, hand.PalmCentre);
            }
            if (_holds[i] == null)
            {
                Release(i, hand, thrown: false); // (the hand free again)
            }
        }

        private static void ResumeAi(Actor a)
        {
            if (a != null && a.controller != null && !a.controller.enabled)
            {
                a.controller.enabled = true;
            }
        }

        private void Follow(int i, VRHand hand, Hold hold, Actor player, bool canHold)
        {
            if (hand.Role != HandRole.Body)
            {
                // Taken over: the game drew a weapon into the hand (the weapon button, a respawn), as a held magazine is let go
                // then (HeldWeapon). The body stayed on the gun hand's joint, and the soldier on the hand after it let go.
                Release(i, hand, thrown: false);
                return;
            }
            if (hold.Colliders != null && hold.PlayerGeneration != PropPhysics.PlayerGeneration)
            {
                // The player's colliders switched off and on (out of a seat, off a ladder, up from the ground): Unity forgot the
                // ignored pairs, and the held bones met the player's body again (as GunCollider and MagazineProp do it; grab scout).
                hold.PlayerGeneration = PropPhysics.PlayerGeneration;
                PropPhysics.IgnorePlayer(hold.Colliders);
            }
            if (hold.Neck)
            {
                FollowNeck(i, hand, hold, player, canHold);
                return;
            }
            Actor a = hold.Actor;
            // (A body the game took away, its ragdoll switched off: Actor.KillSilently. The hand held it on, invisible.)
            if (a == null || hold.Bone == null || hold.Joint == null || a.ragdoll == null || !a.ragdoll.IsRagdoll() || !canHold
                || !hold.Bone.gameObject.activeInHierarchy)
            {
                if (a != null && !a.dead && a.ragdoll != null && !a.ragdoll.IsRagdoll())
                {
                    // Held down by fallAction each frame, so something else stood them up (the user's mutators include No Knockdown).
                    Log.InfoOnce("bodygrab-up", $"Let go of {a.name}: stood up while held (a mutator that stops knockdowns does that).");
                }
                Release(i, hand, thrown: false);
                return;
            }
            if (!hand.GrabHeld)
            {
                Release(i, hand, thrown: true);
                return;
            }
            Rigidbody anchor = Anchor(i);
            Vector3 palm = hand.PalmCentre;
            Quaternion rotation = hand.Palm.rotation;
            bool jumped = (palm - hold.LastPalm).sqrMagnitude > JumpDistance * JumpDistance || Quaternion.Angle(rotation, hold.LastRotation) > JumpAngle;
            if (jumped)
            {
                // Held in both hands, both jump in the same frame: the body is carried once (twice, it moved and turned twice).
                if (_carriedActor != a || _carriedFrame != Time.frameCount)
                {
                    CarryBody(a, hold.LastPalm, hold.LastRotation, palm, rotation);
                    _carriedActor = a;
                    _carriedFrame = Time.frameCount;
                }
                anchor.transform.SetPositionAndRotation(palm, rotation);
                anchor.position = palm;
                anchor.rotation = rotation;
                Physics.SyncTransforms();
            }
            hold.LastPalm = palm;
            hold.LastRotation = rotation;
            // Moved in each physics step (GrabAnchor): the hand's place against the player, wherever the player is in that step.
            anchor.GetComponent<GrabAnchor>().Follow(palm, rotation, _rig.PlacedBody, _rig.PlacedBodyAt, _rig.PlacedSmoothing, jumped);
            if (hand.Model != null && hold.Bone != null)
            {
                // The glove on the body where the palm met it (Attach), moved with the bone; turned as the hand is.
                Runtime.RigidPose glove = hand.Model.TrackedGlovePose;
                Vector3 shift = hold.Bone.transform.TransformPoint(hold.SeatAt) - palm;
                hand.Model.SnapPose = new Runtime.RigidPose(glove.position + shift, glove.rotation);
            }
            if ((hold.Bone.position - palm).magnitude > hold.Reach + MaxStretch)
            {
                Release(i, hand, thrown: false); // wedged or pulled through something: let go rather than tear the ragdoll
                return;
            }
            if (!a.dead)
            {
                a.fallAction.Start(); // held, not getting up (the game gets up when the fall timer is done and the body is still)
            }
            // Killed while held (a knife, a bullet), the game stiffened the joints again for the body (Actor.Die: SetDrive 50):
            // limp again while it hangs from the hand (only while held: see SavedDrive).
            if (a.ragdoll.jointDrive.positionSpring > 0f)
            {
                BodyImpacts.Limp(a);
            }
            MeetGun(hold);
            // No bone faster than the hand by more than MaxOverHand: a body caught in the ground or a wall was shot out by the
            // physics before (see MaxOverHand).
            float limit = hand.Velocity.magnitude / SlowMotion() + MaxOverHand; // (the hand's real-time speed in game units)
            Rigidbody[] bones = a.ragdoll.rigidbodies;
            if (bones != null)
            {
                foreach (Rigidbody rb in bones)
                {
                    if (rb == null)
                    {
                        continue;
                    }
                    Vector3 v = rb.velocity;
                    if (v.sqrMagnitude > limit * limit)
                    {
                        rb.velocity = v.normalized * limit;
                    }
                }
            }
        }

        private void Release(int i, VRHand hand, bool thrown)
        {
            Hold hold = _holds[i];
            _holds[i] = null;
            if (_anchors[i] != null)
            {
                _anchors[i].GetComponent<GrabAnchor>().Stop();
            }
            Hold still = _holds[1 - i];
            if (hold != null && still != null && still.Actor == hold.Actor && !still.Neck && _anchors[1 - i] != null && hold.Actor != null && hold.Actor.ragdoll != null)
            {
                _anchors[1 - i].GetComponent<GrabAnchor>().Bones = hold.Actor.ragdoll.rigidbodies; // the other hand carries him now
            }
            if (hand != null && hand.Model != null && hold != null && !hold.Neck)
            {
                // The glove back on the tracked hand, and its fingers off the bone it held (only what this hold set). Also with the bone
                // already gone (a scene change: the soldiers went with the old level), when the glove stayed snapped where he had been
                // in the next level (round-3 scout, 2026-10-28): a destroyed anchor reads as null.
                hand.Model.SnapPose = null;
                if (hand.Model.SolidAnchor == null || hold.Bone != null && hand.Model.SolidAnchor == hold.Bone.transform)
                {
                    hand.Model.Solid = null;
                    hand.Model.SolidAnchor = null;
                }
            }
            if (hand != null && hold != null && ReferenceEquals(hand.Held, hold.Actor))
            {
                hand.Held = null; // (also when the hand was taken over: the soldier left on it kept it from ever grabbing or punching)
            }
            if (hold != null && hold.Neck)
            {
                ResumeAi(hold.Actor); // let go of the neck: he is free again
                hold = null;
            }
            if (hold != null)
            {
                if (hold.Joint != null)
                {
                    // Freed before it goes (Destroy waits for the frame's end: a let-go from a physics callback, a crush, kept
                    // pulling the bone to the palm for the steps still to run).
                    hold.Joint.xMotion = hold.Joint.yMotion = hold.Joint.zMotion = ConfigurableJointMotion.Free;
                    hold.Joint.slerpDrive = default;
                    hold.Joint.projectionMode = JointProjectionMode.None;
                    Object.Destroy(hold.Joint);
                }
                // Still held by the other hand: stays armed (not for 3 s only) and down; the other hand's let-go throws.
                Actor a = hold.Actor;
                Hold other = _holds[1 - i];
                if (a != null && !(other != null && other.Actor == a))
                {
                    DrawAsGame(a);
                    if (a.ragdoll == null || !a.ragdoll.IsRagdoll())
                    {
                        BodyImpacts.Disarm(a); // stood up or respawned under the hand: his joints as the game set them, now
                    }
                    else
                    {
                        if (hold.Colliders != null)
                        {
                            // Only this let-go's time counts: an earlier one's falling due restored his pairs while this throw
                            // still had him armed (taken again and thrown within the 3 s).
                            for (int k = _selfPairs.Count - 1; k >= 0; k--)
                            {
                                if (_selfPairs[k].Actor == a)
                                {
                                    _selfPairs.RemoveAt(k);
                                }
                            }
                            _selfPairs.Add(new SelfPairs { Actor = a, Colliders = hold.Colliders, At = Time.time + ImpactsAfterThrow });
                        }
                        BodyImpacts.Arm(a, Time.time + ImpactsAfterThrow, GameContext.PlayerActor);
                        if (thrown && hand != null)
                        {
                            ThrowBody(a, hand);
                        }
                        if (!a.dead)
                        {
                            a.fallAction.Start(); // down a moment after the throw too, before the game lets them get up
                        }
                    }
                }
            }
            if (hand != null && hand.Role == HandRole.Body)
            {
                hand.Role = HandRole.Free;
                hand.Held = null;
            }
        }

        /// <summary>
        /// Thrown with the hand's motion (as everything let go: ThrowAndMelee), the whole body, not only the bone that was held:
        /// the rest of a ragdoll hanging from one joint lags far behind it. One velocity for every bone (the joints' own pulls
        /// left in made the body tumble off at any speed), at most <see cref="MaxThrowSpeed"/>.
        /// </summary>
        private static void ThrowBody(Actor a, VRHand hand)
        {
            Rigidbody[] bones = a.ragdoll.rigidbodies;
            if (bones == null)
            {
                return;
            }
            Vector3 v = Vector3.ClampMagnitude(ThrowableHandler.Released(hand), MaxThrowSpeed);
            foreach (Rigidbody rb in bones)
            {
                if (rb != null)
                {
                    rb.velocity = v;
                    rb.angularVelocity *= 0.25f;
                }
            }
            // In slow motion he flies at the hand's real speed and falls as in real time until he first touches something, as a
            // magazine let go does: the hand's real-time velocity taken as game speed left him hanging by the hand, sinking at a
            // twenty-fifth of gravity (round 9 hunt, after the user's floating magazine).
            RealTimeFlight.BeginGroup(bones);
        }

        /// <summary>
        /// What a hand's velocity is divided by to be in game units: the game's time scale (0.05-1) for hands at the real pace, 1 for
        /// slowed hands (Hands in slow motion: Slowed), whose velocity is in game units already (VRHand).
        /// </summary>
        internal static float SlowMotion() => VRConfig.SlowedHands ? 1f : Mathf.Clamp(Time.timeScale, 0.05f, 1f);

        /// <summary>Autotest: how many times a held body was carried over a jump of the hand.</summary>
        internal static int Carries;

        private static Vector3[] CarryAt = new Vector3[0];
        private static Quaternion[] CarryTurn = new Quaternion[0];

        /// <summary>The whole body moved and turned as the hand moved and turned between two frames (its pose kept).</summary>
        private static void CarryBody(Actor a, Vector3 fromPalm, Quaternion fromRotation, Vector3 toPalm, Quaternion toRotation)
        {
            Carries++;
            Rigidbody[] bones = a.ragdoll != null ? a.ragdoll.rigidbodies : null;
            if (bones == null)
            {
                return;
            }
            Quaternion turn = toRotation * Quaternion.Inverse(fromRotation);
            // Every new pose from the old ones first: the bones are nested (a thigh under the hips, the head under the chest), and set
            // one by one each child was carried again with every parent before it, the head three times: thrown 1.5 m off the hand by
            // a snap turn, the hold torn 0.9 m apart, the body flung (soldier check, 2026-10-25: "same when I turn": user).
            if (CarryAt.Length < bones.Length)
            {
                CarryAt = new Vector3[bones.Length];
                CarryTurn = new Quaternion[bones.Length];
            }
            // From the physics' own poses (Rigidbody), not the drawn ones: a held body is drawn extrapolated (DrawAhead), each bone
            // carried on by its own speed and spin, and those poses do not fit its joints. Written back as the new physics poses, the
            // solver pulled the joints together again in one step: an arm at 14.6 m/s after a snap turn (final test, 2026-10-27).
            for (int k = 0; k < bones.Length; k++)
            {
                if (bones[k] != null)
                {
                    CarryAt[k] = toPalm + turn * (bones[k].position - fromPalm);
                    CarryTurn[k] = turn * bones[k].rotation;
                }
            }
            for (int k = 0; k < bones.Length; k++)
            {
                if (bones[k] != null)
                {
                    bones[k].transform.SetPositionAndRotation(CarryAt[k], CarryTurn[k]);
                    bones[k].velocity = turn * bones[k].velocity;
                    bones[k].angularVelocity = turn * bones[k].angularVelocity;
                }
            }
        }


        /// <summary>
        /// While a hand holds a body, its bones are drawn carried on to the frame's time (extrapolated): drawn at the last physics
        /// step (the game's ragdolls are not interpolated), at 60 steps a second under a 90 Hz view, a held body moved in steps and
        /// trailed the hand by up to a step ("a grab delay between my hand and the body": user, 2026-10-25). As the game had them
        /// when let go of (a body flying or lying is drawn as any other).
        /// </summary>
        private static void DrawAhead(Actor a)
        {
            if (a == null || a.ragdoll == null || a.ragdoll.rigidbodies == null || DrawnAs.ContainsKey(a))
            {
                return;
            }
            Rigidbody[] bones = a.ragdoll.rigidbodies;
            var was = new RigidbodyInterpolation[bones.Length];
            for (int k = 0; k < bones.Length; k++)
            {
                if (bones[k] != null)
                {
                    was[k] = bones[k].interpolation;
                    bones[k].interpolation = RigidbodyInterpolation.Extrapolate;
                }
            }
            DrawnAs[a] = was;
        }

        private static void DrawAsGame(Actor a)
        {
            if (a == null || !DrawnAs.TryGetValue(a, out RigidbodyInterpolation[] was))
            {
                return;
            }
            DrawnAs.Remove(a);
            Rigidbody[] bones = a.ragdoll != null ? a.ragdoll.rigidbodies : null;
            for (int k = 0; bones != null && k < bones.Length && k < was.Length; k++)
            {
                if (bones[k] != null)
                {
                    bones[k].interpolation = was[k];
                }
            }
        }

        private static readonly Dictionary<Actor, RigidbodyInterpolation[]> DrawnAs = new Dictionary<Actor, RigidbodyInterpolation[]>();

        /// <summary>Lets go of everything (scene change, VR stopping).</summary>
        public void ReleaseAll()
        {
            _selfPairs.Clear(); // (a scene change: their soldiers went with it)
            for (int i = 0; i < 2; i++)
            {
                Release(i, _rig.Hands[i], thrown: false);
            }
            DrawnAs.Clear(); // (a soldier gone with his scene before the let-go was never given his drawing back, nor taken off the list)
        }

        public void Destroy()
        {
            if (_instance == this)
            {
                _instance = null;
            }
            // VR stopping, the game going on flat: no Update restores the let-go bodies' pairs from here, so now (those
            // overlapping just now stay ignored): the earlier let-goes' first (ReleaseAll forgets them), then those it lets go.
            RestoreSelfPairs(now: true);
            ReleaseAll();
            RestoreSelfPairs(now: true);
            foreach (Rigidbody a in _anchors)
            {
                if (a != null)
                {
                    Object.Destroy(a.gameObject);
                }
            }
        }

        /// <summary>The grab anchor's mass (kg): a hundred soldiers.</summary>
        private const float AnchorMass = 10000f;

        private Rigidbody Anchor(int i)
        {
            if (_anchors[i] == null)
            {
                var go = new GameObject("RavenfieldVR Body Grab " + (i == 0 ? "L" : "R"));
                Object.DontDestroyOnLoad(go);
                Rigidbody rb = go.AddComponent<Rigidbody>();
                rb.isKinematic = true; // (until it holds: see GrabAnchor) no collider: it only carries the joint
                rb.useGravity = false;
                rb.mass = AnchorMass; // a held body's pull on it moves it next to nothing (it is set going again every step)
                rb.inertiaTensor = Vector3.one * AnchorMass;
                rb.inertiaTensorRotation = Quaternion.identity;
                rb.drag = rb.angularDrag = 0f;
                rb.maxAngularVelocity = 100f;
                go.AddComponent<GrabAnchor>();
                _anchors[i] = rb;
            }
            return _anchors[i];
        }

        /// <summary>
        /// Where a palm at <paramref name="palm"/> meets a bone's collider, and the surface's outward normal there: the nearest
        /// surface point from outside. From inside (a reach goes through bodies) the shortest way out across the shape: off a
        /// capsule's axis, through a box's nearest face, away from a sphere's centre.
        /// </summary>
        internal static Vector3 SurfaceAt(Collider c, Vector3 palm, out Vector3 normal)
        {
            Vector3 closest = c.ClosestPoint(palm);
            Vector3 off = palm - closest;
            if (off.sqrMagnitude > 1e-8f)
            {
                normal = off.normalized;
                return closest;
            }
            Transform t = c.transform;
            Vector3 local = t.InverseTransformPoint(palm);
            Vector3 way;
            switch (c)
            {
                case CapsuleCollider cap:
                {
                    Vector3 axis = cap.direction == 0 ? Vector3.right : cap.direction == 1 ? Vector3.up : Vector3.forward;
                    Vector3 rel = local - cap.center;
                    way = rel - axis * Vector3.Dot(rel, axis);
                    if (way.sqrMagnitude < 1e-10f)
                    {
                        way = Vector3.Cross(axis, axis == Vector3.up ? Vector3.right : Vector3.up);
                    }
                    break;
                }
                case BoxCollider box:
                {
                    Vector3 rel = local - box.center, half = box.size * 0.5f;
                    float dx = half.x - Mathf.Abs(rel.x), dy = half.y - Mathf.Abs(rel.y), dz = half.z - Mathf.Abs(rel.z);
                    way = dx <= dy && dx <= dz ? new Vector3(rel.x >= 0f ? 1f : -1f, 0f, 0f)
                        : dy <= dz ? new Vector3(0f, rel.y >= 0f ? 1f : -1f, 0f)
                        : new Vector3(0f, 0f, rel.z >= 0f ? 1f : -1f);
                    break;
                }
                case SphereCollider sphere:
                    way = local - sphere.center;
                    break;
                default:
                    way = local - t.InverseTransformPoint(c.bounds.center);
                    break;
            }
            Vector3 dir = t.TransformDirection(way);
            dir = dir.sqrMagnitude > 1e-10f ? dir.normalized : Vector3.up;
            // Back in from past the shape along that way: where the ray enters is where the palm comes out.
            float past = c.bounds.extents.magnitude * 2f + 0.05f;
            if (c.Raycast(new Ray(palm + dir * past, -dir), out RaycastHit hit, past))
            {
                normal = hit.normal;
                return hit.point;
            }
            normal = dir;
            return c.ClosestPoint(palm + dir * past);
        }

        private static Rigidbody NearestBone(Actor a, Vector3 palm, Rigidbody taken)
        {
            Rigidbody best = null;
            float bestDistance = float.MaxValue;
            if (a.ragdoll.rigidbodies == null)
            {
                return null;
            }
            foreach (Rigidbody rb in a.ragdoll.rigidbodies)
            {
                if (rb == null || rb == taken)
                {
                    continue;
                }
                float d = BoneDistanceSq(rb, palm);
                // Inside more than one bone (a hand reached into his middle: the hands have no collision on the way), the heaviest
                // of them: the first one counted took a thigh as often as the hips, and a soldier held by a leg was flung while the
                // player ran (21-68 m/s bones in the soldier check, 2026-10-27). A hand on a limb from outside still takes the limb.
                if (d < 1e-8f)
                {
                    d = -rb.mass;
                }
                if (d < bestDistance)
                {
                    bestDistance = d;
                    best = rb;
                }
            }
            return best;
        }

        /// <summary>How far a point is from a ragdoll bone, squared: from its collider's surface, or its centre of mass.</summary>
        private static float BoneDistanceSq(Rigidbody rb, Vector3 p)
        {
            Collider c = rb.GetComponent<Collider>();
            return ((c != null && c.enabled ? c.ClosestPoint(p) : rb.worldCenterOfMass) - p).sqrMagnitude;
        }
    }

    /// <summary>
    /// A hand's grab anchor, moved in each physics step, not once a frame: the hand's place against the player's physics body
    /// (<see cref="VRRig.PlacedBody"/>), put where that body is in the step (the body moves in the physics step while the rig is placed
    /// once a frame: moved with the hand's frame pose the anchor went by uneven steps as the player walked).
    /// <para>
    /// A heavy body set going at the speed that takes it there in the step, not a kinematic one moved there: PhysX placed a kinematic
    /// anchor right but its joint pulled the held bone as if the anchor stood still, braking the body against the world at every
    /// step while it was dragged along. Walking or running, the hold first lagged and stretched, then the physics' answer went wrong
    /// (10^17 N in a step on a body held calmly as a run started): bodies spun, were pulled "long", flung, or slipped out of the
    /// hand (user, 2026-10-25: "when I run the bodies still turn crazy"; "he let go of an enemy while walking forward and
    /// backwards"). Turning in place it held: the hand moved slowly then.
    /// </para>
    /// </summary>
    internal sealed class GrabAnchor : MonoBehaviour
    {
        private Rigidbody _body;
        private Transform _player;
        // The last frames' samples (game time, the hand's place against the body, the hand itself, its turn), newest at _newest: each
        // physics step takes the hand as it was a fixed time before the step, between two of them (see FixedUpdate).
        private const int Samples = 8;
        /// <summary>How far past the newest sample the hand is carried on (in sample spans: one more frame at most).</summary>
        private const float AnchorAhead = 2f;
        private readonly float[] _time = new float[Samples];
        private readonly Vector3[] _from = new Vector3[Samples], _palm = new Vector3[Samples];
        private readonly Quaternion[] _turn = new Quaternion[Samples];
        private int _newest, _count;
        private bool _active;
        // The player's physics body in the last step, and its speed then (see Carried).
        private Vector3 _lastBody, _lastSpeed;
        private bool _hasBody, _hasSpeed;

        /// <summary>
        /// The held body's bones, which take every change of the player's speed with the anchor (null on a second hand holding the same
        /// body: one hand carries it). See <see cref="Carried"/>.
        /// </summary>
        internal Rigidbody[] Bones;

        private void Awake() => _body = GetComponent<Rigidbody>();

        /// <summary>
        /// Where the hand is this frame, and the player's physics body the rig was placed against (where it was then, and the game's
        /// smoothing of the view then: the hand's place against the body holds none of it). <paramref name="jumped"/>: the hand
        /// jumped (a teleport, a snap turn, a long frame) and the body was carried: nothing between the old place and the new.
        /// </summary>
        internal void Follow(Vector3 palm, Quaternion rotation, Transform body, Vector3 bodyAt, Vector3 smoothing, bool jumped = false)
        {
            Vector3 from = palm - bodyAt - smoothing;
            float now = Time.time;
            if (!_active || jumped || body != _player)
            {
                _count = 0; // (nothing between the old place and this one)
                _hasBody = _hasSpeed = false;
            }
            if (_count == 0 || now > _time[_newest])
            {
                _newest = (_newest + 1) % Samples;
                _count = Mathf.Min(_count + 1, Samples);
            }
            _time[_newest] = now;
            _from[_newest] = from;
            _palm[_newest] = palm;
            _turn[_newest] = rotation;
            _player = body;
            _active = true;
        }

        /// <summary>Let go: it stays where it is (kinematic until the next grab, not drifting off at its last speed).</summary>
        internal void Stop()
        {
            _active = false;
            if (_body != null && !_body.isKinematic)
            {
                _body.velocity = _body.angularVelocity = Vector3.zero;
                _body.isKinematic = true;
            }
        }

        /// <summary>
        /// The player's change of speed in this step given to every bone of the held body too, so it is carried as one thing. The game
        /// starts, stops and turns back a walk or a run in one physics step (0 to 3.4 m/s walking, about 6 running): left to the hold,
        /// that reaches the one bone it holds at once and the rest by its joints, the limbs whipped round (the soldier check's walk,
        /// 2026-10-25: fastest bone 6.5 to 7 m/s without, 4 to 5 with). Smoothing the anchor's walk instead left the body a run start's
        /// half metre behind the hand. The hand's own swings stay the physics' (a throw, a slam).
        /// </summary>
        private void Carried(Vector3 body)
        {
            float dt = Time.fixedDeltaTime;
            if (_hasBody)
            {
                Vector3 speed = (body - _lastBody) / dt;
                if (_hasSpeed && Bones != null)
                {
                    Vector3 change = speed - _lastSpeed;
                    // (Not a respawn's or a ladder's jump: no walk changes by 15 m/s in a step.)
                    if (change.sqrMagnitude > 1e-4f && change.sqrMagnitude < 15f * 15f)
                    {
                        foreach (Rigidbody rb in Bones)
                        {
                            if (rb != null)
                            {
                                rb.velocity += change;
                            }
                        }
                    }
                }
                _lastSpeed = speed;
                _hasSpeed = true;
            }
            _lastBody = body;
            _hasBody = true;
        }

        private void FixedUpdate()
        {
            if (!_active || _body == null)
            {
                return;
            }
            // The hand as it is at this step's time, on the line through the frames' samples: taken as the last frame left it, a turn of
            // the view (applied once a frame) reached the anchor in uneven steps, one or two frames of it a step. At the step's own time,
            // not a step and a frame before it (always between two samples, for the kinematic anchor's sake): the body trailed the hand
            // by about 45 ms, 9 cm at a 2 m/s swing ("a grab delay between my hand and the body": user, 2026-10-25). The newest sample
            // is a frame old at most, so this looks a frame ahead at most (AnchorAhead).
            float at = Time.fixedTime;
            int newer = _newest, older = _newest;
            for (int k = 1; k < _count; k++)
            {
                int i = (_newest - k + Samples) % Samples;
                older = i;
                if (_time[i] <= at)
                {
                    break;
                }
                newer = i;
            }
            float span = _time[newer] - _time[older];
            float u = span > 1e-5f ? Mathf.Clamp((at - _time[older]) / span, 0f, AnchorAhead) : 1f;
            // From the player's physics body, which moves in the physics step only (the camera point the rig follows, and its parent,
            // also move once a frame: from them the anchor still went by uneven steps, 3.5 m/s off the step before while walking
            // steadily). (Not on foot, or the player gone since the frame: the hand as it was.)
            if (_player != null)
            {
                Carried(_player.position);
            }
            Vector3 place = _player != null ? _player.position + Vector3.LerpUnclamped(_from[older], _from[newer], u) : Vector3.LerpUnclamped(_palm[older], _palm[newer], u);
            Quaternion facing = Quaternion.SlerpUnclamped(_turn[older], _turn[newer], u);
            // Set going at the speed that takes it there in this step (see the class: the joint's physics sees how fast it moves).
            float dt = Time.fixedDeltaTime;
            if (dt < 1e-5f)
            {
                return; // the game's time stop (a katana finisher) sets the fixed step to 0: no infinite velocity from it
            }
            _body.velocity = (place - _body.position) / dt;
            (facing * Quaternion.Inverse(_body.rotation)).ToAngleAxis(out float angle, out Vector3 axis);
            angle = angle > 180f ? angle - 360f : angle;
            _body.angularVelocity = float.IsNaN(axis.x) || float.IsInfinity(axis.x) ? Vector3.zero : axis * (angle * Mathf.Deg2Rad / dt);
        }
    }

    /// <summary>
    /// Autotest only (<see cref="BodyImpacts.RecordContacts"/>): on a hand's grab anchor, how unevenly it moves from one physics step to
    /// the next (the largest change of its step velocity, m/s). Steady walking moves it the same each step; moved in Update with the
    /// hand's frame pose, a frame rate not a multiple of the physics rate could move it by uneven steps.
    /// </summary>
    internal sealed class AnchorProbe : MonoBehaviour
    {
        internal static float MostJerk;
        /// <summary>Set by the autotest while the walk is steady (not starting, turning back or stopping).</summary>
        internal static bool Steady;
        private Rigidbody _body;
        private Vector3 _last, _lastStep;
        private bool _has, _hasStep;

        private void Awake() => _body = GetComponent<Rigidbody>();

        private void FixedUpdate()
        {
            Vector3 now = _body.position;
            if (_has)
            {
                Vector3 step = (now - _last) / Time.fixedDeltaTime;
                if (_hasStep && BodyImpacts.RecordContacts && Steady)
                {
                    MostJerk = Mathf.Max(MostJerk, (step - _lastStep).magnitude);
                }
                _lastStep = step;
                _hasStep = true;
            }
            _last = now;
            _has = true;
        }
    }

    /// <summary>Autotest only (<see cref="BodyImpacts.RecordContacts"/>): every contact of a held bone each step, not only the first.</summary>
    internal sealed class ContactRecorder : MonoBehaviour
    {
        internal Actor Owner;
        private Rigidbody _body;

        private void Awake() => _body = GetComponent<Rigidbody>();

        private void OnCollisionEnter(Collision c) => BodyImpacts.Record(c, _body, Owner);

        private void OnCollisionStay(Collision c) => BodyImpacts.Record(c, _body, Owner);

        private void FixedUpdate()
        {
            if (!BodyImpacts.RecordContacts)
            {
                Destroy(this); // recording over
            }
        }
    }

    /// <summary>
    /// On each ragdoll bone of a soldier the player has taken hold of: an impact harder than <see cref="Threshold"/> hurts the
    /// soldier, and a soldier it hits too (Boneworks' blunt impact damage: a threshold, then more the harder), its speed counted
    /// no higher than its force allows (<see cref="ImpactMass"/>: a limb brushing the ground while he is lifted is no blow).
    /// Armed while held and for a few seconds after the throw; friends are hurt as enemies are.
    /// </summary>
    internal sealed class BodyImpacts : MonoBehaviour
    {
        /// <summary>
        /// Relative speed (m/s) below which an impact does nothing: a body dropped or dragged along the ground. Boneworks' (its
        /// thr_Impact of 10 on the speed squared): at 5, once the held body stopped whipping round the hand, a slam at a hand's
        /// real speed hurt nobody ("slamming them against the ground or an object they don't take damage": user, 2026-10-25).
        /// </summary>
        internal const float Threshold = 3.2f;
        /// <summary>
        /// Health per (m/s)^2 past the threshold's square: Boneworks' Enemy_Health weighs an impact by its speed squared (its
        /// thr_Impact 10 and mod_Impact 0.1 are on relativeVelocity.sqrMagnitude), so a slam at twice the speed hurts four times
        /// as much and a shove barely at all: 4 m/s takes 2.6, 5 m/s 6.6, 6 m/s 11.6, 8 m/s 24.
        /// </summary>
        internal const float PerSquare = 0.45f;
        /// <summary>The most one impact takes: a soldier has 100; three hard slams kill, one does not.</summary>
        internal const float Cap = 35f;
        /// <summary>
        /// The mass an impact's impulse is weighed against (kg, a trunk bone's): an impact counts at the speed at which that much body
        /// would have taken its impulse, if that is less than the contact's own speed. A trunk slammed down counts at its speed as
        /// before; a foot or a hand brushing the ground while the body is lifted or dragged, fast but with next to no force behind
        /// it, does not count ("they take damage even when only slightly touching the ground, maybe calculate force?": user,
        /// 2026-10-28).
        /// </summary>
        internal const float ImpactMass = 16f;

        private static readonly Dictionary<Actor, float> ArmedUntil = new Dictionary<Actor, float>();

        /// <summary>
        /// The joint motors of soldiers made limp (see <see cref="Arm"/>), to put back once they are no longer armed. The game's
        /// soldiers are active ragdolls: knocked over, every joint is still driven toward the animated pose (ActiveRaggy.Update,
        /// a slerp drive of 700-1000), so a held soldier stayed stiff in his falling pose, "crammed together" with "no physics"
        /// (user, 2026-10-24). Held and for the seconds after a throw the motors are off and he hangs and flies as a body does.
        /// Tried and dropped the same night (each blew a limb up to 800-2300 m/s in the soldier check): the dead bodies' drive
        /// of 50 on a living soldier (the game drives his joints toward the falling animation every frame, against the hand),
        /// motors off again on a body that died in flight, and more solver passes for armed bones.
        /// </summary>
        private static readonly Dictionary<Actor, JointDrive> SavedDrive = new Dictionary<Actor, JointDrive>();
        private struct HitWindow
        {
            public float At, Dealt;
        }

        private static readonly Dictionary<Actor, HitWindow> LastHit = new Dictionary<Actor, HitWindow>();
        /// <summary>The bones with a live component (one each: a bone re-armed in the frame its old one goes gets one new one).</summary>
        private static readonly HashSet<Rigidbody> ArmedBones = new HashSet<Rigidbody>();
        private static Actor _by;

        private Actor _owner;
        private Transform _ragdollRoot;
        private Rigidbody _body;
        private float _depenetration, _maxSpin;
        private int _iterations, _velocityIterations;
        private bool _limited;
        /// <summary>Destroyed at the end of this frame: no more impacts through it.</summary>
        private bool _gone;

        /// <summary>
        /// While armed (held, and for the seconds after a throw), the bone is pushed out of what it sinks into gently and never
        /// moves faster than this: a thrown body landing in the ground was shot out of it by the physics, one bone at 180 m/s
        /// in the autotest (2026-10-24).
        /// </summary>
        private const float ArmedDepenetration = 5f, ArmedMaxSpeed = 25f;
        /// <summary>
        /// And never turns faster than this (rad/s, about 1700 deg/s: a throw's whirl is under it), should the physics' answer for
        /// a held body go wrong again (with the hand's old anchor its hips read up to millions of deg/s: see GrabAnchor).
        /// </summary>
        private const float ArmedMaxSpin = 30f;
        /// <summary>
        /// And is solved with this many passes (the game's ragdolls get 3 and 2): 13 bones, 80 kg, hanging from one joint to the
        /// hand settle the closer for it, and it is one body at a time.
        /// </summary>
        private const int ArmedIterations = 16, ArmedVelocityIterations = 8;

        /// <summary>
        /// Only while armed: then the bone puts back what it changed and the component goes (it stayed on every bone of every
        /// soldier ever held, each running this every physics step for the rest of the match; perf pass, 2026-10-24).
        /// </summary>
        private void FixedUpdate()
        {
            if (_body == null)
            {
                Go();
                return;
            }
            if (_owner != null && ArmedUntil.TryGetValue(_owner, out float until))
            {
                if (Time.time <= until)
                {
                    if (!_limited)
                    {
                        _limited = true;
                        _depenetration = _body.maxDepenetrationVelocity;
                        _body.maxDepenetrationVelocity = ArmedDepenetration;
                        _maxSpin = _body.maxAngularVelocity;
                        _body.maxAngularVelocity = ArmedMaxSpin;
                        _iterations = _body.solverIterations;
                        _velocityIterations = _body.solverVelocityIterations;
                        _body.solverIterations = Mathf.Max(_iterations, ArmedIterations);
                        _body.solverVelocityIterations = Mathf.Max(_velocityIterations, ArmedVelocityIterations);
                    }
                    // The caps in game units at the real-time pace: in slow motion a held body follows the real-time hand (and a
                    // thrown one flies in real time), so many times faster in the slowed world; capped as at full speed, a body held
                    // at slow motion lagged the hand past a fifth of the cap.
                    float k = 1f / BodyGrab.SlowMotion();
                    float maxSpeed = ArmedMaxSpeed * k, maxSpin = ArmedMaxSpin * k;
                    if (!Mathf.Approximately(_body.maxAngularVelocity, maxSpin))
                    {
                        _body.maxAngularVelocity = maxSpin;
                    }
                    if (_body.velocity.sqrMagnitude > maxSpeed * maxSpeed)
                    {
                        _body.velocity = _body.velocity.normalized * maxSpeed;
                    }
                    if (_body.angularVelocity.sqrMagnitude > maxSpin * maxSpin)
                    {
                        _body.angularVelocity = _body.angularVelocity.normalized * maxSpin;
                    }
                    return;
                }
                Disarm(_owner); // the first of its bones to see the time is up: the soldier's joints back, once
            }
            if (_limited)
            {
                _body.maxDepenetrationVelocity = _depenetration;
                _body.maxAngularVelocity = _maxSpin;
                _body.solverIterations = _iterations;
                _body.solverVelocityIterations = _velocityIterations;
            }
            Go();
        }

        private void Go()
        {
            if (!_gone)
            {
                _gone = true;
                ArmedBones.Remove(_body);
                Destroy(this);
            }
        }

        /// <summary>The damage of an impact at this relative speed (pure: the offline tests check the curve).</summary>
        internal static float ImpactDamage(float speed) => speed <= Threshold ? 0f : Mathf.Min((speed * speed - Threshold * Threshold) * PerSquare, Cap);

        /// <summary>Arms (or keeps armed) every bone of the soldier until <paramref name="until"/> (game time).</summary>
        internal static void Arm(Actor a, float until, Actor by)
        {
            if (a == null || a.ragdoll == null || a.ragdoll.rigidbodies == null)
            {
                return;
            }
            _by = by;
            ArmedUntil[a] = until;
            Limp(a);
            Transform root = a.ragdoll.ragdollObject != null ? a.ragdoll.ragdollObject.transform : null;
            foreach (Rigidbody rb in a.ragdoll.rigidbodies)
            {
                if (rb != null && ArmedBones.Add(rb))
                {
                    BodyImpacts b = rb.gameObject.AddComponent<BodyImpacts>();
                    b._owner = a;
                    b._body = rb;
                    b._ragdollRoot = root;
                }
                if (RecordContacts && rb != null && rb.GetComponent<ContactRecorder>() == null)
                {
                    rb.gameObject.AddComponent<ContactRecorder>().Owner = a; // (the autotest's: every contact each step, not only the first)
                }
            }
        }

        /// <summary>
        /// A held body's joints: no spring, a little damping, as a limp body's muscles have (a spring fought the hand and blew limbs
        /// up: see above).
        /// </summary>
        private const float HeldDamper = 5f;

        /// <summary>The soldier's joint motors off (kept as the game set them, to put back on <see cref="Disarm"/>).</summary>
        internal static void Limp(Actor a)
        {
            if (!SavedDrive.ContainsKey(a))
            {
                // A corpse's drive is Actor.Die's 50, not the living one: the game respawned him in the hand (SpawnAt sets 700/3), the
                // let-go gave him the saved 50 back as a living soldier, and his next knockdown was floppy. Disarm's dead branch does
                // not read the saved drive, so a corpse is let go as before.
                SavedDrive[a] = a.dead ? new JointDrive { positionSpring = 700f, positionDamper = 3f, maximumForce = a.ragdoll.jointDrive.maximumForce } : a.ragdoll.jointDrive;
            }
            a.ragdoll.SetDrive(0f, HeldDamper);
            HoldTogether(a);
        }

        /// <summary>A ragdoll's joints' projection as the game set it, put back on <see cref="Disarm"/>.</summary>
        private sealed class Projection
        {
            public ConfigurableJoint[] Joints;
            public JointProjectionMode[] Modes;
            public float[] Distances, Angles;
        }

        private static readonly Dictionary<Actor, Projection> SavedProjection = new Dictionary<Actor, Projection>();

        /// <summary>
        /// While armed, the ragdoll's own joints are projected: no limb further than a few centimetres off the bone it hangs from,
        /// whatever pulls at it (bodies "formed themselves long": user, 2026-10-25, 1.1 m pulled to 2.9-8.2 m in the soldier check's
        /// walk while the hand's anchor was wrong: see GrabAnchor).
        /// </summary>
        private static void HoldTogether(Actor a)
        {
            ConfigurableJoint[] joints = a.ragdoll.joints;
            if (joints == null || SavedProjection.ContainsKey(a))
            {
                return;
            }
            var saved = new Projection
            {
                Joints = joints,
                Modes = new JointProjectionMode[joints.Length],
                Distances = new float[joints.Length],
                Angles = new float[joints.Length]
            };
            for (int k = 0; k < joints.Length; k++)
            {
                ConfigurableJoint j = joints[k];
                if (j == null)
                {
                    continue;
                }
                saved.Modes[k] = j.projectionMode;
                saved.Distances[k] = j.projectionDistance;
                saved.Angles[k] = j.projectionAngle;
                j.projectionMode = JointProjectionMode.PositionAndRotation;
                j.projectionDistance = 0.05f;
                j.projectionAngle = 180f; // (places only: a limb's turn is its joint's limits' business)
            }
            SavedProjection[a] = saved;
        }

        /// <summary>The ragdoll's joints projected as the game had them (see <see cref="HoldTogether"/>).</summary>
        private static void LetApart(Actor a)
        {
            if (!SavedProjection.TryGetValue(a, out Projection saved))
            {
                return;
            }
            SavedProjection.Remove(a);
            for (int k = 0; k < saved.Joints.Length; k++)
            {
                ConfigurableJoint j = saved.Joints[k];
                if (j != null)
                {
                    j.projectionMode = saved.Modes[k];
                    j.projectionDistance = saved.Distances[k];
                    j.projectionAngle = saved.Angles[k];
                }
            }
        }

        private static readonly List<Actor> Expired = new List<Actor>();

        /// <summary>
        /// Each frame: soldiers whose armed time is up (or who are gone) disarmed, whether or not a bone of theirs still runs: a thrown
        /// soldier who got up had his ragdoll switched off inside the 3 s, its bones' FixedUpdate never saw the time, and he stayed
        /// limp and armed until he was knocked down again (his own bones' knocks then hurt him as a thrown body's; grab scout).
        /// </summary>
        internal static void DisarmExpired()
        {
            if (ArmedUntil.Count == 0)
            {
                return;
            }
            float now = Time.time;
            foreach (KeyValuePair<Actor, float> kv in ArmedUntil)
            {
                if (kv.Key == null || now > kv.Value)
                {
                    Expired.Add(kv.Key);
                }
            }
            foreach (Actor a in Expired)
            {
                Disarm(a);
            }
            Expired.Clear();
        }

        /// <summary>
        /// No longer armed: the joints as the game would have them now (a living soldier's to get up with; a dead one's as
        /// Actor.Die sets them, not the living stiffness he had when taken, which held the body rigid in its last pose).
        /// The bones' components see it and go.
        /// </summary>
        internal static void Disarm(Actor a)
        {
            ArmedUntil.Remove(a);
            // Away from the hand now: the held gun's stand-in collides with him again (it ignored him while he was held; lifted
            // at the release itself, the body still overlapping the gun was shoved out of it).
            if (a != null && a.ragdoll != null)
            {
                GunCollider.Current?.StopIgnoring(a.ragdoll.ragdollObject);
                GunCollider.Current?.StopIgnoringUnder(a.ragdoll.ragdollObject);
            }
            if (SavedDrive.TryGetValue(a, out JointDrive drive))
            {
                SavedDrive.Remove(a);
                Restore(a, drive);
            }
            LetApart(a); // (its joints' projection as the game had it)
            if (ArmedUntil.Count == 0)
            {
                LastHit.Clear(); // (the soldiers hit by thrown bodies too: only the last 0.15 s matter)
            }
        }

        private static void Restore(Actor a, JointDrive drive)
        {
            if (a == null || a.ragdoll == null)
            {
                return;
            }
            if (a.dead)
            {
                a.ragdoll.SetDrive(DeadSpring, DeadDamper);
            }
            else
            {
                a.ragdoll.SetDrive(drive.positionSpring, drive.positionDamper);
            }
        }

        /// <summary>The joints of a dead soldier, as the game sets them (Actor.Die).</summary>
        private const float DeadSpring = 50f, DeadDamper = 1f;

        /// <summary>A new scene or VR stopping: nothing stays armed.</summary>
        internal static void Clear()
        {
            foreach (KeyValuePair<Actor, JointDrive> kv in SavedDrive)
            {
                Restore(kv.Key, kv.Value);
            }
            SavedDrive.Clear();
            // (Their joints' projection as the game had it, where the soldier is still there; the scene's gone ones dropped: kept,
            // every soldier ever held stayed in the list for the session.)
            foreach (Actor a in new List<Actor>(SavedProjection.Keys))
            {
                LetApart(a);
            }
            SavedProjection.Clear();
            ArmedUntil.Clear();
            LastHit.Clear();
            ArmedBones.RemoveWhere(b => b == null); // a scene's bones went with it; live ones go on their next step
            _by = null;
        }

        /// <summary>
        /// Autotest: while set, the hardest contact any armed bone has (its impulse, against what), each physics step: what pushes a
        /// held body about (walking with one held "pulled them apart and flung them": user, 2026-10-25).
        /// </summary>
        internal static bool RecordContacts;
        internal static float HardestImpulse;
        internal static string HardestContact = "";

        internal static void Record(Collision c, Rigidbody on, Actor owner)
        {
            float impulse = c.impulse.magnitude;
            if (impulse > HardestImpulse && c.collider != null)
            {
                HardestImpulse = impulse;
                Collider o = c.collider;
                bool own = owner != null && owner.ragdoll != null && owner.ragdoll.rigidbodies != null && o.attachedRigidbody != null
                           && System.Array.IndexOf(owner.ragdoll.rigidbodies, o.attachedRigidbody) >= 0;
                Actor whose = o.GetComponentInParent<Actor>();
                Rigidbody body = o.attachedRigidbody;
                HardestContact = $"{o.name} ({o.GetType().Name}, layer {o.gameObject.layer}{(PropPhysics.IsPlayerCollider(o) ? ", THE PLAYER'S" : "")}"
                                 + $"{(own ? ", its own bone" : "")}{(whose == owner ? ", ITS OWN SOLDIER'S" : whose != null ? $", {whose.name}'s" : "")}"
                                 + $", under {(o.transform.parent != null ? o.transform.parent.name : "nothing")}"
                                 + $"{(body == null ? ", no body" : body.isKinematic ? ", kinematic" : ", dynamic")}{(o.isTrigger ? ", trigger" : "")}, {(o.enabled ? "on" : "off")}) on {(on != null ? on.name : "?")}, {impulse:0.0} N s";
            }
        }

        private void OnCollisionStay(Collision c) => Crushed(c);

        /// <summary>
        /// What a contact's speed and impulse are multiplied by for the world's pace: a bone flying in real time (a throw in slow
        /// motion) by its flight's scale, a held one (it follows the real-time hand) by the time scale; else 1.
        /// </summary>
        private float WorldPace(float until)
        {
            float scale = RealTimeFlight.ScaleAtTouch(_body);
            return scale < 0.999f ? scale : float.IsPositiveInfinity(until) ? BodyGrab.SlowMotion() : 1f;
        }

        /// <summary>Crushed against something that does not give while a hand holds him (see BodyGrab.CrushImpulse): let go.</summary>
        private void Crushed(Collision c)
        {
            float crush = BodyGrab.CrushImpulse * BodyGrab.CrushImpulse;
            float impulse = c.impulse.sqrMagnitude;
            if (_gone || _owner == null || impulse < crush || c.collider == null)
            {
                return; // (first on the raw impulse: the world's pace is never over 1, and this runs for every resting contact)
            }
            float pace = ArmedUntil.TryGetValue(_owner, out float held) ? WorldPace(held) : 1f;
            if (impulse * pace * pace < crush)
            {
                return;
            }
            Rigidbody other = c.rigidbody;
            if (other != null && !other.isKinematic || BodyGrab.IsHeldBone(other) || OwnBone(c.collider))
            {
                return; // (a loose thing gives way)
            }
            BodyGrab.LetGoCrushed(_owner, c.collider, c.impulse.magnitude * pace);
        }

        private void OnCollisionEnter(Collision c)
        {
            Crushed(c);
            if (RecordContacts && !_gone)
            {
                Record(c, _body, _owner);
            }
            if (_gone || _owner == null || !ArmedUntil.TryGetValue(_owner, out float until) || Time.time > until)
            {
                return;
            }
            Collider other = c.collider;
            if (other == null || _ragdollRoot != null && other.transform.IsChildOf(_ragdollRoot) || OwnBone(other) || PropPhysics.IsPlayerCollider(other)
                || GunCollider.IsStandIn(other) || HandBody.Is(other))
            {
                return; // its own bones, the player's body and hands, the held gun's stand-in
            }
            // At the world's pace: in slow motion a thrown or held body's speed in game units is its real-time one over the time
            // scale, and a throw in slow motion hurt five times a throw at full speed.
            float pace = WorldPace(until);
            float speed = c.relativeVelocity.magnitude * pace;
            float damage = ImpactDamage(Mathf.Min(speed, c.impulse.magnitude * pace / ImpactMass));
            if (damage <= 0f)
            {
                return;
            }
            Vector3 point = c.contactCount > 0 ? c.GetContact(0).point : transform.position;
            Vector3 dir = speed > 1e-3f ? -c.relativeVelocity.normalized : Vector3.down;
            Hurt(_owner, damage, speed, point, dir, other);
            BodyGrab.FeelImpact(_owner, damage);
            // A soldier it hits is hurt and knocked over as well (thrown into a squad).
            Hitbox hb = other.GetComponent<Hitbox>();
            if (hb != null && hb.parent is Actor hit && hit != _owner && hit != _by && !hit.dead)
            {
                Hurt(hit, damage, speed, point, -dir, null);
                if (hit.ragdoll != null && !hit.ragdoll.IsRagdoll())
                {
                    hit.KnockOver(-dir * Mathf.Min(speed, 15f) * 20f);
                }
            }
        }

        /// <summary>One of the owner's own ragdoll bones (also without a ragdoll root to test against).</summary>
        private bool OwnBone(Collider other)
        {
            Rigidbody body = other.attachedRigidbody;
            Rigidbody[] own = _owner.ragdoll != null ? _owner.ragdoll.rigidbodies : null;
            return body != null && own != null && System.Array.IndexOf(own, body) >= 0;
        }

        private static void Hurt(Actor a, float damage, float speed, Vector3 point, Vector3 dir, Collider against)
        {
            // (A friend thrown or slammed is hurt as an enemy is: the player's bullets hurt his own side too.)
            if (a == null || a.dead)
            {
                return;
            }
            // One impact, many bones touching at once: counted once, at its hardest. A hand or a foot meets the ground first, its blow
            // light now (ImpactMass), and the trunk a few steps later takes the difference: first come alone, it held the slam to the
            // hand's share.
            if (LastHit.TryGetValue(a, out HitWindow window) && Time.time - window.At < 0.15f)
            {
                if (damage <= window.Dealt)
                {
                    return;
                }
                LastHit[a] = new HitWindow { At = window.At, Dealt = damage };
                damage -= window.Dealt;
            }
            else
            {
                LastHit[a] = new HitWindow { At = Time.time, Dealt = damage };
            }
            try
            {
                var info = new DamageInfo(DamageInfo.DamageSourceType.Melee, _by, null)
                {
                    healthDamage = damage,
                    balanceDamage = damage * 4f,
                    point = point,
                    direction = dir,
                };
                a.Damage(info);
                string key = "bodyimpact:" + a.GetInstanceID();
                if (!Log.Logged(key))
                {
                    Log.InfoOnce(key, $"{a.name}: thrown or slammed into {(against != null ? $"{against.name} (layer {against.gameObject.layer})" : "a soldier")} at {speed:0.0} m/s, {damage:0} damage.");
                }
            }
            catch (System.Exception e)
            {
                Log.ExceptionOnce("BodyImpacts.Hurt", e);
            }
        }
    }

    /// <summary>
    /// A soldier held in front is a human shield (user, 2026-10-24: "human shield when people are shooting people can hold it
    /// infront"). The game lets a bot's bullets pass through its own team's soldiers (Projectile.CanHitHitbox), so a held enemy
    /// stopped none of his friends' bullets: a held soldier's hitboxes are hit by every bullet, and the hit hurts him (the game's
    /// Actor.Damage has no team check). His ragdoll stands between the hand and whoever shoots.
    /// </summary>
    [HarmonyPatch(typeof(Projectile), nameof(Projectile.CanHitHitbox))]
    internal static class HumanShieldPatch
    {
        private static void Postfix(Projectile __instance, Hitbox hitbox, ref bool __result)
        {
            if (__result || !BodyGrab.AnyHeld || hitbox == null || !VRManager.Active)
            {
                return; // (nothing held, the usual case: no Unity object asked)
            }
            if (hitbox.parent is Actor a && a != __instance.killCredit && BodyGrab.IsHeld(a))
            {
                __result = true;
                if (!Log.Logged("humanshield")) // (its line not built again for every bullet after the first)
                {
                    Log.InfoOnce("humanshield", $"A shot hit {a.name}, held as a shield.");
                }
            }
        }
    }

    /// <summary>
    /// A live grenade let go on a held body sticks to the bone it was let go on (user, 2026-10-24: "Strapping grenade to a body
    /// and throwing it"): it follows the bone (and so the throw) until its own fuse sets it off. The game's grenade moves
    /// itself each frame (GrenadeProjectile.Update): after that, it is put back on the bone, its velocity the bone's (so it flies
    /// on with the body's motion if the body is gone).
    /// </summary>
    internal sealed class StrappedGrenade : MonoBehaviour
    {
        private Rigidbody _bone;
        private Transform _boneTransform, _self;
        private Projectile _projectile;
        private Actor _carrier;
        /// <summary>The carrier was seen dead: alive again later means respawned (actors are reused), the strap is off.</summary>
        private bool _carrierDied;
        private Vector3 _localPosition;
        private Quaternion _localRotation;

        /// <summary>Grenades strapped this session (the autotest's check).</summary>
        internal static int Count { get; private set; }

        /// <summary>The last grenade strapped (the autotest's).</summary>
        private static StrappedGrenade _last;

        /// <summary>Autotest: where the last strapped grenade sits against its bone's surface, and the countdown over it.</summary>
        internal static string LastNote()
        {
            StrappedGrenade s = _last;
            if (s == null || s._bone == null || s._self == null)
            {
                return "no strapped grenade";
            }
            Collider c = s._bone.GetComponent<Collider>();
            Vector3 at = s._self.position;
            string place = c == null ? "its bone has no collider"
                : (c.ClosestPoint(at) - at).sqrMagnitude < 1e-6f ? "INSIDE THE BODY"
                : $"{(c.ClosestPoint(at) - at).magnitude * 100f:0.0} cm out of {s._bone.name}";
            FuseCountdown fc = s.GetComponent<FuseCountdown>();
            return $"{place}, countdown {(fc != null && fc.Shown != null ? $"'{fc.Shown}'" : "NOT SHOWN")}";
        }

        internal static StrappedGrenade Strap(Projectile projectile, Rigidbody bone, Actor carrier, Vector3 at)
        {
            StrappedGrenade s = projectile.gameObject.AddComponent<StrappedGrenade>();
            s._projectile = projectile;
            s._carrier = carrier;
            s._bone = bone;
            s._boneTransform = bone.transform;
            s._self = projectile.transform;
            Transform b = s._boneTransform;
            at = OnSurface(bone, at);
            projectile.transform.position = at;
            s._localPosition = b.InverseTransformPoint(at);
            s._localRotation = Quaternion.Inverse(b.rotation) * projectile.transform.rotation;
            projectile.velocity = bone.velocity;
            if (projectile is GrenadeProjectile grenade && projectile.GetComponent<FuseCountdown>() == null)
            {
                projectile.gameObject.AddComponent<FuseCountdown>().Grenade = grenade;
            }
            Count++;
            _last = s;
            Log.InfoOnce("grenadestrap", $"Strapped {projectile.name} to {bone.name}: it goes off with the body.");
            return s;
        }

        /// <summary>How far out of the body a strapped grenade sits (m): about its own half width.</summary>
        private const float OnTop = 0.035f;

        /// <summary>
        /// On the bone's surface where the hand let it go, not where the palm was: let go against the body, the palm is inside its
        /// collider and the grenade was strapped inside the body, out of sight ("I'm not even seeing the grenade": user, 2026-10-25).
        /// </summary>
        private static Vector3 OnSurface(Rigidbody bone, Vector3 at)
        {
            Collider c = bone.GetComponent<Collider>();
            if (c == null || !c.enabled)
            {
                return at;
            }
            Vector3 surface = c.ClosestPoint(at);
            Vector3 outward;
            if ((surface - at).sqrMagnitude > 1e-6f)
            {
                outward = (at - surface).normalized; // (outside: on the side the hand was)
            }
            else
            {
                // Inside: out along the way from the bone's middle through the palm, to where that line leaves the collider.
                Vector3 middle = c.bounds.center;
                outward = at - middle;
                outward = outward.sqrMagnitude > 1e-6f ? outward.normalized : Vector3.up;
                surface = c.ClosestPoint(middle + outward * 2f);
            }
            return surface + outward * OnTop;
        }

        /// <summary>
        /// Gone off: the soldier it was strapped to dies at once, credited to the player (user, 2026-10-24: "they take damage
        /// and die directly (because the grenade is on them)"); everyone else takes the grenade's own explosion by distance,
        /// as the game (or the grenade's mod) sets it.
        /// </summary>
        internal void WentOff()
        {
            Actor a = _carrier;
            _carrier = null;
            Actor by = _projectile != null ? _projectile.killCredit : null;
            if (a == null || a.dead)
            {
                return; // (a friend it is strapped to dies of it as well: the player chose to strap it there)
            }
            try
            {
                a.Kill(new DamageInfo(DamageInfo.DamageSourceType.Explosion, by, _projectile != null ? _projectile.sourceWeapon : null)
                {
                    healthDamage = a.health + 100f,
                    balanceDamage = 500f,
                    isSplashDamage = true,
                    point = transform.position,
                    direction = Vector3.up
                });
                Log.InfoOnce("grenadestrap-kill", $"The grenade strapped to {a.name} went off on him.");
            }
            catch (System.Exception e)
            {
                Log.ExceptionOnce("StrappedGrenade.WentOff", e);
            }
        }

        private void LateUpdate()
        {
            if (_bone == null || _projectile == null)
            {
                Destroy(this); // the body gone: the grenade flies on by itself
                return;
            }
            if (_carrier != null)
            {
                if (_carrier.dead)
                {
                    _carrierDied = true;
                }
                else if (_carrierDied || _carrier.ragdoll != null && !_carrier.ragdoll.IsRagdoll() && !BodyGrab.IsHeld(_carrier))
                {
                    // (Not while he is held: a grab in the step after he was stood up put him down again, and his ragdoll read as put
                    // away for that step while the hand had him; review, 2026-10-30.)
                    // Respawned elsewhere before it went off (the game reuses its soldiers), or got up: his ragdoll is put away when he
                    // stands, and the grenade stayed frozen on its bone where he lay while its going off killed him wherever he had
                    // walked (bug hunt, 2026-10-30). It stays where the body was, a grenade like any other.
                    _carrier = null;
                    Destroy(this);
                    return;
                }
            }
            _self.SetPositionAndRotation(_boneTransform.TransformPoint(_localPosition), _boneTransform.rotation * _localRotation);
            _projectile.velocity = _bone.velocity;
        }
    }

    /// <summary>
    /// The seconds left on a strapped grenade's fuse, shown over it and turned to the player's eyes, white, then red for the last
    /// second (user, 2026-10-25: "there isn't a number going down"). It follows the grenade wherever it goes (a body thrown with
    /// it, the grenade flying on by itself) and is gone with the explosion.
    /// </summary>
    internal sealed class FuseCountdown : MonoBehaviour
    {
        internal GrenadeProjectile Grenade;
        /// <summary>The text shown now (the autotest's check), null before the first frame.</summary>
        internal string Shown => _text != null ? _text.text : null;
        private static Font _font;
        private GameObject _go;
        private TextMesh _text;
        private TextPlate _plate;
        private int _shown = -1;
        private int _fitFrames;

        private void Build()
        {
            if (_font == null)
            {
                _font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            }
            _go = new GameObject("RavenfieldVR Fuse Countdown") { layer = 18 }; // (as the magazine readout: drawn by the VR view)
            _text = _go.AddComponent<TextMesh>();
            _text.font = _font;
            _text.fontSize = 64;
            _text.characterSize = 0.005f; // about 3 cm tall digits: read from across a room's width
            _text.fontStyle = FontStyle.Bold;
            _text.anchor = TextAnchor.LowerCenter;
            _text.alignment = TextAlignment.Center;
            var mr = _go.GetComponent<MeshRenderer>();
            mr.sharedMaterial = _font.material;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
            _plate = new TextPlate(_text, _font.material, 0.75f, 0.008f);
            _fitFrames = 2;
        }

        private void LateUpdate()
        {
            VRRig rig = VRRig.Instance;
            if (Grenade == null || !Grenade.enabled || rig == null || rig.Head == null)
            {
                Destroy(this); // gone off (the game switches the grenade off as it explodes), or nothing to show it to
                return;
            }
            if (_go == null)
            {
                Build();
            }
            float left = Mathf.Max(0f, Grenade.detonationTime - Time.time);
            // Whole seconds, then tenths for the last one.
            int shown = left >= 1f ? Mathf.CeilToInt(left) * 10 : Mathf.CeilToInt(left * 10f);
            if (shown != _shown)
            {
                _shown = shown;
                _text.text = left >= 1f ? (shown / 10).ToString() : (shown / 10f).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
                _text.color = left >= 1f ? new Color(0.85f, 0.93f, 1f) : new Color(1f, 0.3f, 0.25f);
                _fitFrames = 2; // (the plate round the new text, laid out by the next frame)
            }
            Transform head = rig.Head;
            Vector3 at = Grenade.transform.position + Vector3.up * 0.07f;
            Vector3 toEye = head.position - at;
            at += toEye.normalized * 0.04f; // (a little toward the eyes: in front of the body it sits on)
            // A beat as each new number comes up (a quarter bigger, back in a quarter second).
            float beat = 1f + 0.25f * Mathf.Clamp01(1f - (Mathf.Ceil(left) - left) * 4f);
            _go.transform.SetPositionAndRotation(at, Quaternion.LookRotation(-toEye.normalized, head.up));
            _go.transform.localScale = Vector3.one * beat;
            if (_fitFrames > 0)
            {
                _fitFrames--;
                _plate.Fit();
            }
        }

        private void OnDestroy()
        {
            _plate?.Destroy(); // (its own material: left, every strapped grenade leaked one)
            if (_go != null)
            {
                Destroy(_go);
            }
        }
    }

    /// <summary>
    /// A living soldier the hand holds as a ragdoll does not get up: the user's no-knockdown mutator stood held soldiers up out of
    /// the hand, and as he got up the game blended his limbs back to his animation while the hand held them, a bone to 33 m/s
    /// (user, 2026-10-25; the soldier check with his mods: "stood up while held"). Let go, he gets up as the game has him.
    /// (A respawn stands the soldier up another way, ActiveRaggy.InstantAnimate: not stopped.)
    /// </summary>
    [HarmonyPatch]
    internal static class HeldStaysDownPatch
    {
        private static System.Collections.Generic.IEnumerable<System.Reflection.MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(Actor), "GetUp");
            yield return AccessTools.Method(typeof(Actor), nameof(Actor.InstantGetUp));
        }

        private static bool Prefix(Actor __instance, System.Reflection.MethodBase __originalMethod)
        {
            if (__instance == null || __instance.dead || __instance.ragdoll == null || !__instance.ragdoll.IsRagdoll() || !BodyGrab.IsHeld(__instance))
            {
                return true;
            }
            if (!Log.Logged("bodygrab-keptdown"))
            {
                Log.InfoOnce("bodygrab-keptdown", $"Kept {__instance.name} down while held ({__originalMethod.Name} asked him up: the game's fall timer, or a mod such as a no-knockdown mutator).");
            }
            return false;
        }
    }

    /// <summary>A strapped grenade going off kills the soldier it is strapped to (<see cref="StrappedGrenade.WentOff"/>).</summary>
    [HarmonyPatch(typeof(GrenadeProjectile), nameof(GrenadeProjectile.Explode))]
    internal static class StrappedGrenadeExplodePatch
    {
        private static void Postfix(GrenadeProjectile __instance)
        {
            StrappedGrenade s = __instance != null ? __instance.GetComponent<StrappedGrenade>() : null;
            if (s != null)
            {
                s.WentOff();
            }
        }
    }
}
