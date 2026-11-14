using HarmonyLib;
using RavenfieldVR.Core;
using RavenfieldVR.Input;
using RavenfieldVR.Rig;
using RavenfieldVR.Runtime;
using UnityEngine;

namespace RavenfieldVR.UI
{
    /// <summary>
    /// Squad orders in VR (hold the squad button, let go to give the order). The game takes the spot, flag or soldier the
    /// camera looks at, and writes "GO TO" in the middle of the view. In VR you point: either hand held out straight (the
    /// arm reaching out, the hand pointing along it) aims the order, from the eye through the hand. With no hand held out
    /// straight, letting go gives no order (it is cancelled). What the order will be (GO TO, REGROUP, a flag's name,
    /// ATTACK ...) is written on the pointing hand, with an arrow over it toward the spot, flag or soldier, instead of in
    /// the middle of the view.
    /// </summary>
    public sealed class SquadPointing
    {
        /// <summary>A hand is held out straight: orders go where it points.</summary>
        internal static bool Pointing { get; private set; }

        /// <summary>The pointing line, from the eye through the hand.</summary>
        internal static Ray Line { get; private set; }

        /// <summary>The hand that points (null while none does).</summary>
        internal static VRHand PointingHand { get; private set; }

        /// <summary>
        /// The capture point the pointing line is on (within a few degrees), or null. An order given while it is set goes to
        /// that point, as the flat game's order to a flag (task 297).
        /// </summary>
        internal static SpawnPoint Objective { get; private set; }

        /// <summary>
        /// The enemy soldier the pointing line is on while the squad button is held, or null: letting go then gives the game's
        /// attack-target order for them (in the flat game the squad button with the aim held; user, 2026-10-24: "I cannot mark
        /// an enemy to tell my squad to attack him"). Picked as the game picks one: only an enemy that can see the player (in
        /// view, not behind a wall or a door), a soldier in a vehicle by the vehicle.
        /// </summary>
        internal static Actor Enemy { get; private set; }

        /// <summary>The vehicle point the game picked along the line (enter, leave, take over), or null: the game's, as it is for a flag out of the mod's reach.</summary>
        internal static SquadOrderPoint VehiclePoint()
        {
            SquadOrderPoint point = SquadPointUi.instance != null ? SquadPointUi.instance.aimPoint : null;
            return IsPointedVehicle(point) ? point : null;
        }

        /// <summary>A vehicle's point the line really is on: the game's pick reaches about 18 degrees, and a go-to beside a parked car turned into getting in it.</summary>
        internal static bool IsPointedVehicle(SquadOrderPoint point) =>
            point != null && (point.type == SquadOrderPoint.ObjectiveType.Enter || point.type == SquadOrderPoint.ObjectiveType.Exit || point.type == SquadOrderPoint.ObjectiveType.Commandeer)
            && Vector3.Angle(Line.direction, point.transform.position - Line.origin) < VehicleDegrees;

        private static string VehicleOrder(SquadOrderPoint point) =>
            point.type == SquadOrderPoint.ObjectiveType.Enter ? "ENTER" : point.type == SquadOrderPoint.ObjectiveType.Exit ? "GET OUT" : "TAKE IT";

        private const float VehicleDegrees = 8f;

        /// <summary>
        /// A go-to along the line has somewhere to go: the game's own test (<c>IssueSquadRaycastOrder</c>), which orders only when the
        /// ground is hit; water only moves the point to its surface. Open water alone read "GO TO", and the game gave no order, only
        /// its grunt and commit sound (squad scout, 2026-11-12).
        /// </summary>
        internal static bool LandsSomewhere() => Physics.Raycast(Line, 999999f, 1);

        /// <summary>How far off the pointing line an enemy may be (degrees), or the body's own half width there when wider.</summary>
        private const float EnemyDegrees = 4f;

        private const float Reach = 0.42f;      // eye to palm with the arm held out (a hand at the chest is about 0.3)
        private const float Straight = 32f;     // the hand points along the arm within this (degrees)

