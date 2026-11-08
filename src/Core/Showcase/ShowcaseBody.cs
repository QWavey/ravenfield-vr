using System;
using System.Collections.Generic;
using RavenfieldVR.Rig;
using UnityEngine;

namespace RavenfieldVR.Core.Showcase
{
    /// <summary>
    /// The player's body as the cinematic cameras see it during a showcase take: the game animates it on its own (its facing, its aim
    /// pose holding a gun that is not there), while the gun and the gloves follow the VR hands, so in third person the gun floated beside
    /// the body and its arms held nothing (take 5, 2026-11-08). Each frame before rendering (after the rig and the gloves are placed):
    /// the body turned to the headset's facing and put under it, its head looking where the headset looks, each arm reaching its glove
    /// (two-bone IK, the elbow down and out), the body's own hands tucked inside the gloves' cuffs. Showcase takes only; bones the game
    /// does not animate (the hands' scale) are put back at the end.
    /// </summary>
    internal static class ShowcaseBody
    {
        private static bool _on;
        private static readonly List<(Transform bone, Vector3 scale)> Scaled = new List<(Transform, Vector3)>();
        private static int _warned;

        internal static void Begin()
        {
            if (_on)
            {
                return;
            }
            _on = true;
            Application.onBeforeRender += Pose; // (after the mod's own: the rig, the gun and the gloves are placed first)
        }

        internal static void End()
        {
            if (!_on)
            {
                return;
            }
            _on = false;
            Application.onBeforeRender -= Pose;
            Fold(null, false);
            foreach ((Transform bone, Vector3 scale) in Scaled)
            {
                if (bone != null)
                {
                    bone.localScale = scale;
                }
            }
            Scaled.Clear();
        }

        private static void Pose()
        {
            try
            {
                PoseNow();
            }
            catch (Exception e)
            {
                if (_warned++ == 0)
                {
                    Log.Warn("Showcase: the body could not be posed: " + e);
                }
            }
        }

        private static void PoseNow()
        {
            Actor a = LocalPlayer.actor;
            VRRig rig = VRRig.Instance;
            if (a == null || a.dead || a.ragdoll == null || rig == null || rig.Head == null || a.ragdoll.IsRagdoll())
            {
                return;
            }
            Transform B(HumanBodyBones b) => a.ragdoll.HumanBoneTransformAnimated(b);
            Transform hips = B(HumanBodyBones.Hips), head = B(HumanBodyBones.Head);
            Transform lUp = B(HumanBodyBones.LeftUpperArm), rUp = B(HumanBodyBones.RightUpperArm);
            if (hips == null || head == null || lUp == null || rUp == null)
            {
                return;
            }
            Transform eyes = rig.Head;
            // From the eyes this frame (the scene's camera, asked before the drawing): the head and its helmet folded into the neck, the
            // camera sits where they are. Unfolded again for the other cameras (its scale is not the animator's to put back).
            ShowcaseCamera.Shot next = ShowcaseRecorder.Current != null && ShowcaseRecorder.Current.Shot != null ? ShowcaseRecorder.Current.Shot() : ShowcaseCamera.Shot.None;
            Fold(head, next.Valid && next.FromEyes);
            Vector3 lookFlat = Vector3.ProjectOnPlane(eyes.forward, Vector3.up);
            if (lookFlat.sqrMagnitude < 1e-4f)
            {
                lookFlat = Vector3.ProjectOnPlane(eyes.up, Vector3.up);
            }
            lookFlat.Normalize();
            // Turned: the shoulders' line square to the headset's facing.
            Vector3 right = Vector3.ProjectOnPlane(rUp.position - lUp.position, Vector3.up);
            if (right.sqrMagnitude > 1e-6f)
            {
                Vector3 bodyFwd = Vector3.Cross(right.normalized, Vector3.up);
                float turn = Vector3.SignedAngle(bodyFwd, lookFlat, Vector3.up);
                hips.RotateAround(hips.position, Vector3.up, turn);
            }
            // Under the head: the head bone (the skull's base) a little behind and below the eyes.
            Vector3 headAt = eyes.position - lookFlat * 0.09f - Vector3.up * 0.1f;
            Vector3 shift = headAt - head.position;
            hips.position += new Vector3(shift.x, 0f, shift.z);
            // The head looking where the headset looks (from the body's own facing, at most 70 degrees off it).
            Quaternion look = Quaternion.FromToRotation(lookFlat, eyes.forward);
            look.ToAngleAxis(out float ang, out Vector3 axis);
            if (ang > 180f)
            {
                ang -= 360f;
            }
            head.rotation = Quaternion.AngleAxis(Mathf.Clamp(ang, -70f, 70f), axis) * head.rotation;
            // The arms to the gloves.
            foreach (VRHand hand in rig.Hands)
            {
                if (hand == null || !hand.HasPose || hand.Model == null || hand.Model.Root == null)
                {
                    continue;
                }
                bool left = hand.IsLeft;
                Transform up = left ? lUp : rUp;
                Transform low = B(left ? HumanBodyBones.LeftLowerArm : HumanBodyBones.RightLowerArm);
                Transform wrist = B(left ? HumanBodyBones.LeftHand : HumanBodyBones.RightHand);
                if (low == null || wrist == null)
                {
                    continue;
                }
                Transform glove = hand.Model.Root;
                // The glove's cuff (palm space: +Z the fingers, its wrist row at -0.058): the sleeve ends just inside it.
                Vector3 target = glove.TransformPoint(new Vector3(0f, 0f, -0.075f));
                Vector3 outward = Vector3.ProjectOnPlane(up.position - hips.position, Vector3.up).normalized;
                Vector3 pole = up.position + Vector3.down * 0.5f + outward * 0.35f - lookFlat * 0.15f;
                TwoBone(up, low, wrist, target, pole);
                Tuck(wrist);
            }
        }

