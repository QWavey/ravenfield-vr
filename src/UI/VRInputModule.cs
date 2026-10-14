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
        public Hand PointerHand;
        public IEnumerable<Canvas> Targets;

        public bool HasHit { get; private set; }
        public Vector3 HitPoint { get; private set; }
        public GameObject HoveredObject => _data?.pointerCurrentRaycast.gameObject;

        private PointerEventData _data;
        private readonly List<RaycastResult> _results = new List<RaycastResult>();
        private readonly List<Canvas> _nested = new List<Canvas>();
        private readonly List<Canvas> _candidates = new List<Canvas>();
        private bool _pressed;
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
                    Release(null);
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
            eventSystem.RaycastAll(_data, _results);
            RaycastResult first = FindFirstRaycast(_results);
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
            if (press && over != null)
            {
                Press(over);
            }
            if (_pressed && (release || !_triggerDown[(int)PointerHand]))
            {
                Release(over);
            }

            HandlePointerExitAndEnter(_data, over);
            ProcessDrag();
            ProcessScroll(over);
        }

        /// <summary>Where a ray meets the visible panels right now (for drawing the laser from the current hand pose).</summary>
        public bool TryHit(Ray ray, out Vector3 hit) => IntersectCanvases(ray, out hit);

        /// <summary>Nearest intersection of the ray with any visible canvas rectangle.</summary>
        private bool IntersectCanvases(Ray ray, out Vector3 hit)
        {
            hit = Vector3.zero;
            float best = 12f;
            bool any = false;
            if (Targets == null)
            {
                return false;
            }
            _candidates.Clear();
            foreach (Canvas root in Targets)
            {
                if (root == null || !root.isActiveAndEnabled)
                {
                    continue;
                }
                // Nested canvases (dropdown lists, popups) can extend past their root's rectangle.
                root.GetComponentsInChildren(false, _nested);
                _candidates.AddRange(_nested);
            }
            foreach (Canvas c in _candidates)
            {
                if (c == null || !c.isActiveAndEnabled)
                {
                    continue;
                }
                var rt = (RectTransform)c.transform;
                var plane = new UnityEngine.Plane(rt.forward, rt.position);
                if (!plane.Raycast(ray, out float dist) || dist > best || dist < 0.02f)
                {
                    continue;
                }
                Vector3 p = ray.GetPoint(dist);
                Vector2 local = rt.InverseTransformPoint(p);
                if (rt.rect.Contains(local))
                {
                    best = dist;
                    hit = p;
                    any = true;
                }
            }
            return any;
        }

        private void Press(GameObject over)
        {
            _pressed = true;
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

        private void Release(GameObject over)
        {
            _pressed = false;
            ExecuteEvents.Execute(_data.pointerPress, _data, ExecuteEvents.pointerUpHandler);
            GameObject clickHandler = ExecuteEvents.GetEventHandler<IPointerClickHandler>(over);
            if (_data.pointerPress != null && _data.pointerPress == clickHandler && _data.eligibleForClick)
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
                float threshold = Mathf.Max(eventSystem.pixelDragThreshold, Screen.height * 0.012f);
                if ((_data.pressPosition - _data.position).sqrMagnitude >= threshold * threshold)
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
