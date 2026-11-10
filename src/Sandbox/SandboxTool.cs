using System;
using System.Collections.Generic;
using RavenfieldVR.Core;
using RavenfieldVR.Input;
using RavenfieldVR.Rig;
using RavenfieldVR.UI;
using RavenfieldVR.Util;
using UnityEngine;

namespace RavenfieldVR.Sandbox
{
    /// <summary>
    /// The sandbox's spawn tool: while taken (<see cref="Sandbox.Armed"/>), the free hand points a beam at the world, a ring lies on
    /// the ground where it ends, and a pull of that hand's trigger makes what the panel is set to there, facing the player. Pointed
    /// at a soldier, the pull gives him the panel's behaviour instead (so any soldier of the match can be made neutral, frozen ...).
    /// Only a free hand points: one on the gun, a magazine or a ledge keeps its own work, and the tool waits.
    /// </summary>
    internal sealed class SandboxTool
    {
        private const float Reach = 120f, PullAt = 0.7f, ReleaseAt = 0.35f;
        private const int RingPoints = 28;
        private static readonly RaycastHit[] Hits = new RaycastHit[24];
        private static readonly Vector3[] Circle = MakeCircle();

        private static Vector3[] MakeCircle()
        {
            var circle = new Vector3[RingPoints];
            for (int i = 0; i < RingPoints; i++)
            {
                float a = i / (float)RingPoints * Mathf.PI * 2f;
                circle[i] = new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a));
            }
            return circle;
        }

        private readonly VRRig _rig;
        private readonly GameObject _go;
        private readonly LineRenderer _beam, _ring;
        private readonly Material _material;

        // The spawn gun (the user, 2026-11-10: "we need a spawngun too yk?"): a small tool in the free hand while the tool is
        // taken, built from plain shapes (no model file, no Unity editor on this PC): a body, a grip, a barrel and a lamp at its
        // mouth in the colour of what the next pull makes. The beam leaves from the lamp.
        private readonly GameObject _gun;
        private readonly Material _gunBody, _gunLamp;
        private readonly Transform _muzzle;
        private bool _gunShown;
        private float _kick;

        // Its model: one of the game's own pistols (the user, 2026-11-10: "the tool looks bad, could you use a default gun for the
        // toolgun from ravenfield? (secondary)"): a copy of a vanilla secondary's third-person model, looked for once in a match;
        // the blocks stay as the fallback, the lamp at its mouth stays as the light of what the next pull makes.
        private readonly List<GameObject> _blocks = new List<GameObject>();
        private GameObject _model;
        private bool _modelTried;

        private void TryModel()
        {
            if (WeaponManager.instance == null || WeaponManager.instance.allWeapons == null)
            {
                return; // (asked again at the next showing)
            }
            _modelTried = true;
            try
            {
                foreach (WeaponManager.WeaponEntry e in WeaponManager.instance.allWeapons)
                {
                    if (e == null || e.prefab == null || e.hidden || e.slot != WeaponManager.WeaponSlot.Secondary || e.sourceMod == null || !e.sourceMod.isOfficialContent)
                    {
                        continue;
                    }
                    Weapon w = e.prefab.GetComponent<Weapon>();
                    if (w == null || w.thirdPersonTransform == null)
                    {
                        continue;
                    }
                    Transform source = w.thirdPersonTransform, root = e.prefab.transform;
                    GameObject copy = UnityEngine.Object.Instantiate(source.gameObject);
                    copy.name = "RavenfieldVR Spawn Gun Model";
                    foreach (MonoBehaviour script in copy.GetComponentsInChildren<MonoBehaviour>(true))
                    {
                        UnityEngine.Object.Destroy(script);
                    }
                    foreach (Collider collider in copy.GetComponentsInChildren<Collider>(true))
                    {
                        UnityEngine.Object.Destroy(collider);
                    }
                    foreach (AudioSource audio in copy.GetComponentsInChildren<AudioSource>(true))
                    {
                        UnityEngine.Object.Destroy(audio);
                    }
                    foreach (Light light in copy.GetComponentsInChildren<Light>(true))
                    {
                        UnityEngine.Object.Destroy(light);
                    }
                    foreach (ParticleSystem particles in copy.GetComponentsInChildren<ParticleSystem>(true))
                    {
                        particles.gameObject.SetActive(false);
                    }
                    copy.SetActive(true);
                    // As it sits in the weapon's own frame, whose forward is the barrel's (the game hangs its muzzles on this model).
                    Transform t = copy.transform;
                    t.SetParent(_gun.transform, false);
                    t.localRotation = Quaternion.Inverse(root.rotation) * source.rotation;
                    Vector3 ls = source.lossyScale, rs = root.lossyScale;
                    t.localScale = new Vector3(ls.x / Mathf.Max(1e-4f, Mathf.Abs(rs.x)), ls.y / Mathf.Max(1e-4f, Mathf.Abs(rs.y)), ls.z / Mathf.Max(1e-4f, Mathf.Abs(rs.z)));
                    t.localPosition = Vector3.zero;
                    // Its box in the tool's frame, from its meshes (a renderer's own bounds are not kept while the tool is hidden).
                    bool any = false;
                    var box = new Bounds();
                    Matrix4x4 toTool = _gun.transform.worldToLocalMatrix;
                    foreach (MeshFilter filter in copy.GetComponentsInChildren<MeshFilter>(false))
                    {
                        if (filter.sharedMesh == null)
                        {
                            continue;
                        }
                        Bounds mesh = filter.sharedMesh.bounds;
                        Matrix4x4 m = toTool * filter.transform.localToWorldMatrix;
                        for (int corner = 0; corner < 8; corner++)
                        {
                            Vector3 point = m.MultiplyPoint3x4(mesh.center + Vector3.Scale(mesh.extents, new Vector3((corner & 1) == 0 ? -1f : 1f, (corner & 2) == 0 ? -1f : 1f, (corner & 4) == 0 ? -1f : 1f)));
                            if (!any)
                            {
                                box = new Bounds(point, Vector3.zero);
                                any = true;
                            }
                            else
                            {
                                box.Encapsulate(point);
                            }
                        }
                    }
                    // (A skinned model too, by its mesh as it lies under its own transform: measured by mesh filters only it was passed over.)
                    foreach (SkinnedMeshRenderer skinned in copy.GetComponentsInChildren<SkinnedMeshRenderer>(false))
                    {
                        if (skinned.sharedMesh == null)
                        {
                            continue;
                        }
                        Bounds mesh = skinned.sharedMesh.bounds;
                        Matrix4x4 m = toTool * skinned.transform.localToWorldMatrix;
                        for (int corner = 0; corner < 8; corner++)
                        {
                            Vector3 point = m.MultiplyPoint3x4(mesh.center + Vector3.Scale(mesh.extents, new Vector3((corner & 1) == 0 ? -1f : 1f, (corner & 2) == 0 ? -1f : 1f, (corner & 4) == 0 ? -1f : 1f)));
                            if (!any)
                            {
                                box = new Bounds(point, Vector3.zero);
                                any = true;
                            }
                            else
                            {
                                box.Encapsulate(point);
                            }
                        }
                    }
                    Vector3 size = box.size;
                    if (!any || !(size.z > 0.08f && size.z < 0.45f && size.z >= Mathf.Max(size.x, size.y) * 0.9f))
                    {
                        Log.Info($"Sandbox tool: {e.name}'s model is not taken ({(any ? $"{size.x * 100f:0} x {size.y * 100f:0} x {size.z * 100f:0} cm in the tool's frame" : "no mesh found")}).");
                    }
                    // A pistol lying along the pointing line: 8 to 45 cm long, and no longer any other way.
                    bool fits = any && size.z > 0.08f && size.z < 0.45f && size.z >= Mathf.Max(size.x, size.y) * 0.9f && !float.IsNaN(size.x + size.y + size.z);
                    if (!fits)
                    {
                        UnityEngine.Object.Destroy(copy);
                        continue;
                    }
                    // In the hand: its middle 5 cm ahead of the hand, its top 4.5 cm over the pointing line's start.
                    t.localPosition = new Vector3(-box.center.x, 0.045f - box.max.y, 0.05f - box.center.z);
                    _muzzle.localPosition = new Vector3(0f, 0.03f, 0.05f + size.z * 0.5f + 0.012f);
                    _muzzle.localScale = Vector3.one * 0.018f;
                    foreach (GameObject block in _blocks)
                    {
                        if (block != null && block.transform != _muzzle)
                        {
                            block.SetActive(false);
                        }
                    }
                    _model = copy;
                    Log.Info($"Sandbox tool: its model is the game's {e.name} ({size.z * 100f:0} cm long, {size.y * 100f:0} cm high).");
                    return;
                }
                Log.Info("Sandbox tool: none of the game's pistols has a model that fits the hand; the tool stays the one made from blocks.");
            }
            catch (Exception e)
            {
                Log.WarnOnce("sandbox-tool-model", "Sandbox tool: a game pistol could not be taken as its model (the blocks stay): " + e);
            }
        }

        /// <summary>The check's: whether the spawn gun is drawn in the hand now, and where its mouth is.</summary>
        internal bool GunShown => _gunShown;
        internal Vector3 Muzzle => _muzzle.position;
        private bool _pulled, _aiming, _onGround;
        private Vector3 _from, _to, _normal;
        private Actor _pointed;
        private Vehicle _pointedVehicle;

        internal SandboxTool(VRRig rig)
        {
            _rig = rig;
            _go = new GameObject("RavenfieldVR Sandbox Tool");
            UnityEngine.Object.DontDestroyOnLoad(_go);
            _go.layer = 5;
            _material = new Material(Shader.Find("Sprites/Default")) { color = Color.white };
            _beam = Line(_go, 2, 0.006f, false);
            var ring = new GameObject("Ring");
            ring.layer = 5;
            ring.transform.SetParent(_go.transform, false);
            _ring = Line(ring, RingPoints, 0.02f, true);
            _go.SetActive(false);

            _gun = new GameObject("RavenfieldVR Spawn Gun");
            UnityEngine.Object.DontDestroyOnLoad(_gun);
            Shader lit = Shader.Find("Standard");
            _gunBody = new Material(lit != null ? lit : Shader.Find("Sprites/Default")) { color = new Color(0.16f, 0.17f, 0.19f) };
            _gunBody.SetFloat("_Glossiness", 0.35f);
            _gunLamp = new Material(Shader.Find("Sprites/Default")) { color = Color.white };
            // (In the hand's pointing frame: z along the pointing direction, the grip at the origin, in metres.)
            Part(PrimitiveType.Cube, new Vector3(0f, 0.012f, 0.055f), new Vector3(0.034f, 0.046f, 0.15f), Vector3.zero, _gunBody);       // body
            Part(PrimitiveType.Cube, new Vector3(0f, -0.04f, 0.0f), new Vector3(0.028f, 0.085f, 0.036f), new Vector3(-14f, 0f, 0f), _gunBody); // grip
            Part(PrimitiveType.Cylinder, new Vector3(0f, 0.016f, 0.165f), new Vector3(0.022f, 0.04f, 0.022f), new Vector3(90f, 0f, 0f), _gunBody); // barrel
            Part(PrimitiveType.Cube, new Vector3(0f, 0.045f, 0.03f), new Vector3(0.026f, 0.02f, 0.07f), Vector3.zero, _gunBody);          // top rail
            Part(PrimitiveType.Cube, new Vector3(0f, 0.0565f, 0.03f), new Vector3(0.02f, 0.003f, 0.05f), Vector3.zero, _gunLamp);          // the strip of light on top
            _muzzle = Part(PrimitiveType.Sphere, new Vector3(0f, 0.016f, 0.21f), Vector3.one * 0.03f, Vector3.zero, _gunLamp);            // the lamp at the mouth
            _gun.SetActive(false);
        }

        private Transform Part(PrimitiveType kind, Vector3 at, Vector3 size, Vector3 euler, Material material)
        {
            GameObject part = GameObject.CreatePrimitive(kind);
            _blocks.Add(part);
            UnityEngine.Object.Destroy(part.GetComponent<Collider>());
            part.transform.SetParent(_gun.transform, false);
            part.transform.localPosition = at;
            part.transform.localRotation = Quaternion.Euler(euler);
            part.transform.localScale = size;
            var renderer = part.GetComponent<Renderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            return part.transform;
        }

        private LineRenderer Line(GameObject on, int points, float width, bool loop)
        {
            LineRenderer line = on.AddComponent<LineRenderer>();
            line.sharedMaterial = _material;
            line.positionCount = points;
            line.useWorldSpace = true;
            line.loop = loop;
            line.widthMultiplier = width;
            line.numCapVertices = 2;
            line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            line.receiveShadows = false;
            return line;
        }

        /// <summary>What <paramref name="kind"/> makes at a point, facing <paramref name="facing"/>; the line for the player.</summary>
        internal static string Make(Sandbox.ToolKind kind, Vector3 at, Vector3 facing)
        {
            string problem;
            if (kind == Sandbox.ToolKind.Vehicle)
            {
                Vehicle v = Sandbox.SpawnVehicle(Sandbox.Vehicle, Sandbox.VehicleFriendly, at, facing, out problem);
                return v != null ? $"{Sandbox.Vehicle} made." : "No vehicle: " + problem + ".";
            }
            List<Actor> made = Sandbox.SpawnSoldiers(Sandbox.Soldier, at, facing, out problem);
            return made.Count > 0 ? $"{made.Count} soldier(s) made." : "No soldier: " + problem + ".";
        }

        private static bool Own(Collider c, Actor player)
        {
            if (c == null)
            {
                return true;
            }
            Hitbox box = c.GetComponent<Hitbox>();
            if (box != null)
            {
                return box.parent == player;
            }
            return player != null && c.transform.IsChildOf(player.transform);
        }

        /// <summary>The hand the tool is in (the user, 2026-11-10: "the tool is also stuck to one hand only"): the gun hand while it
        /// holds nothing, else the other one.</summary>
        private Hand _side;

        private VRHand ToolHand()
        {
            VRHand main = _rig.Dominant, other = _rig.Off;
            bool mainEmpty = main.Tracked && (main.Role == HandRole.Free || main.Role == HandRole.Pointer) && main.Held == null
                             && (Weapons.WeaponSystem.Instance == null || Weapons.WeaponSystem.Instance.Held == null);
            VRHand hand = mainEmpty ? main : other;
            _side = hand.Side;
            return hand;
        }

        /// <summary>Each frame of the game from the UI's update (<paramref name="menu"/>: a panel or menu has the laser).</summary>
        internal void Update(bool menu)
        {
            Sandbox.FlyTick(_rig.Head);
            _aiming = false;
            _pointed = null;
            _pointedVehicle = null;
            _gunShown = false;
            if (Sandbox.Armed == Sandbox.ToolKind.None)
            {
                _pulled = false;
                return;
            }
            Actor player = LocalPlayer.actor;
            VRHand hand = ToolHand();
            if (menu || !GameContext.InGame || player == null || player.dead || !hand.Tracked || hand.Role != HandRole.Free)
            {
                _pulled = hand.Trigger > ReleaseAt; // (a pull begun elsewhere, on a panel's button, is not the tool's)
                // (With a panel up the gun stays in the hand, lowered work aside: only the beam and the pull wait.)
                _gunShown = GameContext.InGame && player != null && !player.dead && hand.Tracked && (hand.Role == HandRole.Free || hand.Role == HandRole.Pointer);
                return;
            }
            // With a gun drawn the tool is in the other hand, whose trigger is also the gun's (the pump, the launcher under the
            // barrel, the light): near the gun that hand is the gun's, and the tool waits (it took every press; review, 2026-11-10).
            if (Weapons.WeaponSystem.Instance != null && Weapons.WeaponSystem.Instance.Held != null
                && Vector3.Distance(hand.Palm.position, _rig.Dominant.Palm.position) < 0.35f)
            {
                _gunShown = false;
                _pulled = hand.Trigger > ReleaseAt;
                return;
            }
            _gunShown = true;

            _from = hand.TrackedPointer.position;
            Vector3 direction = hand.TrackedPointer.Forward;
            int n = Physics.RaycastNonAlloc(_from, direction, Hits, Reach, ~(1 << 2), QueryTriggerInteraction.Ignore);
            float nearest = float.MaxValue;
            bool any = false;
            RaycastHit best = default;
            for (int i = 0; i < n; i++)
            {
                if (Hits[i].distance < nearest && !Own(Hits[i].collider, player))
                {
                    nearest = Hits[i].distance;
                    best = Hits[i];
                    any = true;
                }
            }
            _aiming = true;
            _onGround = any;
            _to = any ? best.point : _from + direction * 12f;
            _normal = any ? best.normal : Vector3.up;
            if (any)
            {
                Hitbox box = best.collider.GetComponent<Hitbox>();
                // (Only the soldier tool changes a soldier: with the vehicle tool the beam went yellow on one and the pull made a vehicle on him.)
                if (Sandbox.Armed != Sandbox.ToolKind.Vehicle && box != null && box.parent is Actor soldier && !soldier.dead && soldier.aiControlled)
                {
                    _pointed = soldier;
                }
                else if (Sandbox.Armed == Sandbox.ToolKind.Remove)
                {
                    _pointedVehicle = best.collider.GetComponentInParent<Vehicle>();
                }
            }

            // The free hand's trigger is the game's Use: while the tool points, the pull is the tool's.
            VRInput.Consume(DigitalAction.Use, hand.Side);
            VRInput.Consume(DigitalAction.Fire, hand.Side); // (in the emptied gun hand the trigger is the game's Fire)
            float pull = hand.Trigger;
            if (_pulled)
            {
                _pulled = pull > ReleaseAt;
                return;
            }
            if (pull < PullAt)
            {
                return;
            }
            _pulled = true;
            string said;
            if (Sandbox.Armed == Sandbox.ToolKind.Remove)
            {
                said = _pointed != null ? Sandbox.RemoveSoldier(_pointed) : _pointedVehicle != null ? Sandbox.RemoveVehicle(_pointedVehicle) : "Point at a soldier or a vehicle.";
            }
            else if (_pointed != null && Sandbox.Armed == Sandbox.ToolKind.Soldier)
            {
                if (Net.NetGame.IsRemote(_pointed))
                {
                    said = "That one is another PC's soldier: the host sets what he does.";
                }
                else
                {
                    Sandbox.SetBehaviour(_pointed, Sandbox.Soldier.Behaviour);
                    said = "That soldier: " + Sandbox.Soldier.Behaviour + ".";
                }
            }
            else if (!any)
            {
                said = "Point at the ground.";
            }
            else
            {
                Vector3 at = Sandbox.Ground(best.point, best.normal);
                said = Make(Sandbox.Armed, at, _rig.Head.position - at);
            }
            hand.Haptic(0.03f, 140f, 0.6f);
            _kick = 1f;
            UISystem.Instance?.Notice?.Show(said, 2.5f);
            Log.Info($"Sandbox tool ({Sandbox.Armed}, {hand.Side} hand): {said} Pointed at {(any ? best.point.ToString("F1") + " on '" + best.collider.name + "'" : "nothing")}.");
        }

        /// <summary>The beam and the ring from this frame's hand (the same pose the eyes are drawn from).</summary>
        internal void BeforeRender()
        {
            if (_go.activeSelf != _aiming)
            {
                _go.SetActive(_aiming);
            }
            if (_gun.activeSelf != _gunShown)
            {
                _gun.SetActive(_gunShown);
            }
            VRHand hand = _rig.Hand(_side);
            if (_gunShown && !_modelTried)
            {
                TryModel();
            }
            if (_gunShown)
            {
                // In the hand's pointing frame, tipped back a little by each pull.
                _kick = Mathf.MoveTowards(_kick, 0f, Time.unscaledDeltaTime * 6f);
                var pose = hand.TrackedPointer;
                _gun.transform.SetPositionAndRotation(pose.position - pose.Forward * (0.012f * _kick), pose.rotation * Quaternion.Euler(-7f * _kick, 0f, 0f));
                Color lamp = Sandbox.Armed == Sandbox.ToolKind.Remove ? (_pointed != null || _pointedVehicle != null ? new Color(1f, 0.55f, 0.1f) : Color.white)
                    : _pointed != null ? new Color(1f, 0.8f, 0.2f) : Sandbox.Armed == Sandbox.ToolKind.Vehicle ? new Color(0.35f, 0.75f, 1f)
                    : Sandbox.Soldier.Side == SoldierSide.Friendly ? new Color(0.35f, 0.75f, 1f) : new Color(1f, 0.4f, 0.35f);
                if (_gunLamp.color != lamp)
                {
                    _gunLamp.color = lamp;
                }
            }
            if (!_aiming)
            {
                return;
            }
            Vector3 from = _muzzle.position;
            Color c = Sandbox.Armed == Sandbox.ToolKind.Remove ? (_pointed != null || _pointedVehicle != null ? new Color(1f, 0.55f, 0.1f) : Color.white)
                : _pointed != null ? new Color(1f, 0.8f, 0.2f) : Sandbox.Armed == Sandbox.ToolKind.Vehicle ? new Color(0.35f, 0.75f, 1f)
                : Sandbox.Soldier.Side == SoldierSide.Friendly ? new Color(0.35f, 0.75f, 1f) : new Color(1f, 0.4f, 0.35f);
            Color start = c, end = c;
            start.a = 0.85f;
            end.a = _onGround ? 0.5f : 0f;
            _beam.startColor = start;
            _beam.endColor = end;
            _beam.SetPosition(0, from);
            _beam.SetPosition(1, _to);
            bool ring = _onGround && _pointed == null && Sandbox.Armed != Sandbox.ToolKind.Remove;
            if (_ring.gameObject.activeSelf != ring)
            {
                _ring.gameObject.SetActive(ring);
            }
            if (ring)
            {
                Vector3 centre = _to + _normal * 0.03f;
                Quaternion lie = Quaternion.FromToRotation(Vector3.up, _normal);
                // (A soldier's footprint, a vehicle's: seen from far, the line's width grows with the distance.)
                float size = Sandbox.Armed == Sandbox.ToolKind.Vehicle ? 2.2f : 0.45f + 0.75f * (Mathf.Clamp(Sandbox.Soldier.Count, 1, 12) - 1);
                for (int i = 0; i < RingPoints; i++)
                {
                    _ring.SetPosition(i, centre + lie * (Circle[i] * size));
                }
                float far = Vector3.Distance(from, _to);
                _ring.widthMultiplier = Mathf.Clamp(far * 0.004f, 0.012f, 0.25f);
                _ring.startColor = _ring.endColor = start;
            }
        }

        internal void OnSceneChanged()
        {
            _aiming = false;
            _pointed = null;
            _pulled = false;
            _gunShown = false;
        }

        internal void Destroy()
        {
            if (_go != null)
            {
                UnityEngine.Object.Destroy(_go);
            }
            if (_gun != null)
            {
                UnityEngine.Object.Destroy(_gun);
            }
            if (_gunBody != null)
            {
                UnityEngine.Object.Destroy(_gunBody);
            }
            if (_gunLamp != null)
            {
                UnityEngine.Object.Destroy(_gunLamp);
            }
            if (_material != null)
            {
                UnityEngine.Object.Destroy(_material);
            }
        }
    }
}
