using RavenfieldVR.Core;
using RavenfieldVR.Rig;
using RavenfieldVR.Runtime;
using UnityEngine;

namespace RavenfieldVR.Weapons
{
    /// <summary>
    /// The weapon's own first-person arms (its gloves, sleeves and authored finger poses) driven by the VR hands:
    /// both arms are re-rooted at shoulders on the player's body; the gun hand keeps its authored grip on the weapon;
    /// the off hand takes the authored foregrip pose while it holds the gun and follows the controller otherwise (settled
    /// onto a bolt, handle or cover it works, as the VR glove is), easing between the two. Two-bone IK per arm, elbow
    /// bent toward a natural pole; an out-of-reach target slides the shoulder toward it.
    /// </summary>
    internal sealed class ArmIK
    {
        private sealed class Arm
        {
            public ArmChain Chain;
            public Transform Root, Upper, Lower, Hand;
            public bool Valid => Root != null && Upper != null && Lower != null && Hand != null;
        }

        private readonly WeaponSystem _sys;
        private readonly HeldWeapon _held;
        private readonly Arm _right, _left;
        /// <summary>How far the off arm has gone over to the weapon's support grip (0: the controller, 1: the grip).</summary>
        private float _onGunBlend = -1f;

        /// <summary>Self-test: after the last solve, how far (m) each hand bone ended from its target, and whether a shoulder had to slide.</summary>
        internal float GunHandError { get; private set; } = -1f;
        internal float OffHandError { get; private set; } = -1f;
        internal bool Stretched { get; private set; }

        public bool Active => _right.Valid || _left.Valid;

        public ArmIK(WeaponSystem sys, HeldWeapon held)
        {
            _sys = sys;
            _held = held;
            Transform root = held.Weapon.transform;
            // The model's right arm is the gun arm (its mesh is not mirrored for left-handed play).
            _right = Resolve(root, held.Rig.Data.ArmRight);
            _left = Resolve(root, held.Rig.Data.ArmLeft);
        }

        private static Arm Resolve(Transform root, ArmChain chain)
        {
            var arm = new Arm { Chain = chain };
            if (chain != null)
            {
                arm.Root = WeaponRig.Resolve(root, chain.Root);
                arm.Upper = WeaponRig.Resolve(root, chain.Upper);
                arm.Lower = WeaponRig.Resolve(root, chain.Lower);
                arm.Hand = WeaponRig.Resolve(root, chain.Hand);
            }
            return arm;
        }

        /// <summary>After the weapon is placed and its parts posed, before rendering.</summary>
        public void Solve()
        {
            BodyZones zones = _sys.Zones;
            Vector3 neck = _sys.Rig.Head.position - Vector3.up * 0.12f - zones.TorsoForward * 0.08f;
            float side = VRConfig.LeftHanded ? -1f : 1f; // the gun arm's shoulder is on the dominant side
            Vector3 gunShoulder = neck - Vector3.up * 0.06f + zones.TorsoRight * (0.17f * side);
            Vector3 offShoulder = neck - Vector3.up * 0.06f - zones.TorsoRight * (0.17f * side);
            Vector3 down = Vector3.down;

            Stretched = false;
            if (_right.Valid)
            {
                // Gun hand: exactly where its animation holds the grip.
                RigidPose target = RigidPose.Of(_right.Hand);
                Vector3 pole = gunShoulder + down * 0.6f + zones.TorsoRight * (0.35f * side) - zones.TorsoForward * 0.2f;
                SolveArm(_right, gunShoulder, target, pole);
                GunHandError = Vector3.Distance(_right.Hand.position, target.position);
            }
            if (_left.Valid)
            {
                VRHand off = _sys.Rig.Off;
                bool onGun = off.Role == HandRole.Foregrip || _held.TwoHanded || !off.Tracked;
                RigidPose grip = RigidPose.Of(_left.Hand); // the authored support grip
                // Free: the grasp follows the controller palm, settled onto a bolt, handle or cover it works.
                ArmChain c = _left.Chain;
                RigidPose palm = off.Palm;
                Quaternion rot = Quaternion.LookRotation(palm.Forward, palm.Up) * Quaternion.Inverse(Quaternion.LookRotation(c.FingerDir, c.BackDir));
                Vector3 palmPoint = rot * Vector3.Scale(c.PalmPoint, _left.Hand.lossyScale);
                Vector3 at = off.PalmCentre;
                if (off.Model != null && off.Model.GraspAnchor.HasValue)
                {
                    at += Vector3.ClampMagnitude(off.Model.GraspAnchor.Value - at, 0.05f);
                }
                var free = new RigidPose(at - palmPoint, rot);
                // Taking hold of the handguard or letting go eases over a tenth of a second instead of snapping.
                float want = onGun ? 1f : 0f;
                _onGunBlend = _onGunBlend < 0f ? want : Mathf.MoveTowards(_onGunBlend, want, Time.unscaledDeltaTime * 10f);
                RigidPose target = new RigidPose(Vector3.Lerp(free.position, grip.position, _onGunBlend), Quaternion.Slerp(free.rotation, grip.rotation, _onGunBlend));
                Vector3 pole = offShoulder + down * 0.6f - zones.TorsoRight * (0.35f * side) - zones.TorsoForward * 0.2f;
                SolveArm(_left, offShoulder, target, pole);
                OffHandError = Vector3.Distance(_left.Hand.position, target.position);
            }
        }

        private void SolveArm(Arm arm, Vector3 shoulder, RigidPose target, Vector3 pole)
        {
            float a = Vector3.Distance(arm.Upper.position, arm.Lower.position);
            float b = Vector3.Distance(arm.Lower.position, arm.Hand.position);
            if (a < 1e-3f || b < 1e-3f)
            {
                return;
            }
            // Re-root: move the arm's own root so the shoulder joint lands on the body's shoulder.
            Vector3 toTarget = target.position - shoulder;
            float reach = (a + b) * 0.995f;
            if (toTarget.magnitude > reach)
            {
                shoulder = target.position - toTarget.normalized * reach; // stretch: the shoulder follows the hand
                Stretched = true;
            }
            arm.Root.position += shoulder - arm.Upper.position;

            Vector3 s = arm.Upper.position;
            Vector3 t = target.position;
            float d = Mathf.Clamp(Vector3.Distance(s, t), Mathf.Abs(a - b) + 1e-3f, reach);
            Vector3 dir = (t - s).sqrMagnitude > 1e-8f ? (t - s).normalized : Vector3.forward;
            float x = (a * a - b * b + d * d) / (2f * d);
            float h = Mathf.Sqrt(Mathf.Max(0f, a * a - x * x));
            Vector3 bend = Vector3.ProjectOnPlane(pole - s, dir);
            bend = bend.sqrMagnitude > 1e-6f ? bend.normalized : Vector3.ProjectOnPlane(Vector3.down, dir).normalized;
            Vector3 elbow = s + dir * x + bend * h;

            arm.Upper.rotation = Quaternion.FromToRotation(arm.Lower.position - s, elbow - s) * arm.Upper.rotation;
            arm.Lower.rotation = Quaternion.FromToRotation(arm.Hand.position - arm.Lower.position, t - arm.Lower.position) * arm.Lower.rotation;
            arm.Hand.rotation = target.rotation;
        }
    }
}
