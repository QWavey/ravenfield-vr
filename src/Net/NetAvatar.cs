using System.Collections.Generic;
using System.Linq;
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
            /// <summary>How far each hand's grip is squeezed (0 open, 1 a fist), eased here (see Fingers).</summary>
            public float GripL, GripR, GripNowL, GripNowR;
            /// <summary>The soldier's left hand is on the gun (the game's own hold: its fingers are the game's grip).</summary>
            public bool LeftOnGun;
            public float Heard = -10f;

            // Eased (the told ones come 30 times a second, the eyes are drawn 90 times).
            public Vector3 LeftNow, RightNow, MuzzleNow;
            public Quaternion MuzzleRotNow = Quaternion.identity;
            /// <summary>The wrists' turns as shown, eased like the palms (the told ones stepped 30 times a second while the palms glided).</summary>
            public Quaternion LeftRotNow = Quaternion.identity, RightRotNow = Quaternion.identity;
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
            /// <summary>
            /// The gun in his free hand (his second weapon): its slot (-1: none), where it is and how it is turned (its weapon's own
            /// root, as his PC holds it), and his shots with it counted (a new count is a shot here: its sound at his hand).
            /// </summary>
            public int SecondSlot = -1;
            public Vector3 SecondAt;
            public Quaternion SecondRot = Quaternion.identity;
            public byte SecondShots;
            public bool SecondShotsKnown;
            public GameObject SecondCopy;
            public Weapon SecondCopyOf;
            public Vector3 SecondModelPos;
            public AudioSource SecondSound;
            public Quaternion SecondModelRot = Quaternion.identity;
            /// <summary>The weapon whose magazine the copy in his hand is (made again when he holds another), and its middle in the copy.</summary>
            public Weapon MagazineOf;
            public Vector3 MagazineMiddle;
            /// <summary>The gun's own magazine part, not drawn while his hand holds one (two magazines showed: one seated, one in his hand).</summary>
            public Transform MagazinePart;
            public bool MagazinePartHidden;

            /// <summary>The game's own right-hand target on this gun, or (with none) the hand bone's place on the gun taken once, before ours moved it.</summary>
            public Transform OwnRight;
            public bool GripKnown;
            /// <summary>The frame the arms were let go to take the grip off the animation (see BeforeIk).</summary>
            public int IkOffFrame = -10;
            public Vector3 GripInModel;

            /// <summary>
            /// His palm's turn against the soldier's hand bone, each hand (the controller's axes are not the glove's): learnt while the
            /// hand is on the gun, where the game's own hold gives the bone's turn; with it, a hand off the gun turns with his wrist.
            /// </summary>
            public Quaternion PalmToBoneL = Quaternion.identity, PalmToBoneR = Quaternion.identity;
            public bool PalmKnownL, PalmKnownR;
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
                // (One key per remote: a re-made stand-in left its old key, and Fingers curled his hands once per key; scout.)
                foreach (ActorIk old in ByIk.Where(kv => ReferenceEquals(kv.Value, remote) && kv.Key != standIn.ik).Select(kv => kv.Key).ToList())
                {
                    ByIk.Remove(old);
                }
                ByIk[standIn.ik] = remote;
            }
        }

        internal static void Forget(NetGame.Remote remote)
        {
            ForgetCarried(remote);
            Heads.Remove(remote);
            if (remote.Pose != null)
            {
                ShowMagazinePart(remote.Pose);
                GiveBack(remote.Pose);
                if (remote.Pose.MagazineBox != null)
                {
                    Object.Destroy(remote.Pose.MagazineBox);
                }
                if (remote.Pose.SecondCopy != null)
                {
                    Object.Destroy(remote.Pose.SecondCopy);
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
            // (Every key of his: each new stand-in of his added one, and only the first was taken away.)
            foreach (ActorIk gone in ByIk.Where(kv => ReferenceEquals(kv.Value, remote)).Select(kv => kv.Key).ToList())
            {
                ByIk.Remove(gone);
                if (gone != null)
                {
                    // (Our targets are destroyed above: the soldier's IK does not keep them.)
                    gone.handTargetL = null;
                    gone.handTargetR = null;
                    gone.SetHandIkEnabled(false, false);
                }
            }
        }

        internal static void Clear()
        {
            ByIk.Clear();
            Heads.Clear(); // (keyed by remote, each holding a bone of the old scene; scout)
            Inventories.Clear(); // (the copies went with the scene, or with their soldiers)
        }

        /// <summary>
        /// The muzzle a gun's place is told by and put by: its first, always. The one it fires from next changes with every shot
        /// of a gun with two barrels, and the gun on the other PCs jumped by the barrels' spacing at each shot.
        /// </summary>
        internal static Transform FirstMuzzle(Weapon gun)
        {
            Transform[] all = gun.configuration != null ? gun.configuration.muzzles : null;
            return all != null && all.Length > 0 && all[0] != null ? all[0] : gun.CurrentMuzzle();
        }

        /// <summary>One pose as it goes over the line (the check's second player writes the same).</summary>
        internal static void Put(NetWriter w, int flags, Vector3 headForward, Vector3 left, Quaternion leftRot, Vector3 right, Quaternion rightRot,
                                 Vector3 muzzle, Quaternion muzzleRot, float gripL = 0f, float gripR = 0f)
        {
            w.Byte((byte)flags);
            w.SmallVector(headForward);
            w.SmallVector(left);
            w.Rotation(leftRot);
            w.SmallVector(right);
            w.Rotation(rightRot);
            w.SmallVector(muzzle);
            w.Rotation(muzzleRot);
            w.Byte((byte)Mathf.RoundToInt(Mathf.Clamp01(gripL) * 255f));
            w.Byte((byte)Mathf.RoundToInt(Mathf.Clamp01(gripR) * 255f));
        }

        private static byte _secondShots;

        /// <summary>This player fired the gun in his free hand: counted, so the others hear it at his hand.</summary>
        internal static void OnSecondShot() => _secondShots++;

        /// <summary>His shot with the gun in his free hand: that gun's own report (the game's sound of it) at his hand.</summary>
        private static void SecondShot(NetGame.Remote remote, Pose p)
        {
            Actor a = remote.Actor;
            Weapon w = a != null && a.weapons != null && p.SecondSlot < a.weapons.Length ? a.weapons[p.SecondSlot] : null;
            AudioSource own = w != null ? w.audio : null;
            if (own == null || p.SecondCopy == null)
            {
                return;
            }
            // The game's own fire clips of that gun (one picked as it picks them), else its source's clip, through a source on the
            // copy in his hand set as the gun's own (its mixer group, so the game's volume applies; its distances).
            AudioClip clip = w.fireClips != null && w.fireClips.Length > 0 ? w.fireClips[Random.Range(0, w.fireClips.Length)] : own.clip;
            if (clip == null)
            {
                return;
            }
            if (p.SecondSound == null)
            {
                p.SecondSound = p.SecondCopy.AddComponent<AudioSource>();
                p.SecondSound.playOnAwake = false;
                p.SecondSound.outputAudioMixerGroup = own.outputAudioMixerGroup;
                p.SecondSound.spatialBlend = 1f;
                p.SecondSound.rolloffMode = own.rolloffMode;
                p.SecondSound.minDistance = own.minDistance;
                p.SecondSound.maxDistance = own.maxDistance;
                p.SecondSound.dopplerLevel = 0f;
            }
            p.SecondSound.PlayOneShot(clip, Mathf.Clamp01(own.volume > 0f ? own.volume : 1f));
        }

        /// <summary>
        /// The copy of his second weapon in his free hand, each frame before the eyes are drawn: its third-person model at the place
        /// his PC holds the weapon (the model's own offset under its weapon kept); gone when he has none in that hand.
        /// </summary>
        private static void PlaceSecond(NetGame.Remote remote, Pose p)
        {
            Actor a = remote.Actor;
            bool show = p.SecondSlot >= 0 && Usable(remote, out _, out _) && a.weapons != null && p.SecondSlot < a.weapons.Length;
            Weapon w = show ? a.weapons[p.SecondSlot] : null;
            if (p.SecondCopy != null && (!show || !ReferenceEquals(p.SecondCopyOf, w)))
            {
                Object.Destroy(p.SecondCopy);
                p.SecondCopy = null;
                p.SecondCopyOf = null;
                p.SecondSound = null;
            }
            if (w == null || w.thirdPersonTransform == null)
            {
                if (p.SecondCopy != null)
                {
                    Object.Destroy(p.SecondCopy); // (his weapon gone from under it: not left hanging in the air)
                    p.SecondCopy = null;
                    p.SecondCopyOf = null;
                }
                return;
            }
            if (p.SecondCopy == null)
            {
                p.SecondCopy = Object.Instantiate(w.thirdPersonTransform.gameObject);
                p.SecondCopy.name = "RavenfieldVR Net Second Weapon";
                p.SecondCopy.SetActive(true);
                foreach (MonoBehaviour script in p.SecondCopy.GetComponentsInChildren<MonoBehaviour>(true))
                {
                    Object.Destroy(script);
                }
                foreach (Collider collider in p.SecondCopy.GetComponentsInChildren<Collider>(true))
                {
                    Object.Destroy(collider);
                }
                foreach (ParticleSystem particles in p.SecondCopy.GetComponentsInChildren<ParticleSystem>(true))
                {
                    particles.gameObject.SetActive(false);
                }
                p.SecondCopy.transform.SetParent(null, false);
                p.SecondCopy.transform.localScale = w.thirdPersonTransform.lossyScale;
                p.SecondCopyOf = w;
                // (Where the model sits under its weapon's root: placed on the told root, the model is where his gun is.)
                p.SecondModelPos = w.transform.InverseTransformPoint(w.thirdPersonTransform.position);
                p.SecondModelRot = Quaternion.Inverse(w.transform.rotation) * w.thirdPersonTransform.rotation;
            }
            Vector3 root = a.Position() + p.SecondAt;
            p.SecondCopy.transform.SetPositionAndRotation(root + p.SecondRot * Vector3.Scale(p.SecondModelPos, w.transform.lossyScale), p.SecondRot * p.SecondModelRot);
        }

        /// <summary>My own pose told (a VR player on his feet), thirty times a second with another player near.</summary>
        internal static void Tell(NetSession session)
        {
            if (Time.unscaledTime < _nextSend || session.PlayerCount < 2 || !VRManager.Active)
            {
                return;
            }
            _nextSend = Time.unscaledTime + SendEvery;
            Actor player = LocalPlayer.actor;
            VRRig rig = VRRig.Instance;
            if (player == null || player.dead || rig == null || player.fallenOver)
            {
                return;
            }
            Vector3 origin = player.Position();
            // Less often with nobody near to see the hands: every pose goes through the host to every other player, so its cost
            // grows with the square of the players (16 VR players at 30 a second: about 250 kB/s through the host; scout,
            // 2026-11-11). Past 40 m from the nearest other player 15 a second, past 120 m 5 (a pose is fresh for 0.6 s, and the
            // stand-in's hands are eased between two).
            float nearest = NetGame.NearestOtherPlayer(origin);
            if (nearest > 40f)
            {
                _nextSend = Time.unscaledTime + (nearest > 120f ? 0.2f : 1f / 15f);
            }
            VRHand left = rig.Hand(Input.Hand.Left), right = rig.Hand(Input.Hand.Right);
            // In a seat whose hands are the game's (on a wheel, a turret's grips): the head only, his hands told as none (it was not sent
            // at all, and his head stayed still while he drove and looked about; joined-player scout, 2026-11-14).
            bool handsOwn = HandsFree(player);
            int flags = handsOwn ? (left.Tracked ? LeftHand : 0) | (right.Tracked ? RightHand : 0) | (Input.VRInput.GunHand == Input.Hand.Left ? GunInLeft : 0)
                        | (rig.Off.Role == HandRole.Magazine ? Magazine : 0) : 0;
            Vector3 muzzle = Vector3.zero;
            Quaternion muzzleRot = Quaternion.identity;
            Weapon gun = handsOwn ? player.activeWeapon : null;
            if (gun != null)
            {
                Transform m = null;
                try
                {
                    m = FirstMuzzle(gun);
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
            Put(Out, flags, rig.Head.forward, left.Palm.position - origin, left.Palm.rotation, right.Palm.position - origin, right.Palm.rotation, muzzle, muzzleRot,
                left.Grip, right.Grip);
            // The gun in the free hand, if any (the others saw it on his hip and never firing; 2026-11-14): its slot, its shots, and
            // where its weapon is.
            Weapons.SecondWeapon second = Weapons.WeaponSystem.Instance != null ? Weapons.WeaponSystem.Instance.Second : null;
            int secondSlot = second != null && second.Weapon != null && player.weapons != null ? System.Array.IndexOf(player.weapons, second.Weapon) : -1;
            Out.Byte((byte)(secondSlot >= 0 && secondSlot < 255 ? secondSlot : 255));
            Out.Byte(_secondShots);
            if (secondSlot >= 0)
            {
                Out.SmallVector(Vector3.ClampMagnitude(second.Weapon.transform.position - origin, 4f));
                Out.Rotation(second.Weapon.transform.rotation);
            }
            session.Send(NetGame.VrPose, Out, false);
        }

        internal static void Read(NetGame.Remote remote, ref NetReader r)
        {
            Pose p = remote.Pose ?? (remote.Pose = new Pose());
            p.Flags = r.Byte();
            p.HeadForward = r.SmallVector();
            if (p.HeadForward.sqrMagnitude < 1e-6f)
            {
                p.HeadForward = Vector3.forward; // (a zero told: his aim point and the head's turn want a direction; scout)
            }
            p.Left = r.SmallVector();
            p.LeftRot = r.Rotation();
            p.Right = r.SmallVector();
            p.RightRot = r.Rotation();
            p.Muzzle = r.SmallVector();
            p.MuzzleRot = r.Rotation();
            if (r.Remaining >= 2)
            {
                p.GripL = r.Byte() / 255f;
                p.GripR = r.Byte() / 255f;
            }
            p.SecondSlot = -1;
            if (r.Remaining >= 2)
            {
                int slot = r.Byte();
                byte shots = r.Byte();
                if (slot != 255 && r.Remaining >= 7)
                {
                    p.SecondSlot = slot;
                    p.SecondAt = Vector3.ClampMagnitude(r.SmallVector(), 4f);
                    p.SecondRot = r.Rotation();
                }
                int delta = (byte)(shots - p.SecondShots);
                if (p.SecondShotsKnown && p.SecondSlot >= 0 && delta >= 1 && delta <= 127)
                {
                    // (Only a count ahead of the last: a late packet's older count played a shot again.)
                    for (int i = 0; i < Mathf.Min(delta, 3); i++)
                    {
                        SecondShot(remote, p);
                    }
                }
                if (!p.SecondShotsKnown || delta >= 1 && delta <= 127)
                {
                    p.SecondShots = shots;
                }
                p.SecondShotsKnown = true;
            }
            // (Held to what an arm can do: a broken or made-up message does not throw a hand a kilometre off.)
            p.Left = Vector3.ClampMagnitude(p.Left, 3f);
            p.Right = Vector3.ClampMagnitude(p.Right, 3f);
            p.Muzzle = Vector3.ClampMagnitude(p.Muzzle, 4f);
            p.Heard = Time.unscaledTime;
        }

        private static Material _magazineMaterial;

        private static readonly float[] MostCurl = { 75f, 85f, 60f }; // (one array, not one a call: a hand's curl per frame per remote)

        private static bool Usable(NetGame.Remote r, out Actor a, out Pose p)
        {
            a = r.Actor;
            p = r.Pose;
            return p != null && p.Fresh && a != null && !a.dead && !a.fallenOver && HandsFree(a);
        }

        /// <summary>
        /// On his feet, or seated where he uses his own gun (a passenger's or gunner's seat with no wheel or stick of its own): his VR
        /// hands and gun are shown. Left out in every seat, a player in a vehicle sat with the game's still arms and no gun in them (the
        /// user, 2026-11-11: "make sure that when players are sitting etc I can still see their arms with guns"). A seat whose
        /// controls hold the hands (a driver's wheel, a turret's handles) keeps the game's own.
        /// </summary>
        internal static bool HandsFree(Actor a)
        {
            Seat seat = a != null ? a.seat : null;
            if (seat == null)
            {
                return true;
            }
            try
            {
                return seat.CanUsePersonalWeapons() && !seat.HasHandIkTargets();
            }
            catch (System.Exception)
            {
                return false;
            }
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
                p.LeftRotNow = p.LeftRot;
                p.RightRotNow = p.RightRot;
            }
            p.LeftNow = Vector3.Lerp(p.LeftNow, p.Left, ease);
            // (OnAnimatorIK may run more than once a frame on a soldier with several IK layers: the frame let go stays let go.)
            if (p.IkOffFrame == Time.frameCount)
            {
                __instance.SetHandIkEnabled(false, false);
                return;
            }
            p.RightNow = Vector3.Lerp(p.RightNow, p.Right, ease);
            p.MuzzleNow = Vector3.Lerp(p.MuzzleNow, p.Muzzle, ease);
            p.MuzzleRotNow = Quaternion.Slerp(p.MuzzleRotNow, p.MuzzleRot, ease);
            p.LeftRotNow = Quaternion.Slerp(p.LeftRotNow, p.LeftRot, ease);
            p.RightRotNow = Quaternion.Slerp(p.RightRotNow, p.RightRot, ease);
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
                        // Measured from the soldier's own animation only: the hand bone holds where last frame's IK put it, and after a
                        // switch of guns (no gun for a moment: the arms on his free hands) that was his VR hand, not this gun's grip. The
                        // grip was taken there and the arm hung off the gun for as long as he held it (the user's friend, 2026-11-11: "when
                        // switched guns my arm bugged out"). The arms are let go for one frame, and the grip taken on the next.
                        if (p.IkOffFrame != Time.frameCount - 1)
                        {
                            if (p.IkOffFrame != Time.frameCount)
                            {
                                p.IkOffFrame = Time.frameCount;
                            }
                            __instance.SetHandIkEnabled(false, false);
                            return;
                        }
                        p.GripKnown = true;
                        p.GripInModel = inRest * (boneR.position - restAt);
                        p.GripRotInModel = inRest * boneR.rotation;
                    }
                    p.TargetR.SetPositionAndRotation(p.ModelPosition + p.ModelRotation * p.GripInModel, p.ModelRotation * p.GripRotInModel);
                }
                __instance.handTargetR = p.TargetR;
                if (p.Is(RightHand) && !p.Is(GunInLeft))
                {
                    Learn(ref p.PalmToBoneR, ref p.PalmKnownR, p.RightRot, p.TargetR.rotation);
                }
                // The other hand: on the gun as the game has it (its own left-hand target rides on the gun's model) while his
                // free hand is near the gun; off it, where his free hand is.
                Transform own = __instance.handTargetL == p.TargetL ? p.OwnLeft : __instance.handTargetL; // (ours stands in for it from the second frame on)
                bool onGun = own != null && own != p.TargetL && own.IsChildOf(p.Model);
                Vector3 gunHold = onGun ? p.ModelPosition + p.ModelRotation * (inRest * (own.position - restAt)) : Vector3.zero;
                if (onGun && (!freeTold || Vector3.Distance(gunHold, freeAt) < 0.22f))
                {
                    Quaternion hold = p.ModelRotation * (inRest * own.rotation);
                    if (freeTold && !p.Is(GunInLeft) && Vector3.Distance(gunHold, freeAt) < 0.12f)
                    {
                        Learn(ref p.PalmToBoneL, ref p.PalmKnownL, p.LeftRot, hold);
                    }
                    p.TargetL.SetPositionAndRotation(gunHold, hold);
                    p.LeftOnGun = true;
                    p.OwnLeft = own;
                    __instance.handTargetL = p.TargetL;
                    __instance.SetHandIkEnabled(true, true);
                }
                else
                {
                    p.LeftOnGun = false;
                    if (onGun)
                    {
                        p.OwnLeft = own;
                    }
                    // (His wrist's turn once learnt on the gun; a left-handed player's free hand is his right, the soldier's left: not
                    // the same hand's axes, so the soldier's own turn stays.)
                    p.TargetL.SetPositionAndRotation(freeAt, p.PalmKnownL && !p.Is(GunInLeft) ? p.LeftRotNow * p.PalmToBoneL : boneL != null ? boneL.rotation : Quaternion.identity);
                    __instance.handTargetL = p.TargetL;
                    __instance.SetHandIkEnabled(freeTold, true);
                }
            }
            else
            {
                // (No gun: each hand where his is, turned with his wrist where that was learnt on a gun.)
                p.TargetL.SetPositionAndRotation(origin + p.LeftNow, p.PalmKnownL ? p.LeftRotNow * p.PalmToBoneL : boneL != null ? boneL.rotation : Quaternion.identity);
                p.TargetR.SetPositionAndRotation(origin + p.RightNow, p.PalmKnownR ? p.RightRotNow * p.PalmToBoneR : boneR != null ? boneR.rotation : Quaternion.identity);
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
                    muzzle = FirstMuzzle(gun);
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

        /// <summary>
        /// The palm's turn against the hand bone, from one moment the hand is on the gun (<paramref name="bone"/>: the bone's turn there):
        /// taken at once the first time, then eased toward each new reading (a hand never sits on a gun exactly as the game's hold).
        /// </summary>
        private static void Learn(ref Quaternion palmToBone, ref bool known, Quaternion palm, Quaternion bone)
        {
            Quaternion now = Quaternion.Inverse(palm) * bone;
            if (float.IsNaN(now.x) || float.IsNaN(now.w))
            {
                return;
            }
            palmToBone = known ? Quaternion.Slerp(palmToBone, now, 0.05f) : now;
            known = true;
        }

        // ---- his fingers ---------------------------------------------------------------------------------------------------

        private static readonly HumanBodyBones[][] FingerBones =
        {
            new[] { HumanBodyBones.LeftIndexProximal, HumanBodyBones.LeftIndexIntermediate, HumanBodyBones.LeftIndexDistal },
            new[] { HumanBodyBones.LeftMiddleProximal, HumanBodyBones.LeftMiddleIntermediate, HumanBodyBones.LeftMiddleDistal },
            new[] { HumanBodyBones.LeftRingProximal, HumanBodyBones.LeftRingIntermediate, HumanBodyBones.LeftRingDistal },
            new[] { HumanBodyBones.LeftLittleProximal, HumanBodyBones.LeftLittleIntermediate, HumanBodyBones.LeftLittleDistal },
            new[] { HumanBodyBones.RightIndexProximal, HumanBodyBones.RightIndexIntermediate, HumanBodyBones.RightIndexDistal },
            new[] { HumanBodyBones.RightMiddleProximal, HumanBodyBones.RightMiddleIntermediate, HumanBodyBones.RightMiddleDistal },
            new[] { HumanBodyBones.RightRingProximal, HumanBodyBones.RightRingIntermediate, HumanBodyBones.RightRingDistal },
            new[] { HumanBodyBones.RightLittleProximal, HumanBodyBones.RightLittleIntermediate, HumanBodyBones.RightLittleDistal },
        };

        /// <summary>The check's: fingers curled in the last frame.</summary>
        internal static int FingersCurled;

        /// <summary>
        /// A VR player's free hand closes on the other PCs as he squeezes its grip, and opens as he lets go (the open list of VR
        /// presence: "the fingers"). After the animation and the arms' IK: each finger's joints are bent toward the palm by up to 75, 85
        /// and 60 degrees, about the axis across the hand worked out from the bones' own places (no rig's axes assumed). The hand on the
        /// gun keeps the game's grip pose.
        /// </summary>
        private static void Fingers()
        {
            FingersCurled = 0;
            if (ByIk.Count == 0)
            {
                return;
            }
            float ease = 1f - Mathf.Exp(-20f * Time.deltaTime);
            foreach (KeyValuePair<ActorIk, NetGame.Remote> kv in ByIk)
            {
                Pose p = kv.Value.Pose;
                Actor a = kv.Value.Actor;
                if (p == null || !Usable(kv.Value, out Actor _, out Pose _) || a == null || a.animator == null)
                {
                    continue;
                }
                p.GripNowL = Mathf.Lerp(p.GripNowL, p.GripL, ease);
                p.GripNowR = Mathf.Lerp(p.GripNowR, p.GripR, ease);
                bool gunLeft = p.Is(GunInLeft);
                if (p.Is(HasGun))
                {
                    // With a gun the soldier's right hand is on its grip (the game's pose) and his left is the free hand: the VR left
                    // hand's, or a left-handed player's right (see BeforeIk). On the gun's own hold, the game's fingers.
                    if (!p.LeftOnGun && p.Is(gunLeft ? RightHand : LeftHand))
                    {
                        Curl(a.animator, 0, gunLeft ? p.GripNowR : p.GripNowL);
                    }
                }
                else
                {
                    if (p.Is(LeftHand))
                    {
                        Curl(a.animator, 0, p.GripNowL);
                    }
                    if (p.Is(RightHand))
                    {
                        Curl(a.animator, 4, p.GripNowR);
                    }
                }
            }
        }

        private static void Curl(Animator animator, int first, float amount)
        {
            if (amount < 0.03f)
            {
                return;
            }
            Transform index = animator.GetBoneTransform(FingerBones[first][0]);
            Transform little = animator.GetBoneTransform(FingerBones[first + 3][0]);
            Transform middleTip = animator.GetBoneTransform(FingerBones[first + 1][1]);
            Transform middle = animator.GetBoneTransform(FingerBones[first + 1][0]);
            if (index == null || little == null || middle == null || middleTip == null)
            {
                return;
            }
            // Across the hand (index to little), along it (knuckle to the next joint): the joints bend about the line across.
            Vector3 across = (little.position - index.position).normalized;
            Vector3 along = (middleTip.position - middle.position).normalized;
            // (Which side the palm is on: the bend turns the finger's tip to it. Index to little runs the other way on a right hand.)
            Vector3 palm = Vector3.Cross(along, across).normalized * (first >= 4 ? -1f : 1f);
            if (across.sqrMagnitude < 0.5f || palm.sqrMagnitude < 0.5f)
            {
                return;
            }
            float[] most = MostCurl;
            for (int f = first; f < first + 4; f++)
            {
                for (int j = 0; j < 3; j++)
                {
                    Transform bone = animator.GetBoneTransform(FingerBones[f][j]);
                    if (bone == null)
                    {
                        continue;
                    }
                    // The axis that turns this finger's direction toward the palm: the finger's own way crossed with the palm's.
                    // (The tip: its finger's own last segment's way, from the joint before; the middle finger's, taken for every tip,
                    // bent the others' tips sideways; scout.)
                    Transform from = j < 2 ? bone : animator.GetBoneTransform(FingerBones[f][1]);
                    Transform next = j < 2 ? animator.GetBoneTransform(FingerBones[f][j + 1]) : bone;
                    Vector3 dir = next != null && from != null && next != from ? (next.position - from.position).normalized : along;
                    Vector3 axis = Vector3.Cross(dir, palm);
                    if (axis.sqrMagnitude < 1e-4f)
                    {
                        continue;
                    }
                    bone.rotation = Quaternion.AngleAxis(most[j] * amount, axis.normalized) * bone.rotation;
                }
            }
            FingersCurled++;
        }

        // ---- his head, where he looks ---------------------------------------------------------------------------------

        /// <summary>A player's soldier's head: the bone's own forward (learnt once, as it stands), and his look eased.</summary>
        private sealed class Head
        {
            public Transform Bone;
            public Vector3 LocalForward;
            public Vector3 Look;
            public bool Eased;
        }

        private static readonly Dictionary<NetGame.Remote, Head> Heads = new Dictionary<NetGame.Remote, Head>();

        /// <summary>The check's: players' heads turned in the last frame.</summary>
        internal static int HeadsTurned;

        /// <summary>
        /// Each other player's soldier's head turned where he looks (a VR player: his headset's way; a flat one: his aim), after the
        /// animation and before the eyes draw it: the game's own look turned it only part of the way, and a player looking around stood
        /// with his face straight ahead (the user, 2026-11-11: "The face needs to be able to turn where the player is looking"). Held to
        /// what a neck does: 80 degrees either side of his body, 60 up or down.
        /// </summary>
        internal static void TurnHeads()
        {
            HeadsTurned = 0;
            if (NetGame.RemoteCount == 0)
            {
                return;
            }
            foreach (NetGame.Remote r in NetGame.RemoteValues)
            {
                Actor a = r.Actor;
                if (r.Id != 0 || a == null || a.dead || a.fallenOver || !r.HasState || a.animator == null)
                {
                    continue;
                }
                if (!Heads.TryGetValue(r, out Head h) || h.Bone == null)
                {
                    if (a.IsSeated() || a.IsOnLadder() || a.stance != Actor.Stance.Stand)
                    {
                        continue; // (learnt standing only: a head bent in a seat or a crouch would be wrong by that for his life)
                    }
                    Transform bone = a.animator.GetBoneTransform(HumanBodyBones.Head);
                    if (bone == null)
                    {
                        continue;
                    }
                    // (And only while he looks about level and the body's way: the game's own look-at turns the head toward his aim
                    // before this runs, and a head learnt turned stayed off by that angle for his life; remote visuals scout.)
                    Vector3 facing = r.Facing;
                    Vector3 flat = new Vector3(facing.x, 0f, facing.z);
                    if (facing.sqrMagnitude > 1e-4f && (Mathf.Abs(facing.normalized.y) > 0.2f
                        || flat.sqrMagnitude > 1e-4f && Vector3.Angle(flat, a.transform.forward) > 10f))
                    {
                        continue;
                    }
                    // (Learnt as he stands: the animation holds the head about the body's way then.)
                    h = new Head { Bone = bone, LocalForward = Quaternion.Inverse(bone.rotation) * a.transform.forward };
                    Heads[r] = h;
                }
                Vector3 want = r.Pose != null && r.Pose.Fresh ? r.Pose.HeadForward : r.Facing;
                if (want.sqrMagnitude < 1e-4f)
                {
                    continue;
                }
                want.Normalize();
                Vector3 body = a.transform.forward;
                body.y = 0f;
                if (body.sqrMagnitude < 1e-4f)
                {
                    continue;
                }
                body.Normalize();
                Vector3 look = HeadLook(body, want);
                h.Look = h.Eased ? Vector3.Slerp(h.Look, look, 1f - Mathf.Exp(-18f * Time.deltaTime)) : look;
                h.Eased = true;
                Vector3 now = h.Bone.rotation * h.LocalForward;
                h.Bone.rotation = Quaternion.FromToRotation(now, h.Look) * h.Bone.rotation;
                HeadsTurned++;
            }
        }

        /// <summary>
        /// Where a head looks for <paramref name="want"/> (a unit direction) on a body facing <paramref name="body"/> (level, unit): at
        /// most 80 degrees to either side and 60 up or down. Pure (no engine call): the offline tests check it.
        /// </summary>
        internal static Vector3 HeadLook(Vector3 body, Vector3 want)
        {
            Vector3 flat = new Vector3(want.x, 0f, want.z);
            float yaw = flat.sqrMagnitude > 1e-4f ? Mathf.Clamp(Vector3.SignedAngle(body, flat, Vector3.up), -80f, 80f) * Mathf.Deg2Rad : 0f;
            float pitch = Mathf.Clamp(Mathf.Asin(Mathf.Clamp(want.y, -1f, 1f)) * Mathf.Rad2Deg, -60f, 60f) * Mathf.Deg2Rad;
            Vector3 right = Vector3.Cross(Vector3.up, body);
            Vector3 turned = body * Mathf.Cos(yaw) + right * Mathf.Sin(yaw);
            return turned * Mathf.Cos(pitch) + Vector3.up * Mathf.Sin(pitch);
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
            TurnHeads();
            Fingers();
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
                PlaceSecond(kv.Value, p);
                // The magazine in his free hand while he reloads by hand.
                bool magazine = p.Is(Magazine) && Usable(kv.Value, out Actor holder, out Pose _);
                Weapon held = kv.Value.Actor != null ? kv.Value.Actor.activeWeapon : null;
                if ((magazine && p.MagazineBox != null || p.MagazinePartHidden) && !ReferenceEquals(p.MagazineOf, held))
                {
                    ShowMagazinePart(p);
                    if (p.MagazineBox != null)
                    {
                        Object.Destroy(p.MagazineBox); // (another gun in his hands since: its own magazine)
                        p.MagazineBox = null;
                    }
                }
                if (magazine && p.MagazineBox == null)
                {
                    // His gun's own magazine, copied from its model (the plain box only when it has none by name).
                    p.MagazineOf = held;
                    p.MagazineBox = MagazineCopy(held, out p.MagazineMiddle, out p.MagazinePart);
                }
                if (magazine && !p.MagazinePartHidden && p.MagazinePart != null)
                {
                    SetPartDrawn(p.MagazinePart, false);
                    p.MagazinePartHidden = true;
                }
                else if (!magazine && p.MagazinePartHidden)
                {
                    ShowMagazinePart(p);
                }
                if (magazine && p.MagazineBox == null)
                {
                    p.MagazineMiddle = Vector3.zero;
                    p.MagazineBox = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    Object.Destroy(p.MagazineBox.GetComponent<Collider>());
                    p.MagazineBox.name = "RavenfieldVR Net Magazine";
                    p.MagazineBox.transform.localScale = new Vector3(0.026f, 0.12f, 0.062f);
                    var renderer = p.MagazineBox.GetComponent<Renderer>();
                    // (One material for all of them: one made for each box was never destroyed with it, a leak each reload; scout.)
                    if (_magazineMaterial == null)
                    {
                        Shader lit = Shader.Find("Standard");
                        _magazineMaterial = new Material(lit != null ? lit : Shader.Find("Sprites/Default")) { color = new Color(0.13f, 0.13f, 0.14f) };
                    }
                    renderer.sharedMaterial = _magazineMaterial;
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
                        Quaternion turn = left ? p.LeftRotNow : p.RightRotNow;
                        Vector3 at = kv.Value.Actor.Position() + (left ? p.LeftNow : p.RightNow);
                        p.MagazineBox.transform.SetPositionAndRotation(at - turn * p.MagazineMiddle, turn);
                    }
                }
                if (kv.Value.Actor != null && kv.Value.Actor.dead && p.Heard > 0f)
                {
                    // Dead: the pose is the last life's. A quick respawn showed its hands, gun and head for up to 0.6 s (the pose was still
                    // fresh; remote visuals scout, 2026-11-12). The next life starts from its own first word.
                    p.Heard = -10f;
                }
                if (!Usable(kv.Value, out Actor _, out Pose _) && p.Eased)
                {
                    // (No pose told any more: the soldier's arms are his own again, with a gun or without.)
                    p.Eased = false;
                    if (kv.Key != null)
                    {
                        if (kv.Key.handTargetL == p.TargetL)
                        {
                            kv.Key.handTargetL = p.OwnLeft;
                        }
                        if (kv.Key.handTargetR == p.TargetR)
                        {
                            kv.Key.handTargetR = p.OwnRight;
                        }
                        // (Only a hand that was ours: in a seat with hand targets of its own (a wheel, a turret's handles) the game set the
                        // flags itself, and they were written off, his hands left the controls on the other PCs; scout, 2026-11-11.)
                        bool ownSeat = kv.Value.Actor != null && kv.Value.Actor.seat != null && kv.Value.Actor.seat.HasHandIkTargets();
                        if (!ownSeat)
                        {
                            kv.Key.SetHandIkEnabled(kv.Key.handTargetL != null && p.OwnLeft != null, kv.Key.handTargetR != null && p.OwnRight != null);
                        }
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

        private static void ShowMagazinePart(Pose p)
        {
            if (p.MagazinePartHidden && p.MagazinePart != null)
            {
                SetPartDrawn(p.MagazinePart, true);
            }
            p.MagazinePartHidden = false;
        }

        private static void SetPartDrawn(Transform part, bool drawn)
        {
            foreach (MeshRenderer r in part.GetComponentsInChildren<MeshRenderer>(true))
            {
                r.forceRenderingOff = !drawn;
            }
        }

        /// <summary>
        /// A copy of the magazine of <paramref name="w"/>'s model (the largest part named as a magazine, with its rounds), without its
        /// scripts and colliders, for a VR player's free hand while he reloads; <paramref name="middle"/>: its middle in its own frame.
        /// Null when the model has none by name (the plain box is shown then).
        /// </summary>
        private static GameObject MagazineCopy(Weapon w, out Vector3 middle, out Transform part)
        {
            middle = Vector3.zero;
            part = null;
            Transform model = w != null ? w.thirdPersonTransform : null;
            if (model == null)
            {
                return null;
            }
            try
            {
                Transform best = null;
                float bestSize = 0f;
                foreach (Transform t in model.GetComponentsInChildren<Transform>(true))
                {
                    if (t == model || !Weapons.WeaponAnalyzer.NamedAsAmmunition(t.name))
                    {
                        continue;
                    }
                    float size = 0f;
                    // (Plain meshes only: a skinned part's copy still follows the gun's own bones, and showed in the gun, not the hand.)
                    foreach (MeshRenderer r in t.GetComponentsInChildren<MeshRenderer>(true))
                    {
                        size = Mathf.Max(size, r.bounds.size.magnitude);
                    }
                    if (size > bestSize && size < 0.6f)
                    {
                        bestSize = size;
                        best = t;
                    }
                }
                if (best == null)
                {
                    return null;
                }
                part = best;
                Vector3 scale = best.lossyScale;
                GameObject copy = Object.Instantiate(best.gameObject);
                copy.name = "RavenfieldVR Net Magazine";
                copy.SetActive(true);
                foreach (MonoBehaviour script in copy.GetComponentsInChildren<MonoBehaviour>(true))
                {
                    Object.Destroy(script);
                }
                foreach (Collider collider in copy.GetComponentsInChildren<Collider>(true))
                {
                    Object.Destroy(collider);
                }
                foreach (SkinnedMeshRenderer skinned in copy.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                {
                    skinned.enabled = false;
                }
                Transform ct = copy.transform;
                ct.SetParent(null, false);
                ct.localScale = scale;
                ct.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
                var box = new Bounds();
                bool any = false;
                foreach (MeshRenderer r in copy.GetComponentsInChildren<MeshRenderer>(true))
                {
                    r.enabled = true;
                    r.forceRenderingOff = false;
                    r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                    if (!any)
                    {
                        box = r.bounds;
                        any = true;
                    }
                    else
                    {
                        box.Encapsulate(r.bounds);
                    }
                }
                if (!any)
                {
                    Object.Destroy(copy);
                    return null;
                }
                middle = box.center; // (the copy at the origin, unturned: its middle in its own frame)
                return copy;
            }
            catch (System.Exception e)
            {
                Log.WarnOnce("net-magcopy", "Multiplayer: a player's magazine could not be copied: " + e.Message);
                return null;
            }
        }

        // ---- what he carries, on his soldier -------------------------------------------------------------------------

        /// <summary>One carried weapon shown on a player's soldier: a copy of its model on his back or at his hip.</summary>
        private sealed class Carried
        {
            public Weapon Source;
            public GameObject Copy;
        }

        private static readonly Dictionary<NetGame.Remote, Carried[]> Inventories = new Dictionary<NetGame.Remote, Carried[]>();
        private static readonly List<Renderer> RendererScratch = new List<Renderer>();

        /// <summary>The check's: weapons shown on players' soldiers now.</summary>
        internal static int CarriedShown
        {
            get
            {
                int n = 0;
                foreach (Carried[] slots in Inventories.Values)
                {
                    foreach (Carried c in slots)
                    {
                        n += c != null && c.Copy != null && c.Copy.activeSelf ? 1 : 0;
                    }
                }
                return n;
            }
        }

        /// <summary>
        /// The other player's inventory seen on him (the user, 2026-11-10: "hands (gun hands with then gun models etc and
        /// inventory) should be seen"): the long gun he is not holding lies across his soldier's back, the sidearm he is not
        /// holding sits at the right thigh. Copies of the weapons' own third-person models (no scripts, no colliders), hung on
        /// the chest and the thigh bone. Looked at twice a second from the stand-ins' upkeep.
        /// </summary>
        internal static void KeepCarried(NetGame.Remote r, Actor a, bool alive)
        {
            if (!Inventories.TryGetValue(r, out Carried[] slots))
            {
                if (!alive)
                {
                    return;
                }
                slots = new Carried[2];
                Inventories[r] = slots;
            }
            for (int slot = 0; slot < 2; slot++)
            {
                Weapon w = alive && a.weapons != null && slot < a.weapons.Length ? a.weapons[slot] : null;
                bool show = w != null && w != a.activeWeapon && w.thirdPersonTransform != null && !a.fallenOver
                            && !(r.Pose != null && r.Pose.SecondCopy != null && r.Pose.SecondSlot == slot); // (shown in his free hand, not on his hip)
                Carried c = slots[slot];
                if (c != null && (c.Copy == null || c.Source != w))
                {
                    if (c.Copy != null)
                    {
                        Object.Destroy(c.Copy);
                    }
                    slots[slot] = c = null;
                }
                if (show && c == null)
                {
                    GameObject copy = MakeCopy(w, a, slot == 0);
                    if (copy != null)
                    {
                        slots[slot] = c = new Carried { Source = w, Copy = copy };
                    }
                }
                if (c != null && c.Copy.activeSelf != show)
                {
                    c.Copy.SetActive(show);
                }
            }
        }

        private static GameObject MakeCopy(Weapon w, Actor a, bool onBack)
        {
            Animator animator = a.animator;
            Transform bone = animator != null ? animator.GetBoneTransform(onBack ? HumanBodyBones.Chest : HumanBodyBones.RightUpperLeg) : null;
            if (bone == null && animator != null && onBack)
            {
                bone = animator.GetBoneTransform(HumanBodyBones.Spine);
            }
            if (bone == null)
            {
                return null;
            }
            try
            {
                Transform model = w.thirdPersonTransform;
                Vector3 size = model.lossyScale;
                GameObject copy = Object.Instantiate(model.gameObject);
                copy.name = "RavenfieldVR Net Carried " + (onBack ? "long gun" : "sidearm");
                copy.SetActive(true);
                foreach (MonoBehaviour script in copy.GetComponentsInChildren<MonoBehaviour>(true))
                {
                    Object.Destroy(script);
                }
                foreach (Collider collider in copy.GetComponentsInChildren<Collider>(true))
                {
                    Object.Destroy(collider);
                }
                foreach (ParticleSystem particles in copy.GetComponentsInChildren<ParticleSystem>(true))
                {
                    particles.gameObject.SetActive(false); // (its muzzle flash does not hang on his back)
                }
                Transform t = copy.transform;
                t.SetParent(null, false);
                t.localScale = size;
                t.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
                // The model's box in its own frame: its long side is the gun's length.
                RendererScratch.Clear();
                copy.GetComponentsInChildren(false, RendererScratch);
                bool any = false;
                var box = new Bounds();
                foreach (Renderer renderer in RendererScratch)
                {
                    if (!(renderer is MeshRenderer) && !(renderer is SkinnedMeshRenderer))
                    {
                        continue;
                    }
                    if (!any)
                    {
                        box = renderer.bounds;
                        any = true;
                    }
                    else
                    {
                        box.Encapsulate(renderer.bounds);
                    }
                }
                if (!any)
                {
                    Object.Destroy(copy);
                    return null;
                }
                Vector3 e = box.extents;
                Vector3 along = e.x >= e.y && e.x >= e.z ? Vector3.right : e.y >= e.z ? Vector3.up : Vector3.forward;
                Vector3 thin = e.x <= e.y && e.x <= e.z ? Vector3.right : e.y <= e.z ? Vector3.up : Vector3.forward;
                // Where it goes on him, in the world now: across the back, leaning to his right shoulder, its thin side against
                // him; or down along the right thigh.
                Vector3 up = a.transform.up, forward = a.transform.forward, right = a.transform.right;
                Vector3 wantAlong = onBack ? (up * 0.82f + right * 0.57f).normalized : -up;
                Vector3 wantThin = onBack ? -forward : right;
                Quaternion turn = Quaternion.LookRotation(wantAlong, wantThin) * Quaternion.Inverse(Quaternion.LookRotation(along, thin));
                Vector3 place = onBack ? bone.position - forward * (0.13f + Mathf.Min(e.x, Mathf.Min(e.y, e.z))) + up * 0.02f
                    : bone.position + right * (0.07f + Mathf.Min(e.x, Mathf.Min(e.y, e.z))) - up * 0.14f;
                t.rotation = turn;
                t.position = place - turn * box.center;
                t.SetParent(bone, true);
                return copy;
            }
            catch (System.Exception e)
            {
                Log.WarnOnce("net-carried", "Multiplayer: a carried weapon could not be shown on a player's soldier: " + e.Message);
                return null;
            }
        }

        internal static void ForgetCarried(NetGame.Remote r)
        {
            if (Inventories.TryGetValue(r, out Carried[] slots))
            {
                foreach (Carried c in slots)
                {
                    if (c != null && c.Copy != null)
                    {
                        Object.Destroy(c.Copy);
                    }
                }
                Inventories.Remove(r);
            }
        }
    }
}
