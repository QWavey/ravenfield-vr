using System.Collections.Generic;
using RavenfieldVR.Core;
using RavenfieldVR.Rig;
using RavenfieldVR.Runtime;
using UnityEngine;
using UnityEngine.Rendering;

namespace RavenfieldVR.Weapons
{
    /// <summary>
    /// Hands = Weapon's arms with no weapon in the hand: arms all the time, not only while a gun is held (user, 2026-10-29: "i
    /// wanna have the arms the whole time and not just when i touch the gun"). The arms are each gun's own (a mesh skinned to
    /// the gun's arm bones), so with nothing in the hand there were none to draw and the VR gloves showed. When a gun whose
    /// arms were drawn is put away, its arm skeleton and arm meshes are copied (only those: no gun part, no script, no
    /// animator), and the copy follows both controllers while the hands are empty, both arms as the weapon's off arm does off
    /// the gun (<see cref="ArmIK.FreeTarget"/>), the fingers from the controllers (<see cref="ArmFingers"/>), the gloves hidden.
    /// A gun drawn again takes over with its own arms as before: the gun hand on its grip, the support hand on the foregrip,
    /// a magazine as its reload holds it (user: "the gun held and support hand held weapon positions need to stay").
    /// </summary>
    internal sealed class EmptyArms
    {
        private GameObject _root;
        /// <summary>The weapon the arms were copied from, and the copied renderers (a content reload can take their meshes).</summary>
        private string _key;
        private readonly List<Renderer> _renderers = new List<Renderer>();
        private Transform[] _right, _left; // root, upper arm, forearm, hand
        private ArmChain _rightChain, _leftChain;
        private ArmFingers _rightFingers, _leftFingers;
        private bool _shown;

        /// <summary>
        /// The arms of <paramref name="held"/>, copied as it is put away (its bones in this frame's pose), when they were drawn
        /// and both arms are there; the copy of the last such weapon is kept until another one's is taken.
        /// </summary>
        public void TakeFrom(HeldWeapon held)
        {
            if (held == null || held.Weapon == null || !held.ArmsShownByMod || VRConfig.HandsNow != HandsMode.WeaponArms)
            {
                return;
            }
            WeaponRigData d = held.Rig.Data;
            if (d.ArmRight == null || d.ArmLeft == null || _root != null && _key == d.Key && _renderers.TrueForAll(HasMesh))
            {
                return;
            }
            Transform weaponRoot = held.Weapon.transform;
            Transform[] right = Chain(weaponRoot, d.ArmRight), left = Chain(weaponRoot, d.ArmLeft);
            if (right == null || left == null)
            {
                return;
            }
            // What to copy: the arm renderers (skinned ones with every bone they use), the two arm chains, and their parents.
            var renderers = new List<Renderer>();
            var needed = new List<Transform>();
            foreach (Renderer r in held.Rig.ArmRenderers)
            {
                if (!HasMesh(r) || !r.transform.IsChildOf(weaponRoot))
                {
                    continue;
                }
                if (r is SkinnedMeshRenderer s)
                {
                    if (s.bones == null || !System.Array.TrueForAll(s.bones, b => b == null || b.IsChildOf(weaponRoot)))
                    {
                        continue; // a bone outside the weapon (it would stay behind with it)
                    }
                    needed.AddRange(s.bones);
                    needed.Add(s.rootBone);
                }
                renderers.Add(r);
                needed.Add(r.transform);
            }
            if (renderers.Count == 0)
            {
                return;
            }
            needed.AddRange(right);
            needed.AddRange(left);
            Destroy();
            var root = new GameObject("RavenfieldVR empty-hand arms");
            root.SetActive(false);
            Object.DontDestroyOnLoad(root);
            root.transform.SetPositionAndRotation(weaponRoot.position, weaponRoot.rotation);
            root.transform.localScale = weaponRoot.lossyScale;
            int layer = renderers[0].gameObject.layer;
            var map = new Dictionary<Transform, Transform> { [weaponRoot] = root.transform };
            Transform Mirror(Transform t)
            {
                if (t == null)
                {
                    return null;
                }
                if (map.TryGetValue(t, out Transform m))
                {
                    return m;
                }
                var go = new GameObject(t.name) { layer = layer };
                go.transform.SetParent(Mirror(t.parent), false);
                go.transform.localPosition = t.localPosition;
                go.transform.localRotation = t.localRotation;
                go.transform.localScale = t.localScale;
                map[t] = go.transform;
                return go.transform;
            }
            foreach (Transform t in needed)
            {
                Mirror(t);
            }
            _renderers.Clear();
            foreach (Renderer r in renderers)
            {
                GameObject at = Mirror(r.transform).gameObject;
                at.layer = r.gameObject.layer;
                Renderer copy;
                if (r is SkinnedMeshRenderer s)
                {
                    var skin = at.AddComponent<SkinnedMeshRenderer>();
                    var bones = new Transform[s.bones.Length];
                    for (int i = 0; i < bones.Length; i++)
                    {
                        bones[i] = Mirror(s.bones[i]);
                    }
                    skin.sharedMesh = s.sharedMesh;
                    skin.bones = bones;
                    skin.rootBone = Mirror(s.rootBone);
                    skin.quality = s.quality;
                    skin.updateWhenOffscreen = true; // its bones go wherever the hands are: the bind-pose bounds culled it
                    copy = skin;
                }
                else
                {
                    at.AddComponent<MeshFilter>().sharedMesh = r.GetComponent<MeshFilter>().sharedMesh;
                    copy = at.AddComponent<MeshRenderer>();
                }
                copy.sharedMaterials = r.sharedMaterials;
                copy.shadowCastingMode = ShadowCastingMode.Off;
                copy.receiveShadows = r.receiveShadows;
                _renderers.Add(copy);
            }
            _right = System.Array.ConvertAll(right, Mirror);
            _left = System.Array.ConvertAll(left, Mirror);
            _rightChain = d.ArmRight;
            _leftChain = d.ArmLeft;
            _rightFingers = new ArmFingers(_right[3]);
            _leftFingers = new ArmFingers(_left[3]);
            _root = root;
            _key = d.Key;
            _shown = false;
            Log.Info($"Empty-hand arms: {d.Key}'s arms ({renderers.Count} renderer(s), {map.Count - 1} bone(s) and holders) drawn on both hands "
                     + "while no weapon is in the hand.");
        }

