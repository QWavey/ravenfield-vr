using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using HarmonyLib;
using Lua;
using Lua.Proxy;
using RavenfieldVR.Core;
using UnityEngine;

namespace RavenfieldVR.Input
{
    public enum ScriptKeyPurpose { FireMode, Magnifier, Light, Laser, Bipod, Suppressor, Inspect, Skin, Sprint, Hud, Other }

    /// <summary>One raw key a Ravenscript polls, as seen at runtime.</summary>
    public sealed class ScriptKey
    {
        public ScriptedBehaviour Script;
        /// <summary>Canonical Unity key name: "x", "left shift", "mouse 3"...</summary>
        public string Key;
        public ScriptKeyPurpose Purpose;
        public string Label;
        public float LastPolled;

        public bool Alive => Script != null && Time.unscaledTime - LastPolled < 3f;
        public string KeyLabel => Key.Length == 1 ? Key.ToUpperInvariant() : char.ToUpperInvariant(Key[0]) + Key.Substring(1);
    }

    /// <summary>
    /// Ravenscript in weapons and mutators reads raw keyboard keys (Input.GetKeyDown(KeyCode.X) for a fire-mode
    /// switch, H for a flashlight, I to inspect, Shift for a tactical sprint...), which a VR controller cannot press.
    /// Every key a script polls goes through the game's Lua InputProxy; patching it lets this class
    ///  - record which script polls which key (including keys a mod sets in its own options), and classify what the
    ///    key is for from the script's name and the source around the check, and
    ///  - answer "pressed" for virtual taps and holds, delivered to one target script (so switching a weapon's fire
    ///    mode does not also trigger a HUD mutator that happens to listen to the same key) or to all of them.
    /// </summary>
    public static class ScriptKeys
    {
        private sealed class VirtualKey
        {
            public bool Down;
            public int ChangeFrame;
            public float ReleaseAt = float.MaxValue;
            public ScriptedBehaviour Target;
            /// <summary>
            /// The tap is for one script. Kept apart from <see cref="Target"/>: a destroyed target reads as null to Unity, and
            /// its tap then went to every script listening to the key for the rest of its 80 ms (bug hunt, 2026-10-22).
            /// </summary>
            public bool Targeted;
            public bool WasDown;
            /// <summary>A tap cut into a hold (Shift while sprinting): the hold is still on under the tap (see Tap).</summary>
            public bool HeldUnderTap;
            /// <summary>Script instance id -> the frame it first saw the edge in.</summary>
            public readonly Dictionary<int, int> DownDelivered = new Dictionary<int, int>();
            public readonly Dictionary<int, int> UpDelivered = new Dictionary<int, int>();
        }

        /// <summary>
        /// An edge reaches each script in exactly one frame, and stays true for every check that script makes in that
        /// frame (as Input.GetKeyDown does): a script testing the same key in two branches still sees it in both.
        /// </summary>
        private static bool Deliver(Dictionary<int, int> delivered, int id)
        {
            int frame = Time.frameCount;
            if (delivered.TryGetValue(id, out int at))
            {
                return at == frame;
            }
            delivered[id] = frame;
            return true;
        }

        /// <summary>A press must reach scripts that already ran their Update this frame, so edges stay valid a few frames.</summary>
        private const int EdgeFrames = 3;
        private const float TapSeconds = 0.08f;

        private static readonly Dictionary<string, VirtualKey> Virtual = new Dictionary<string, VirtualKey>();
        private static readonly Dictionary<int, Dictionary<string, ScriptKey>> Seen = new Dictionary<int, Dictionary<string, ScriptKey>>();
        private static float _lastPrune;
        private static readonly List<int> DeadScripts = new List<int>(); // (made once: a new list every 5 s before)

        /// <summary>Only in VR: flat play keeps the game's input untouched.</summary>
        public static bool Enabled;

        // ---- virtual presses -------------------------------------------------------------------------