        private readonly VRRig _rig;
        private readonly GameObject _label;
        private readonly TextMesh _text;
        private readonly TextPlate _plate;
        private readonly GameObject _arrow;
        private readonly Material _arrowMaterial;
        private readonly SquadObjectiveMarkers _objectives;
        private float _alpha;

        public SquadPointing(VRRig rig)
        {
            _rig = rig;
            _objectives = new SquadObjectiveMarkers();
            Font font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            _label = new GameObject("RavenfieldVR Squad Order");
            Object.DontDestroyOnLoad(_label);
            _label.layer = 5;
            _text = _label.AddComponent<TextMesh>();
            _text.font = font;
            _text.fontSize = 64;
            _text.characterSize = 0.0011f;
            _text.anchor = TextAnchor.MiddleCenter;
            _text.alignment = TextAlignment.Center;
            var mr = _label.GetComponent<MeshRenderer>();
            mr.sharedMaterial = font.material;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _plate = new TextPlate(_text, font.material, 0.8f, 0.01f);
            _label.SetActive(false);

            _arrow = new GameObject("RavenfieldVR Squad Arrow") { layer = 5 };
            Object.DontDestroyOnLoad(_arrow);
            _arrow.AddComponent<MeshFilter>().sharedMesh = ArrowMesh();
            var ar = _arrow.AddComponent<MeshRenderer>();
            _arrowMaterial = new Material(Shader.Find("Sprites/Default")) { color = new Color(0.45f, 0.85f, 1f, 0.9f), renderQueue = 4000 };
            ar.sharedMaterial = _arrowMaterial;
            ar.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _arrow.SetActive(false);
        }

        /// <summary>Before the game reads the squad button (its player controller's update): which hand points, if any.</summary>
        internal static void Compute()
        {
            Pointing = false;
            PointingHand = null;
            Objective = null;
            Enemy = null;
            VRRig rig = VRRig.Instance;
            if (rig == null || !VRManager.Active)
            {
                return;
            }
            Transform head = rig.Head;
            float best = float.MinValue;
            foreach (VRHand h in rig.Hands)
            {
                if (!h.Tracked)
                {
                    continue;
                }
                Vector3 fromEye = h.PalmCentre - head.position;
                float reach = fromEye.magnitude;
                if (reach < Reach || Vector3.Angle(fromEye, head.forward) > 70f)
                {
                    continue; // not held out in front
                }
                float bend = Vector3.Angle(h.Pointer.Forward, fromEye);
                if (bend > Straight)
                {
                    continue; // held out, but not pointing along the arm
                }
                float score = reach - bend * 0.004f;
                if (score > best)
                {
                    best = score;
                    PointingHand = h;
                }
            }
            if (PointingHand != null)
            {
                Pointing = true;
                Line = new Ray(head.position, (PointingHand.PalmCentre - head.position).normalized);
                Objective = SquadObjectiveMarkers.Aimed(Line, head.position, out float flagAngle);
                // Only while an order is being made (the squad button held, let go this frame, or its screen still open).
                SquadPointUi ui = SquadPointUi.instance;
                if (VRInput.GetHeld(DigitalAction.Squad) || VRInput.GetReleased(DigitalAction.Squad) || ui != null && ui.canvas != null && ui.canvas.enabled)
                {
                    Actor enemy = AimedEnemy(Line, out float enemyAngle);
                    if (enemy != null && (Objective == null || enemyAngle < flagAngle))
                    {
                        Enemy = enemy;
                        Objective = null; // the nearer of the two to the line: the soldier
                    }
                }
            }
        }