        /// <summary>
        /// Each frame before the held weapon's step: shown on both hands when <paramref name="show"/> (Weapon's arms, a match,
        /// no weapon in the hand), the gloves hidden; put away otherwise, the gloves shown again (a weapon drawn hides them again in
        /// its own step after this, so no glove shows for a frame).
        /// </summary>
        public void BeforeRender(WeaponSystem sys, bool show)
        {
            show &= _root != null && _renderers.TrueForAll(HasMesh);
            if (show != _shown)
            {
                _shown = show;
                if (_root != null)
                {
                    _root.SetActive(show);
                }
                if (!show)
                {
                    foreach (VRHand h in sys.Rig.Hands)
                    {
                        if (h.Model != null)
                        {
                            h.Model.Suppressed = false;
                        }
                    }
                }
            }
            if (!show)
            {
                return;
            }
            BodyZones zones = sys.Zones;
            Vector3 neck = sys.Rig.Head.position - Vector3.up * 0.12f - zones.TorsoForward * 0.08f;
            // The model's right arm on the right controller, its left on the left (as ArmIK's shoulders, both sides).
            Solve(sys.Rig.Hand(Input.Hand.Right), _right, _rightChain, _rightFingers, neck - Vector3.up * 0.06f + zones.TorsoRight * 0.17f, 1f, zones);
            Solve(sys.Rig.Hand(Input.Hand.Left), _left, _leftChain, _leftFingers, neck - Vector3.up * 0.06f - zones.TorsoRight * 0.17f, -1f, zones);
        }

        private static void Solve(VRHand hand, Transform[] arm, ArmChain chain, ArmFingers fingers, Vector3 shoulder, float side, BodyZones zones)
        {
            if (hand.Model != null)
            {
                hand.Model.Suppressed = true;
            }
            if (!hand.HasPose || arm[0] == null || arm[1] == null || arm[2] == null || arm[3] == null)
            {
                return; // (held where it was: the glove holds its last pose too)
            }
            RigidPose target = ArmIK.FreeTarget(hand, chain);
            Vector3 pole = shoulder + Vector3.down * 0.6f + zones.TorsoRight * (0.35f * side) - zones.TorsoForward * 0.2f;
            ArmIK.SolveArm(arm[0], arm[1], arm[2], arm[3], shoulder, target, pole);
            fingers.Pose(hand, arm[3], chain, 0f);
        }

        public void Destroy()
        {
            if (_root != null)
            {
                Object.Destroy(_root);
            }
            _root = null;
            _key = null;
            _renderers.Clear();
            _shown = false;
        }

        private static Transform[] Chain(Transform root, ArmChain c)
        {
            var t = new[] { WeaponRig.Resolve(root, c.Root), WeaponRig.Resolve(root, c.Upper), WeaponRig.Resolve(root, c.Lower), WeaponRig.Resolve(root, c.Hand) };
            return System.Array.TrueForAll(t, x => x != null) ? t : null;
        }

        private static bool HasMesh(Renderer r) =>
            r is SkinnedMeshRenderer s ? s.sharedMesh != null : r is MeshRenderer && r.GetComponent<MeshFilter>() != null && r.GetComponent<MeshFilter>().sharedMesh != null;
    }
}