        /// <summary>
        /// The classic two-bone solve: the upper bone turned so the elbow lies on the circle where both lengths meet the target, on the
        /// pole's side, then the lower bone turned onto the target (out of reach: the arm straight toward it).
        /// </summary>
        private static void TwoBone(Transform upper, Transform lower, Transform end, Vector3 target, Vector3 pole)
        {
            Vector3 s = upper.position;
            float a = Vector3.Distance(s, lower.position), b = Vector3.Distance(lower.position, end.position);
            if (a < 1e-4f || b < 1e-4f)
            {
                return;
            }
            Vector3 toT = target - s;
            float d = Mathf.Clamp(toT.magnitude, Mathf.Abs(a - b) + 1e-3f, a + b - 1e-3f);
            Vector3 dir = toT.sqrMagnitude > 1e-8f ? toT.normalized : upper.forward;
            Vector3 bend = Vector3.ProjectOnPlane(pole - s, dir);
            bend = bend.sqrMagnitude > 1e-8f ? bend.normalized : Vector3.ProjectOnPlane(Vector3.down, dir).normalized;
            float cosA = Mathf.Clamp((a * a + d * d - b * b) / (2f * a * d), -1f, 1f);
            float sinA = Mathf.Sqrt(1f - cosA * cosA);
            Vector3 elbow = s + dir * (cosA * a) + bend * (sinA * a);
            upper.rotation = Quaternion.FromToRotation(lower.position - s, elbow - s) * upper.rotation;
            lower.rotation = Quaternion.FromToRotation(end.position - lower.position, target - lower.position) * lower.rotation;
        }

        private static Transform _folded;
        private static Vector3 _foldedScale;

        private static void Fold(Transform head, bool fold)
        {
            if (fold && _folded == null)
            {
                _folded = head;
                _foldedScale = head.localScale;
                head.localScale = Vector3.one * 0.001f;
            }
            else if (!fold && _folded != null)
            {
                _folded.localScale = _foldedScale;
                _folded = null;
            }
        }

        /// <summary>The body's own hand made tiny (inside the glove), its scale kept to be put back.</summary>
        private static void Tuck(Transform hand)
        {
            if (hand.localScale.x > 0.01f)
            {
                Scaled.Add((hand, hand.localScale));
                hand.localScale = Vector3.one * 0.001f;
            }
        }
    }
}
