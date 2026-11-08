using RavenfieldVR.Rig;
using UnityEngine;

namespace RavenfieldVR.Core.Showcase
{
    /// <summary>
    /// The gun hand alive while the other hand works the gun (the user, 2026-11-08: "the arm which is not reloading is too stiff,
    /// nobody would be like that in VR"): held at a base pose, the gun breathes a little, comes to meet the free hand (toward its palm,
    /// at most 4 cm, its well tipped toward it), and answers each moment of the work: a flick that shakes the magazine out, a jolt when
    /// the new one slaps home, tipped toward the rack hand and pushed against its pull, a knock when the slide slams home. Each moment by
    /// the choreography's own note (its time from when the note began).
    /// </summary>
    internal sealed class ShowcaseGunLife
    {
        private string _note;
        private float _since;

        /// <summary>The gun placed at <paramref name="basePose"/> with its life this frame; <paramref name="note"/> the work's moment (or null).</summary>
        internal void Place(ShowcaseRecorder rec, Showcase.GunPose basePose, string note, VRHand off, bool support)
        {
            float t = rec.Elapsed;
            if (note != _note)
            {
                _note = note;
                _since = t;
            }
            float u = t - _since;
            Vector3 at = basePose.At;
            Quaternion aim = basePose.Aim;
            Vector3 fwd = aim * Vector3.forward, up = aim * Vector3.up, right = aim * Vector3.right;
            // Breathing: a few millimetres and about a degree, slow.
            at += up * (0.004f * Mathf.Sin(t * 1.9f)) + right * (0.003f * Mathf.Sin(t * 1.3f + 1f));
            aim = Quaternion.AngleAxis(1.1f * Mathf.Sin(t * 1.6f), up) * Quaternion.AngleAxis(0.8f * Mathf.Sin(t * 2.1f + 0.5f), right) * aim;
            // Meeting the free hand as it comes within 45 cm: toward its palm and the gun's underside tipped to it.
            if (off != null && !support)
            {
                Vector3 to = off.Palm.position - at;
                float near = Mathf.Clamp01((0.45f - to.magnitude) / 0.3f);
                if (near > 0f)
                {
                    at += Vector3.ClampMagnitude(to * 0.12f, 0.04f) * near;
                    Vector3 side = Vector3.ProjectOnPlane(to, fwd);
                    if (side.sqrMagnitude > 1e-6f)
                    {
                        aim = Quaternion.FromToRotation(-up, Vector3.Slerp(-up, side.normalized, 0.14f * near)) * aim;
                    }
                }
            }
            float Pulse(float from, float length) => u < from || u > from + length ? 0f : Mathf.Sin(Mathf.PI * (u - from) / length);
            float Knock(float from, float length) => u < from ? 0f : Mathf.Exp(-(u - from) / length) * Mathf.Clamp01((u - from) / 0.02f);
            switch (note)
            {
                case "dropping":
                    // A flick of the wrist: rolled out and back, a little down, as the magazine is shaken free.
                    aim = Quaternion.AngleAxis(-14f * Pulse(0f, 0.26f), fwd) * aim;
                    at -= up * (0.015f * Pulse(0f, 0.26f));
                    break;
                case "inserting":
                    // The slap home (about 0.3 s into the insert): the gun jolts up the well's line and nods.
                    at += up * (0.012f * Knock(0.3f, 0.06f));
                    aim = Quaternion.AngleAxis(-3f * Knock(0.3f, 0.07f), right) * aim;
                    break;
                case "rack reaching":
                case "bolt reaching":
                    // Turned to the hand coming for the handle, brought a little back toward the body.
                    aim = Quaternion.AngleAxis(9f * Mathf.Clamp01(u / 0.3f), fwd) * aim;
                    at -= fwd * (0.02f * Mathf.Clamp01(u / 0.3f));
                    break;
                case "rack taking":
                case "bolt taking":
                    aim = Quaternion.AngleAxis(9f, fwd) * aim;
                    at -= fwd * 0.02f;
                    break;
                case "rack pulling back":
                case "bolt pulling back":
                    // Pushed forward against the pull.
                    aim = Quaternion.AngleAxis(9f, fwd) * aim;
                    at += fwd * (-0.02f + 0.03f * Pulse(0f, 0.24f));
                    break;
                case "rack let go":
                case "bolt let go":
                    // The slide slamming home knocks it forward; the turn eases back.
                    aim = Quaternion.AngleAxis(9f * (1f - Mathf.Clamp01(u / 0.3f)), fwd) * aim;
                    at += fwd * (-0.02f * (1f - Mathf.Clamp01(u / 0.3f)) + 0.008f * Knock(0f, 0.05f));
                    break;
            }
            Showcase.PlaceGunAt(at, aim, support);
        }
    }
}
