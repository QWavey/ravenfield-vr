using UnityEngine;

namespace RavenfieldVR.Weapons
{
    /// <summary>
    /// A magazine the player lets go of in slow motion flies as it would in real time until it first touches something. The
    /// hand's velocity is measured in real time (the player's arms are not slowed), and the slowed world took it as game speed:
    /// at the game's slow motion (time scale 0.2) a magazine let go at 1 m/s went on at a fifth of that, stopped by the hand and
    /// sank there under a twenty-fifth of gravity ("the mag floats for one second in the air and then falls"; user, 2026-10-28).
    /// Here its velocity and spin are divided by the time scale and gravity is made as strong in game time as it is in real time
    /// (g / s^2), so the flight looks as without slow motion. At its first touch it is given back to the world's pace (its velocity
    /// times the time scale), so it settles with the slowed world and never rests under 25 times gravity. Slow motion switched on
    /// or off in flight is followed; after 3 s of real time it ends anyway. A thrown soldier's bones fly as one (BeginGroup): the
    /// first of them to touch something gives them all back, so his joints never pull between bones at two paces.
    /// </summary>
    internal sealed class RealTimeFlight : MonoBehaviour
    {
        private Rigidbody _body;
        /// <summary>The time scale the body's velocity is made out for (1: none).</summary>
        private float _scale = 1f;
        private float _maxSpin;
        private float _until;
        /// <summary>The bodies flying as one with this one (a soldier's bones), or null.</summary>
        private RealTimeFlight[] _group;
        /// <summary>The scale it ended from, and the physics step it ended in: a touch's other handlers read it (ScaleAtTouch).</summary>
        private float _endedScale = 1f, _endedStep = -1f;

        /// <summary>
        /// The time scale the body's velocity was made out for at a touch now (1 when it is not flying in real time): a contact's
        /// speed times this is its speed at the world's pace (a thrown soldier's impact damage). Read in a collision callback, in
        /// whichever order the components get it.
        /// </summary>
        internal static float ScaleAtTouch(Rigidbody body)
        {
            RealTimeFlight f = body != null ? body.GetComponent<RealTimeFlight>() : null;
            if (f == null)
            {
                return 1f;
            }
            if (f.enabled)
            {
                return f._scale;
            }
            return Mathf.Approximately(f._endedStep, Time.fixedTime) ? f._endedScale : 1f;
        }

        /// <summary>Gives the body back to the world's pace now, if it flies (taken into a hand, it may be let go before a step ran).</summary>
        internal static void Stop(Rigidbody body)
        {
            RealTimeFlight f = body != null ? body.GetComponent<RealTimeFlight>() : null;
            if (f != null && f.enabled)
            {
                f.End();
            }
        }

        /// <summary>
        /// <see cref="Begin"/> for bodies flying as one (a thrown soldier's bones, each given the hand's velocity first). Returns
        /// what their velocity was multiplied by (1 when not in slow motion).
        /// </summary>
        internal static float BeginGroup(Rigidbody[] bodies)
        {
            float s = Time.timeScale;
            if (bodies == null || s <= 0f || s >= 0.999f || Core.VRConfig.SlowedHands)
            {
                return 1f;
            }
            var group = new System.Collections.Generic.List<RealTimeFlight>(bodies.Length);
            foreach (Rigidbody b in bodies)
            {
                if (b != null && !b.isKinematic && Begin(b) != 1f)
                {
                    group.Add(b.GetComponent<RealTimeFlight>());
                }
            }
            RealTimeFlight[] members = group.ToArray();
            foreach (RealTimeFlight f in members)
            {
                f._group = members;
            }
            return 1f / s;
        }

        /// <summary>
        /// Called right after the body was let go with its velocity: in slow motion it flies in real time from here. Returns what its
        /// velocity was multiplied by (1 when not in slow motion).
        /// </summary>
        internal static float Begin(Rigidbody body)
        {
            float s = Time.timeScale;
            // (Slowed hands: the hand lives in the slowed world, its velocity is in game units already, and what it lets go of
            // moves at the world's pace.)
            if (body == null || body.isKinematic || s <= 0f || s >= 0.999f || Core.VRConfig.SlowedHands)
            {
                return 1f; // (paused: let go with no time passing, it waits as the world does)
            }
            RealTimeFlight f = body.GetComponent<RealTimeFlight>() ?? body.gameObject.AddComponent<RealTimeFlight>();
            f._body = body;
            f._until = Time.unscaledTime + 3f;
            if (f._scale >= 0.999f)
            {
                f._maxSpin = body.maxAngularVelocity;
            }
            f.enabled = true;
            f._group = null; // (alone unless BeginGroup says otherwise)
            f._scale = 1f; // the velocity just given is the hand's own, real-time one (also when let go again mid-flight)
            f.Rescale(s);
            return 1f / s;
        }

        /// <summary>The velocity made out for time scale <paramref name="to"/> instead of the one it was made out for.</summary>
        private void Rescale(float to)
        {
            float k = _scale / to;
            if (!_body.isKinematic)
            {
                _body.velocity *= k;
                _body.angularVelocity *= k;
            }
            _body.maxAngularVelocity = to >= 0.999f ? _maxSpin : _maxSpin / to; // (a 9 rad/s tumble is 45 in game time)
            _scale = to;
        }

        private void FixedUpdate()
        {
            if (_body == null || _body.isKinematic)
            {
                End(); // (taken into a hand again)
                return;
            }
            float s = Time.timeScale;
            if (s >= 0.999f || Time.unscaledTime > _until)
            {
                End();
                return;
            }
            if (s < 0.01f)
            {
                return; // (the game's time stop, a katana finisher: no rescale by a zero scale, it waits as the world does)
            }
            if (!Mathf.Approximately(s, _scale))
            {
                Rescale(s);
            }
            if (_body.useGravity)
            {
                _body.AddForce(Physics.gravity * (1f / (s * s) - 1f), ForceMode.Acceleration); // (PhysX adds the one g itself)
            }
        }

        private void OnCollisionEnter(Collision c) => End();

        private void End()
        {
            if (!enabled)
            {
                return; // (a touch on a body that is not flying: collision messages reach disabled components too)
            }
            _endedScale = _scale;
            _endedStep = Time.fixedTime;
            if (_body != null && _scale < 0.999f)
            {
                Rescale(1f);
            }
            enabled = false;
            RealTimeFlight[] group = _group;
            _group = null;
            if (group != null)
            {
                foreach (RealTimeFlight f in group)
                {
                    if (f != null && f != this)
                    {
                        f.End();
                    }
                }
            }
        }
    }
}