        /// <summary>The enemy nearest the pointing line within its tolerance, as <see cref="Enemy"/> says, or null.</summary>
        private static Actor AimedEnemy(Ray line, out float bestAngle)
        {
            bestAngle = float.MaxValue;
            if (ActorManager.instance == null || GameManager.instance == null)
            {
                return null;
            }
            System.Collections.Generic.List<Actor> enemies = ActorManager.AliveActorsOnTeam(1 - GameManager.PlayerTeam());
            if (enemies == null)
            {
                return null;
            }
            Actor best = null;
            foreach (Actor a in enemies)
            {
                if (a == null || a.dead || !ActorManager.ActorCanSeePlayer(a))
                {
                    continue;
                }
                bool seated = a.IsSeated() && a.seat != null && a.seat.vehicle != null && a.seat.vehicle.targetLockPoint != null;
                Vector3 to = (seated ? a.seat.vehicle.targetLockPoint.position : a.CenterPosition()) - line.origin;
                float distance = to.magnitude;
                if (distance < 0.5f)
                {
                    continue;
                }
                float angle = Vector3.Angle(line.direction, to);
                // A soldier near you counts anywhere on the body, a vehicle on its hull.
                float allowed = Mathf.Max(EnemyDegrees, Mathf.Atan2(seated ? 2f : 0.45f, distance) * Mathf.Rad2Deg);
                if (angle < allowed && angle < bestAngle)
                {
                    bestAngle = angle;
                    best = a;
                }
            }
            return best;
        }

        // ---- the game's cameras, turned along the pointing line for the squad order's own code -----------------------

        private static readonly Transform[] Turned = new Transform[2];
        private static readonly RigidPose[] TurnedFrom = new RigidPose[2];
        private static int _turned;

        /// <summary>The camera(s) the game's squad code looks through, along the pointing line until <see cref="Restore"/>.</summary>
        internal static void Aim()
        {
            if (!Pointing || _turned > 0)
            {
                return;
            }
            Quaternion along = Quaternion.LookRotation(Line.direction, Vector3.up);
            Camera main = null, active = null;
            try
            {
                main = GameManager.GetMainCamera();
                active = LocalPlayer.controller != null ? LocalPlayer.controller.GetActiveCamera() : null;
            }
            catch (System.Exception e) when (e is System.NullReferenceException || e is MissingReferenceException)
            {
                // the player or its cameras are going with the scene
            }
            for (int k = 0; k < 2; k++)
            {
                Camera c = k == 0 ? main : active; // (no array a frame)
                if (c == null || _turned >= Turned.Length || (_turned == 1 && Turned[0] == c.transform))
                {
                    continue;
                }
                Turned[_turned] = c.transform;
                TurnedFrom[_turned] = RigidPose.Of(c.transform);
                _turned++;
                c.transform.SetPositionAndRotation(Line.origin, along);
            }
        }

        internal static void Restore()
        {
            for (int i = 0; i < _turned; i++)
            {
                if (Turned[i] != null)
                {
                    TurnedFrom[i].ApplyTo(Turned[i]);
                }
                Turned[i] = null;
            }
            _turned = 0;
        }

        // ---- on the hand ------------------------------------------------------------------------------------

