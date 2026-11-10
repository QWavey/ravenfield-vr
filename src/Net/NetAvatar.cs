using System.Collections.Generic;
using RavenfieldVR.Core;
using RavenfieldVR.Rig;
using UnityEngine;

namespace RavenfieldVR.Net
{
    /// <summary>
    /// The other player as a VR player (the user, 2026-11-10: "make sure i can see the arms of other players and hown they use the
    /// guns with physics and other stuff so its actually like a real multiplayer vr game"): thirty times a second each VR player
    /// tells where his head looks, where his two hands are and where his gun really is (its muzzle, as his hands hold it). On the
    /// other PCs his soldier's arms reach for those hands (the game's own arm IK, <c>ActorIk</c>: its hand targets and look-at
    /// point are the told ones), and the gun in the soldier's hands is put where his gun is, so a reload, a gun held sideways, one
    /// lifted over cover or dropped to the hip is seen as he does it.
    ///
    /// Told relative to his own soldier's place, and applied relative to the stand-in's: the stand-in walks a little behind what
    /// was told, and hands told in the world would trail off its body.
    /// </summary>
    internal static class NetAvatar
    {
        internal const int HasGun = 1, LeftHand = 2, RightHand = 4;
        private const float SendEvery = 1f / 30f, FreshFor = 0.6f;

        /// <summary>What a VR player's game told last, and where the stand-in's hands and gun are being eased to.</summary>
        internal sealed class Pose
        {
            public int Flags;
            public Vector3 HeadForward = Vector3.forward;
            public Vector3 Left, Right, Muzzle;
            public Quaternion LeftRot = Quaternion.identity, RightRot = Quaternion.identity, MuzzleRot = Quaternion.identity;
            public float Heard = -10f;

            // Eased (the told ones come 30 times a second, the eyes are drawn 90 times).
            public Vector3 LeftNow, RightNow, MuzzleNow;
            public Quaternion MuzzleRotNow = Quaternion.identity;
            public bool Eased;
            public Transform TargetL, TargetR;

            public bool Fresh => Time.unscaledTime - Heard < FreshFor;
            public bool Is(int flag) => (Flags & flag) != 0;
        }

        private static readonly Dictionary<ActorIk, NetGame.Remote> ByIk = new Dictionary<ActorIk, NetGame.Remote>();
        private static readonly NetWriter Out = new NetWriter(128);
        private static float _nextSend;

        /// <summary>The check's: how often a stand-in's arms were set from a told pose.</summary>
        internal static int ArmsSet;

        internal static void Register(Actor standIn, NetGame.Remote remote)
        {
            if (standIn != null && standIn.ik != null)
            {
                ByIk[standIn.ik] = remote;
            }
        }

        internal static void Forget(NetGame.Remote remote)
        {
            if (remote.Pose != null)
            {
                if (remote.Pose.TargetL != null)
                {
                    Object.Destroy(remote.Pose.TargetL.gameObject);
                }
                if (remote.Pose.TargetR != null)
                {
                    Object.Destroy(remote.Pose.TargetR.gameObject);
                }
                remote.Pose = null;
            }
            ActorIk gone = null;
            foreach (KeyValuePair<ActorIk, NetGame.Remote> kv in ByIk)
            {
                if (ReferenceEquals(kv.Value, remote))
                {
                    gone = kv.Key;
                    break;
                }
            }
            if (!ReferenceEquals(gone, null))
            {
                ByIk.Remove(gone);
            }
        }

        internal static void Clear() => ByIk.Clear();

        /// <summary>One pose as it goes over the line (the check's second player writes the same).</summary>
        internal static void Put(NetWriter w, int flags, Vector3 headForward, Vector3 left, Quaternion leftRot, Vector3 right, Quaternion rightRot,
                                 Vector3 muzzle, Quaternion muzzleRot)
        {
            w.Byte((byte)flags);
            w.SmallVector(headForward);
            w.SmallVector(left);
            w.Rotation(leftRot);
            w.SmallVector(right);
            w.Rotation(rightRot);
            w.SmallVector(muzzle);
            w.Rotation(muzzleRot);
        }

        /// <summary>My own pose told (a VR player on his feet), thirty times a second.</summary>
        internal static void Tell(NetSession session)
        {
            if (Time.unscaledTime < _nextSend || session.PlayerCount < 2 || !VRManager.Active)
            {
                return;
            }
            _nextSend = Time.unscaledTime + SendEvery;
            Actor player = LocalPlayer.actor;
            VRRig rig = VRRig.Instance;
            if (player == null || player.dead || rig == null || player.fallenOver || player.IsSeated())
            {
                return;
            }
            Vector3 origin = player.Position();
            VRHand left = rig.Hand(Input.Hand.Left), right = rig.Hand(Input.Hand.Right);
            int flags = (left.Tracked ? LeftHand : 0) | (right.Tracked ? RightHand : 0);
            Vector3 muzzle = Vector3.zero;
            Quaternion muzzleRot = Quaternion.identity;
            Weapon gun = player.activeWeapon;
            if (gun != null)
            {
                Transform m = null;
                try
                {
                    m = gun.CurrentMuzzle();
                }
                catch (System.Exception)
                {
                    // (a weapon with no muzzle: told without its gun's place)
                }
                if (m != null)
                {
                    flags |= HasGun;
                    muzzle = m.position - origin;
                    muzzleRot = m.rotation;
                }
            }
            Out.Reset();
            Put(Out, flags, rig.Head.forward, left.Palm.position - origin, left.Palm.rotation, right.Palm.position - origin, right.Palm.rotation, muzzle, muzzleRot);
            session.Send(NetGame.VrPose, Out, false);
        }

