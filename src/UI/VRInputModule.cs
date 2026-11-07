using System.Collections.Generic;
using RavenfieldVR.Core;
using RavenfieldVR.Input;
using UnityEngine;
using UnityEngine.EventSystems;

namespace RavenfieldVR.UI
{
    /// <summary>
    /// Laser-pointer input for world-space Unity UI. Each frame the pointer ray is intersected with the visible
    /// canvases; a hidden event camera (every converted canvas uses it as worldCamera) aims at the hit point so
    /// the standard GraphicRaycasters work unchanged. While the trigger is held the camera freezes, turning the
    /// laser's sweep into real screen-space motion for sliders, scrollbars and drag-scrolling. Thumbsticks scroll.
    /// </summary>
    public sealed class VRInputModule : BaseInputModule
    {
        public Camera EventCamera;

        /// <summary>Set by the UI system each frame: whether the laser is live and where it comes from.</summary>
        public bool PointerActive;
        public Ray PointerRay;

        /// <summary>Self-test: what the laser is over now (the top-most thing the event system hit).</summary>
        internal static GameObject Hovered { get; private set; }
        public Hand PointerHand;
        /// <summary>The panels the laser can hit (a list, iterated every frame without allocating).</summary>
        public List<Canvas> Targets;

        public bool HasHit { get; private set; }
        public Vector3 HitPoint { get; private set; }
        public GameObject HoveredObject => _data?.pointerCurrentRaycast.gameObject;

        private PointerEventData _data;
        private readonly List<RaycastResult> _results = new List<RaycastResult>();
        private readonly List<Canvas> _nested = new List<Canvas>();
        private readonly List<Canvas> _candidates = new List<Canvas>();
        /// <summary>The panels <see cref="_candidates"/> was gathered from, and until when it stands.</summary>
        private readonly List<Canvas> _candidatesFrom = new List<Canvas>();
        private float _candidatesUntil = -1f;
        private bool _pressed;

        /// <summary>The laser's press is down (a button held under it).</summary>
        public bool PressActive => _pressed;
        private readonly bool[] _triggerDown = new bool[2];
        private readonly bool[] _triggerPressed = new bool[2];
        private readonly bool[] _triggerReleased = new bool[2];

        public override bool ShouldActivateModule() => base.ShouldActivateModule() && VRManager.Active;
        public override bool IsModuleSupported() => true;

        public override void ActivateModule()
        {
            base.ActivateModule();
            _data = new PointerEventData(eventSystem) { pointerId = -1 };
        }

        public override void DeactivateModule()
        {
            if (_data != null)
            {
                if (_pressed)
                {
                    // Switched off mid-press (the scene's event system replaced, VR off): no click, but the button gets its pointer
                    // up. Left pressed, it stayed drawn pressed, and the press carried into the next activation (bug hunt, 2026-10-22).
                    Release(null, cancel: true);
                }
                HandlePointerExitAndEnter(_data, null);
            }
            base.DeactivateModule();
        }

        private void UpdateTriggers()
        {
            for (int h = 0; h < 2; h++)
            {
                float v = VRInput.GetTrigger((Hand)h);
                bool was = _triggerDown[h];
                bool now = was ? v > 0.35f : v > 0.7f;
                _triggerDown[h] = now;
                _triggerPressed[h] = now && !was;
                _triggerReleased[h] = !now && was;
            }
        }

        public bool TriggerPressed(Hand h) => _triggerPressed[(int)h];

        /// <summary>When the laser was last used on a menu (moved over its items, clicked, scrolled); unscaled time.</summary>
        public static float LastUsed { get; private set; } = -100f;
        private GameObject _lastOver;