        /// <summary>The order's label and arrow on the pointing hand while the squad button is held.</summary>
        public void BeforeRender()
        {
            SquadPointUi ui = SquadPointUi.instance;
            bool open = VRManager.Active && ui != null && ui.canvas != null && ui.canvas.enabled && LocalPlayer.actor != null && !LocalPlayer.actor.dead;
            // The capture points' flags: while the order screen is open and a hand points, and gone the moment the pointing ends.
            _objectives.BeforeRender(open && Pointing, _rig.Head.position, Objective);
            _alpha = Mathf.MoveTowards(_alpha, open ? 1f : 0f, Time.unscaledDeltaTime * 8f);
            bool show = _alpha > 0.01f;
            if (_label.activeSelf != show)
            {
                _label.SetActive(show);
                _refitFrames = 2; // shown again: refit once it is laid out
            }
            if (!show)
            {
                if (_arrow.activeSelf)
                {
                    _arrow.SetActive(false);
                }
                return;
            }
            VRHand hand = PointingHand ?? _rig.Off;
            Transform head = _rig.Head;
            Vector3 palm = hand.PalmCentre;
            // Just above the hand, facing the eye.
            Vector3 at = palm + Vector3.up * 0.075f;
            _label.transform.SetPositionAndRotation(at, Quaternion.LookRotation(at - head.position, Vector3.up));
            // What the order will be, from the pointing itself: the game's own text can name a flag that is hidden in VR. And what it will not be: with
            // nobody to tell, or the sky on the line, it read GO TO, and the order's sound and grunt went out for nothing.
            SquadOrderPoint vehicle = VehiclePoint();
            string order = !Pointing ? "POINT WITH A HAND" : Enemy != null ? EnemyOrder(Enemy)
                : !SquadHandSigns.HasSquadmates() ? (SpotsShared ? "SPOT" : "NO SQUAD")
                : Objective != null ? ObjectiveOrder(Objective)
                : vehicle != null ? VehicleOrder(vehicle)
                : Line.direction.y < -0.7f ? "REGROUP"
                : LandsSomewhere() ? "GO TO" : "NOTHING THERE";
            // The label is refitted only when its text changed or it came back (and the frame after, once laid out), and its
            // colours are set only when they change: all of it ran every frame the squad button was held (and TextMesh.text's
            // getter builds a new string on every read, so the last text set is remembered).
            if (!string.Equals(order, _shownOrder))
            {
                _text.text = order;
                _shownOrder = order;
                _refitFrames = 2;
            }
            Color col = Pointing ? new Color(0.88f, 0.95f, 1f, _alpha) : new Color(0.45f, 0.7f, 1f, _alpha);
            if (col.r != _textColor.r || col.g != _textColor.g || col.b != _textColor.b || col.a != _textColor.a)
            {
                _textColor = col;
                _text.color = col;
            }
            if (_refitFrames > 0)
            {
                _refitFrames--;
                _plate.Fit();
            }
            if (_alpha != _plateAlpha)
            {
                _plateAlpha = _alpha;
                _plate.SetAlpha(_alpha);
            }

            // (Not with the order screen gone: the label still fades out for a moment, and Target read the destroyed screen.)
            Vector3? target = Pointing && ui != null ? Target(ui) : null;
            bool arrow = target.HasValue;
            if (_arrow.activeSelf != arrow)
            {
                _arrow.SetActive(arrow);
            }
            if (arrow)
            {
                Vector3 to = target.Value - palm;
                Vector3 start = palm + Vector3.up * 0.035f;
                _arrow.transform.SetPositionAndRotation(start, Quaternion.LookRotation(to.sqrMagnitude > 1e-6f ? to : Line.direction, Vector3.up));
                if (_alpha != _arrowAlpha)
                {
                    _arrowAlpha = _alpha;
                    Color c = _arrowMaterial.color;
                    c.a = 0.9f * _alpha;
                    _arrowMaterial.color = c;
                }
            }
        }

        private string _shownOrder;
        private int _refitFrames = 2;
        private Color _textColor = new Color(-1f, 0f, 0f, 0f);
        private float _plateAlpha = -1f, _arrowAlpha = -1f;
        private SpawnPoint _orderSpawn;
        private bool _orderDefend;
        private string _orderText = "";
        private Actor _orderEnemy;
        private Vehicle _orderVehicle;
        private string _enemyText = "";

        /// <summary>"ATTACK SOLDIER", or "ATTACK" and the vehicle the enemy sits in: the game's own words, built when the target changes.</summary>
        private string EnemyOrder(Actor enemy)
        {
            Vehicle vehicle = enemy.IsSeated() && enemy.seat != null ? enemy.seat.vehicle : null;
            if (!ReferenceEquals(enemy, _orderEnemy) || !ReferenceEquals(vehicle, _orderVehicle))
            {
                _orderEnemy = enemy;
                _orderVehicle = vehicle;
                _enemyText = vehicle != null ? "ATTACK " + vehicle.name : "ATTACK SOLDIER";
            }
            return _enemyText;
        }