        /// <summary>Taps a key for one script (or every script when <paramref name="target"/> is null).</summary>
        public static void Tap(string key, ScriptedBehaviour target = null)
        {
            VirtualKey v = Get(key);
            // A tap that cuts into a hold (a sprint's Shift) must not end that hold when it runs out: Update lets the hold
            // carry on, so Shift is not pressed again for every script (2026-10-19). A tap already in flight keeps the
            // hold it found.
            if (v.ReleaseAt == float.MaxValue)
            {
                v.HeldUnderTap = v.Down;
            }
            v.Down = true;
            v.WasDown = true;
            v.ChangeFrame = Time.frameCount;
            v.ReleaseAt = Time.unscaledTime + TapSeconds;
            v.Target = target;
            v.Targeted = !ReferenceEquals(target, null);
            v.DownDelivered.Clear();
            v.UpDelivered.Clear();
        }

        /// <summary>Holds a key for every script while <paramref name="held"/> is true (Shift while sprinting).</summary>
        public static void SetHeld(string key, bool held)
        {
            if (!Virtual.TryGetValue(key, out VirtualKey v))
            {
                if (!held)
                {
                    return;
                }
                v = Get(key);
            }
            if (v.Down == held && v.ReleaseAt == float.MaxValue)
            {
                return;
            }
            if (v.ReleaseAt != float.MaxValue)
            {
                // A tap is in flight: let it run out, for the one script it was meant for. Taken over by a hold (sprinting),
                // it went to every script and stayed down for the whole sprint. The hold takes over once it has run out.
                // A sprint that ends under the tap is over: the key goes up when the tap runs out (2026-10-19).
                if (!held)
                {
                    v.HeldUnderTap = false;
                }
                return;
            }
            v.Down = held;
            v.WasDown |= held;
            v.ChangeFrame = Time.frameCount;
            v.ReleaseAt = float.MaxValue;
            v.Target = null;
            v.Targeted = false;
            v.DownDelivered.Clear();
            v.UpDelivered.Clear();
        }

        private static VirtualKey Get(string key)
        {
            if (!Virtual.TryGetValue(key, out VirtualKey v))
            {
                v = new VirtualKey();
                Virtual[key] = v;
            }
            return v;
        }

        public static void Update()
        {
            float now = Time.unscaledTime;
            foreach (VirtualKey v in Virtual.Values)
            {
                if (v.Down && now >= v.ReleaseAt)
                {
                    if (v.HeldUnderTap)
                    {
                        // The hold the tap cut into is still on: it carries on for every script with no press or release
                        // edge. Releasing here let SetHeld press Shift again, which reached every script as a new press
                        // (2026-10-19). The edge is aged out so that no script reads the old press as a new one.
                        v.HeldUnderTap = false;
                        v.Target = null;
                        v.Targeted = false;
                        v.ReleaseAt = float.MaxValue;
                        v.ChangeFrame = Time.frameCount - EdgeFrames - 1;
                        continue;
                    }
                    v.Down = false;
                    v.ReleaseAt = float.MaxValue;
                    v.ChangeFrame = Time.frameCount;
                    v.UpDelivered.Clear();
                }
            }
            if (now - _lastPrune > 5f)
            {
                _lastPrune = now;
                List<int> dead = DeadScripts;
                dead.Clear();
                foreach (KeyValuePair<int, Dictionary<string, ScriptKey>> kv in Seen)
                {
                    foreach (ScriptKey k in kv.Value.Values)
                    {
                        if (k.Script == null)
                        {
                            dead.Add(kv.Key);
                            break;
                        }
                    }
                }
                foreach (int id in dead)
                {
                    Seen.Remove(id);
                }
            }
        }

        public static void Clear()
        {
            Virtual.Clear();
            Seen.Clear();
        }

        /// <param name="key">Canonical key name.</param>
        /// <param name="kind">0 held, 1 went down, 2 went up.</param>
        internal static bool Query(string key, int kind)
        {
            ScriptedBehaviour script = ScriptedBehaviour.ResolveCurrent();
            if (script != null)
            {
                Observe(script, key);
            }
            if (!Virtual.TryGetValue(key, out VirtualKey v))
            {
                return false;
            }
            if (v.Targeted && !ReferenceEquals(v.Target, script))
            {
                // Not the script the tap was for: it still sees the hold the tap cut into (no edge), as it did before the tap.
                return kind == 0 && v.HeldUnderTap;
            }
            int id = script != null ? script.GetInstanceID() : 0;
            bool fresh = Time.frameCount - v.ChangeFrame <= EdgeFrames;
            switch (kind)
            {
                case 0:
                    return v.Down;
                case 1:
                    return v.Down && fresh && Deliver(v.DownDelivered, id);
                default:
                    return !v.Down && v.WasDown && fresh && Deliver(v.UpDelivered, id);
            }
        }

