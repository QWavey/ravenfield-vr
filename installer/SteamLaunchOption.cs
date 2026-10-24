// Sets / removes Ravenfield's Steam launch option so pressing Play in Steam opens the Ravenfield VR launcher.
// Steam keeps launch options per account in userdata\<account>\config\localconfig.vdf under
// UserLocalConfigStore/Software/Valve/Steam/apps/636480/LaunchOptions, and rewrites that file when it exits,
// so this only writes while Steam is closed.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace RavenfieldVR.Setup
{
    internal static class SteamLaunchOption
    {
        private const string AppId = "636480";

        public static bool SteamRunning() => Process.GetProcessesByName("steam").Length > 0;

        /// <summary>The launch option text a user would paste into Steam > Properties > Launch Options.</summary>
        public static string OptionFor(string launcherPath) => "\"" + launcherPath + "\" %command%";

        public static IEnumerable<string> LocalConfigs()
        {
            string userdata = Path.Combine(Installer.SteamDir(), "userdata");
            if (!Directory.Exists(userdata))
            {
                yield break;
            }
            foreach (string account in Directory.GetDirectories(userdata))
            {
                string file = Path.Combine(account, "config", "localconfig.vdf");
                if (File.Exists(file))
                {
                    yield return file;
                }
            }
        }

        /// <summary>Adds the launcher in front of %command%, keeping the user's own options. Returns accounts changed.</summary>
        public static int Apply(string launcherPath, Action<string> log)
        {
            List<string> configs = LocalConfigs().ToList();
            int changed = configs.Count(file => ApplyToFile(file, launcherPath, log));
            // With no account's settings found (Steam elsewhere than the registry says, never signed in here) nothing was set:
            // "already" claimed a launch option that does not exist (installer scout, 2026-10-23).
            log(configs.Count == 0
                ? "No Steam account settings were found under " + Path.Combine(Installer.SteamDir(), "userdata")
                  + ", so Steam's launch option was not set. Start Ravenfield VR with " + Installer.LauncherName
                  + ", or paste " + OptionFor(launcherPath) + " into Ravenfield's launch options in Steam."
                : changed > 0 ? $"Steam now opens the Ravenfield VR launcher when you press Play ({changed} Steam account(s))." : "Steam already opens the launcher.");
            return changed;
        }

        /// <summary>Takes the launcher back out of the launch options; the user's own options stay.</summary>
        public static int Remove(Action<string> log)
        {
            int changed = LocalConfigs().Count(file => RemoveFromFile(file, log));
            if (changed > 0)
            {
                log($"Removed the launcher from Ravenfield's launch options ({changed} Steam account(s)).");
            }
            return changed;
        }

        /// <summary>Ravenfield's current launch options in one localconfig.vdf ("" when none).</summary>
        public static string Read(string file)
        {
            VdfNode opt = AppNode(Vdf.Parse(File.ReadAllText(file)), create: false)?.Child("LaunchOptions");
            return opt != null ? Vdf.Unescape(opt.RawValue) : "";
        }

        public static bool ApplyToFile(string file, string launcherPath, Action<string> log)
        {
            VdfNode root = Vdf.Parse(File.ReadAllText(file));
            VdfNode app = AppNode(root, create: true);
            if (app == null)
            {
                log("Skipped " + file + ": not a Steam local config.");
                return false;
            }
            VdfNode opt = app.Child("LaunchOptions");
            string current = opt != null ? Vdf.Unescape(opt.RawValue) : "";
            string ours = OptionFor(launcherPath);
            if (current.IndexOf(Installer.LauncherName, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                // Set already. After the game moved to another Steam library the option still named the old launcher, and Steam
                // could not start it: an old path is replaced with this one (review, 2026-10-20).
                string quoted = "\"" + launcherPath + "\"";
                if (current.IndexOf(quoted, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return false;
                }
                string moved = System.Text.RegularExpressions.Regex.Replace(current, "\"[^\"]*" + System.Text.RegularExpressions.Regex.Escape(Installer.LauncherName) + "\"",
                    quoted.Replace("$", "$$"), System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (moved == current)
                {
                    return false; // named some other way (unquoted): left as the user wrote it
                }
                opt.RawValue = Vdf.Escape(moved);
                Write(file, root);
                log("Updated the launch option in " + file + " to the launcher's new place.");
                return true;
            }
            string updated = current.Contains("%command%") ? ReplaceFirst(current, "%command%", ours) : (ours + " " + current).Trim();
            if (opt == null)
            {
                opt = new VdfNode { Key = "LaunchOptions" };
                app.Children.Add(opt);
            }
            opt.RawValue = Vdf.Escape(updated);
            Write(file, root);
            return true;
        }

        public static bool RemoveFromFile(string file, Action<string> log)
        {
            VdfNode root = Vdf.Parse(File.ReadAllText(file));
            VdfNode app = AppNode(root, create: false);
            VdfNode opt = app?.Child("LaunchOptions");
            if (opt == null)
            {
                return false;
            }
            string current = Vdf.Unescape(opt.RawValue);
            int at = current.IndexOf(Installer.LauncherName, StringComparison.OrdinalIgnoreCase);
            if (at < 0)
            {
                return false;
            }
            // Remove the quoted launcher path; %command% and the user's own options stay.
            int start = current.LastIndexOf('"', Math.Max(0, at - 1));
            int end = current.IndexOf('"', at);
            if (start < 0 || end < 0)
            {
                log("Could not take the launcher out of: " + current);
                return false;
            }
            string updated = (current.Substring(0, start) + current.Substring(end + 1)).Trim();
            if (updated == "%command%" || updated.Length == 0)
            {
                app.Children.Remove(opt);
            }
            else
            {
                opt.RawValue = Vdf.Escape(updated);
            }
            Write(file, root);
            return true;
        }

        private static VdfNode AppNode(VdfNode root, bool create)
        {
            VdfNode node = root.Child("UserLocalConfigStore");
            foreach (string key in new[] { "Software", "Valve", "Steam", "apps" })
            {
                if (node == null)
                {
                    return null;
                }
                VdfNode next = node.Child(key);
                if (next == null)
                {
                    if (!create)
                    {
                        return null;
                    }
                    next = new VdfNode { Key = key, Children = new List<VdfNode>() };
                    node.Children.Add(next);
                }
                node = next;
            }
            VdfNode app = node.Child(AppId);
            if (app == null && create)
            {
                app = new VdfNode { Key = AppId, Children = new List<VdfNode>() };
                node.Children.Add(app);
            }
            return app;
        }

        private static string ReplaceFirst(string s, string find, string with)
        {
            int at = s.IndexOf(find, StringComparison.Ordinal);
            return at < 0 ? s : s.Substring(0, at) + with + s.Substring(at + find.Length);
        }

        private static void Write(string file, VdfNode root)
        {
            string backup = file + ".rfvr-backup";
            if (!File.Exists(backup))
            {
                File.Copy(file, backup);
            }
            string temp = file + ".rfvr-tmp";
            string text = Vdf.Serialize(root);
            File.WriteAllText(temp, text, new UTF8Encoding(false));
            // Read back whole before it replaces Steam's, as the Play entry's app cache is.
            bool readBack;
            try
            {
                string back = File.ReadAllText(temp, Encoding.UTF8);
                readBack = back == text && Vdf.Serialize(Vdf.Parse(back)) == text;
            }
            catch (FormatException)
            {
                readBack = false; // (the temp file went with it too: a parse that threw left it behind)
            }
            if (!readBack)
            {
                File.Delete(temp);
                throw new InvalidDataException("the changed Steam config did not read back; Steam's file was left as it was.");
            }
            ReplaceFile(temp, file);
        }

        /// <summary>
        /// <paramref name="temp"/> takes the place of <paramref name="file"/> in one step (Windows' ReplaceFile): copied over it, a
        /// crash part way through left Steam's account config cut short (installer scout, 2026-10-23). A volume that cannot
        /// replace in place (FAT, some network drives) gets the copy.
        /// </summary>
        internal static void ReplaceFile(string temp, string file)
        {
            try
            {
                File.Replace(temp, file, null);
            }
            catch (Exception e) when (e is IOException || e is PlatformNotSupportedException)
            {
                if (!File.Exists(temp))
                {
                    throw;
                }
                File.Copy(temp, file, true);
                File.Delete(temp);
            }
        }

        /// <summary>Asks Steam to exit and waits for it (Steam -shutdown).</summary>
        public static bool ShutdownSteam(TimeSpan timeout)
        {
            string exe = Path.Combine(Installer.SteamDir(), "steam.exe");
            if (!File.Exists(exe))
            {
                return false;
            }
            Process.Start(new ProcessStartInfo(exe, "-shutdown") { UseShellExecute = false });
            var watch = Stopwatch.StartNew();
            while (SteamRunning() && watch.Elapsed < timeout)
            {
                System.Threading.Thread.Sleep(250);
            }
            return !SteamRunning();
        }

        public static void StartSteam()
        {
            string exe = Path.Combine(Installer.SteamDir(), "steam.exe");
            if (File.Exists(exe))
            {
                Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
            }
        }
    }

    internal sealed class VdfNode
    {
        public string Key;
        /// <summary>Value exactly as written between the quotes (still escaped); null for a block.</summary>
        public string RawValue;
        public List<VdfNode> Children;

        public VdfNode Child(string key) => Children?.FirstOrDefault(c => string.Equals(c.Key, key, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Steam's text KeyValues format, round-tripped without touching anything it does not change.</summary>
    internal static class Vdf
    {
        public static VdfNode Parse(string text)
        {
            int i = 0;
            var root = new VdfNode { Key = "", Children = new List<VdfNode>() };
            ParseBlock(text, ref i, root, top: true);
            return root;
        }

        private static void ParseBlock(string t, ref int i, VdfNode parent, bool top)
        {
            while (true)
            {
                SkipSpace(t, ref i);
                if (i >= t.Length)
                {
                    if (!top)
                    {
                        throw new FormatException("Unexpected end of file in a block.");
                    }
                    return;
                }
                if (t[i] == '}')
                {
                    if (top)
                    {
                        // Written back, the rest of the file after it was dropped.
                        throw new FormatException("Unexpected '}' outside any block.");
                    }
                    i++;
                    return;
                }
                string key = ReadString(t, ref i);
                SkipSpace(t, ref i);
                if (i < t.Length && t[i] == '{')
                {
                    i++;
                    var node = new VdfNode { Key = key, Children = new List<VdfNode>() };
                    parent.Children.Add(node);
                    ParseBlock(t, ref i, node, top: false);
                }
                else
                {
                    parent.Children.Add(new VdfNode { Key = key, RawValue = ReadString(t, ref i) });
                }
            }
        }

        private static void SkipSpace(string t, ref int i)
        {
            while (i < t.Length)
            {
                if (char.IsWhiteSpace(t[i]))
                {
                    i++;
                }
                else if (t[i] == '/' && i + 1 < t.Length && t[i + 1] == '/')
                {
                    while (i < t.Length && t[i] != '\n')
                    {
                        i++;
                    }
                }
                else
                {
                    return;
                }
            }
        }

        /// <summary>A quoted string, returned raw (escape sequences kept as written).</summary>
        // A truncated file (Steam stopped mid-write) ends in a FormatException, not an index error: the end was read past at the
        // opening quote, after an escape at the very end, and an unclosed string was taken as whole (audit, issue #15).
        private static string ReadString(string t, ref int i)
        {
            if (i >= t.Length || t[i] != '"')
            {
                throw new FormatException("Expected a quoted string at " + i + (i >= t.Length ? " (the file ends there)." : "."));
            }
            int start = ++i;
            while (i < t.Length && t[i] != '"')
            {
                i += t[i] == '\\' ? 2 : 1;
            }
            if (i >= t.Length)
            {
                throw new FormatException("A quoted string from " + (start - 1) + " is never closed (the file ends first).");
            }
            string raw = t.Substring(start, i - start);
            i++;
            return raw;
        }

        public static string Escape(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");

        public static string Unescape(string raw)
        {
            var sb = new StringBuilder(raw.Length);
            for (int i = 0; i < raw.Length; i++)
            {
                if (raw[i] == '\\' && i + 1 < raw.Length)
                {
                    char n = raw[++i];
                    sb.Append(n == 'n' ? '\n' : n == 't' ? '\t' : n);
                }
                else
                {
                    sb.Append(raw[i]);
                }
            }
            return sb.ToString();
        }

        public static string Serialize(VdfNode root)
        {
            var sb = new StringBuilder();
            foreach (VdfNode child in root.Children)
            {
                Write(sb, child, 0);
            }
            return sb.ToString();
        }

        private static void Write(StringBuilder sb, VdfNode node, int depth)
        {
            string indent = new string('\t', depth);
            if (node.Children != null)
            {
                sb.Append(indent).Append('"').Append(node.Key).Append("\"\n");
                sb.Append(indent).Append("{\n");
                foreach (VdfNode child in node.Children)
                {
                    Write(sb, child, depth + 1);
                }
                sb.Append(indent).Append("}\n");
            }
            else
            {
                sb.Append(indent).Append('"').Append(node.Key).Append("\"\t\t\"").Append(node.RawValue).Append("\"\n");
            }
        }
    }
}