        /// <summary>"ATTACK A" or "DEFEND A" for a capture point: the game's own words for its flag, built again only when the point changes.</summary>
        private string ObjectiveOrder(SpawnPoint spawn)
        {
            bool defend = spawn.squadOrderPoint.type == SquadOrderPoint.ObjectiveType.Defend;
            if (!ReferenceEquals(spawn, _orderSpawn) || defend != _orderDefend)
            {
                _orderSpawn = spawn;
                _orderDefend = defend;
                _orderText = (defend ? "DEFEND " : "ATTACK ") + spawn.shortName;
            }
            return _orderText;
        }

        /// <summary>In a shared match with someone to show it to: a point made with no squad marks the spot for the others (NetSpots).</summary>
        internal static bool SpotsShared => Net.Multiplayer.Active && Net.Multiplayer.Session != null && Net.Multiplayer.Session.PlayerCount > 1;

        /// <summary>Where the pointing lands, for the others' mark (null: nowhere: the sky, or no line).</summary>
        internal static Vector3? SpotPoint()
        {
            if (!Pointing || SquadPointUi.instance == null || Enemy == null && Objective == null && !LandsSomewhere())
            {
                return null;
            }
            return Target(SquadPointUi.instance);
        }

        /// <summary>Where the order would go: the capture point the line is on, the soldier picked, or the ground the line meets.</summary>
        private static Vector3? Target(SquadPointUi ui)
        {
            if (Enemy != null)
            {
                return Enemy.IsSeated() && Enemy.seat != null && Enemy.seat.vehicle != null && Enemy.seat.vehicle.targetLockPoint != null
                    ? Enemy.seat.vehicle.targetLockPoint.position : Enemy.CenterPosition();
            }
            if (Objective != null)
            {
                return Objective.transform.position;
            }
            // The soldier the game's target order locked: only while that order is being made (its selector shows), and not a dead
            // one. The game never clears it, so the arrow pointed at the soldier of an older target order while a ground order went
            // where the hand pointed (squad hunt, 2026-10-21).
            if (ui.aimActor != null && !ui.aimActor.dead && ui.targetSelector != null && ui.targetSelector.activeSelf)
            {
                return ui.aimActor.CenterPosition();
            }
            if (Physics.Raycast(Line, out RaycastHit hit, 999999f, 1))
            {
                // (At the water's surface when that is nearer, as the order goes there: the arrow pointed at the seabed.)
                return WaterLevel.Raycast(Line, out RaycastHit water) && water.distance < hit.distance ? water.point : hit.point;
            }
            return Line.origin + Line.direction * 50f;
        }

        /// <summary>A slim arrow along +Z, 6 cm long, its tail at the origin.</summary>
        private static Mesh ArrowMesh()
        {
            const float L = 0.06f, H = 0.022f, W = 0.009f, S = 0.004f;
            var v = new[]
            {
                // shaft
                new Vector3(-S, 0f, 0f), new Vector3(S, 0f, 0f), new Vector3(S, 0f, L - H), new Vector3(-S, 0f, L - H),
                // head
                new Vector3(-W, 0f, L - H), new Vector3(W, 0f, L - H), new Vector3(0f, 0f, L),
                // the same, upright (a cross section, readable from any side)
                new Vector3(0f, -S, 0f), new Vector3(0f, S, 0f), new Vector3(0f, S, L - H), new Vector3(0f, -S, L - H),
                new Vector3(0f, -W, L - H), new Vector3(0f, W, L - H), new Vector3(0f, 0f, L)
            };
            int[] t =
            {
                0, 2, 1, 0, 3, 2, 4, 6, 5,
                0, 1, 2, 0, 2, 3, 4, 5, 6,
                7, 9, 8, 7, 10, 9, 11, 13, 12,
                7, 8, 9, 7, 9, 10, 11, 12, 13
            };
            var m = new Mesh { name = "RavenfieldVR Squad Arrow" };
            m.vertices = v;
            m.triangles = t;
            m.RecalculateBounds();
            return m;
        }