        public override void Process()
        {
            if (_data == null)
            {
                _data = new PointerEventData(eventSystem) { pointerId = -1 };
            }
            UpdateTriggers();
            if (!PointerActive || EventCamera == null)
            {
                HasHit = false;
                VRGameInput.PointerOverUI = false;
                if (_pressed)
                {
                    Release(null, cancel: true); // the pointer went away (tracking, menu closed): no click
                }
                HandlePointerExitAndEnter(_data, null);
                return;
            }

            HasHit = IntersectCanvases(PointerRay, out Vector3 hit);
            Vector3 target = HasHit ? hit : PointerRay.origin + PointerRay.direction * 3f;
            HitPoint = target;
            if (!_pressed)
            {
                EventCamera.transform.SetPositionAndRotation(PointerRay.origin, Quaternion.LookRotation(target - PointerRay.origin));
            }

            Vector2 screen = EventCamera.WorldToScreenPoint(target);
            _data.delta = screen - _data.position;
            _data.position = screen;
            _data.button = PointerEventData.InputButton.Left;

            _results.Clear();
            // The first hit on one of the canvases the laser targets: past the panel, the event camera's ray found any raycast
            // target of any canvas (a HUD element), which took the click and blocked the trigger (review, 2026-10-20); and the
            // results come sorted by sorting order first, so a HUD canvas sorted above the menu took it over the panel too (UI hunt,
            // 2026-10-20 night). Cast only when the laser is on a panel: its results were thrown away otherwise, after asking every
            // raycaster in the scene (the game's HUD too) every menu frame (UI hunt, 2026-10-23).
            RaycastResult first = default;
            if (HasHit)
            {
                eventSystem.RaycastAll(_data, _results);
                foreach (RaycastResult r in _results)
                {
                    if (r.gameObject != null && OnTarget(r))
                    {
                        first = r;
                        break;
                    }
                }
            }
            _data.pointerCurrentRaycast = first;
            _results.Clear();
            GameObject over = first.gameObject;
            VRGameInput.PointerOverUI = over != null;

            bool press = _triggerPressed[(int)PointerHand];
            bool release = _triggerReleased[(int)PointerHand];
            if (press || _pressed || over != _lastOver && over != null || over != null && Mathf.Abs(VRInput.TurnStick.y + VRInput.Move.y) >= 0.25f)
            {
                LastUsed = Time.unscaledTime; // pointing along the menu, clicking, scrolling
            }
            _lastOver = over;
            Hovered = over;
            HoverTick(over);
            if (press && over != null)
            {
                Press(over);
            }
            if (_pressed && (_triggerReleased[(int)_pressHand] || !_triggerDown[(int)_pressHand]))
            {
                Release(over);
            }

            HandlePointerExitAndEnter(_data, over);
            ProcessDrag();
            ProcessScroll(over);
        }

        private GameObject _hoveredTarget;
        private float _tickAt = -1f;

        /// <summary>
        /// A faint tick in the pointing hand when the laser moves onto something that can be clicked (a button, a toggle, a
        /// list row), at most twenty a second: targets are felt, not only seen.
        /// </summary>
        private void HoverTick(GameObject over)
        {
            GameObject target = over != null ? ExecuteEvents.GetEventHandler<IPointerClickHandler>(over) : null;
            if (target == _hoveredTarget)
            {
                return;
            }
            _hoveredTarget = target;
            if (target != null && !_pressed && Time.unscaledTime - _tickAt > 0.05f)
            {
                _tickAt = Time.unscaledTime;
                VRInput.Haptic(PointerHand, 0.006f, 180f, 0.12f);
                HoverTicks++;
            }
        }

        /// <summary>Self-test: hover ticks given.</summary>
        internal static int HoverTicks { get; private set; }

        /// <summary>Where a ray meets the visible panels right now (for drawing the laser from the current hand pose).</summary>
        public bool TryHit(Ray ray, out Vector3 hit) => IntersectCanvases(ray, out hit);

        /// <summary>
        /// The ray's intersection with the canvases: the nearest one that draws something there, else the nearest rectangle.
        /// Menus are stacked toward the player by sorting order (4 mm a step, CanvasManager.PlaceMenu), so a canvas above the
        /// menu that is clear where the laser points (a fader, a tooltip layer, a click blocker) stood centimetres in front of it,
        /// and the laser's ball stopped on nothing (user, 2026-10-20 night: "some sort of invisible wall"; task 323).
        /// </summary>
        private bool IntersectCanvases(Ray ray, out Vector3 hit)
        {
            hit = Vector3.zero;
            if (Targets == null)
            {
                return false;
            }
            RefreshCandidates();
            _rectHits.Clear();
            foreach (Canvas c in _candidates)
            {
                if (c == null || !c.isActiveAndEnabled)
                {
                    continue;
                }
                var rt = (RectTransform)c.transform;
                var plane = new UnityEngine.Plane(rt.forward, rt.position);
                if (!plane.Raycast(ray, out float dist) || dist > 12f || dist < 0.02f)
                {
                    continue;
                }
                Vector3 p = ray.GetPoint(dist);
                Vector2 local = rt.InverseTransformPoint(p);
                if (rt.rect.Contains(local))
                {
                    // Kept nearest first as they come (a canvas or three): List.Sort with a comparison made a comparer object on
                    // each call, twice a frame while the laser is on a menu.
                    int at = _rectHits.Count;
                    while (at > 0 && _rectHits[at - 1].Dist > dist)
                    {
                        at--;
                    }
                    _rectHits.Insert(at, (dist, c, p));
                }
            }
            if (_rectHits.Count == 0)
            {
                return false;
            }
            foreach ((float _, Canvas c, Vector3 p) in _rectHits)
            {
                if (ShowsAt(c, p))
                {
                    hit = p;
                    return true;
                }
            }
            hit = _rectHits[0].Point; // a gap between a menu's parts: on the nearest rectangle, as before
            return true;
        }

