// Ravenfield VR setup program: one window, everything it installs is embedded in the exe.
// Install / update / uninstall follow the same manifest + backup rules as install.ps1 / uninstall.ps1.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace RavenfieldVR.Setup
{
    internal static class Program
    {
        /// <summary>
        /// No arguments: the setup window. Unattended:
        ///   --install [--game DIR] [--no-steam-option]      install or update
        ///   --uninstall [--game DIR] [--keep-settings]      uninstall
        ///   (--install also adds the "Ravenfield VR" entry to Steam's Play list unless --no-steam-play-entry; --uninstall removes it)
        ///   --steam-play-entry add|remove                  only that entry, in Steam's cache (Steam closed)
        ///   --steam-option-test FILE --launcher PATH        add then remove the launch option in a copy of a localconfig.vdf
        ///   --steam-play-test FILE                          add then remove the Play entry in a copy of an appinfo.vdf
        /// Unattended runs log to RavenfieldVR-setup.log next to the exe and return 0 on success.
        /// </summary>
        [STAThread]
        private static int Main(string[] args)
        {
            if (args.Length == 0)
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new SetupForm());
                return 0;
            }
            string logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "RavenfieldVR-setup.log");
            var log = new List<string>();
            Action<string> write = line => log.Add(DateTime.Now.ToString("HH:mm:ss ") + line);
            try
            {
                string Arg(string name)
                {
                    int i = Array.FindIndex(args, a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
                    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
                }
                bool Flag(string name) => args.Any(a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
                string game = Arg("--game") ?? Installer.FindRavenfield();
                var installer = new Installer(write);
                if (Flag("--steam-option-test"))
                {
                    string file = Arg("--steam-option-test"), launcher = Arg("--launcher");
                    write("Before: " + SteamLaunchOption.Read(file));
                    SteamLaunchOption.ApplyToFile(file, launcher, write);
                    write("After add: " + SteamLaunchOption.Read(file));
                    SteamLaunchOption.RemoveFromFile(file, write);
                    write("After remove: " + SteamLaunchOption.Read(file));
                }
                else if (Flag("--steam-play-test"))
                {
                    string file = Arg("--steam-play-test");
                    byte[] before = File.ReadAllBytes(file);
                    write("Before: entry " + Present(SteamPlayEntry.IsPresent(file)));
                    SteamPlayEntry.Change(file, true, write);
                    write("After add: entry " + Present(SteamPlayEntry.IsPresent(file)) + ", " + new FileInfo(file).Length + " bytes (was " + before.Length + "); kept as " + file + ".added");
                    File.Copy(file, file + ".added", true);
                    SteamPlayEntry.Change(file, false, write);
                    bool same = File.ReadAllBytes(file).SequenceEqual(before);
                    write("After remove: entry " + Present(SteamPlayEntry.IsPresent(file)) + ", " + (same ? "byte for byte the file it was" : "NOT the file it was"));
                    if (!same)
                    {
                        throw new InvalidDataException("add + remove did not give back the original file.");
                    }
                }
                else if (Flag("--steam-play-entry"))
                {
                    // add | remove, in Steam's own cache (Steam closed).
                    string what = Arg("--steam-play-entry");
                    if (what != "add" && what != "remove")
                    {
                        throw new ArgumentException("--steam-play-entry takes add or remove.");
                    }
                    if (SteamLaunchOption.SteamRunning())
                    {
                        throw new InvalidOperationException("Steam is running; close it first.");
                    }
                    SteamPlayEntry.Change(SteamPlayEntry.CachePath(), what == "add", write);
                    write("Entry " + Present(SteamPlayEntry.IsPresent(SteamPlayEntry.CachePath())));
                }
                else if (Flag("--install"))
                {
                    if (!Installer.IsRavenfieldFolder(game))
                    {
                        throw new InvalidOperationException("Ravenfield folder not found; pass --game DIR.");
                    }
                    installer.Install(game);
                    bool option = !Flag("--no-steam-option"), entry = !Flag("--no-steam-play-entry");
                    if ((option || entry) && SteamLaunchOption.SteamRunning())
                    {
                        write("Steam is running: Steam's Play options not changed (close Steam or use the setup window).");
                    }
                    else
                    {
                        if (option)
                        {
                            SteamLaunchOption.Apply(Path.Combine(game, Installer.LauncherName), write);
                        }
                        if (entry)
                        {
                            SteamPlayEntry.Apply(write);
                        }
                    }
                }
                else if (Flag("--uninstall"))
                {
                    // Steam's options go first: uninstalled first, a Steam that could not be changed was left starting a deleted
                    // launcher, and Ravenfield did not start from Steam at all (review, 2026-10-20).
                    if (string.IsNullOrEmpty(game))
                    {
                        // As --install says it: with no folder found, the record's path was built from null and the run ended on
                        // "Value cannot be null. Parameter name: path1" (bug hunt, 2026-10-22).
                        throw new InvalidOperationException("Ravenfield folder not found; pass --game DIR.");
                    }
                    installer.CheckCanUninstall(game); // before Steam is touched: a refused uninstall left Steam's options taken out
                    bool steamRunning = SteamLaunchOption.SteamRunning();
                    if (!steamRunning)
                    {
                        SteamLaunchOption.Remove(write);
                        SteamPlayEntry.Remove(write);
                    }
                    installer.Uninstall(game, Flag("--keep-settings"));
                    if (steamRunning)
                    {
                        write("Steam is running: remove the launcher from Ravenfield's launch options yourself; the \"" + SteamPlayEntry.Description +
                              "\" Play entry goes with Ravenfield's next update, or run --steam-play-entry remove with Steam closed.");
                    }
                }
                else
                {
                    throw new ArgumentException("Unknown arguments: " + string.Join(" ", args));
                }
                File.WriteAllLines(logPath, log);
                return 0;
            }
            catch (Exception e)
            {
                write("Error: " + e.Message);
                File.WriteAllLines(logPath, log);
                return 1;
            }
        }

        private static string Present(bool? present) => present == null ? "unknown (no Ravenfield app info)" : present.Value ? "present" : "absent";
    }

    /// <summary>Installs the embedded release into a Ravenfield folder and removes it again.</summary>
    internal sealed class Installer
    {
        public const string ManifestRelative = @"BepInEx\plugins\RavenfieldVR\install-manifest.txt";
        public const string LauncherName = "RavenfieldVR-Launcher.exe";
        private static readonly string[] LoaderFiles = { "winhttp.dll", "doorstop_config.ini", ".doorstop_version", "changelog.txt" };

        private readonly Action<string> _log;

        public Installer(Action<string> log)
        {
            _log = log;
        }

        public static string Version
        {
            get
            {
                using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("RavenfieldVR.Setup.version.txt"))
                using (var r = new StreamReader(s))
                {
                    return r.ReadToEnd().Trim();
                }
            }
        }

        // ---- finding the game --------------------------------------------------------------------------

        public static string FindRavenfield()
        {
            foreach (string lib in SteamLibraries())
            {
                string dir = Path.Combine(lib, "steamapps", "common", "Ravenfield");
                if (File.Exists(Path.Combine(dir, "ravenfield.exe")))
                {
                    return dir;
                }
            }
            return null;
        }

        /// <summary>Offline tests: a made-up Steam folder in place of the one the registry names (null: the registry's).</summary>
        internal static string SteamDirOverride;

        public static string SteamDir()
        {
            if (SteamDirOverride != null)
            {
                return SteamDirOverride;
            }
            string steam = null;
            foreach (string key in new[] { @"HKEY_CURRENT_USER\Software\Valve\Steam", @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", @"HKEY_LOCAL_MACHINE\SOFTWARE\Valve\Steam" })
            {
                steam = (Registry.GetValue(key, "SteamPath", null) ?? Registry.GetValue(key, "InstallPath", null)) as string;
                if (!string.IsNullOrEmpty(steam))
                {
                    break;
                }
            }
            if (string.IsNullOrEmpty(steam))
            {
                steam = @"C:\Program Files (x86)\Steam";
            }
            return steam.Replace('/', '\\');
        }

        private static IEnumerable<string> SteamLibraries()
        {
            var libs = new List<string>();
            string steam = SteamDir();
            libs.Add(steam);
            string vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
            if (File.Exists(vdf))
            {
                foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\""))
                {
                    string p = m.Groups[1].Value.Replace(@"\\", @"\");
                    if (!libs.Contains(p, StringComparer.OrdinalIgnoreCase))
                    {
                        libs.Add(p);
                    }
                }
            }
            return libs;
        }

        public static bool IsRavenfieldFolder(string dir) => !string.IsNullOrEmpty(dir) && File.Exists(Path.Combine(dir, "ravenfield.exe"));

        /// <summary>
        /// Ravenfield, or the launcher waiting for the headset to start it: the launcher holds its own exe and openvr_api.dll, and a
        /// delete then failed as if it needed admin rights (review, 2026-10-20).
        /// </summary>
        public static bool GameRunning() => Process.GetProcessesByName("ravenfield").Length > 0
                                            || Process.GetProcessesByName(Path.GetFileNameWithoutExtension(LauncherName)).Length > 0;

        /// <summary>Installed mod version, or null.</summary>
        public static string InstalledVersion(string game)
        {
            string manifest = Path.Combine(game, ManifestRelative);
            if (!File.Exists(manifest))
            {
                return File.Exists(Path.Combine(game, @"BepInEx\plugins\RavenfieldVR\RavenfieldVR.dll")) ? "unknown version" : null;
            }
            string line = File.ReadAllLines(manifest).FirstOrDefault(l => l.StartsWith("version=", StringComparison.Ordinal));
            return line != null ? line.Substring("version=".Length) : "unknown version";
        }

        public static string BepInExProblem(string game)
        {
            string dll = Path.Combine(game, @"BepInEx\core\BepInEx.dll");
            if (!File.Exists(dll))
            {
                return null;
            }
            string ver = FileVersionInfo.GetVersionInfo(dll).FileVersion ?? "";
            return ver.StartsWith("6", StringComparison.Ordinal)
                ? "BepInEx " + ver + " (version 6) is installed. Ravenfield mods, including this one, need BepInEx 5."
                : null;
        }

        // ---- install -----------------------------------------------------------------------------------

        public void Install(string game)
        {
            InstallFrom(game,
                () => Assembly.GetExecutingAssembly().GetManifestResourceStream("RavenfieldVR.Setup.payload.zip"),
                Version);
        }

        /// <summary>
        /// Installs a release into <paramref name="game"/> from any payload zip: the one embedded in this setup, or a
        /// RavenfieldVR-&lt;ver&gt;.zip downloaded from GitHub by the "install another version" advanced option.
        /// <paramref name="versionLabel"/> is written to the install manifest so the installed version reads back right.
        /// </summary>
        public void InstallFrom(string game, Func<Stream> openPayload, string versionLabel)
        {
            if (GameRunning())
            {
                throw new InvalidOperationException("Ravenfield (or its VR launcher) is running. Close it first.");
            }
            string bepProblem = BepInExProblem(game);
            if (bepProblem != null)
            {
                throw new InvalidOperationException(bepProblem);
            }

            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var added = new List<string>();
            var replaced = new List<string>();
            var ours = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string previousBackup = "";
            bool previousInstalledBepInEx = false;

            string manifestPath = Path.Combine(game, ManifestRelative);
            if (File.Exists(manifestPath))
            {
                foreach (string line in File.ReadAllLines(manifestPath))
                {
                    if (line.StartsWith("added=", StringComparison.Ordinal)) ours.Add(line.Substring(6));
                    else if (line.StartsWith("replaced=", StringComparison.Ordinal)) replaced.Add(line.Substring(9));
                    else if (line.StartsWith("backup=", StringComparison.Ordinal)) previousBackup = line.Substring(7);
                    else if (line == "installedBepInEx=True") previousInstalledBepInEx = true;
                }
                _log("Updating the existing install.");
                // The plugin folder is cleared but for the install record: an update that fails part way still leaves the
                // record of what the install added and replaced, so Uninstall can still put the game back (bug hunt
                // 2026-10-19: deleted with the folder first, a failed update left no record and Uninstall refused).
                string pluginDir = Path.Combine(game, @"BepInEx\plugins\RavenfieldVR");
                foreach (string f in Directory.GetFiles(pluginDir))
                {
                    if (!string.Equals(Path.GetFileName(f), Path.GetFileName(ManifestRelative), StringComparison.OrdinalIgnoreCase))
                    {
                        File.Delete(f);
                    }
                }
                foreach (string dir in Directory.GetDirectories(pluginDir))
                {
                    Directory.Delete(dir, true);
                }
            }
            // An update backs up into the install's own backup folder: the record names one folder, and a file this update
            // replaced for the first time went into a new one Uninstall never looked in, so its original was not put back.
            string backupDir = previousBackup.Length > 0 ? previousBackup : Path.Combine(game, "RavenfieldVR-backup", stamp);

            bool installBepInEx = previousInstalledBepInEx || !File.Exists(Path.Combine(game, @"BepInEx\core\BepInEx.dll"));
            using (Stream payload = openPayload())
            using (var zip = new ZipArchive(payload, ZipArchiveMode.Read))
            {
                if (!installBepInEx)
                {
                    _log("Keeping the BepInEx 5 already installed.");
                }
                foreach (ZipArchiveEntry entry in zip.Entries)
                {
                    if (entry.FullName.EndsWith("/", StringComparison.Ordinal) || entry.Length == 0 && entry.Name.Length == 0)
                    {
                        continue;
                    }
                    string rel = entry.FullName.Replace('/', '\\');
                    bool loader = LoaderFiles.Contains(rel, StringComparer.OrdinalIgnoreCase) || rel.StartsWith(@"BepInEx\core\", StringComparison.OrdinalIgnoreCase);
                    bool mod = rel.StartsWith(@"BepInEx\plugins\RavenfieldVR\", StringComparison.OrdinalIgnoreCase) || rel.StartsWith(@"ravenfield_Data\", StringComparison.OrdinalIgnoreCase)
                               || rel.Equals(LauncherName, StringComparison.OrdinalIgnoreCase);
                    if (!(mod || loader && installBepInEx))
                    {
                        continue;
                    }
                    InstallEntry(entry, game, rel, backupDir, ours, added, replaced);
                }
            }

            // Files an earlier version added that this one no longer has: they were the install's, so they go (left, they lingered
            // and dropped out of the record, and Uninstall never removed them).
            foreach (string old in ours)
            {
                if (!added.Contains(old, StringComparer.OrdinalIgnoreCase))
                {
                    string path = Inside(game, old);
                    if (path != null && File.Exists(path))
                    {
                        File.Delete(path);
                        _log("Removed " + old + " (no longer part of Ravenfield VR).");
                    }
                }
            }

            string backupRef = previousBackup.Length > 0 ? previousBackup : Directory.Exists(backupDir) ? backupDir : "";
            var lines = new List<string>
            {
                "# Ravenfield VR install " + stamp,
                "version=" + versionLabel,
                "installedBepInEx=" + (installBepInEx ? "True" : "False"),
                "backup=" + backupRef
            };
            lines.AddRange(added.Select(a => "added=" + a));
            lines.AddRange(replaced.Distinct(StringComparer.OrdinalIgnoreCase).Select(r => "replaced=" + r));
            File.WriteAllLines(manifestPath, lines, new UTF8Encoding(false));
            _log($"Installed Ravenfield VR {versionLabel}: {added.Count} files, {replaced.Count} original file(s) backed up.");
        }

        /// <summary>
        /// <paramref name="rel"/> under <paramref name="root"/>, or null when it would land outside it: a rooted path, or one whose
        /// '..' climbs out (a zip entry 'ravenfield_Data\..\..\x' passed the prefix test; a manifest line is trusted data too; audit,
        /// issue #1).
        /// </summary>
        internal static string Inside(string root, string rel)
        {
            if (string.IsNullOrWhiteSpace(rel) || string.IsNullOrEmpty(root) || Path.IsPathRooted(rel))
            {
                return null;
            }
            string full = Path.GetFullPath(Path.Combine(root, rel));
            string bas = Path.GetFullPath(root).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            return full.StartsWith(bas, StringComparison.OrdinalIgnoreCase) ? full : null;
        }

        private void InstallEntry(ZipArchiveEntry entry, string game, string rel, string backupDir, HashSet<string> ours, List<string> added, List<string> replaced)
        {
            string dst = Inside(game, rel);
            if (dst == null)
            {
                _log("Skipped " + rel + ": it would be written outside the game folder.");
                return;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(dst));
            if (ours.Contains(rel))
            {
                Extract(entry, dst);
                added.Add(rel);
                return;
            }
            if (File.Exists(dst))
            {
                if (SameContent(entry, dst))
                {
                    return;
                }
                if (!replaced.Contains(rel, StringComparer.OrdinalIgnoreCase))
                {
                    string bak = Inside(backupDir, rel) ?? throw new InvalidOperationException("Backup path outside the backup folder: " + rel);
                    Directory.CreateDirectory(Path.GetDirectoryName(bak));
                    File.Copy(dst, bak, true);
                    replaced.Add(rel);
                }
                Extract(entry, dst);
                return;
            }
            Extract(entry, dst);
            added.Add(rel);
        }

        private static void Extract(ZipArchiveEntry entry, string dst)
        {
            using (Stream src = entry.Open())
            using (FileStream f = File.Create(dst))
            {
                src.CopyTo(f);
            }
        }

        private static bool SameContent(ZipArchiveEntry entry, string path)
        {
            if (new FileInfo(path).Length != entry.Length)
            {
                return false;
            }
            using (SHA256 sha = SHA256.Create())
            using (Stream a = entry.Open())
            using (FileStream b = File.OpenRead(path))
            {
                return sha.ComputeHash(a).SequenceEqual(sha.ComputeHash(b));
            }
        }

        // ---- uninstall ---------------------------------------------------------------------------------

        /// <summary>Throws what would refuse an uninstall, before anything (Steam's options included) is changed.</summary>
        public void CheckCanUninstall(string game)
        {
            if (GameRunning())
            {
                throw new InvalidOperationException("Ravenfield (or its VR launcher) is running. Close it first.");
            }
            if (!File.Exists(Path.Combine(game, ManifestRelative)))
            {
                throw new InvalidOperationException("Ravenfield VR was not installed by this setup (no install record found).");
            }
        }

        public void Uninstall(string game, bool keepSettings)
        {
            CheckCanUninstall(game);
            string manifestPath = Path.Combine(game, ManifestRelative);
            string[] lines = File.ReadAllLines(manifestPath);
            string backup = lines.Where(l => l.StartsWith("backup=", StringComparison.Ordinal)).Select(l => l.Substring(7)).FirstOrDefault() ?? "";
            bool installedBepInEx = lines.Contains("installedBepInEx=True");
            // Decided before anything goes: BepInEx stays when other plugins use it or the settings are kept, and then its loader and
            // core (files this setup added with it) stay too. Deleted first, BepInEx was left in place but no longer loaded, and the
            // other mods stopped without a word (review, 2026-10-20).
            string pluginsDir = Path.Combine(game, @"BepInEx\plugins");
            bool otherPlugins = Directory.Exists(pluginsDir) && Directory.EnumerateFileSystemEntries(pluginsDir)
                .Any(e => !string.Equals(Path.GetFileName(e), "RavenfieldVR", StringComparison.OrdinalIgnoreCase));
            bool settingsKept = keepSettings && File.Exists(Path.Combine(game, @"BepInEx\config\com.qwavey.ravenfieldvr.cfg"));
            bool keepBepInEx = !installedBepInEx || otherPlugins || settingsKept;
            // A Steam launch option still running the launcher (Steam could not be changed): the launcher stays, or Ravenfield no
            // longer starts from Steam.
            bool launcherInUse = SteamLaunchOption.LocalConfigs().Any(f =>
            {
                try
                {
                    return SteamLaunchOption.Read(f).IndexOf(LauncherName, StringComparison.OrdinalIgnoreCase) >= 0;
                }
                catch (Exception)
                {
                    return false;
                }
            });
            int removed = 0, restored = 0;
            foreach (string line in lines.Where(l => l.StartsWith("added=", StringComparison.Ordinal)))
            {
                string rel = line.Substring(6);
                // (BepInEx's own: what Install adds as the loader.)
                bool bepInExOwn = LoaderFiles.Contains(rel, StringComparer.OrdinalIgnoreCase) || rel.StartsWith(@"BepInEx\core\", StringComparison.OrdinalIgnoreCase);
                if (keepBepInEx && bepInExOwn)
                {
                    continue;
                }
                if (launcherInUse && string.Equals(rel, LauncherName, StringComparison.OrdinalIgnoreCase))
                {
                    _log("Kept " + LauncherName + ": Steam's launch options for Ravenfield still start it. Remove it from Steam > Ravenfield > Properties > Launch Options, then delete the file.");
                    continue;
                }
                string path = Inside(game, rel);
                if (path == null)
                {
                    _log("Skipped the record's " + rel + ": not a path inside the game folder.");
                    continue;
                }
                if (File.Exists(path))
                {
                    File.Delete(path);
                    removed++;
                }
            }
            foreach (string line in lines.Where(l => l.StartsWith("replaced=", StringComparison.Ordinal)))
            {
                string rel = line.Substring(9);
                string dst = Inside(game, rel);
                string src = backup.Length > 0 ? Inside(backup, rel) : null;
                if (dst == null)
                {
                    _log("Skipped the record's " + rel + ": not a path inside the game folder.");
                }
                else if (src != null && File.Exists(src))
                {
                    File.Copy(src, dst, true);
                    restored++;
                }
                else
                {
                    _log("No backup for " + rel + "; left the modded file in place.");
                }
            }
            DeleteDir(Path.Combine(game, @"BepInEx\plugins\RavenfieldVR"));
            DeleteDir(Path.Combine(game, @"ravenfield_Data\UnitySubsystems\XRSDKOpenVR"));
            string subsystems = Path.Combine(game, @"ravenfield_Data\UnitySubsystems");
            if (Directory.Exists(subsystems) && !Directory.EnumerateFileSystemEntries(subsystems).Any())
            {
                Directory.Delete(subsystems);
            }
            // The launcher's log goes; its choices (VR or flat, the preset) are settings.
            string launcherLog = Path.Combine(game, @"BepInEx\RavenfieldVR-launcher.log");
            if (File.Exists(launcherLog))
            {
                File.Delete(launcherLog);
            }
            if (!keepSettings)
            {
                foreach (string cfg in new[] { "com.qwavey.ravenfieldvr.cfg", "RavenfieldVR.weapons.json", "RavenfieldVR.launcher.ini" })
                {
                    string p = Path.Combine(game, @"BepInEx\config", cfg);
                    if (File.Exists(p))
                    {
                        File.Delete(p);
                    }
                }
            }
            if (installedBepInEx)
            {
                if (keepBepInEx)
                {
                    _log(otherPlugins ? "Other BepInEx plugins are installed, so BepInEx stays." : "BepInEx stays so your Ravenfield VR settings are kept.");
                }
                else
                {
                    DeleteDir(Path.Combine(game, "BepInEx"));
                    foreach (string f in LoaderFiles)
                    {
                        string p = Path.Combine(game, f);
                        if (File.Exists(p))
                        {
                            File.Delete(p);
                        }
                    }
                    _log("Removed BepInEx (this setup had installed it).");
                }
            }
            _log($"Uninstalled: {removed} files removed, {restored} original file(s) restored.");
        }

        private static void DeleteDir(string dir)
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, true);
            }
        }
    }

    /// <summary>
    /// Reads Ravenfield VR's public GitHub releases so Setup's advanced option can install any version, not only
    /// the one baked into this exe. Anonymous HTTPS — the repo is public, so no token is needed.
    /// </summary>
    internal static class GitHub
    {
        public const string Repo = "QWavey/ravenfield-vr";
        private const string ReleasesApi = "https://api.github.com/repos/" + Repo + "/releases?per_page=100";

        public sealed class Release
        {
            public string Version;   // e.g. "1.10.1"
            public string ZipUrl;    // browser_download_url of RavenfieldVR-<ver>.zip
            public override string ToString() => Version;
        }

        private static void PrepareTls()
        {
            // 3072 = Tls12. Numeric cast so it builds against any 4.x System.dll; GitHub needs TLS 1.2+.
            try { ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072; }
            catch { /* best effort: some runtimes reject OR-ing unknown protocol flags */ }
        }

        private static HttpWebRequest Request(string url)
        {
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.UserAgent = "RavenfieldVR-Setup";           // GitHub's API rejects requests without a User-Agent.
            req.Accept = "application/vnd.github+json";
            req.Timeout = 30000;
            req.ReadWriteTimeout = 120000;
            return req;
        }

        /// <summary>Installable releases, newest first; each carries its RavenfieldVR-&lt;ver&gt;.zip download URL.</summary>
        public static List<Release> Releases()
        {
            PrepareTls();
            string json;
            using (var resp = (HttpWebResponse)Request(ReleasesApi).GetResponse())
            using (var r = new StreamReader(resp.GetResponseStream()))
            {
                json = r.ReadToEnd();
            }
            // The zip asset URL already carries the version (…/RavenfieldVR-1.10.1.zip), so one regex over the whole
            // response pulls (url, version) pairs without walking the JSON structure.
            var found = new List<Release>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var rx = new Regex("\"browser_download_url\"\\s*:\\s*\"([^\"]*?/RavenfieldVR-([0-9][^\"/]*?)\\.zip)\"",
                               RegexOptions.IgnoreCase);
            foreach (Match m in rx.Matches(json))
            {
                if (seen.Add(m.Groups[2].Value))
                {
                    found.Add(new Release { Version = m.Groups[2].Value, ZipUrl = m.Groups[1].Value });
                }
            }
            if (found.Count == 0)
            {
                throw new InvalidOperationException("No installable RavenfieldVR-<version>.zip was found in the GitHub releases.");
            }
            found.Sort((a, b) => CompareVersions(b.Version, a.Version));
            return found;
        }

        /// <summary>Downloads a release zip to a temp file and returns its path; reports (bytesDone, bytesTotal).</summary>
        public static string DownloadZip(Release release, Action<long, long> progress)
        {
            PrepareTls();
            string dest = Path.Combine(Path.GetTempPath(),
                "RavenfieldVR-" + release.Version + "-" + Guid.NewGuid().ToString("N") + ".zip");
            using (var resp = (HttpWebResponse)Request(release.ZipUrl).GetResponse())
            using (Stream src = resp.GetResponseStream())
            using (var dst = new FileStream(dest, FileMode.Create, FileAccess.Write))
            {
                long total = resp.ContentLength, done = 0;
                byte[] buf = new byte[81920];
                int n;
                while ((n = src.Read(buf, 0, buf.Length)) > 0)
                {
                    dst.Write(buf, 0, n);
                    done += n;
                    if (progress != null) progress(done, total);
                }
            }
            return dest;
        }

        /// <summary>Compares dotted numeric versions (1.10.1 &gt; 1.9.0); non-numeric parts fall back to ordinal.</summary>
        internal static int CompareVersions(string a, string b)
        {
            string[] pa = a.Split('.'), pb = b.Split('.');
            int n = Math.Max(pa.Length, pb.Length);
            for (int i = 0; i < n; i++)
            {
                string sa = i < pa.Length ? pa[i] : "0", sb = i < pb.Length ? pb[i] : "0";
                int ia, ib;
                if (int.TryParse(sa, out ia) && int.TryParse(sb, out ib))
                {
                    if (ia != ib) return ia.CompareTo(ib);
                }
                else
                {
                    int c = string.Compare(sa, sb, StringComparison.OrdinalIgnoreCase);
                    if (c != 0) return c;
                }
            }
            return 0;
        }
    }

    /// <summary>Small modal that lists the GitHub versions and returns the one to install.</summary>
    internal sealed class VersionPicker : Form
    {
        private readonly ComboBox _combo = new ComboBox();
        private readonly List<GitHub.Release> _releases;
        public GitHub.Release Selected { get; private set; }

        public VersionPicker(List<GitHub.Release> releases, string installed)
        {
            _releases = releases;
            Text = "Install another version";
            Font = new Font("Segoe UI", 9.75f);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(384, 156);

            var label = new Label
            {
                Text = "Pick a Ravenfield VR version from GitHub to install. This replaces the currently installed version.",
                Location = new Point(16, 14),
                MaximumSize = new Size(352, 0),
                AutoSize = true
            };
            _combo.DropDownStyle = ComboBoxStyle.DropDownList;
            _combo.SetBounds(16, 66, 352, 26);
            foreach (GitHub.Release r in releases)
            {
                _combo.Items.Add(r.Version == installed ? r.Version + "   (installed now)" : r.Version);
            }
            _combo.SelectedIndex = 0;

            var ok = new Button { Text = "Install", DialogResult = DialogResult.OK, Bounds = new Rectangle(180, 110, 90, 32) };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Bounds = new Rectangle(278, 110, 90, 32) };
            ok.Click += (s, e) => Selected = _releases[_combo.SelectedIndex];
            Controls.AddRange(new Control[] { label, _combo, ok, cancel });
            AcceptButton = ok;
            CancelButton = cancel;
        }
    }

    internal sealed class SetupForm : Form
    {
        private readonly TextBox _path = new TextBox();
        private readonly Label _status = new Label();
        private readonly Button _install = new Button();
        private readonly Button _uninstall = new Button();
        private readonly Button _close = new Button();
        private readonly CheckBox _keepSettings = new CheckBox();
        private readonly CheckBox _steamLaunch = new CheckBox();
        private readonly CheckBox _steamPlay = new CheckBox();
        private readonly LinkLabel _openConfig = new LinkLabel();
        private readonly LinkLabel _advanced = new LinkLabel();
        private readonly TextBox _log = new TextBox();
        private readonly Installer _installer;

        public SetupForm()
        {
            _installer = new Installer(Log);
            Text = "Ravenfield VR Setup";
            Font = new Font("Segoe UI", 9.75f);
            AutoScaleMode = AutoScaleMode.Dpi;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(600, 452);
            BackColor = SystemColors.Window;

            var title = new Label { Text = "Ravenfield VR " + Installer.Version, Font = new Font("Segoe UI Semibold", 15f), AutoSize = true, Location = new Point(20, 16) };
            var subtitle = new Label
            {
                Text = "Plays Ravenfield in your headset through SteamVR.",
                ForeColor = SystemColors.GrayText,
                AutoSize = true,
                Location = new Point(22, 50)
            };
            var folderLabel = new Label { Text = "Ravenfield folder", AutoSize = true, Location = new Point(20, 88) };
            _path.SetBounds(20, 110, 470, 26);
            _path.TextChanged += (s, e) => RefreshState();
            var browse = new Button { Text = "Browse…", Bounds = new Rectangle(498, 108, 82, 29) };
            browse.Click += (s, e) => Browse();
            _status.SetBounds(20, 144, 560, 40);

            _steamLaunch.Text = "Pressing Play in Steam opens the launcher (VR or flat, graphics preset)";
            _steamLaunch.Checked = true;
            _steamLaunch.AutoSize = true;
            _steamLaunch.Location = new Point(20, 188);

            _steamPlay.Text = "Add \"" + SteamPlayEntry.Description + "\" to the list Steam shows when you press Play";
            _steamPlay.Checked = true;
            _steamPlay.AutoSize = true;
            _steamPlay.Location = new Point(20, 212);

            _keepSettings.Text = "Keep my settings when uninstalling";
            _keepSettings.Checked = true;
            _keepSettings.AutoSize = true;
            _keepSettings.Location = new Point(20, 238);

            _openConfig.Text = "Open the settings file";
            _openConfig.AutoSize = true;
            _openConfig.Location = new Point(400, 239);
            _openConfig.LinkClicked += (s, e) => OpenConfig();

            _log.Multiline = true;
            _log.ReadOnly = true;
            _log.ScrollBars = ScrollBars.Vertical;
            _log.BackColor = SystemColors.Control;
            _log.Font = new Font("Consolas", 9f);
            _log.SetBounds(20, 270, 560, 96);

            _install.SetBounds(20, 378, 150, 34);
            _install.Click += async (s, e) =>
            {
                string game = _path.Text.Trim();
                bool ok = await RunAsync(() => _installer.Install(game), "Installing…", null);
                if (ok && (_steamLaunch.Checked || _steamPlay.Checked))
                {
                    await SetSteamOptions(game, true, _steamLaunch.Checked, _steamPlay.Checked);
                }
                if (ok)
                {
                    Log(_steamLaunch.Checked
                        ? "Done. Press Play on Ravenfield in Steam and pick " + (_steamPlay.Checked ? "\"" + SteamPlayEntry.Description + "\", or Ravenfield for VR or flat." : "VR or flat.")
                        : _steamPlay.Checked
                            ? "Done. Press Play on Ravenfield in Steam and pick \"" + SteamPlayEntry.Description + "\"."
                            : "Done. Start Ravenfield with RavenfieldVR-Launcher.exe in the game folder, or with SteamVR running.");
                }
            };
            _uninstall.Text = "Uninstall";
            _uninstall.SetBounds(180, 378, 120, 34);
            _uninstall.Click += async (s, e) =>
            {
                bool keep = _keepSettings.Checked;
                string game = _path.Text.Trim();
                // Steam's options first, then the files: the launcher is kept while Steam still starts it (see Uninstall). Checked
                // first: a refused uninstall (the game running) left Steam's options taken out and the mod installed.
                if (!await RunAsync(() => _installer.CheckCanUninstall(game), "Checking…", null))
                {
                    return;
                }
                await SetSteamOptions(game, false, true, true);
                await RunAsync(() => _installer.Uninstall(game, keep), "Uninstalling…", null);
            };
            _close.Text = "Close";
            _close.SetBounds(480, 378, 100, 34);
            _close.Click += (s, e) => Close();
            AcceptButton = _install;
            CancelButton = _close;

            // Advanced: install any published version straight from GitHub, not only the one baked into this setup.
            _advanced.Text = "Advanced: install another version from GitHub…";
            _advanced.AutoSize = true;
            _advanced.Location = new Point(20, 424);
            _advanced.LinkClicked += async (s, e) => await UpdateFromGitHubFlow();

            Controls.AddRange(new Control[] { title, subtitle, folderLabel, _path, browse, _status, _steamLaunch, _steamPlay, _keepSettings, _openConfig, _log, _install, _uninstall, _close, _advanced });

            string found = Installer.FindRavenfield();
            _path.Text = found ?? "";
            Log(found != null ? "Found Ravenfield in your Steam library." : "Ravenfield was not found automatically. Choose its folder with Browse.");
            RefreshState();
        }

        private void Log(string line)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action<string>(Log), line);
                return;
            }
            _log.AppendText(line + Environment.NewLine);
        }

        private void RefreshState()
        {
            string game = _path.Text.Trim();
            bool valid = Installer.IsRavenfieldFolder(game);
            string installed = valid ? Installer.InstalledVersion(game) : null;
            string problem = valid ? Installer.BepInExProblem(game) : null;
            if (!valid)
            {
                _status.Text = "This folder does not contain ravenfield.exe.";
            }
            else if (problem != null)
            {
                _status.Text = problem;
            }
            else if (installed == null)
            {
                _status.Text = "Ravenfield VR is not installed yet.";
            }
            else
            {
                _status.Text = installed == Installer.Version
                    ? $"Ravenfield VR {installed} is installed."
                    : $"Ravenfield VR {installed} is installed; this setup has {Installer.Version}.";
            }
            _install.Text = installed == null ? "Install" : installed == Installer.Version ? "Reinstall" : "Update";
            _install.Enabled = valid && problem == null;
            _uninstall.Enabled = valid && installed != null;
            _openConfig.Visible = valid && File.Exists(ConfigPath(game));
        }

        private static string ConfigPath(string game) => Path.Combine(game, @"BepInEx\config\com.qwavey.ravenfieldvr.cfg");

        private void Browse()
        {
            using (var dialog = new FolderBrowserDialog { Description = "Select the Ravenfield folder (the one with ravenfield.exe)." })
            {
                if (dialog.ShowDialog(this) == DialogResult.OK)
                {
                    _path.Text = dialog.SelectedPath;
                }
            }
        }

        private void OpenConfig()
        {
            string cfg = ConfigPath(_path.Text.Trim());
            if (File.Exists(cfg))
            {
                Process.Start(new ProcessStartInfo("notepad.exe", "\"" + cfg + "\"") { UseShellExecute = true });
            }
        }

        /// <summary>
        /// Adds or removes the launcher in Ravenfield's Steam launch options and the "Ravenfield VR" entry in Steam's
        /// Play list. Steam writes both files back when it exits, so it is closed for the change (with permission)
        /// and started again; otherwise the user gets the launch option text to paste.
        /// </summary>
        private async Task SetSteamOptions(string game, bool add, bool launchOption, bool playEntry)
        {
            string launcher = Path.Combine(game, Installer.LauncherName);
            string option = SteamLaunchOption.OptionFor(launcher);
            bool restart = false;
            void CopyOption()
            {
                if (add && launchOption)
                {
                    Clipboard.SetText(option);
                    Log("Copied the launch option to the clipboard: " + option);
                }
            }
            if (SteamLaunchOption.SteamRunning())
            {
                string what = add ? "set up" : "take back out";
                DialogResult answer = MessageBox.Show(this,
                    "Steam has to close for a moment to " + what + " Ravenfield's Play options (Steam writes them back when it exits).\n\n" +
                    "Close Steam now? It is started again right after.\n\n" +
                    (add ? "No: " + (launchOption ? "the launch option is copied to the clipboard instead. Paste it in Steam > Ravenfield > Properties > Launch Options. " : "") +
                           (playEntry ? "The \"" + SteamPlayEntry.Description + "\" entry needs Steam closed: run Setup again when Steam is not running." : "")
                         : "No: remove the launcher part from Steam > Ravenfield > Properties > Launch Options yourself. The \"" + SteamPlayEntry.Description +
                           "\" entry goes away by itself with Ravenfield's next update, or run Setup again when Steam is not running."),
                    Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (answer != DialogResult.Yes)
                {
                    CopyOption();
                    return;
                }
                Log("Closing Steam…");
                if (!await Task.Run(() => SteamLaunchOption.ShutdownSteam(TimeSpan.FromSeconds(30))))
                {
                    Log("Steam did not close in time; its Play options were not changed.");
                    CopyOption();
                    return;
                }
                restart = true;
            }
            if (launchOption)
            {
                try
                {
                    await Task.Run(() =>
                    {
                        if (add)
                        {
                            SteamLaunchOption.Apply(launcher, Log);
                        }
                        else
                        {
                            SteamLaunchOption.Remove(Log);
                        }
                    });
                }
                catch (Exception e)
                {
                    Log("Could not change Steam's launch options: " + e.Message);
                    CopyOption();
                }
            }
            if (playEntry)
            {
                try
                {
                    await Task.Run(() =>
                    {
                        if (add)
                        {
                            SteamPlayEntry.Apply(Log);
                        }
                        else
                        {
                            SteamPlayEntry.Remove(Log);
                        }
                    });
                }
                catch (Exception e)
                {
                    Log("Could not change Steam's Play list: " + e.Message);
                }
            }
            if (restart)
            {
                SteamLaunchOption.StartSteam();
                Log("Started Steam again.");
            }
        }

        /// <summary>
        /// Advanced option: fetch the list of published versions from GitHub, let the user pick one, download its
        /// release zip and install it exactly like the embedded one — then set up Steam's Play options as usual.
        /// </summary>
        private async Task UpdateFromGitHubFlow()
        {
            string game = _path.Text.Trim();
            if (!Installer.IsRavenfieldFolder(game))
            {
                MessageBox.Show(this, "Choose your Ravenfield folder first (the one with ravenfield.exe).",
                    Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            string problem = Installer.BepInExProblem(game);
            if (problem != null)
            {
                MessageBox.Show(this, problem, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            Control[] controls = { _install, _uninstall, _close, _path, _advanced };
            foreach (Control c in controls) c.Enabled = false;
            _status.Text = "Checking GitHub for versions…";
            List<GitHub.Release> releases;
            try
            {
                releases = await Task.Run(() => GitHub.Releases());
            }
            catch (Exception e)
            {
                Log("Could not reach GitHub: " + e.Message);
                foreach (Control c in controls) c.Enabled = true;
                RefreshState();
                return;
            }
            foreach (Control c in controls) c.Enabled = true;
            RefreshState();

            GitHub.Release chosen;
            using (var picker = new VersionPicker(releases, Installer.InstalledVersion(game)))
            {
                if (picker.ShowDialog(this) != DialogResult.OK || picker.Selected == null)
                {
                    return;
                }
                chosen = picker.Selected;
            }

            string zipPath = null;
            int lastPct = -1;
            bool ok = await RunAsync(() =>
            {
                Log("Downloading Ravenfield VR " + chosen.Version + " from GitHub…");
                zipPath = GitHub.DownloadZip(chosen, (done, total) =>
                {
                    if (total <= 0) return;
                    int pct = (int)(done * 100 / total);
                    if (pct >= lastPct + 20 || pct == 100)
                    {
                        lastPct = pct;
                        Log("  " + pct + "%  (" + (done / 1024) + " / " + (total / 1024) + " KB)");
                    }
                });
                Log("Installing Ravenfield VR " + chosen.Version + "…");
                _installer.InstallFrom(game, () => File.OpenRead(zipPath), chosen.Version);
            }, "Installing " + chosen.Version + "…", null);

            if (zipPath != null)
            {
                try { File.Delete(zipPath); } catch { /* temp file; ignore */ }
            }
            if (ok && (_steamLaunch.Checked || _steamPlay.Checked))
            {
                await SetSteamOptions(game, true, _steamLaunch.Checked, _steamPlay.Checked);
            }
            if (ok)
            {
                Log("Done. Ravenfield VR " + chosen.Version + " is installed.");
            }
        }

        private async Task<bool> RunAsync(Action work, string busyText, string doneMessage)
        {
            Control[] controls = { _install, _uninstall, _close, _path, _advanced };
            foreach (Control c in controls)
            {
                c.Enabled = false;
            }
            string previous = _status.Text;
            _status.Text = busyText;
            bool ok = false;
            try
            {
                await Task.Run(work);
                ok = true;
                if (doneMessage != null)
                {
                    Log(doneMessage);
                }
            }
            catch (UnauthorizedAccessException)
            {
                Log("Windows denied access to the Ravenfield folder.");
                if (MessageBox.Show(this, "Windows denied access to the Ravenfield folder. Restart the setup as administrator?", Text,
                        MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
                {
                    try
                    {
                        Process.Start(new ProcessStartInfo(Application.ExecutablePath) { Verb = "runas", UseShellExecute = true });
                        Close();
                    }
                    catch (Exception)
                    {
                        Log("The administrator prompt was cancelled.");
                    }
                }
            }
            catch (Exception e)
            {
                Log("Error: " + e.Message);
                _status.Text = previous;
            }
            finally
            {
                foreach (Control c in controls)
                {
                    c.Enabled = true;
                }
                RefreshState();
            }
            return ok;
        }
    }
}