        /// <summary>A new scene: its capture points' flags are gone with it, and no hand sign is in progress.</summary>
        internal void OnSceneChanged()
        {
            _objectives.Clear();
            SquadHandSigns.Reset();
            Objective = null;
        }

        public void Destroy()
        {
            Restore();
            _objectives.Destroy();
            if (_label != null)
            {
                Object.Destroy(_label);
            }
            if (_arrow != null)
            {
                // Its mesh too: made for it (ArrowMesh), not an asset, it outlived the arrow on every VR restart (bug hunt, 2026-10-22).
                MeshFilter filter = _arrow.GetComponent<MeshFilter>();
                if (filter != null && filter.sharedMesh != null)
                {
                    Object.Destroy(filter.sharedMesh);
                }
                Object.Destroy(_arrow);
            }
            if (_arrowMaterial != null)
            {
                Object.Destroy(_arrowMaterial);
            }
            _plate.Destroy();
        }
    }

    /// <summary>Before the game's player controller reads the squad button: the pointing hand for this frame.</summary>
    [HarmonyPatch(typeof(FpsActorController), "Update")]
    internal static class SquadPointingComputePatch
    {
        private static void Prefix(FpsActorController __instance)
        {
            if (VRManager.Active && ReferenceEquals(__instance, LocalPlayer.controller))
            {
                try
                {
                    SquadPointing.Compute();
                }
                catch (System.Exception e)
                {
                    Log.ExceptionOnce("SquadPointing.Compute", e); // never in the way of the game's player update
                }
                try
                {
                    SquadHandSigns.Update(); // after the pointing: a sign's order uses the same capture point the pointing shows
                }
                catch (System.Exception e)
                {
                    Log.ExceptionOnce("SquadHandSigns.Update", e);
                }
            }
        }
    }

    /// <summary>
    /// The order's flag is the capture point the pointing line is on (task 297). The game's own pick of a flag (made in the order
    /// screen's LateUpdate, with the cameras turned along the pointing line) takes any flag within about 18 degrees of it. While
    /// a hand points that pick is narrowed to the capture point the line is within 5 degrees of (the one drawn bright), or to
    /// none, so the order goes where the label and the arrow say. With no hand pointing the game's pick is left alone.
    /// </summary>
    [HarmonyPatch(typeof(SquadPointUi), "GetAimingAtPoint")]
    internal static class SquadPointedFlagPatch
    {
        private static void Postfix(ref SquadOrderPoint __result)
        {
            if (!VRManager.Active || !SquadPointing.Pointing)
            {
                return;
            }
            // The game's own order for that flag (attack or defend it), exactly as it would give it for a flag in view. A vehicle's point is left as the game
            // picked it: narrowed to flags alone, a car could not be pointed at to send the squad in (it went to the ground behind it).
            __result = SquadPointing.Objective != null ? SquadPointing.Objective.squadOrderPoint
                : SquadPointing.IsPointedVehicle(__result) ? __result : null;
        }
    }

