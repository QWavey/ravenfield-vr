using System;
using System.Globalization;
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

        /// <summary>When the selected field may be tried again (see <see cref="Update"/>).</summary>
        private float _retryAt;

        /// <summary>When the keyboard was last shown (Time.unscaledTime).</summary>
        private float _openedAt;

        public void Update()
        {
            // Its field gone (the menu closed, a map loaded): the keyboard goes too. It stayed in front until dismissed by hand, and
            // no other field could open one meanwhile (UI hunt, 2026-10-20 night).
            if (_open && (_target == null || !_target.activeInHierarchy))
            {
                OpenVR.Overlay?.HideKeyboard();
                _open = false;
                _target = null;
                _retryAt = 0f;
            }
            EventSystem es = EventSystem.current;
            GameObject selected = es != null ? es.currentSelectedGameObject : null;
            // The laser clicked something that is no text field (a button, the panel): the player has gone on, and the keyboard goes
            // too. It stayed in front of him until Done or Cancel, and Done then wrote into the field he had left (scout, 2026-11-11).
            // (Not in its first half second: the click that selected the field is still being let go.)
            if (_open && Time.unscaledTime - _openedAt > 0.5f && (selected == null || selected != _target
                && selected.GetComponent<InputField>() == null && selected.GetComponent<TMP_InputField>() == null))
            {
                OpenVR.Overlay?.HideKeyboard();
                _open = false;
                _target = null;
                _retryAt = 0f;
                return;
            }
            // Another text field selected while the keyboard types for one: the selection goes back to that one, where Done writes
            // the text (it wrote into the first field while the second showed selected, and the second's text was lost; round 9).
            // The other field gets its own keyboard once this one closes. (Not hidden and reopened here: SteamVR's closed event for
            // the old keyboard would arrive after the new one opened.)
            if (_open && selected != null && _target != null && selected != _target
                && (selected.GetComponent<InputField>() != null || selected.GetComponent<TMP_InputField>() != null))
            {
                es.SetSelectedGameObject(_target);
                return;
            }
            // The same field again only after a retry time: a field not yet editable, or a keyboard SteamVR could not show,
            // stayed latched and never got one (a shown keyboard latches for good, so closing it does not reopen it).
            if (_open || selected == null || selected == _target && Time.unscaledTime < _retryAt)
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
                _retryAt = Time.unscaledTime + 0.5f;
                return;
            }
            _target = selected;
            _retryAt = Time.unscaledTime + 1f;
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
            if (_open)
            {
                _retryAt = float.MaxValue;
                _openedAt = Time.unscaledTime;
            }
            else
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
            if (OpenVR.Overlay == null)
            {
                Deselect(); // (no overlay to read: the field was emptied with the "" read; keyboard scout)
                return;
            }
            var sb = new StringBuilder(1024);
            OpenVR.Overlay.GetKeyboardText(sb, (uint)sb.Capacity);
            string text = sb.ToString();
            if (_target != null)
            {
                var field = _target.GetComponent<InputField>();
                if (field != null)
                {
                    string valid = Filter(text, field.characterValidation == InputField.CharacterValidation.Integer,
                        field.characterValidation == InputField.CharacterValidation.Decimal,
                        field.characterValidation == InputField.CharacterValidation.Alphanumeric, field.characterLimit);
                    if (valid != null)
                    {
                        field.text = valid;
                        field.onEndEdit?.Invoke(valid);
                    }
                }
                var tmp = _target.GetComponent<TMP_InputField>();
                if (tmp != null)
                {
                    string valid = Filter(text, tmp.characterValidation == TMP_InputField.CharacterValidation.Integer || tmp.characterValidation == TMP_InputField.CharacterValidation.Digit,
                        tmp.characterValidation == TMP_InputField.CharacterValidation.Decimal,
                        tmp.characterValidation == TMP_InputField.CharacterValidation.Alphanumeric, tmp.characterLimit,
                        signed: tmp.characterValidation != TMP_InputField.CharacterValidation.Digit);
                    if (valid != null)
                    {
                        tmp.text = valid;
                        tmp.onEndEdit?.Invoke(valid);
                        tmp.onSubmit?.Invoke(valid);
                    }
                }
            }
            Deselect();
        }

        /// <summary>
        /// The typed text as the field itself would have taken it (digits only in a number field, and so on): the SteamVR
        /// keyboard types anything, and the game's own handler threw on a bot count that was not a number. A number that
        /// does not fit an int is refused as well (int.Parse throws on it too). Null: nothing usable, the field keeps what
        /// it had. <paramref name="signed"/>: a minus sign is allowed (not on a digits-only field).
        /// </summary>
        private static string Filter(string text, bool integer, bool decimalNumber, bool alphanumeric, int limit, bool signed = true)
        {
            if (text == null)
            {
                return null;
            }
            var sb = new StringBuilder(text.Length);
            bool point = false;
            foreach (char ch in text.Trim())
            {
                if (ch >= '0' && ch <= '9') // not char.IsDigit: Arabic-Indic or fullwidth digits do not parse as numbers
                {
                    sb.Append(ch);
                }
                else if (signed && (integer || decimalNumber) && ch == '-' && sb.Length == 0)
                {
                    sb.Append(ch);
                }
                else if (decimalNumber && (ch == '.' || ch == ',') && !point)
                {
                    point = true;
                    sb.Append('.');
                }
                else if (!integer && !decimalNumber && (!alphanumeric || char.IsLetter(ch)))
                {
                    sb.Append(ch);
                }
            }
            string result = sb.ToString();
            if (limit > 0 && result.Length > limit)
            {
                result = result.Substring(0, limit);
            }
            if ((integer || decimalNumber) && (result.Length == 0 || result == "-" || result == "." || result == "-."))
            {
                return null; // no number in it
            }
            if (integer && !int.TryParse(result, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _))
            {
                return null; // more digits than an int holds (Tasks 213: the game's int.Parse would throw on it)
            }
            return result;
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