        // ---- what scripts poll -------------------------------------------------------------------------

        private static void Observe(ScriptedBehaviour script, string key)
        {
            int id = script.GetInstanceID();
            if (!Seen.TryGetValue(id, out Dictionary<string, ScriptKey> keys))
            {
                keys = new Dictionary<string, ScriptKey>();
                Seen[id] = keys;
            }
            if (!keys.TryGetValue(key, out ScriptKey k))
            {
                k = new ScriptKey { Script = script, Key = key };
                Classify(k);
                keys[key] = k;
            }
            k.LastPolled = Time.unscaledTime;
        }

        private sealed class Rule
        {
            public readonly ScriptKeyPurpose Purpose;
            public readonly string Label;
            public readonly Regex Words;

            public Rule(ScriptKeyPurpose purpose, string label, string words)
            {
                Purpose = purpose;
                Label = label;
                Words = new Regex(words, RegexOptions.IgnoreCase);
            }
        }

        private static readonly Rule[] Rules =
        {
            new Rule(ScriptKeyPurpose.FireMode, "Fire mode", @"fire ?mode|firemode|isauto|semi|burst|switchauto|modechange|weaponmode|selector"),
            new Rule(ScriptKeyPurpose.Bipod, "Bipod", @"bipod"),
            // Laser before Magnifier: a laser's script is often named after its sight ("Laser Sight", "LaserSight"), and
            // "sight" is a magnifier word. Checked the other way round, the laser's toggle is taken for the magnifier, so the
            // aim press taps the laser and the scope's magnifier does not flip (reported 2026-10-19: a scope that did not
            // work on a gun with a laser). 镭射 and 激光 are laser in Chinese mod names.
            new Rule(ScriptKeyPurpose.Laser, "Laser", @"laser|镭射|激光"),
            new Rule(ScriptKeyPurpose.Magnifier, "Magnifier", @"magnif|zoom|scope|sight"),
            // "flash$": a behaviour named for its flash at the end ("KLVFlash", the MK18 R3's light: H turns the animator's
            // light on and off) is a light; only at the very end (no multiline), so a muzzle flash in the code read after a key
            // check is not taken for one (scan of the workshop's weapon scripts, improve round, 2026-10-22).
            new Rule(ScriptKeyPurpose.Light, "Flashlight", @"flash ?light|torch|light|flash$"),
            new Rule(ScriptKeyPurpose.Suppressor, "Suppressor", @"suppress|silenc"),
            new Rule(ScriptKeyPurpose.Inspect, "Inspect", @"inspect"),
            new Rule(ScriptKeyPurpose.Skin, "Skin", @"skin|camo"),
            new Rule(ScriptKeyPurpose.Sprint, "Sprint", @"sprint|dash"),
            new Rule(ScriptKeyPurpose.Hud, "HUD", @"hud|feed|killcam|minimap|overlay|scoreboard|crosshair|damageindicator"),
        };

