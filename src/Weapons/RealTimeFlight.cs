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
    /// or off in flight is followed; after 3 s of real time it ends anyway.
    /// </summary>
    internal sealed class RealTimeFlight : MonoBehaviour
    {
        private Rigidbody _body;
        /// <summary>The time scale the body's velocity is made out for (1: none).</summary>
        private float _scale = 1f;
        private float _maxSpin;
        private float _until;

        /// <summary>
        /// Called right after the body was let go with its velocity: in slow motion it flies in real time from here. Returns what its
        /// velocity was multiplied by (1 when not in slow motion).
        /// </summary>
        internal static float Begin(Rigidbody body)
        {
            float s = Time.timeScale;
            if (body == null || body.isKinematic || s <= 0f || s >= 0.999f)
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
            if (_body != null && _scale < 0.999f)
            {
                Rescale(1f);
            }
            enabled = false;
        }
    }
}