    /// <summary>
    /// The order is given along the pointing line; with no hand held out straight it is cancelled. Pointing down at your
    /// feet is the regroup order (the game's "look down").
    /// </summary>
    [HarmonyPatch(typeof(FpsActorController), "IssueSquadPointOrder")]
    internal static class SquadOrderByPointingPatch
    {
        private static bool Prefix(FpsActorController __instance)
        {
            if (!VRManager.Active)
            {
                return true;
            }
            try
            {
                // The spot for the other players of a shared match (invention 2, 2026-11-14): an order's point, or a point made alone.
                bool regroupLine = SquadPointing.Line.direction.y < -0.7f;
                Vector3? spot = SquadPointing.SpotsShared && !regroupLine ? SquadPointing.SpotPoint() : null;
                if (spot.HasValue)
                {
                    Net.NetSpots.Tell(spot.Value, SquadPointing.Enemy != null);
                }
                // (Alone with no enemy on the line there is no one to tell and nothing to mark: the order's sound and grunt for nothing,
                // as the hand signs already knew.)
                if (!SquadPointing.Pointing || SquadPointing.Enemy == null && !SquadHandSigns.HasSquadmates())
                {
                    __instance.CancelPointSquadOrder();
                    return false;
                }
                bool regroup = SquadPointing.Line.direction.y < -0.7f;
                // (At the sky with no flag, enemy or vehicle on the line: the game's ray finds nothing and only the order's sound goes out.)
                if (SquadPointing.Enemy == null && SquadPointing.Objective == null && SquadPointing.VehiclePoint() == null && !regroup && !SquadPointing.LandsSomewhere())
                {
                    __instance.CancelPointSquadOrder();
                    return false;
                }
                __instance.readyToIssueRegroupOrder = regroup;
                if (SquadPointing.Enemy != null && SquadPointUi.instance != null)
                {
                    // The game's attack-target order for the enemy pointed at: its squad attacks them (or, alone, the enemy is
                    // marked), as the squad button with the aim held does in the flat game.
                    __instance.pointOrderBaseType = SquadPointUi.SquadPointOrderBaseType.TargetOrder;
                    SquadPointUi.instance.aimActor = SquadPointing.Enemy;
                }
                SquadPointing.Aim();
            }
            catch (System.Exception e)
            {
                SquadPointing.Restore();
                Log.ExceptionOnce("FpsActorController.IssueSquadPointOrder (VR)", e);
            }
            return true;
        }

        private static void Postfix() => SquadPointing.Restore();
    }

    /// <summary>
    /// The game's squad order screen picks its flag or soldier along the pointing line, and draws nothing in the middle of
    /// the view in VR (the order is written on the hand).
    /// </summary>
    [HarmonyPatch(typeof(SquadPointUi), "LateUpdate")]
    internal static class SquadPointUiByPointingPatch
    {
        /// <summary>The order screen's canvas group, looked up once (not every frame).</summary>
        private static CanvasGroup _group;
        private static SquadPointUi _groupOwner;

        private static void Prefix(SquadPointUi __instance)
        {
            if (!VRManager.Active)
            {
                return;
            }
            // The cameras along the pointing line only while the order screen is open: the game's LateUpdate returns at once
            // when it is closed, and both cameras were turned and put back every frame a hand was held out (255.P5-5).
            if (__instance.canvas != null && __instance.canvas.enabled)
            {
                SquadPointing.Aim();
            }
            if (SquadPointing.Pointing)
            {
                // The default line follows the hand (the game set it from where the head looks): down at your feet is REGROUP.
                SquadPointUi.SetDefaultText(SquadPointing.Line.direction.y < -0.7f ? "REGROUP" : "GO TO");
            }
            if (__instance.canvas != null && __instance.canvas.enabled)
            {
                if (_group == null || _groupOwner != __instance)
                {
                    _groupOwner = __instance;
                    _group = __instance.GetComponent<CanvasGroup>() ?? __instance.gameObject.AddComponent<CanvasGroup>();
                }
                CanvasGroup g = _group;
                if (g.alpha > 0f)
                {
                    g.alpha = 0f;
                }
            }
        }

        private static void Postfix()
        {
            SquadPointing.Restore();
            if (VRManager.Active && SquadPointUi.instance != null && LocalPlayer.controller != null && SquadPointing.Pointing)
            {
                // While held: pointing down at your feet reads REGROUP, as looking down does in the flat game.
                LocalPlayer.controller.readyToIssueRegroupOrder = SquadPointing.Line.direction.y < -0.7f;
            }
        }
    }
}