        /// <summary>From the behaviour name, then the source right after the check for this key.</summary>
        private static void Classify(ScriptKey k)
        {
            string behaviour = k.Script.behaviour ?? k.Script.name ?? "";
            string around = "";
            try
            {
                string source = k.Script.source != null ? k.Script.source.text : null;
                if (!string.IsNullOrEmpty(source))
                {
                    string code = CodeNameOf(k.Key);
                    // The whole name ("KeyCode.E" is not "KeyCode.Escape"), else the key named in quotes (GetKeyDown("x")):
                    // a prefix match read the purpose from unrelated code, and a quoted key from the top of the file.
                    int at = -1;
                    Match m = code != null ? Regex.Match(source, @"KeyCode\." + Regex.Escape(code) + @"\b", RegexOptions.IgnoreCase) : Match.Empty;
                    if (!m.Success)
                    {
                        m = Regex.Match(source, "[\"']" + Regex.Escape(k.Key) + "[\"']", RegexOptions.IgnoreCase);
                    }
                    at = m.Success ? m.Index : -1;
                    around = at >= 0 ? source.Substring(at, Math.Min(500, source.Length - at)) : source.Substring(0, Math.Min(1500, source.Length));
                }
            }
            catch (Exception)
            {
                // Source not readable: the behaviour name alone decides.
            }
            // The behaviour name is the strongest hint: the first rule, in order, whose words it has.
            Rule rule = FirstRule(behaviour);
            // Then the code around the check. The word nearest the check decides, not the first rule in order: a laser toggle
            // whose code also mentions a sight further on is still a laser, and an unrelated word later on is not read.
            if (rule == null)
            {
                rule = NearestRule(around);
            }
            if (rule != null)
            {
                k.Purpose = rule.Purpose;
                k.Label = rule.Label;
                return;
            }
            k.Purpose = ScriptKeyPurpose.Other;
            k.Label = Regex.Replace(behaviour, "(?<=[a-z])(?=[A-Z])", " ");
        }

        private static Rule FirstRule(string text)
        {
            foreach (Rule rule in Rules)
            {
                if (rule.Words.IsMatch(text))
                {
                    // A name with a laser word and a strong magnifier word ("Laser Scope", "LaserZoom") is the magnifier: only
                    // "sight" is weak (a "Laser Sight" is a laser). Laser first took them all, and the magnifier's flip went to
                    // the laser (review, 2026-10-20).
                    if (rule.Purpose == ScriptKeyPurpose.Laser && StrongMagnifierWords.IsMatch(text))
                    {
                        return Array.Find(Rules, r => r.Purpose == ScriptKeyPurpose.Magnifier) ?? rule;
                    }
                    return rule;
                }
            }
            return null;
        }

        private static readonly Regex StrongMagnifierWords = new Regex(@"magnif|zoom|scope", RegexOptions.IgnoreCase);

        /// <summary>The rule whose word appears first in the text (ties go to the rule listed first); null when none matches.</summary>
        private static Rule NearestRule(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return null;
            }
            Rule best = null;
            int bestAt = int.MaxValue;
            foreach (Rule rule in Rules)
            {
                Match m = rule.Words.Match(text);
                if (m.Success && m.Index < bestAt)
                {
                    best = rule;
                    bestAt = m.Index;
                }
            }
            return best;
        }

        /// <summary>Keys polled recently by scripts on this weapon (and its sub-weapons).</summary>
        public static List<ScriptKey> KeysOn(Weapon weapon)
        {
            var list = new List<ScriptKey>();
            if (weapon == null)
            {
                return list;
            }
            Transform root = weapon.transform;
            foreach (Dictionary<string, ScriptKey> keys in Seen.Values)
            {
                foreach (ScriptKey k in keys.Values)
                {
                    if (k.Alive && k.Script.transform.IsChildOf(root))
                    {
                        list.Add(k);
                    }
                }
            }
            list.Sort((a, b) => a.Purpose != b.Purpose ? ((int)a.Purpose).CompareTo((int)b.Purpose) : string.CompareOrdinal(a.Key, b.Key));
            return list;
        }

        /// <summary>Keys polled recently by scripts that are not on a weapon (mutators, HUD mods, vehicles, maps).</summary>
        public static List<ScriptKey> OtherKeys()
        {
            var list = new List<ScriptKey>();
            foreach (Dictionary<string, ScriptKey> keys in Seen.Values)
            {
                foreach (ScriptKey k in keys.Values)
                {
                    if (k.Alive && k.Script.GetComponentInParent<Weapon>() == null)
                    {
                        list.Add(k);
                    }
                }
            }
            list.Sort((a, b) => a.Purpose != b.Purpose ? ((int)a.Purpose).CompareTo((int)b.Purpose) : string.CompareOrdinal(a.Label, b.Label));
            return list;
        }

