using System;
using System.Collections.Generic;
using RavenfieldVR.Core;
using UnityEngine;

namespace RavenfieldVR.Local
{
    /// <summary>
    /// A splitscreen copy's own menu on its pause key (the user, 2026-11-14: "give the other players (except the player 1 because he got
    /// all options) some options when pressed ESC for their own, they dont pause the game tho, only player 1 does"). Worked by keys (the
    /// copy's window has no pointer): up and down by the game's forward and back keys (W, S, the stick), changed by its left and right keys
    /// (A, D), chosen by fire, jump or Enter, closed by the pause key again. The game goes on meanwhile; the soldier stands.
    /// <list type="bullet">
    /// <item>Resume; switch team; respawn;</item>
    /// <item>the weapons for the next spawn (primary, secondary, gear), from the game's own lists;</item>
    /// <item>mouse sensitivity, invert up and down;</item>
    /// <item>leave (this copy closes; the others play on).</item>
    /// </list>
    /// </summary>
    internal static class SplitCopyMenu
    {
        public static bool Open;
        private static int _row;
        private static bool _upWas, _downWas, _leftWas, _rightWas, _pickWas, _pauseWas;
        private static float _repeatAt;
        private static Action<string> _note;
        private static GUIStyle _title, _item, _picked, _hint;

        private static readonly string[] Rows = { "Resume", "Switch team", "Respawn", "Primary", "Secondary", "Gear", "Mouse sensitivity", "Invert up and down", "Leave this game" };

        public static void Hook(Action<string> note)
        {
            _note = note;
            FlatPlayerInput.AfterCompute = Filter;
            LoadOwn();
        }

        /// <summary>
        /// This player's own mouse settings, kept in a file of their own (the copies share player 1's game settings, so the game's own
        /// store would have changed his): the same sensitivity and invert after a rejoin and in the next splitscreen game.
        /// </summary>
        private static string OwnFile => System.IO.Path.Combine(BepInEx.Paths.BepInExRootPath, $"RavenfieldVR-split-player{SplitGames.ClientIndex + 1}.cfg");

