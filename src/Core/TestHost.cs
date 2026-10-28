using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using BepInEx;
using HarmonyLib;

namespace RavenfieldVR.Core
{
    /// <summary>
    /// The fast test mode (user, 2026-10-24: "we could run tests/whole tests weapon tests etc without starting ravenfield and
    /// waiting that long"). The game loads every subscribed mod before anything else: 89 mods, 96 files, 729 MB in 45-49 s on the
    /// user's PC (Player.log, "Mod Content loaded in 45.2s"), then their mutators in every match. A test of one gun needs one of
    /// them. With -rfvr-mods=TOKENS the game loads only the mods named: a workshop id, or a word of the mod's title, comma
    /// separated; "none" loads only the game's own content (its weapons and maps). After every full load (no filter) the
    /// mod writes which mod each weapon comes from (<see cref="MapPath"/>), so tools/vrshots.py --fast finds the mods a run's
    /// guns need by itself.
    /// </summary>
    internal static class TestHost
    {
        /// <summary>The mods to load, or null: every mod, as the game does.</summary>
        internal static readonly string[] Mods = ParseMods(Environment.GetCommandLineArgs());

        /// <summary>When the game's content was ready, in seconds after it started (0 before).</summary>
        internal static float ContentReadyAt { get; set; }

        /// <summary>Which mod each weapon comes from (weapon name, workshop id or folder, mod title; tab separated).</summary>
        internal static string MapPath => Path.Combine(Paths.CachePath, "RavenfieldVR-weapon-mods.tsv");

        /// <summary>The tokens of -rfvr-mods= (lower case, trimmed), or null without it.</summary>
        internal static string[] ParseMods(string[] args)
        {
            foreach (string a in args ?? new string[0])
            {
                const string flag = "-rfvr-mods=";
                if (a != null && a.StartsWith(flag, StringComparison.OrdinalIgnoreCase))
                {
                    var tokens = new List<string>();
                    foreach (string t in a.Substring(flag.Length).Split(','))
                    {
                        string s = t.Trim().ToLowerInvariant();
                        if (s.Length > 0)
                        {
                            tokens.Add(s);
                        }
                    }
                    return tokens.ToArray();
                }
            }
            return null;
        }

        /// <summary>
        /// Whether a mod named so is loaded under these tokens: a workshop id or folder name exactly, or a word of its title.
        /// "none" keeps nothing but the game's own content (which is always kept).
        /// </summary>
        internal static bool Keeps(string[] tokens, string id, string folder, string title)
        {
            if (tokens == null)
            {
                return true;
            }
            foreach (string t in tokens)
            {
                if (t == "none")
                {
                    continue;
                }
                bool digits = t.Length > 0;
                foreach (char c in t)
                {
                    digits &= c >= '0' && c <= '9';
                }
                // Whole words of the title (a short token such as "ar" took most of the 89 mods as a part of a word, and the fast
                // mode loaded nearly all of them); a token of several words is those words in a row.
                if (digits ? t == id || t == (folder ?? "").ToLowerInvariant()
                    : Words(title).Contains(Words(t)) || (folder ?? "").ToLowerInvariant() == t)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>The text's words (letters and digits, any script), lower-case, one space apart and round them: " shotguns expansion ".</summary>
        private static string Words(string text)
        {
            var s = new System.Text.StringBuilder(" ");
            bool inWord = false;
            foreach (char c in (text ?? "").ToLowerInvariant())
            {
                if (char.IsLetterOrDigit(c))
                {
                    s.Append(c);
                    inWord = true;
                }
                else if (inWord)
                {
                    s.Append(' ');
                    inWord = false;
                }
            }
            if (inWord)
            {
                s.Append(' ');
            }
            return s.ToString();
        }

        internal static bool Keeps(ModInformation mod)
        {
            if (mod == null || mod.isOfficialContent || mod.hideInModList)
            {
                return true; // the game's own content and its data folder
            }
            string id = mod.IsWorkshopItem() ? mod.workshopItemId.m_PublishedFileId.ToString() : "";
            return Keeps(Mods, id, mod.directoryName, mod.title);
        }

        /// <summary>After a full load: each weapon's mod, for the test tools to pick mods by gun.</summary>
        internal static void WriteMap()
        {
            if (Mods != null || WeaponManager.instance == null || WeaponManager.instance.allWeapons == null)
            {
                return; // (a filtered load knows only part of the weapons: the full map stays)
            }
            try
            {
                var sb = new StringBuilder("# weapon\tmod (workshop id or folder)\tmod title\n");
                foreach (WeaponManager.WeaponEntry e in WeaponManager.instance.allWeapons)
                {
                    if (e == null)
                    {
                        continue;
                    }
                    ModInformation m = e.sourceMod;
                    bool own = m == null || m.isOfficialContent || m.hideInModList;
                    string id = own ? "game" : m.IsWorkshopItem() ? m.workshopItemId.m_PublishedFileId.ToString() : m.directoryName;
                    sb.Append(Clean(e.name)).Append('\t').Append(Clean(id)).Append('\t').Append(Clean(own ? "Ravenfield" : m.title)).Append('\n');
                }
                Directory.CreateDirectory(Paths.CachePath);
                File.WriteAllText(MapPath, sb.ToString(), new UTF8Encoding(false));
                Log.Info($"Weapon-to-mod list written for the fast test mode: {WeaponManager.instance.allWeapons.Count} weapons ({MapPath}).");
            }
            catch (Exception e)
            {
                Log.WarnOnce("testhost-map", "Writing the weapon-to-mod list failed: " + e.Message);
            }
        }

        private static string Clean(string s) => (s ?? "").Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ');
    }

    /// <summary>The game loads only the mods -rfvr-mods= names (every mod without it).</summary>
    [HarmonyPatch(typeof(ModManager), nameof(ModManager.GetActiveMods))]
    internal static class TestHostModFilterPatch
    {
        private static int _logged = -1;

        private static void Postfix(List<ModInformation> __result)
        {
            if (TestHost.Mods == null || __result == null)
            {
                return;
            }
            int before = __result.Count;
            __result.RemoveAll(m => !TestHost.Keeps(m));
            if (__result.Count != _logged)
            {
                _logged = __result.Count;
                var names = new List<string>();
                foreach (ModInformation m in __result)
                {
                    names.Add(m.ToString());
                }
                Log.Info($"Fast test mode (-rfvr-mods={string.Join(",", TestHost.Mods)}): loading {__result.Count} of {before} active mods: {string.Join("; ", names)}.");
            }
        }
    }

    /// <summary>After the content is loaded: the weapon-to-mod list (full loads only).</summary>
    [HarmonyPatch(typeof(GameManager), nameof(GameManager.OnAllContentLoaded))]
    internal static class TestHostContentLoadedPatch
    {
        private static void Postfix()
        {
            // Every run's timeline (the fast test mode is judged by it): the moment the game's content is ready.
            int mods = ModManager.instance != null && ModManager.instance.mods != null ? ModManager.instance.GetActiveMods().Count : -1;
            TestHost.ContentReadyAt = UnityEngine.Time.realtimeSinceStartup;
            Log.Info($"Content loaded {UnityEngine.Time.realtimeSinceStartup:0.0} s after the game started ({mods} active mod(s){(TestHost.Mods != null ? ", fast test mode" : "")}).");
            TestHost.WriteMap();
        }
    }
}