        /// <summary>
        /// The weapon's key of this purpose (the first by name, as <see cref="KeysOn"/> sorts them), without the list and the sort
        /// KeysOn makes: the input bridge and the reload ask it twice a second for the gun in hand (allocation hunt, 2026-10-24).
        /// </summary>
        public static ScriptKey Find(Weapon weapon, ScriptKeyPurpose purpose)
        {
            if (weapon == null)
            {
                return null;
            }
            Transform root = weapon.transform;
            ScriptKey best = null;
            foreach (Dictionary<string, ScriptKey> keys in Seen.Values)
            {
                foreach (ScriptKey k in keys.Values)
                {
                    if (k.Purpose == purpose && k.Alive && k.Script.transform.IsChildOf(root) && (best == null || string.CompareOrdinal(k.Key, best.Key) < 0))
                    {
                        best = k;
                    }
                }
            }
            return best;
        }

        // ---- key names -----------------------------------------------------------------------------------

        private static readonly Dictionary<KeyCode, string> Names = new Dictionary<KeyCode, string>
        {
            { KeyCode.LeftShift, "left shift" }, { KeyCode.RightShift, "right shift" },
            { KeyCode.LeftControl, "left ctrl" }, { KeyCode.RightControl, "right ctrl" },
            { KeyCode.LeftAlt, "left alt" }, { KeyCode.RightAlt, "right alt" },
            { KeyCode.Space, "space" }, { KeyCode.Return, "return" }, { KeyCode.KeypadEnter, "enter" },
            { KeyCode.Escape, "escape" }, { KeyCode.Tab, "tab" }, { KeyCode.Backspace, "backspace" },
            { KeyCode.Delete, "delete" }, { KeyCode.Insert, "insert" }, { KeyCode.Home, "home" }, { KeyCode.End, "end" },
            { KeyCode.PageUp, "page up" }, { KeyCode.PageDown, "page down" },
            { KeyCode.UpArrow, "up" }, { KeyCode.DownArrow, "down" }, { KeyCode.LeftArrow, "left" }, { KeyCode.RightArrow, "right" },
            { KeyCode.BackQuote, "`" }, { KeyCode.Minus, "-" }, { KeyCode.Equals, "=" }, { KeyCode.LeftBracket, "[" },
            { KeyCode.RightBracket, "]" }, { KeyCode.Semicolon, ";" }, { KeyCode.Quote, "'" }, { KeyCode.Comma, "," },
            { KeyCode.Period, "." }, { KeyCode.Slash, "/" }, { KeyCode.Backslash, "\\" }, { KeyCode.CapsLock, "caps lock" },
        };

        private static readonly Dictionary<string, string> CodeNames = BuildCodeNames();

        private static Dictionary<string, string> BuildCodeNames()
        {
            var map = new Dictionary<string, string>();
            foreach (KeyCode code in (KeyCode[])Enum.GetValues(typeof(KeyCode)))
            {
                string name = BuildName(code); // static init order: the name cache may not exist yet
                if (!map.ContainsKey(name))
                {
                    map[name] = code.ToString();
                }
            }
            return map;
        }

        private static string CodeNameOf(string key) => CodeNames.TryGetValue(key, out string code) ? code : null;

        // By key code (every KeyCode is under 512): a lookup on each key a script polls, every frame, with no enum hashing.
        private static readonly string[] NameCache = new string[512];
        private static readonly Dictionary<string, string> RawNameCache = new Dictionary<string, string>();
        private static readonly string[] MouseNames = { "mouse 0", "mouse 1", "mouse 2", "mouse 3", "mouse 4", "mouse 5", "mouse 6" };

        /// <summary>Canonical name for a mouse button (cached).</summary>
        public static string MouseName(int button) => button >= 0 && button < MouseNames.Length ? MouseNames[button] : "mouse " + button;

        /// <summary>The name Input.GetKey(string) uses for this key code (cached: polled by scripts every frame).</summary>
        public static string Name(KeyCode code)
        {
            int i = (int)code;
            if (i < 0 || i >= NameCache.Length)
            {
                return BuildName(code);
            }
            return NameCache[i] ??= BuildName(code);
        }

