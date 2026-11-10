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
        private bool _pulled, _aiming, _onGround;
        private Vector3 _from, _to, _normal;
        private Actor _pointed;

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

        /// <summary>Each frame of the game from the UI's update (<paramref name="menu"/>: a panel or menu has the laser).</summary>
        internal void Update(bool menu)
        {
            _aiming = false;
            _pointed = null;
            if (Sandbox.Armed == Sandbox.ToolKind.None)
            {
                _pulled = false;
                return;
            }
            Actor player = LocalPlayer.actor;
            VRHand hand = _rig.Off;
            if (menu || !GameContext.InGame || player == null || player.dead || !hand.Tracked || hand.Role != HandRole.Free)
            {
                _pulled = hand.Trigger > ReleaseAt; // (a pull begun elsewhere, on a panel's button, is not the tool's)
                return;
            }

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
                if (box != null && box.parent is Actor soldier && !soldier.dead && soldier.aiControlled)
                {
                    _pointed = soldier;
                }
            }

            // The free hand's trigger is the game's Use: while the tool points, the pull is the tool's.
            VRInput.Consume(DigitalAction.Use, hand.Side);
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
            if (_pointed != null && Sandbox.Armed == Sandbox.ToolKind.Soldier)
            {
                Sandbox.SetBehaviour(_pointed, Sandbox.Soldier.Behaviour);
                said = "That soldier: " + Sandbox.Soldier.Behaviour + ".";
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
            UISystem.Instance?.Notice?.Show(said, 2.5f);
        }

        /// <summary>The beam and the ring from this frame's hand (the same pose the eyes are drawn from).</summary>
        internal void BeforeRender()
        {
            if (_go.activeSelf != _aiming)
            {
                _go.SetActive(_aiming);
            }
            if (!_aiming)
            {
                return;
            }
            VRHand hand = _rig.Off;
            Vector3 from = hand.TrackedPointer.position;
            Color c = _pointed != null ? new Color(1f, 0.8f, 0.2f) : Sandbox.Armed == Sandbox.ToolKind.Vehicle ? new Color(0.35f, 0.75f, 1f)
                : Sandbox.Soldier.Side == SoldierSide.Friendly ? new Color(0.35f, 0.75f, 1f) : new Color(1f, 0.4f, 0.35f);
            Color start = c, end = c;
            start.a = 0.85f;
            end.a = _onGround ? 0.5f : 0f;
            _beam.startColor = start;
            _beam.endColor = end;
            _beam.SetPosition(0, from);
            _beam.SetPosition(1, _to);
            bool ring = _onGround && _pointed == null;
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
        }

        internal void Destroy()
        {
            if (_go != null)
            {
                UnityEngine.Object.Destroy(_go);
            }
            if (_material != null)
            {
                UnityEngine.Object.Destroy(_material);
            }
        }
    }
}