        private static void LoadOwn()
        {
            try
            {
                if (!System.IO.File.Exists(OwnFile))
                {
                    return;
                }
                foreach (string line in System.IO.File.ReadAllLines(OwnFile))
                {
                    string[] kv = line.Split('=');
                    if (kv.Length != 2)
                    {
                        continue;
                    }
                    if (kv[0] == "mouse" && float.TryParse(kv[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float m) && !float.IsNaN(m))
                    {
                        FlatPlayerInput.MouseAxisPerCount = Mathf.Clamp(m, 0.02f, 0.5f);
                    }
                    else if (kv[0] == "invert")
                    {
                        FlatPlayerInput.InvertY = kv[1] == "1";
                    }
                }
                _note?.Invoke($"Splitscreen: this player's own mouse settings: {FlatPlayerInput.MouseAxisPerCount / 0.1f:0.00}x, invert {(FlatPlayerInput.InvertY ? "on" : "off")}.");
            }
            catch (Exception e)
            {
                _note?.Invoke("Splitscreen: this player's mouse settings could not be read: " + e.Message);
            }
        }

        private static void SaveOwn()
        {
            try
            {
                System.IO.File.WriteAllText(OwnFile, "mouse=" + FlatPlayerInput.MouseAxisPerCount.ToString("R", System.Globalization.CultureInfo.InvariantCulture)
                                                     + "\ninvert=" + (FlatPlayerInput.InvertY ? "1" : "0") + "\n");
            }
            catch (Exception)
            {
            }
        }

        /// <summary>After the frame's binds: the pause key opens and closes the menu (never the game's pause); the open menu takes the keys.</summary>
        private static void Filter()
        {
            bool pause = FlatPlayerInput.IsHeld(SteelInput.KeyBinds.TogglePauseMenu) || FlatPlayerInput.RawKey(0x1B); // (Esc: the game reads it itself)
            FlatPlayerInput.Suppress(SteelInput.KeyBinds.TogglePauseMenu); // (only player 1 pauses the game)
            if (pause && !_pauseWas)
            {
                Open = !Open;
                _row = 0;
                _upWas = _downWas = _leftWas = _rightWas = _pickWas = true; // (the keys held as it opens do nothing yet)
            }
            _pauseWas = pause;
            if (!Open)
            {
                return;
            }
            float v = FlatPlayerInput.AxisOf(SteelInput.KeyBinds.Vertical), h = FlatPlayerInput.AxisOf(SteelInput.KeyBinds.Horizontal);
            bool up = v > 0.5f, down = v < -0.5f, left = h > 0.5f, right = h < -0.5f; // (the game's sideways bind is positive to the left)
            bool pick = FlatPlayerInput.IsHeld(SteelInput.KeyBinds.Fire) || FlatPlayerInput.IsHeld(SteelInput.KeyBinds.Jump)
                        || FlatPlayerInput.IsHeld(SteelInput.KeyBinds.OpenLoadout);
            FlatPlayerInput.Suppress(); // (the soldier stands while the menu is open)
            bool repeat = Time.unscaledTime >= _repeatAt;
            if (up && (!_upWas || repeat))
            {
                _row = (_row + Rows.Length - 1) % Rows.Length;
                _repeatAt = Time.unscaledTime + (_upWas ? 0.12f : 0.4f);
            }
            if (down && (!_downWas || repeat))
            {
                _row = (_row + 1) % Rows.Length;
                _repeatAt = Time.unscaledTime + (_downWas ? 0.12f : 0.4f);
            }
            if (left && (!_leftWas || repeat))
            {
                Change(-1);
                _repeatAt = Time.unscaledTime + (_leftWas ? 0.12f : 0.4f);
            }
            if (right && (!_rightWas || repeat))
            {
                Change(1);
                _repeatAt = Time.unscaledTime + (_rightWas ? 0.12f : 0.4f);
            }
            if (pick && !_pickWas)
            {
                Choose();
            }
            _upWas = up;
            _downWas = down;
            _leftWas = left;
            _rightWas = right;
            _pickWas = pick;
        }

        private static void Choose()
        {
            Actor me = LocalPlayer.actor;
            switch (_row)
            {
                case 0:
                    Open = false;
                    break;
                case 1:
                    if (me != null)
                    {
                        SplitDeploy.SwitchTeam(me, _note);
                    }
                    Open = false;
                    break;
                case 2:
                    if (me != null && !me.dead)
                    {
                        me.KillSilently(); // (back at a flag with the weapons picked here)
                    }
                    Open = false;
                    break;
                case 8:
                    _note?.Invoke("Splitscreen: this player left (its copy closes)."); // (the host reads "this player left": not started again)
                    Application.Quit();
                    break;
                default:
                    Change(1);
                    break;
            }
        }

        private static void Change(int step)
        {
            switch (_row)
            {
                case 3:
                    Cycle(WeaponManager.WeaponSlot.Primary, step);
                    break;
                case 4:
                    Cycle(WeaponManager.WeaponSlot.Secondary, step);
                    break;
                case 5:
                    Cycle(WeaponManager.WeaponSlot.Gear, step);
                    break;
                case 6:
                    FlatPlayerInput.MouseAxisPerCount = Mathf.Clamp(FlatPlayerInput.MouseAxisPerCount * (step > 0 ? 1.15f : 1f / 1.15f), 0.02f, 0.5f);
                    SaveOwn();
                    break;
                case 7:
                    FlatPlayerInput.InvertY = !FlatPlayerInput.InvertY;
                    SaveOwn();
                    break;
            }
        }

        private static WeaponManager.WeaponEntry Current(WeaponManager.WeaponSlot slot)
        {
            WeaponManager.LoadoutSet l = LoadoutUi.instance != null ? LoadoutUi.instance.loadout : null;
            if (l == null)
            {
                return null;
            }
            return slot == WeaponManager.WeaponSlot.Primary ? l.primary : slot == WeaponManager.WeaponSlot.Secondary ? l.secondary : l.gear1;
        }

        /// <summary>The slot's next weapon from the game's own list for that slot, put in the loadout the next spawn takes.</summary>
        private static void Cycle(WeaponManager.WeaponSlot slot, int step)
        {
            WeaponManager.LoadoutSet l = LoadoutUi.instance != null ? LoadoutUi.instance.loadout : null;
            List<WeaponManager.WeaponEntry> all = WeaponManager.GetWeaponEntriesOfSlot(slot);
            if (l == null || all == null || all.Count == 0)
            {
                return;
            }
            int i = all.IndexOf(Current(slot));
            WeaponManager.WeaponEntry next = all[((i < 0 ? 0 : i + step) % all.Count + all.Count) % all.Count];
            if (slot == WeaponManager.WeaponSlot.Primary)
            {
                l.primary = next;
            }
            else if (slot == WeaponManager.WeaponSlot.Secondary)
            {
                l.secondary = next;
            }
            else
            {
                l.gear1 = next;
            }
        }

        private static string Value(int row)
        {
            switch (row)
            {
                case 1:
                    return GameManager.instance != null ? (GameManager.PlayerTeam() == 0 ? "now Eagles" : "now Ravens") : "";
                case 3:
                    return Name(Current(WeaponManager.WeaponSlot.Primary));
                case 4:
                    return Name(Current(WeaponManager.WeaponSlot.Secondary));
                case 5:
                    return Name(Current(WeaponManager.WeaponSlot.Gear));
                case 6:
                    return $"{FlatPlayerInput.MouseAxisPerCount / 0.1f:0.00}x";
                case 7:
                    return FlatPlayerInput.InvertY ? "on" : "off";
                default:
                    return "";
            }
        }

        private static string Name(WeaponManager.WeaponEntry e) => e != null ? "< " + e.name + " >" : "< none >";

        public static void Draw()
        {
            if (!Open)
            {
                return;
            }
            if (_title == null)
            {
                _title = new GUIStyle(GUI.skin.label) { fontSize = 28, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
                _item = new GUIStyle(GUI.skin.label) { fontSize = 20, alignment = TextAnchor.MiddleLeft };
                _picked = new GUIStyle(_item) { fontStyle = FontStyle.Bold };
                _picked.normal.textColor = new Color(1f, 0.85f, 0.3f);
                _hint = new GUIStyle(GUI.skin.label) { fontSize = 14, alignment = TextAnchor.MiddleCenter };
            }
            float w = Mathf.Min(560f, Screen.width - 40f), rowH = 34f, h = 110f + Rows.Length * rowH;
            var box = new Rect((Screen.width - w) / 2f, (Screen.height - h) / 2f, w, h);
            GUI.color = new Color(0f, 0f, 0f, 0.75f);
            GUI.DrawTexture(box, Texture2D.whiteTexture);
            GUI.color = Color.white;
            GUI.Label(new Rect(box.x, box.y + 10f, w, 40f), $"Player {SplitGames.ClientIndex + 1}", _title);
            for (int i = 0; i < Rows.Length; i++)
            {
                var r = new Rect(box.x + 30f, box.y + 60f + i * rowH, w - 60f, rowH);
                GUIStyle st = i == _row ? _picked : _item;
                GUI.Label(r, (i == _row ? "> " : "   ") + Rows[i], st);
                string value = Value(i);
                if (value.Length > 0)
                {
                    GUI.Label(new Rect(r.x + 220f, r.y, r.width - 220f, r.height), value, st);
                }
            }
            GUI.Label(new Rect(box.x, box.yMax - 40f, w, 30f),
                      $"{FlatPlayerInput.KeyName(SteelInput.KeyBinds.Vertical)}/S: move   A/D: change   {FlatPlayerInput.KeyName(SteelInput.KeyBinds.Fire)}, Space: choose   "
                      + "Esc: close   (the game goes on)", _hint);
        }
    }
}