        private readonly List<(float Dist, Canvas Canvas, Vector3 Point)> _rectHits = new List<(float, Canvas, Vector3)>();

        /// <summary>Whether a raycast hit is on one of the targeted canvases (its root canvas is a target).</summary>
        private bool OnTarget(RaycastResult r)
        {
            Canvas c = r.module != null ? r.module.GetComponent<Canvas>() : null;
            Canvas root = c != null ? c.rootCanvas : null;
            return root != null && Targets != null && Targets.Contains(root);
        }

        /// <summary>Whether the canvas draws a part at this point: shown, not culled, not see-through (its groups' alpha included).</summary>
        private static bool ShowsAt(Canvas c, Vector3 world)
        {
            IList<UnityEngine.UI.Graphic> graphics = UnityEngine.UI.GraphicRegistry.GetGraphicsForCanvas(c);
            for (int i = 0; i < graphics.Count; i++)
            {
                UnityEngine.UI.Graphic g = graphics[i];
                if (g == null || !g.isActiveAndEnabled || g.canvasRenderer.cull || g.color.a * g.canvasRenderer.GetInheritedAlpha() < 0.02f)
                {
                    continue;
                }
                RectTransform r = g.rectTransform;
                if (r.rect.Contains((Vector2)r.InverseTransformPoint(world)))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// The canvases the laser can hit: the panels and the canvases nested in them (dropdown lists, popups), which can
        /// reach past their panel's rectangle. Finding the nested ones walks each panel's whole hierarchy; that was done twice
        /// a frame (input, then the drawn laser). Now ten times a second, and at once when the set of panels changes.
        /// </summary>
        private void RefreshCandidates()
        {
            bool same = Time.unscaledTime < _candidatesUntil && _candidatesFrom.Count == Targets.Count;
            for (int i = 0; same && i < Targets.Count; i++)
            {
                same = ReferenceEquals(_candidatesFrom[i], Targets[i]);
            }
            if (same)
            {
                return;
            }
            _candidatesUntil = Time.unscaledTime + 0.1f;
            _candidatesFrom.Clear();
            _candidatesFrom.AddRange(Targets);
            _candidates.Clear();
            foreach (Canvas root in Targets)
            {
                if (root == null || !root.isActiveAndEnabled)
                {
                    continue;
                }
                root.GetComponentsInChildren(false, _nested);
                _candidates.AddRange(_nested);
            }
            Walks++;
        }

        /// <summary>Self-test: how often the panels' hierarchies were walked for nested canvases.</summary>
        internal static int Walks { get; private set; }

        /// <summary>Where the laser pointed and when, at the trigger press (for a click that wobbles off its button).</summary>
        private Vector3 _pressDir;
        private float _pressTime;

        /// <summary>How far (degrees) the laser may wander during a click before it is a drag or the click is lost.</summary>
        private const float ClickWobble = 4f;

        /// <summary>The hand whose trigger made the current press (the laser may move to the other hand meanwhile).</summary>
        private Hand _pressHand;

        private void Press(GameObject over)
        {
            if (_pressed)
            {
                Release(null, cancel: true); // the other hand's trigger: the first press ends without a click
            }
            _pressHand = PointerHand;
            _pressed = true;
            _pressDir = PointerRay.direction;
            _pressTime = Time.unscaledTime;
            _data.eligibleForClick = true;
            _data.delta = Vector2.zero;
            _data.dragging = false;
            _data.useDragThreshold = true;
            _data.pressPosition = _data.position;
            _data.pointerPressRaycast = _data.pointerCurrentRaycast;

            GameObject selectHandler = ExecuteEvents.GetEventHandler<ISelectHandler>(over);
            if (selectHandler != eventSystem.currentSelectedGameObject)
            {
                eventSystem.SetSelectedGameObject(null, _data);
            }

            GameObject newPressed = ExecuteEvents.ExecuteHierarchy(over, _data, ExecuteEvents.pointerDownHandler)
                                    ?? ExecuteEvents.GetEventHandler<IPointerClickHandler>(over);
            float time = Time.unscaledTime;
            if (newPressed == _data.lastPress && time - _data.clickTime < 0.3f)
            {
                _data.clickCount++;
            }
            else
            {
                _data.clickCount = 1;
            }
            _data.clickTime = time;
            _data.pointerPress = newPressed;
            _data.rawPointerPress = over;
            _data.pointerDrag = ExecuteEvents.GetEventHandler<IDragHandler>(over);
            if (_data.pointerDrag != null)
            {
                ExecuteEvents.Execute(_data.pointerDrag, _data, ExecuteEvents.initializePotentialDrag);
            }
            VRInput.Haptic(PointerHand, 0.01f, 200f, 0.25f);
        }

        private void Release(GameObject over, bool cancel = false)
        {
            _pressed = false;
            ExecuteEvents.Execute(_data.pointerPress, _data, ExecuteEvents.pointerUpHandler);
            if (cancel)
            {
                over = null;
                _data.eligibleForClick = false;
            }
            GameObject clickHandler = ExecuteEvents.GetEventHandler<IPointerClickHandler>(over);
            // Pulling a trigger moves the controller: a laser that slipped just off the button it pressed still clicks it
            // (a short, steady click), as a finger lifting off a touch screen would. Only one of the pause menu's buttons
            // took clicks in the headset. Off onto nothing: on another button (4 degrees is one button's width at the panel's distance) the
            // dot sat on High and Medium was applied, render scale and all, so that press is let go with no click (UI scout, 2026-11-07).
            bool steady = clickHandler == null && Vector3.Angle(_pressDir, PointerRay.direction) < ClickWobble && Time.unscaledTime - _pressTime < 0.8f;
            if (_data.pointerPress != null && _data.eligibleForClick && (_data.pointerPress == clickHandler || steady && !_data.dragging))
            {
                ExecuteEvents.Execute(_data.pointerPress, _data, ExecuteEvents.pointerClickHandler);
            }
            else if (_data.pointerDrag != null && _data.dragging && over != null)
            {
                ExecuteEvents.ExecuteHierarchy(over, _data, ExecuteEvents.dropHandler);
            }
            _data.eligibleForClick = false;
            _data.pointerPress = null;
            _data.rawPointerPress = null;
            if (_data.pointerDrag != null && _data.dragging)
            {
                ExecuteEvents.Execute(_data.pointerDrag, _data, ExecuteEvents.endDragHandler);
            }
            _data.dragging = false;
            _data.pointerDrag = null;
        }

        private void ProcessDrag()
        {
            if (!_pressed || _data.pointerDrag == null)
            {
                return;
            }
            if (!_data.dragging)
            {
                // A drag starts once the laser has really moved (3 degrees), not at the trigger pull's wobble: at a few pixels
                // a button in a scrolling list lost its click to a drag.
                float threshold = Mathf.Max(eventSystem.pixelDragThreshold, Screen.height * 0.012f);
                bool moved = Vector3.Angle(_pressDir, PointerRay.direction) >= 3f;
                if (moved && (_data.pressPosition - _data.position).sqrMagnitude >= threshold * threshold)
                {
                    ExecuteEvents.Execute(_data.pointerDrag, _data, ExecuteEvents.beginDragHandler);
                    _data.dragging = true;
                }
            }
            if (_data.dragging)
            {
                if (_data.pointerPress != _data.pointerDrag)
                {
                    ExecuteEvents.Execute(_data.pointerPress, _data, ExecuteEvents.pointerUpHandler);
                    _data.eligibleForClick = false;
                    _data.pointerPress = null;
                    _data.rawPointerPress = null;
                }
                ExecuteEvents.Execute(_data.pointerDrag, _data, ExecuteEvents.dragHandler);
            }
        }

        private void ProcessScroll(GameObject over)
        {
            if (over == null)
            {
                return;
            }
            float y = VRInput.TurnStick.y + VRInput.Move.y;
            if (Mathf.Abs(y) < 0.25f)
            {
                return;
            }
            _data.scrollDelta = new Vector2(0f, y * 900f * Time.unscaledDeltaTime);
            GameObject scrollHandler = ExecuteEvents.GetEventHandler<IScrollHandler>(over);
            ExecuteEvents.ExecuteHierarchy(scrollHandler ?? over, _data, ExecuteEvents.scrollHandler);
            _data.scrollDelta = Vector2.zero;
        }
    }
}