        private static string BuildName(KeyCode code)
        {
            if (Names.TryGetValue(code, out string name))
            {
                return name;
            }
            if (code >= KeyCode.A && code <= KeyCode.Z)
            {
                return ((char)('a' + (code - KeyCode.A))).ToString();
            }
            if (code >= KeyCode.Alpha0 && code <= KeyCode.Alpha9)
            {
                return ((char)('0' + (code - KeyCode.Alpha0))).ToString();
            }
            if (code >= KeyCode.Keypad0 && code <= KeyCode.Keypad9)
            {
                return "[" + (code - KeyCode.Keypad0) + "]";
            }
            if (code >= KeyCode.F1 && code <= KeyCode.F15)
            {
                return "f" + (code - KeyCode.F1 + 1);
            }
            if (code >= KeyCode.Mouse0 && code <= KeyCode.Mouse6)
            {
                return "mouse " + (code - KeyCode.Mouse0);
            }
            return code.ToString().ToLowerInvariant();
        }

        public static string Name(string unityName)
        {
            if (unityName == null)
            {
                return "";
            }
            if (!RawNameCache.TryGetValue(unityName, out string name))
            {
                name = unityName.Trim().ToLowerInvariant();
                if (RawNameCache.Count < 512)
                {
                    RawNameCache[unityName] = name;
                }
            }
            return name;
        }
    }

    // ---- patches on the Lua input proxy -----------------------------------------------------------------

    [HarmonyPatch]
    internal static class ScriptKeyPatches
    {
        [HarmonyPostfix, HarmonyPatch(typeof(InputProxy), nameof(InputProxy.GetKey), typeof(KeyCode))]
        private static void GetKeyCode(KeyCode key, ref bool __result) => Merge(ScriptKeys.Name(key), 0, ref __result);

        [HarmonyPostfix, HarmonyPatch(typeof(InputProxy), nameof(InputProxy.GetKeyDown), typeof(KeyCode))]
        private static void GetKeyDownCode(KeyCode key, ref bool __result) => Merge(ScriptKeys.Name(key), 1, ref __result);

        [HarmonyPostfix, HarmonyPatch(typeof(InputProxy), nameof(InputProxy.GetKeyUp), typeof(KeyCode))]
        private static void GetKeyUpCode(KeyCode key, ref bool __result) => Merge(ScriptKeys.Name(key), 2, ref __result);

        [HarmonyPostfix, HarmonyPatch(typeof(InputProxy), nameof(InputProxy.GetKey), typeof(string))]
        private static void GetKeyName(string name, ref bool __result) => Merge(ScriptKeys.Name(name), 0, ref __result);

        [HarmonyPostfix, HarmonyPatch(typeof(InputProxy), nameof(InputProxy.GetKeyDown), typeof(string))]
        private static void GetKeyDownName(string name, ref bool __result) => Merge(ScriptKeys.Name(name), 1, ref __result);

        [HarmonyPostfix, HarmonyPatch(typeof(InputProxy), nameof(InputProxy.GetKeyUp), typeof(string))]
        private static void GetKeyUpName(string name, ref bool __result) => Merge(ScriptKeys.Name(name), 2, ref __result);

        [HarmonyPostfix, HarmonyPatch(typeof(InputProxy), nameof(InputProxy.GetMouseButton))]
        private static void GetMouse(int button, ref bool __result) => Merge(ScriptKeys.MouseName(button), 0, ref __result);

        [HarmonyPostfix, HarmonyPatch(typeof(InputProxy), nameof(InputProxy.GetMouseButtonDown))]
        private static void GetMouseDown(int button, ref bool __result) => Merge(ScriptKeys.MouseName(button), 1, ref __result);

        [HarmonyPostfix, HarmonyPatch(typeof(InputProxy), nameof(InputProxy.GetMouseButtonUp))]
        private static void GetMouseUp(int button, ref bool __result) => Merge(ScriptKeys.MouseName(button), 2, ref __result);

        private static void Merge(string key, int kind, ref bool result)
        {
            if (!ScriptKeys.Enabled || string.IsNullOrEmpty(key))
            {
                return;
            }
            try
            {
                result |= ScriptKeys.Query(key, kind);
            }
            catch (Exception e)
            {
                Log.WarnOnce("scriptkeys", "Script key bridge failed: " + e.Message);
            }
        }
    }
}