        internal static void Read(NetGame.Remote remote, ref NetReader r)
        {
            Pose p = remote.Pose ?? (remote.Pose = new Pose());
            p.Flags = r.Byte();
            p.HeadForward = r.SmallVector();
            p.Left = r.SmallVector();
            p.LeftRot = r.Rotation();
            p.Right = r.SmallVector();
            p.RightRot = r.Rotation();
            p.Muzzle = r.SmallVector();
            p.MuzzleRot = r.Rotation();
            // (Held to what an arm can do: a broken or made-up message does not throw a hand a kilometre off.)
            p.Left = Vector3.ClampMagnitude(p.Left, 3f);
            p.Right = Vector3.ClampMagnitude(p.Right, 3f);
            p.Muzzle = Vector3.ClampMagnitude(p.Muzzle, 4f);
            p.Heard = Time.unscaledTime;
        }

        private static bool Usable(NetGame.Remote r, out Actor a, out Pose p)
        {
            a = r.Actor;
            p = r.Pose;
            return p != null && p.Fresh && a != null && !a.dead && !a.fallenOver && !a.IsSeated();
        }

        /// <summary>
        /// Before the game's own arm IK runs for a soldier (Harmony prefix on ActorIk.OnAnimatorIK): a VR player's stand-in gets
        /// his hands as targets and his head's direction as the point looked at.
        /// </summary>
        internal static void BeforeIk(ActorIk __instance)
        {
            if (ByIk.Count == 0 || !ByIk.TryGetValue(__instance, out NetGame.Remote r) || !Usable(r, out Actor a, out Pose p))
            {
                return;
            }
            Vector3 origin = a.Position();
            if (p.TargetL == null)
            {
                p.TargetL = new GameObject("RavenfieldVR Net Hand L").transform;
                p.TargetR = new GameObject("RavenfieldVR Net Hand R").transform;
            }
            float ease = 1f - Mathf.Exp(-25f * Time.deltaTime);
            if (!p.Eased)
            {
                p.Eased = true;
                ease = 1f;
            }
            p.LeftNow = Vector3.Lerp(p.LeftNow, p.Left, ease);
            p.RightNow = Vector3.Lerp(p.RightNow, p.Right, ease);
            // The hand bones' own turn is kept (the told palm's axes are the glove's, not the soldier's hand bone's): the arms
            // reach, the wrists stay as the soldier's animation has them.
            Animator animator = a.animator;
            Transform boneL = animator != null ? animator.GetBoneTransform(HumanBodyBones.LeftHand) : null;
            Transform boneR = animator != null ? animator.GetBoneTransform(HumanBodyBones.RightHand) : null;
            p.TargetL.SetPositionAndRotation(origin + p.LeftNow, boneL != null ? boneL.rotation : Quaternion.identity);
            p.TargetR.SetPositionAndRotation(origin + p.RightNow, boneR != null ? boneR.rotation : Quaternion.identity);
            __instance.handTargetL = p.TargetL;
            __instance.handTargetR = p.TargetR;
            __instance.SetHandIkEnabled(p.Is(LeftHand), p.Is(RightHand));
            __instance.aimPoint = a.CenterPosition() + Vector3.up * 0.6f + p.HeadForward.normalized * 100f;
            __instance.turnBody = false; // (the head looks there; the body stays as the walk has it)
            __instance.weight = 1f;
            ArmsSet++;
        }

        /// <summary>Before the eyes are drawn: each VR player's gun put where he holds it (its muzzle on the told one).</summary>
        internal static void BeforeRender()
        {
            if (ByIk.Count == 0)
            {
                return;
            }
            foreach (KeyValuePair<ActorIk, NetGame.Remote> kv in ByIk)
            {
                if (!Usable(kv.Value, out Actor a, out Pose p) || !p.Is(HasGun))
                {
                    continue;
                }
                Weapon gun = a.activeWeapon;
                Transform model = gun != null ? gun.thirdPersonTransform : null;
                if (model == null)
                {
                    continue;
                }
                Transform muzzle = null;
                try
                {
                    muzzle = gun.CurrentMuzzle();
                }
                catch (System.Exception)
                {
                    continue;
                }
                if (muzzle == null || !muzzle.IsChildOf(model))
                {
                    continue;
                }
                float ease = 1f - Mathf.Exp(-25f * Time.deltaTime);
                p.MuzzleNow = Vector3.Lerp(p.MuzzleNow, p.Muzzle, p.MuzzleNow == Vector3.zero ? 1f : ease);
                p.MuzzleRotNow = Quaternion.Slerp(p.MuzzleRotNow, p.MuzzleRot, p.MuzzleRotNow == Quaternion.identity ? 1f : ease);
                // The model turned so its muzzle looks as told, then moved so the muzzle is where told.
                Quaternion turn = p.MuzzleRotNow * Quaternion.Inverse(muzzle.rotation);
                model.rotation = turn * model.rotation;
                model.position += a.Position() + p.MuzzleNow - muzzle.position;
            }
        }
    }
}
