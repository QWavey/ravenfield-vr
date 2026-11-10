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
        internal const int HasGun = 1, LeftHand = 2, RightHand = 4, GunInLeft = 8, Magazine = 16;
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

            /// <summary>The gun model being moved, and where it sat in the soldier's hand before (given back when no pose is told).</summary>
            public Transform Model;
            public Vector3 ModelLocalPosition;
            public Quaternion ModelLocalRotation;
            public Vector3 MuzzleInModel;
            public Quaternion MuzzleRotInModel;
            public bool GunPlaced;

            /// <summary>The magazine in his free hand, as a plain dark box (the gun's own magazine model is the weapon mod's; this shows the reload's moves).</summary>
            public GameObject MagazineBox;

            /// <summary>The game's own right-hand target on this gun, or (with none) the hand bone's place on the gun taken once, before ours moved it.</summary>
            public Transform OwnRight;
            public bool GripKnown;
            public Vector3 GripInModel;
            public Quaternion GripRotInModel;

            /// <summary>The game's own left-hand target on this gun (kept while ours stands in for it).</summary>
            public Transform OwnLeft;
            public Vector3 ModelPosition;
            public Quaternion ModelRotation;

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
                GiveBack(remote.Pose);
                if (remote.Pose.MagazineBox != null)
                {
                    Object.Destroy(remote.Pose.MagazineBox);
                }
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
            int flags = (left.Tracked ? LeftHand : 0) | (right.Tracked ? RightHand : 0) | (Input.VRInput.GunHand == Input.Hand.Left ? GunInLeft : 0)
                        | (rig.Off.Role == HandRole.Magazine ? Magazine : 0);
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
                p.MuzzleNow = p.Muzzle;
                p.MuzzleRotNow = p.MuzzleRot;
            }
            p.LeftNow = Vector3.Lerp(p.LeftNow, p.Left, ease);
            p.RightNow = Vector3.Lerp(p.RightNow, p.Right, ease);
            p.MuzzleNow = Vector3.Lerp(p.MuzzleNow, p.Muzzle, ease);
            p.MuzzleRotNow = Quaternion.Slerp(p.MuzzleRotNow, p.MuzzleRot, ease);
            Animator animator = a.animator;
            Transform boneL = animator != null ? animator.GetBoneTransform(HumanBodyBones.LeftHand) : null;
            Transform boneR = animator != null ? animator.GetBoneTransform(HumanBodyBones.RightHand) : null;
            // The hand he does not hold the gun with (or, with no gun, each hand where it is): its own turn is the soldier's.
            Vector3 freeAt = origin + (p.Is(GunInLeft) ? p.RightNow : p.LeftNow);
            bool freeTold = p.Is(GunInLeft) ? p.Is(RightHand) : p.Is(LeftHand);
            p.GunPlaced = false;
            if (p.Is(HasGun) && FindGun(a, p) && boneR != null)
            {
                // Where the gun goes (its muzzle on the told one), and the soldier's gun hand on its grip exactly as his own
                // animation holds this gun: the hand bone's place and turn against the gun at rest in his hand, carried over to
                // the gun's told place. So the wrist is right without knowing the glove's axes.
                Transform parent = p.Model.parent;
                Vector3 restAt = parent != null ? parent.TransformPoint(p.ModelLocalPosition) : p.ModelLocalPosition;
                Quaternion restTurn = parent != null ? parent.rotation * p.ModelLocalRotation : p.ModelLocalRotation;
                Quaternion inRest = Quaternion.Inverse(restTurn);
                p.ModelRotation = p.MuzzleRotNow * Quaternion.Inverse(p.MuzzleRotInModel);
                p.ModelPosition = origin + p.MuzzleNow - p.ModelRotation * p.MuzzleInModel;
                p.GunPlaced = true;
                // (Where the game itself aims the gun hand on this gun, when it has such a point on the gun's model; else the hand
                // bone's own place against the gun. The bone alone fed on itself: where the arm's IK left it last frame became the
                // next target, and the hand hung 10 cm off the grip.)
                Transform ownR = __instance.handTargetR == p.TargetR ? p.OwnRight : __instance.handTargetR;
                if (ownR != null && ownR != p.TargetR && ownR.IsChildOf(p.Model))
                {
                    p.OwnRight = ownR;
                    p.TargetR.SetPositionAndRotation(p.ModelPosition + p.ModelRotation * (inRest * (ownR.position - restAt)), p.ModelRotation * (inRest * ownR.rotation));
                }
                else
                {
                    if (!p.GripKnown)
                    {
                        p.GripKnown = true;
                        p.GripInModel = inRest * (boneR.position - restAt);
                        p.GripRotInModel = inRest * boneR.rotation;
                    }
                    p.TargetR.SetPositionAndRotation(p.ModelPosition + p.ModelRotation * p.GripInModel, p.ModelRotation * p.GripRotInModel);
                }
                __instance.handTargetR = p.TargetR;
                // The other hand: on the gun as the game has it (its own left-hand target rides on the gun's model) while his
                // free hand is near the gun; off it, where his free hand is.
                Transform own = __instance.handTargetL == p.TargetL ? p.OwnLeft : __instance.handTargetL; // (ours stands in for it from the second frame on)
                bool onGun = own != null && own != p.TargetL && own.IsChildOf(p.Model);
                Vector3 gunHold = onGun ? p.ModelPosition + p.ModelRotation * (inRest * (own.position - restAt)) : Vector3.zero;
                if (onGun && (!freeTold || Vector3.Distance(gunHold, freeAt) < 0.22f))
                {
                    p.TargetL.SetPositionAndRotation(gunHold, p.ModelRotation * (inRest * own.rotation));
                    p.OwnLeft = own;
                    __instance.handTargetL = p.TargetL;
                    __instance.SetHandIkEnabled(true, true);
                }
                else
                {
                    if (onGun)
                    {
                        p.OwnLeft = own;
                    }
                    p.TargetL.SetPositionAndRotation(freeAt, boneL != null ? boneL.rotation : Quaternion.identity);
                    __instance.handTargetL = p.TargetL;
                    __instance.SetHandIkEnabled(freeTold, true);
                }
            }
            else
            {
                p.TargetL.SetPositionAndRotation(origin + p.LeftNow, boneL != null ? boneL.rotation : Quaternion.identity);
                p.TargetR.SetPositionAndRotation(origin + p.RightNow, boneR != null ? boneR.rotation : Quaternion.identity);
                __instance.handTargetL = p.TargetL;
                __instance.handTargetR = p.TargetR;
                __instance.SetHandIkEnabled(p.Is(LeftHand), p.Is(RightHand));
            }
            __instance.aimPoint = a.CenterPosition() + Vector3.up * 0.6f + p.HeadForward.normalized * 100f;
            __instance.turnBody = false; // (the head looks there; the body stays as the walk has it)
            __instance.weight = 1f;
            ArmsSet++;
        }

        /// <summary>
        /// The gun model in the stand-in's hands taken note of (where it rests in the hand, where its muzzle is in it); a model
        /// held before is given back to its hand first. False with no gun, no model or no muzzle on it.
        /// </summary>
        private static bool FindGun(Actor a, Pose p)
        {
            Weapon gun = a.activeWeapon;
            Transform model = gun != null ? gun.thirdPersonTransform : null;
            if (model != p.Model)
            {
                GiveBack(p);
                if (model == null)
                {
                    return false;
                }
                Transform muzzle;
                try
                {
                    muzzle = gun.CurrentMuzzle();
                }
                catch (System.Exception)
                {
                    return false;
                }
                if (muzzle == null || !muzzle.IsChildOf(model))
                {
                    return false;
                }
                p.Model = model;
                p.ModelLocalPosition = model.localPosition;
                p.ModelLocalRotation = model.localRotation;
                Quaternion inModel = Quaternion.Inverse(model.rotation);
                p.MuzzleInModel = inModel * (muzzle.position - model.position);
                p.MuzzleRotInModel = inModel * muzzle.rotation;
                p.OwnLeft = null;
                p.OwnRight = null;
                p.GripKnown = false;
            }
            return p.Model != null;
        }

        /// <summary>The gun back where the soldier's hand has it, and the game's own left-hand target back (no pose told any more).</summary>
        private static void GiveBack(Pose p)
        {
            if (p.Model != null)
            {
                p.Model.localPosition = p.ModelLocalPosition;
                p.Model.localRotation = p.ModelLocalRotation;
            }
            p.Model = null;
            p.GunPlaced = false;
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
                Pose p = kv.Value.Pose;
                if (p == null)
                {
                    continue;
                }
                // The magazine in his free hand while he reloads by hand.
                bool magazine = p.Is(Magazine) && Usable(kv.Value, out Actor holder, out Pose _);
                if (magazine && p.MagazineBox == null)
                {
                    p.MagazineBox = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    Object.Destroy(p.MagazineBox.GetComponent<Collider>());
                    p.MagazineBox.name = "RavenfieldVR Net Magazine";
                    p.MagazineBox.transform.localScale = new Vector3(0.026f, 0.12f, 0.062f);
                    Shader lit = Shader.Find("Standard");
                    var renderer = p.MagazineBox.GetComponent<Renderer>();
                    renderer.material = new Material(lit != null ? lit : Shader.Find("Sprites/Default")) { color = new Color(0.13f, 0.13f, 0.14f) };
                    renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                }
                if (p.MagazineBox != null)
                {
                    if (p.MagazineBox.activeSelf != magazine)
                    {
                        p.MagazineBox.SetActive(magazine);
                    }
                    if (magazine)
                    {
                        bool left = !p.Is(GunInLeft);
                        Quaternion turn = left ? p.LeftRot : p.RightRot;
                        Vector3 at = kv.Value.Actor.Position() + (left ? p.LeftNow : p.RightNow);
                        p.MagazineBox.transform.SetPositionAndRotation(at, turn);
                    }
                }
                if (!Usable(kv.Value, out Actor _, out Pose _) || !p.Is(HasGun))
                {
                    if (p.Model != null)
                    {
                        // (No pose told any more, or he is down: the gun is the soldier's own again, and his left hand's hold.)
                        GiveBack(p);
                        if (kv.Key != null && p.OwnLeft != null && kv.Key.handTargetL == p.TargetL)
                        {
                            kv.Key.handTargetL = p.OwnLeft;
                        }
                        if (kv.Key != null && p.OwnRight != null && kv.Key.handTargetR == p.TargetR)
                        {
                            kv.Key.handTargetR = p.OwnRight;
                        }
                        p.Eased = false;
                    }
                    continue;
                }
                if (p.GunPlaced && p.Model != null)
                {
                    p.Model.SetPositionAndRotation(p.ModelPosition, p.ModelRotation);
                }
            }
        }
    }
}
