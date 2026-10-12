using System;
using System.Text;
using RavenfieldVR.Core;
using RavenfieldVR.Runtime;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using Valve.VR;

namespace RavenfieldVR.UI
{
    /// <summary>
    /// When a text field gets selected, opens SteamVR's overlay keyboard and writes the typed text back
    /// (Unity UI InputField and TextMeshPro TMP_InputField).
    /// </summary>
    public sealed class VRKeyboard : IDisposable
    {
        private GameObject _target;
        private bool _open;
        private const ulong UserValue = 0x52465652; // "RFVR"

        public VRKeyboard()
        {
            OpenVRRuntime.EventReceived += OnEvent;
        }

        public void Update()
        {
            EventSystem es = EventSystem.current;
            GameObject selected = es != null ? es.currentSelectedGameObject : null;
            if (_open || selected == null || selected == _target)
            {
                if (selected == null && !_open)
                {
                    _target = null;
                }
                return;
            }
            var field = selected.GetComponent<InputField>();
            var tmp = selected.GetComponent<TMP_InputField>();
            if ((field == null || !field.interactable) && (tmp == null || !tmp.interactable))
            {
                _target = selected;
                return;
            }
            _target = selected;
            string existing = field != null ? field.text : tmp.text;
            bool multiLine = field != null ? field.multiLine : tmp.multiLine;
            int limit = field != null ? field.characterLimit : tmp.characterLimit;
            string description = PlaceholderOf(field, tmp);
            CVROverlay overlay = OpenVR.Overlay;
            if (overlay == null)
            {
                return;
            }
            EVROverlayError err = overlay.ShowKeyboard(
                (int)EGamepadTextInputMode.k_EGamepadTextInputModeNormal,
                multiLine ? (int)EGamepadTextInputLineMode.k_EGamepadTextInputLineModeMultipleLines : (int)EGamepadTextInputLineMode.k_EGamepadTextInputLineModeSingleLine,
                0u, description, (uint)(limit > 0 ? limit : 256), existing ?? "", UserValue);
            _open = err == EVROverlayError.None;
            if (!_open)
            {
                Log.WarnOnce("kbd:" + err, "SteamVR keyboard unavailable: " + err);
            }
        }

        private static string PlaceholderOf(InputField field, TMP_InputField tmp)
        {
            Graphic placeholder = field != null ? field.placeholder : tmp != null ? tmp.placeholder : null;
            if (placeholder is Text t && !string.IsNullOrEmpty(t.text))
            {
                return t.text;
            }
            if (placeholder is TMP_Text tt && !string.IsNullOrEmpty(tt.text))
            {
                return tt.text;
            }
            return "Ravenfield";
        }

        private void OnEvent(VREvent_t e)
        {
            switch ((EVREventType)e.eventType)
            {
                case EVREventType.VREvent_KeyboardDone:
                    if (_open)
                    {
                        Commit();
                    }
                    break;
                case EVREventType.VREvent_KeyboardClosed:
                    if (_open)
                    {
                        _open = false;
                        Deselect();
                    }
                    break;
            }
        }

        private void Commit()
        {
            _open = false;
            var sb = new StringBuilder(1024);
            OpenVR.Overlay?.GetKeyboardText(sb, (uint)sb.Capacity);
            string text = sb.ToString();
            if (_target != null)
            {
                var field = _target.GetComponent<InputField>();
                if (field != null)
                {
                    field.text = text;
                    field.onEndEdit?.Invoke(text);
                }
                var tmp = _target.GetComponent<TMP_InputField>();
                if (tmp != null)
                {
                    tmp.text = text;
                    tmp.onEndEdit?.Invoke(text);
                    tmp.onSubmit?.Invoke(text);
                }
            }
            Deselect();
        }

        private void Deselect()
        {
            EventSystem es = EventSystem.current;
            if (es != null && es.currentSelectedGameObject == _target)
            {
                es.SetSelectedGameObject(null);
            }
            _target = null;
        }

        public void Dispose()
        {
            OpenVRRuntime.EventReceived -= OnEvent;
            if (_open)
            {
                OpenVR.Overlay?.HideKeyboard();
            }
        }
    }
}
