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
    /// arms were drawn is put away (with none yet: a carried weapon's, from its holstered object), its arm skeleton and arm
    /// meshes are copied (only those: no gun part, no script, no animator), and the copy follows both controllers while the hands are empty, both arms as the weapon's off arm does off
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
        /// <summary>This class hid the gloves (and gives them back when it stops showing, whatever made it stop).</summary>
        private bool _glovesHidden;

        /// <summary>
        /// The copy's root against the head as it was copied, where it is carried every frame: left where the gun was put away, the
        /// bones above and beside the two arms stayed there, and a mesh skinned to them stretched back to it as the player walked off.
        /// </summary>
        private RigidPose _rootInHead = RigidPose.Identity;

        /// <summary>
        /// The two arms' bones (right then left: root, upper arm, forearm, hand) as copied, put back before each frame's solve: with
        /// no animator on the copy each solve began from the last one's, and the arms' twist could build up frame by frame.
        /// </summary>
        private readonly RigidPose[] _restLocal = new RigidPose[8];

        /// <summary>Self-test: the arms are drawn on the empty hands, and the farther hand bone's distance from its target (m).</summary>
        internal bool Shown => _root != null && _root.activeSelf;
        internal float HandError { get; private set; } = -1f;

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
            Copy(held.Weapon, held.Rig);
        }

        /// <summary>When the next try at <see cref="TakeFromLoadout"/> may be (Time.unscaledTime).</summary>
        private float _nextLoadoutTry;

        /// <summary>
        /// No copy yet (spawned empty-handed, just respawned, or Weapon's arms chosen with nothing drawn since): the arms of a weapon
        /// the player carries, from its holstered object, whose analysis is already made (never one run here; the loadout's are
        /// made in the loading screen). At most every 2 s while none is found. Before, the gloves showed until a gun had been drawn
        /// and put away once.
        /// </summary>
        private void TakeFromLoadout(WeaponSystem sys)
        {
            if (Time.unscaledTime < _nextLoadoutTry)
            {
                return;
            }
            _nextLoadoutTry = Time.unscaledTime + 2f;
            Actor actor = Util.GameContext.PlayerActor;
            if (actor == null || actor.weapons == null)
            {
                return;
            }
            foreach (Weapon w in actor.weapons)
            {
                if (w == null || !WeaponAnalyzer.IsCached(w, null))
                {
                    continue;
                }
                WeaponRig rig = sys.RigFor(w);
                if (rig.Data.ArmRight != null && rig.Data.ArmLeft != null && rig.ArmRenderers.Length > 0)
                {
                    Copy(w, rig);
                    if (_root != null)
                    {
                        return;
                    }
                }
            }
        }

        /// <summary>The arm skeleton and arm meshes of <paramref name="weapon"/> copied (only those), its bones in their pose now.</summary>
        private void Copy(Weapon weapon, WeaponRig rig)
        {
            WeaponRigData d = rig.Data;
            if (d.ArmRight == null || d.ArmLeft == null || _root != null && _key == d.Key && AllMeshes())
            {
                return;
            }
            try
            {
                Transform weaponRoot = weapon.transform;
                Transform[] right = Chain(weaponRoot, d.ArmRight), left = Chain(weaponRoot, d.ArmLeft);
                if (right == null || left == null)
                {
                    return;
                }
                if (left[3].IsChildOf(right[0]) || right[3].IsChildOf(left[0]))
                {
                    // One arm's root holding the other hand too: solving the second arm would move the first after it was placed (review).
                    Log.InfoOnce("emptyarms-sharedroot:" + d.Key, $"Empty-hand arms: {d.Key}'s two arms share a root bone: not copied.");
                    return;
                }
                // What to copy: the arm renderers (skinned ones with every bone they use), the two arm chains, and their parents.
                var renderers = new List<Renderer>();
                var needed = new List<Transform>();
                // Each arm mesh's own enabled state: the gun's animation's on a gun held with Weapon's arms (the mod leaves the arms to
                // it there), the model's own on a carried one never held (and every arm on again on one that was: the hold's end puts
                // them back). Not while the gun's main arm mesh is off itself: the game's Hide and the flat view's scope picture switch
                // the arms off with the gun (review, 2026-10-30).
                bool byEnabled = weapon.arms == null || weapon.arms.enabled;
                foreach (Renderer r in rig.ArmRenderers)
                {
                    if (!HasMesh(r) || !r.transform.IsChildOf(weaponRoot))
                    {
                        continue;
                    }
                    // Only what the arms draw: a piece the gun's animation hides (a reload's fresh magazine on the hand, the flat view's)
                    // was copied shown and stayed on the empty hand for good (Tasks 355); a part switched off in the model, and an arm
                    // mesh named as ammunition, never.
                    // (With the main arm off, a switched-off piece named like ammunition is still the reload's magazine: "Arms_Mag".)
                    if (byEnabled && !r.enabled || !byEnabled && !r.enabled && r != weapon.arms && WeaponAnalyzer.NamesAmmunition(r.name)
                        || !ActiveInWeapon(r.transform, weaponRoot) || WeaponAnalyzer.NamedAsAmmunition(r.name))
                    {
                        continue;
                    }
                    if (r is SkinnedMeshRenderer s)
                    {
                        if (s.bones == null)
                        {
                            continue;
                        }
                        needed.AddRange(s.bones); // (one outside the weapon gets a stand-in: Mirror)
                        if (s.rootBone != null && s.rootBone.IsChildOf(weaponRoot))
                        {
                            needed.Add(s.rootBone); // (one outside it would be mirrored as loose objects outside the copy; review)
                        }
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
                _root = root; // at once: a step below that throws leaves it to the next Destroy, not an orphan (review)
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
                    if (!t.IsChildOf(weaponRoot))
                    {
                        // A bone outside the weapon (an armature an exporter listed whole): a stand-in right under the copy, where it
                        // is against the weapon now, carried with it. Left out, the whole arm mesh was not copied and the gloves
                        // showed with no arms (review, 2026-10-30).
                        var stand = new GameObject(t.name) { layer = layer };
                        stand.transform.SetParent(root.transform, false);
                        stand.transform.localPosition = weaponRoot.InverseTransformPoint(t.position);
                        stand.transform.localRotation = Quaternion.Inverse(weaponRoot.rotation) * t.rotation;
                        Vector3 own = t.lossyScale, of = weaponRoot.lossyScale;
                        stand.transform.localScale = new Vector3(own.x / Mathf.Max(1e-6f, of.x), own.y / Mathf.Max(1e-6f, of.y), own.z / Mathf.Max(1e-6f, of.z));
                        map[t] = stand.transform;
                        return stand.transform;
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
                        skin.rootBone = s.rootBone != null && s.rootBone.IsChildOf(weaponRoot) ? Mirror(s.rootBone) : null;
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
                for (int i = 0; i < 8; i++)
                {
                    Transform t = i < 4 ? _right[i] : _left[i - 4];
                    _restLocal[i] = new RigidPose(t.localPosition, t.localRotation);
                }
                // Carried with the head from where it is now (the gun in the hand); a carried weapon's object far off (more than a metre),
                // just in front of the chest instead.
                Transform head = VRRig.Instance != null ? VRRig.Instance.Head : null;
                _rootInHead = head != null ? new RigidPose(head.position, head.rotation).Inverse().Multiply(new RigidPose(root.transform.position, root.transform.rotation))
                    : RigidPose.Identity;
                if (_rootInHead.position.magnitude > 1f)
                {
                    _rootInHead = new RigidPose(new Vector3(0f, -0.3f, 0.25f), _rootInHead.rotation);
                }
                _root = root;
                _key = d.Key;
                Log.Info($"Empty-hand arms: {d.Key}'s arms ({renderers.Count} renderer(s), {map.Count - 1} bone(s) and holders) drawn on both hands "
                         + "while no weapon is in the hand.");
            }
            catch (System.Exception e)
            {
                // A step that threw leaves no partial copy (its arms unset, it showed and hid the gloves with nothing drawn; review).
                Destroy();
                Log.ExceptionOnce("EmptyArms.Copy", e);
            }
        }

        /// <summary>
        /// Each frame before the held weapon's step: shown on both hands when <paramref name="show"/> (Weapon's arms, a match,
        /// no weapon in the hand), the gloves hidden; put away otherwise, the gloves shown again (a weapon drawn hides them again in
        /// its own step after this, so no glove shows for a frame).
        /// </summary>
        public void BeforeRender(WeaponSystem sys, bool show)
        {
            if (show && _root != null && !AllMeshes())
            {
                Destroy(); // a mesh gone with its weapon (one made at run time): copied again below, not left hidden for good
            }
            if (show && _root == null)
            {
                TakeFromLoadout(sys);
            }
            // Not while a hand holds a soldier: that glove is on him, drawn (as with a gun in the other hand: HeldWeapon keeps it), and an
            // arm sent to the controller left it hidden and the hand off the body (review, 2026-10-30).
            show &= _root != null && AllMeshes() && sys.Rig.Hands[0].Role != HandRole.Body && sys.Rig.Hands[1].Role != HandRole.Body;
            // Shown by its own state, and the gloves given back by whether this class hid them: a copy made again in the frame the
            // old one went (a mesh gone) cleared a shown flag, and a soldier taken then left the gloves hidden with no arms (review).
            if (_root != null && _root.activeSelf != show)
            {
                _root.SetActive(show);
            }
            if (!show)
            {
                ReturnGloves(sys);
                return;
            }
            _glovesHidden = true;
            RigidPose carried = new RigidPose(sys.Rig.Head.position, sys.Rig.Head.rotation).Multiply(_rootInHead);
            _root.transform.SetPositionAndRotation(carried.position, carried.rotation);
            BodyZones zones = sys.Zones;
            Vector3 neck = sys.Rig.Head.position - Vector3.up * 0.12f - zones.TorsoForward * 0.08f;
            // As ArmIK places them with a gun: the model's right arm (the gun arm) on the gun hand, its shoulder on that side. On the
            // right controller always, left-handed play's arms swapped sides at every draw and holster (review, 2026-10-30).
            float side = Input.VRInput.GunHand == Input.Hand.Left ? -1f : 1f;
            float right = Solve(sys.Rig.Dominant, _right, _rightChain, _rightFingers, neck - Vector3.up * 0.06f + zones.TorsoRight * (0.17f * side), side, zones, _restLocal, 0);
            float left = Solve(sys.Rig.Off, _left, _leftChain, _leftFingers, neck - Vector3.up * 0.06f - zones.TorsoRight * (0.17f * side), -side, zones, _restLocal, 4);
            HandError = Mathf.Max(right, left);
        }

        /// <summary>
        /// One arm on its hand; returns how far its hand bone ended from its target (m: the IK's reach, the placement itself is the
        /// autotest's picture), -1 when it was not solved (no arm, the hand with no pose: a 0 read as on target; review).
        /// </summary>
        private static float Solve(VRHand hand, Transform[] arm, ArmChain chain, ArmFingers fingers, Vector3 shoulder, float side, BodyZones zones, RigidPose[] rest, int restAt)
        {
            if (arm == null || fingers == null || chain == null)
            {
                return -1f; // (a copy that failed part way: never hide a glove for an arm that is not there)
            }
            if (arm[0] == null || arm[1] == null || arm[2] == null || arm[3] == null)
            {
                return -1f; // (a bone gone: the same)
            }
            if (hand.Model != null)
            {
                hand.Model.Suppressed = true;
            }
            if (!hand.HasPose)
            {
                return -1f; // (held where it was: the glove holds its last pose too)
            }
            // From its copied pose (see _restLocal): only an arm solved now, one with no pose kept where it was (review).
            for (int i = 0; i < 4; i++)
            {
                arm[i].localPosition = rest[restAt + i].position;
                arm[i].localRotation = rest[restAt + i].rotation;
            }
            RigidPose target = ArmIK.FreeTarget(hand, chain);
            Vector3 pole = shoulder + Vector3.down * 0.6f + zones.TorsoRight * (0.35f * side) - zones.TorsoForward * 0.2f;
            ArmIK.SolveArm(arm[0], arm[1], arm[2], arm[3], shoulder, target, pole);
            fingers.Pose(hand, arm[3], chain, 0f);
            return Vector3.Distance(arm[3].position, target.position);
        }

        /// <summary>The gloves shown again if this class hid them (put away; and VR stopping, when no later step gives them back: review).</summary>
        public void ReturnGloves(WeaponSystem sys)
        {
            if (!_glovesHidden || sys.Rig == null)
            {
                return;
            }
            _glovesHidden = false;
            foreach (VRHand h in sys.Rig.Hands)
            {
                if (h != null && h.Model != null)
                {
                    h.Model.Suppressed = false;
                }
            }
        }

        /// <summary>
        /// The copy gone. Whether it was shown stays as it was, so the next <see cref="BeforeRender"/> sees the change and shows the
        /// gloves again (cleared here, gloves hidden by a copy destroyed while shown stayed hidden).
        /// </summary>
        public void Destroy()
        {
            if (_root != null)
            {
                Object.Destroy(_root);
            }
            _root = null;
            _key = null;
            _renderers.Clear();
        }

        /// <summary>Whether <paramref name="t"/> and its parents up to the weapon's root are switched on in the model.</summary>
        private static bool ActiveInWeapon(Transform t, Transform weaponRoot)
        {
            for (; t != null && t != weaponRoot; t = t.parent)
            {
                if (!t.gameObject.activeSelf)
                {
                    return false;
                }
            }
            return true;
        }

        private static Transform[] Chain(Transform root, ArmChain c)
        {
            var t = new[] { WeaponRig.Resolve(root, c.Root), WeaponRig.Resolve(root, c.Upper), WeaponRig.Resolve(root, c.Lower), WeaponRig.Resolve(root, c.Hand) };
            return System.Array.TrueForAll(t, x => x != null) ? t : null;
        }

        /// <summary>Every copied renderer still has its mesh (a loop: TrueForAll(HasMesh) made a new delegate on every call, each frame).</summary>
        private bool AllMeshes()
        {
            foreach (Renderer r in _renderers)
            {
                if (!HasMesh(r))
                {
                    return false;
                }
            }
            return true;
        }

        private static bool HasMesh(Renderer r) =>
            r is SkinnedMeshRenderer s ? s.sharedMesh != null : r is MeshRenderer && r.GetComponent<MeshFilter>() != null && r.GetComponent<MeshFilter>().sharedMesh != null;
    }
}
