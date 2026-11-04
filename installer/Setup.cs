// Ravenfield VR setup program: one window, everything it installs is embedded in the exe.
// Install / update / uninstall follow the same manifest + backup rules as install.ps1 / uninstall.ps1.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
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
                // Guarded as the unattended runs are: a throw while the window starts (reading Steam's library list) was a crash
                // with nothing written down (installer scout, 2026-10-23).
                try
                {
                    Application.EnableVisualStyles();
                    Application.SetCompatibleTextRenderingDefault(false);
                    Application.Run(new SetupForm());
                    return 0;
                }
                catch (Exception e)
                {
                    string crash = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "RavenfieldVR-setup.log");
                    try
                    {
                        File.AppendAllText(crash, DateTime.Now.ToString("HH:mm:ss ") + "The setup window failed: " + e + Environment.NewLine);
                    }
                    catch (Exception)
                    {
                    }
                    MessageBox.Show("Ravenfield VR Setup could not start: " + e.Message + "\n\nDetails: " + crash, "Ravenfield VR Setup",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return 1;
                }
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
                string game = Arg("--game") != null ? Installer.GamePath(Arg("--game")) : Installer.FindRavenfield();
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
                        // (A Steam file it cannot read is no reason to keep the mod: uninstalled all the same, as the window does.)
                        try
                        {
                            SteamLaunchOption.Remove(write);
                            SteamPlayEntry.Remove(write);
                        }
                        catch (Exception e) when (e is FormatException || e is InvalidDataException || e is IOException || e is UnauthorizedAccessException)
                        {
                            write("Could not change Steam's Play options: " + e.Message);
                        }
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

        /// <summary>
        /// The experimental settings start off with every install (user, 2026-11-03: "experimental, by default and on installation
        /// off when installed with the setup"): one switched on in the kept settings is set off again, the rest of the file as it
        /// was. A fresh install has no file yet, and the mod writes them off.
        /// </summary>
        private static readonly string[] ExperimentalSettings = { "LoadMagazinesByHand" };

        private void ExperimentalOff(string game)
        {
            string cfg = Path.Combine(game, @"BepInEx\config\com.qwavey.ravenfieldvr.cfg");
            try
            {
                if (!File.Exists(cfg))
                {
                    return;
                }
                string[] lines = File.ReadAllLines(cfg);
                bool changed = false;
                for (int i = 0; i < lines.Length; i++)
                {
                    foreach (string key in ExperimentalSettings)
                    {
                        string[] kv = lines[i].Split(new[] { '=' }, 2);
                        if (kv.Length == 2 && kv[0].Trim() == key && kv[1].Trim().Equals("true", StringComparison.OrdinalIgnoreCase))
                        {
                            lines[i] = key + " = false";
                            changed = true;
                            _log("Set the experimental " + key + " off (it starts off with every install; switch it on again in the settings).");
                        }
                    }
                }
                if (changed)
                {
                    File.WriteAllLines(cfg, lines);
                }
            }
            catch (Exception e)
            {
                _log("Could not check the experimental settings in " + cfg + ": " + e.Message);
            }
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
        internal static string SteamDirOverride = null; // set by the offline tests through reflection (no CS0649)

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

        public static bool IsRavenfieldFolder(string dir) => !string.IsNullOrEmpty(dir) && dir.IndexOfAny(Path.GetInvalidPathChars()) < 0
                                                             && File.Exists(Path.Combine(dir, "ravenfield.exe"));

        /// <summary>
        /// A game folder as typed or pasted, made whole: trimmed, the quotes of Explorer's "Copy as path" taken off, and made
        /// absolute. Used as typed, a pasted path with its quotes threw in the path checks (the window's own status line), and a
        /// relative one went relative into the install record and Steam's launch option (installer scout, 2026-10-23).
        /// </summary>
        public static string GamePath(string text)
        {
            string dir = (text ?? "").Trim().Trim('"').Trim();
            if (dir.Length == 0 || dir.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
            {
                return dir;
            }
            try
            {
                string full = Path.GetFullPath(dir);
                return full.Length > 3 ? full.TrimEnd('\\', '/') : full; // (a drive's root keeps its '\': "C:" is a folder of its own)
            }
            catch (Exception e) when (e is ArgumentException || e is NotSupportedException || e is PathTooLongException)
            {
                return dir;
            }
        }

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
            CheckPayload(openPayload);

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
                    else if (line.StartsWith("backup=", StringComparison.Ordinal)) previousBackup = ResolveBackup(game, line.Substring(7));
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
            if (previousBackup.Length > 0 && !Directory.Exists(previousBackup) && Inside(game, RecordedBackup(game, previousBackup)) == null)
            {
                // The record's backup folder is gone and outside this game folder (an older record, the game moved off a drive
                // since removed): a new one here, not one made, or failed, at the old place.
                _log("The earlier install's backup folder is gone (" + previousBackup + "); new backups go into this game folder.");
                previousBackup = "";
            }
            string backupDir = previousBackup.Length > 0 ? previousBackup : Path.Combine(game, "RavenfieldVR-backup", stamp);

            bool installBepInEx = previousInstalledBepInEx || !File.Exists(Path.Combine(game, @"BepInEx\core\BepInEx.dll"));
            // The record is written however the install ends: one that stopped part way (a locked file, a full disk) wrote none, so
            // a retry took the files it had already written for the game's own (identical: not recorded, never removed) and an
            // original it had backed up was never put back; after a first install Uninstall refused (installer scout, 2026-10-23).
            void WriteRecord(IEnumerable<string> addedFiles, string version)
            {
                string backupRef = previousBackup.Length > 0 ? previousBackup : Directory.Exists(backupDir) ? backupDir : "";
                var lines = new List<string>
                {
                    "# Ravenfield VR install " + stamp,
                    "version=" + version,
                    "installedBepInEx=" + (installBepInEx ? "True" : "False"),
                    "backup=" + RecordedBackup(game, backupRef)
                };
                lines.AddRange(addedFiles.Distinct(StringComparer.OrdinalIgnoreCase).Select(a => "added=" + a));
                lines.AddRange(replaced.Distinct(StringComparer.OrdinalIgnoreCase).Select(r => "replaced=" + r));
                Directory.CreateDirectory(Path.GetDirectoryName(manifestPath));
                // Whole or not at all: written in place, a full disk cut the record short (no version, nothing for Uninstall to undo).
                string tmp = manifestPath + ".tmp";
                File.WriteAllLines(tmp, lines, new UTF8Encoding(false));
                if (File.Exists(manifestPath))
                {
                    File.Replace(tmp, manifestPath, null);
                }
                else
                {
                    File.Move(tmp, manifestPath);
                }
            }
            try
            {
                using (Stream payload = openPayload())
                using (var zip = new ZipArchive(payload, ZipArchiveMode.Read))
                {
                    // A BepInEx 5 without its loader beside the game (winhttp.dll gone: deleted, quarantined by antivirus) loads
                    // nothing, the mod included, while the setup said it installed: the loader files go in, BepInEx's own core stays.
                    bool addLoader = !installBepInEx && !File.Exists(Path.Combine(game, "winhttp.dll"));
                    if (addLoader)
                    {
                        _log("BepInEx 5 is installed but its loader (winhttp.dll) is missing: adding the loader, keeping that BepInEx.");
                    }
                    else if (!installBepInEx)
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
                        bool loader = IsLoaderFile(rel);
                        bool mod = rel.StartsWith(@"BepInEx\plugins\RavenfieldVR\", StringComparison.OrdinalIgnoreCase) || rel.StartsWith(@"ravenfield_Data\", StringComparison.OrdinalIgnoreCase)
                                   || rel.Equals(LauncherName, StringComparison.OrdinalIgnoreCase);
                        if (!(mod || loader && installBepInEx || addLoader && LoaderFiles.Contains(rel, StringComparer.OrdinalIgnoreCase)))
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
                    if (!installBepInEx && IsLoaderFile(old))
                    {
                        // A loader a loader-only install added (its BepInEx had lost winhttp.dll): kept and still recorded. The
                        // next install skips loader entries (winhttp.dll is there now), and it was deleted here as no longer
                        // part of the release, leaving BepInEx without its loader (review of 2026-10-24's changes).
                        added.Add(old);
                        continue;
                    }
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
                // The final record inside the try: written after it, a failed write (a scanner holding the file, a full disk) left the
                // earlier version's record, which did not name what this update added or replaced (round-7 scout).
                WriteRecord(added, versionLabel);
                ExperimentalOff(game);
            }
            catch (Exception)
            {
                // What it wrote and what the earlier install had (not removed yet): Uninstall or the next install finishes it.
                try
                {
                    WriteRecord(added.Concat(ours), versionLabel + " (incomplete: the install stopped part way)");
                    _log("The install stopped part way; its record keeps what it changed, so installing again or Uninstall finishes it.");
                }
                catch (Exception recordError)
                {
                    _log("The install stopped part way, and its record could not be written either: " + recordError.Message);
                }
                throw;
            }
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

        /// <summary>
        /// Throws, before anything in the game folder changes, unless the payload is a whole zip holding the mod's plugin. An
        /// update clears the installed plugin first: a truncated or wrong download (a cut-off connection, an HTML error page)
        /// left the game with no mod at all and the record still naming the old version (installer scout, 2026-10-23).
        /// </summary>
        private static void CheckPayload(Func<Stream> openPayload)
        {
            using (Stream payload = openPayload())
            {
                if (payload == null)
                {
                    throw new InvalidOperationException("The release to install is missing. Nothing was changed.");
                }
                ZipArchive zip;
                try
                {
                    zip = new ZipArchive(payload, ZipArchiveMode.Read);
                }
                catch (InvalidDataException e)
                {
                    throw new InvalidOperationException("The release to install is damaged (not a complete zip: " + e.Message + "). Nothing was changed.", e);
                }
                using (zip)
                {
                    if (!zip.Entries.Any(e => string.Equals(e.FullName.Replace('\\', '/'), PluginEntry, StringComparison.OrdinalIgnoreCase)))
                    {
                        throw new InvalidOperationException("The release to install has no " + PluginEntry + ": not a Ravenfield VR release. Nothing was changed.");
                    }
                }
            }
        }

        private const string PluginEntry = "BepInEx/plugins/RavenfieldVR/RavenfieldVR.dll";

        /// <summary>
        /// The backup folder as the record keeps it: relative to the game folder when inside it. Kept absolute, a game moved with
        /// Steam's "Move install folder" pointed at the old place: Uninstall put no original back, and an update made its new
        /// backups there (installer scout, 2026-10-23).
        /// </summary>
        internal static string RecordedBackup(string game, string backup)
        {
            if (string.IsNullOrEmpty(backup))
            {
                return "";
            }
            string full = Path.GetFullPath(backup);
            string bas = Path.GetFullPath(game).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            return full.StartsWith(bas, StringComparison.OrdinalIgnoreCase) ? full.Substring(bas.Length) : full;
        }

        /// <summary>
        /// The record's backup folder in this game folder: a relative one under it; an absolute one (an older record) as it is
        /// while it exists, else the same RavenfieldVR-backup folder under this game folder when the game was moved with it.
        /// </summary>
        internal static string ResolveBackup(string game, string recorded)
        {
            if (string.IsNullOrEmpty(recorded))
            {
                return "";
            }
            if (!Path.IsPathRooted(recorded))
            {
                return Inside(game, recorded) ?? "";
            }
            if (Directory.Exists(recorded))
            {
                return recorded;
            }
            int at = recorded.IndexOf(@"\RavenfieldVR-backup\", StringComparison.OrdinalIgnoreCase);
            string moved = at >= 0 ? Inside(game, recorded.Substring(at + 1)) : null;
            return moved != null && Directory.Exists(moved) ? moved : recorded;
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
            // Recorded before it is written: one that fails half written (a full disk) is still the install's to remove.
            if (ours.Contains(rel))
            {
                added.Add(rel);
                Extract(entry, dst);
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
            added.Add(rel);
            Extract(entry, dst);
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
            string backup = ResolveBackup(game, lines.Where(l => l.StartsWith("backup=", StringComparison.Ordinal)).Select(l => l.Substring(7)).FirstOrDefault() ?? "");
            bool installedBepInEx = lines.Contains("installedBepInEx=True");
            // Decided before anything goes: BepInEx stays when other plugins use it or the settings are kept, and then its loader and
            // core (files this setup added with it) stay too. Deleted first, BepInEx was left in place but no longer loaded, and the
            // other mods stopped without a word (review, 2026-10-20).
            string pluginsDir = Path.Combine(game, @"BepInEx\plugins");
            // Patchers count as other plugins: deleted with BepInEx, another tool's preloader patches went too (installer scout).
            string patchersDir = Path.Combine(game, @"BepInEx\patchers");
            bool otherPlugins = Directory.Exists(pluginsDir) && Directory.EnumerateFileSystemEntries(pluginsDir)
                .Any(e => !string.Equals(Path.GetFileName(e), "RavenfieldVR", StringComparison.OrdinalIgnoreCase))
                || Directory.Exists(patchersDir) && Directory.EnumerateFileSystemEntries(patchersDir).Any();
            // Kept settings keep BepInEx, as uninstall.ps1 keeps it: decided by the mod's config alone, the launcher's choice, the
            // weapon overrides and the sandbox's poses went with BepInEx when the game had not written its config yet (round-7 scout).
            bool keepBepInEx = !installedBepInEx || otherPlugins || keepSettings;
            // A Steam launch option still running the launcher (Steam could not be changed): the launcher stays, or Ravenfield no
            // longer starts from Steam.
            bool launcherInUse = SteamLaunchOption.LocalConfigs().Any(f =>
            {
                try
                {
                    return SteamLaunchOption.Read(f).IndexOf(LauncherName, StringComparison.OrdinalIgnoreCase) >= 0;
                }
                catch (Exception e)
                {
                    // Unreadable (cut short, locked): whether it still starts the launcher is not known, so the launcher stays. Taken as
                    // no, it was deleted while Steam's option still started it (round-7 scout).
                    _log("Could not read " + f + " (" + e.Message + "): it is not known whether Steam still starts " + LauncherName + ", so it is kept.");
                    return true;
                }
            });
            int removed = 0, restored = 0;
            // The backup copies put back: deleted once the record is gone (each install and uninstall with a conflicting file left
            // another folder of them; round-7 scout). Copies not put back stay.
            var restoredFrom = new List<string>();
            // BepInEx's loader files that were put back as the game's own originals (another tool's winhttp.dll): not deleted with
            // BepInEx afterwards.
            var restoredLoader = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string line in lines.Where(l => l.StartsWith("added=", StringComparison.Ordinal)))
            {
                string rel = line.Substring(6);
                // (BepInEx's own: what Install adds as the loader.)
                bool bepInExOwn = IsLoaderFile(rel);
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
                else if (keepBepInEx && installedBepInEx && IsLoaderFile(rel))
                {
                    // BepInEx stays, so its loader stays: put back, another tool's own winhttp.dll took over from BepInEx's and every
                    // BepInEx mod (Ravenfield VR's kept settings included) stopped loading (installer scout, 2026-10-23).
                    _log("Kept BepInEx's " + rel + " (BepInEx stays); the game's original is in " + (src ?? "the backup folder") + ".");
                }
                else if (rel.StartsWith(@"BepInEx\plugins\RavenfieldVR\", StringComparison.OrdinalIgnoreCase)
                         || !keepBepInEx && installedBepInEx && rel.StartsWith(@"BepInEx\", StringComparison.OrdinalIgnoreCase))
                {
                    // Removed with its folder below: put back first, it was deleted with the folder and the log said "restored"
                    // (round-7 scout). The original stays in the backup folder.
                    _log("Left " + rel + " out of the restore: it goes with its folder; the original is in " + (src ?? "the backup folder") + ".");
                }
                else if (src != null && File.Exists(src))
                {
                    File.Copy(src, dst, true);
                    restored++;
                    restoredFrom.Add(src);
                    if (IsLoaderFile(rel))
                    {
                        restoredLoader.Add(rel);
                    }
                }
                else
                {
                    _log("No backup for " + rel + "; left the modded file in place.");
                }
            }
            DeleteDir(Path.Combine(game, @"BepInEx\plugins\RavenfieldVR"));
            // The record is gone with that folder: the copies put back go now, then their folders if nothing else is left in them.
            foreach (string copy in restoredFrom)
            {
                try
                {
                    File.Delete(copy);
                }
                catch (Exception e)
                {
                    _log("Could not delete the backup copy " + copy + ": " + e.Message);
                }
            }
            foreach (string folder in restoredFrom.Select(Path.GetDirectoryName).Distinct(StringComparer.OrdinalIgnoreCase)
                         .OrderByDescending(d => d.Length))
            {
                // Up to the backups' root, each only when empty (a copy that was not put back keeps its folders).
                for (string d = folder; d != null && backup.Length > 0 && d.TrimEnd('\\').Length >= backup.TrimEnd('\\').Length; d = Path.GetDirectoryName(d))
                {
                    try
                    {
                        if (Directory.Exists(d) && !Directory.EnumerateFileSystemEntries(d).Any())
                        {
                            Directory.Delete(d);
                        }
                        else
                        {
                            break;
                        }
                    }
                    catch (IOException)
                    {
                        break;
                    }
                }
            }
            string backupsRoot = Path.Combine(game, "RavenfieldVR-backup");
            try
            {
                if (Directory.Exists(backupsRoot) && !Directory.EnumerateFileSystemEntries(backupsRoot).Any())
                {
                    Directory.Delete(backupsRoot);
                }
            }
            catch (IOException)
            {
                // (in use: left)
            }
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
                        // (Not one just put back as the game's original: deleted, the other tool lost its loader.)
                        if (!restoredLoader.Contains(f) && File.Exists(p))
                        {
                            File.Delete(p);
                        }
                    }
                    _log("Removed BepInEx (this setup had installed it).");
                }
            }
            _log($"Uninstalled: {removed} files removed, {restored} original file(s) restored.");
        }

        /// <summary>BepInEx's own files as Install lays them out: its loader beside the game and its core.</summary>
        private static bool IsLoaderFile(string rel) =>
            LoaderFiles.Contains(rel, StringComparer.OrdinalIgnoreCase) || rel.StartsWith(@"BepInEx\core\", StringComparison.OrdinalIgnoreCase);

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
            try
            {
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
                    // A connection closed early ends the read without an error: a short file is no release.
                    if (total > 0 && done != total)
                    {
                        throw new IOException($"The download stopped at {done} of {total} bytes.");
                    }
                }
                return dest;
            }
            catch
            {
                // (The caller never learns the path of a download that failed: its temp file went with it here or not at all.)
                try { File.Delete(dest); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                throw;
            }
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

    /// <summary>
    /// Windows 11 look for the plain WinForms dialogs: the user's accent colour, a light/dark palette that
    /// follows the system theme, rounded surfaces and a dark title bar — all drawn by hand, so the setup stays
    /// one self-contained exe with no WinUI/NuGet dependency.
    /// </summary>
    internal static class Ui
    {
        public static readonly bool Dark = ReadDark();
        public static readonly Color Accent = ReadAccent();

        private static Color C(int rgb) => Color.FromArgb((rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);

        public static Color Window => Dark ? C(0x202020) : C(0xF3F3F3);
        public static Color Card => Dark ? C(0x2B2B2B) : C(0xFBFBFB);
        public static Color CardBorder => Dark ? C(0x3A3A3A) : C(0xE6E6E6);
        public static Color TextCol => Dark ? C(0xF2F2F2) : C(0x1A1A1A);
        // The status line's three states, each readable on the window in its theme (Windows 11's critical, caution and success
        // hues, the orange darkened on the light window and lightened on the dark one).
        public static Color Bad => Dark ? C(0xFF99A4) : C(0xC42B1C);
        public static Color Warn => Dark ? C(0xFFB061) : C(0xB85C00);
        public static Color Good => Dark ? C(0x6CCB5F) : C(0x0F7B0F);
        public static Color Subtle => Dark ? C(0xB4B4B4) : C(0x5D5D5D);
        public static Color Field => Dark ? C(0x2D2D2D) : C(0xFFFFFF);
        public static Color FieldBorder => Dark ? C(0x4A4A4A) : C(0xD0D0D0);
        public static Color Ctl => Dark ? C(0x323232) : C(0xFDFDFD);
        public static Color CtlHover => Dark ? C(0x3C3C3C) : C(0xF0F0F0);
        public static Color CtlDown => Dark ? C(0x2A2A2A) : C(0xE8E8E8);
        public static Color CtlBorder => Dark ? C(0x404040) : C(0xDADADA);
        public static Color Disabled => Dark ? C(0x333333) : C(0xE2E2E2);
        public static Color DisabledText => Dark ? C(0x777777) : C(0xA0A0A0);
        public static Color AccentHover => Blend(Accent, Color.White, 0.13);
        public static Color AccentDown => Blend(Accent, Color.White, 0.26);
        public static Color OnAccent => Luminance(Accent) > 0.55 ? C(0x101010) : Color.White;

        public static Color Blend(Color a, Color b, double t) => Color.FromArgb(
            (int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
        private static double Luminance(Color c) => (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0;

        private static HashSet<string> _families;
        private static string Family(params string[] names)
        {
            if (_families == null)
            {
                _families = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                using (var ifc = new System.Drawing.Text.InstalledFontCollection())
                {
                    foreach (FontFamily f in ifc.Families) _families.Add(f.Name);
                }
            }
            foreach (string n in names)
            {
                if (_families.Contains(n)) return n;
            }
            return "Segoe UI";
        }

        public static Font Display(float size, FontStyle style = FontStyle.Regular)
            => new Font(Family("Segoe UI Variable Display Semib", "Segoe UI Variable Display", "Segoe UI Semibold", "Segoe UI"), size, style);
        public static Font Body(float size, FontStyle style = FontStyle.Regular)
            => new Font(Family("Segoe UI Variable Text", "Segoe UI"), size, style);
        public static Font Mono(float size)
            => new Font(Family("Cascadia Mono", "Cascadia Code", "Consolas"), size);

        public static GraphicsPath Round(Rectangle r, int radius)
        {
            var p = new GraphicsPath();
            int d = radius * 2;
            if (d <= 0 || d > r.Width || d > r.Height) { p.AddRectangle(r); return p; }
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        private static bool ReadDark()
        {
            try
            {
                object v = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                                             "AppsUseLightTheme", 1);
                return v is int i && i == 0;
            }
            catch { return false; }
        }

        private static Color ReadAccent()
        {
            try
            {
                // DWM stores the accent as 0xAABBGGRR.
                object v = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\DWM", "AccentColor", null);
                if (v is int argb)
                {
                    return Color.FromArgb(argb & 0xFF, (argb >> 8) & 0xFF, (argb >> 16) & 0xFF);
                }
            }
            catch { }
            return C(0x005FB8); // Windows 11 default accent
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        /// <summary>Paints the window in the theme colours and gives it a dark title bar in dark mode.</summary>
        public static void Apply(Form f)
        {
            f.BackColor = Window;
            f.ForeColor = TextCol;
            Action set = () =>
            {
                try { int dark = Dark ? 1 : 0; DwmSetWindowAttribute(f.Handle, 20, ref dark, 4); } // DWMWA_USE_IMMERSIVE_DARK_MODE
                catch { }
            };
            if (f.IsHandleCreated) set(); else f.HandleCreated += (s, e) => set();
        }
    }

    /// <summary>A rounded, bordered surface (the Windows 11 "card").</summary>
    internal sealed class Card : Panel
    {
        public Card()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
            BackColor = Ui.Card;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Parent != null ? Parent.BackColor : Ui.Window);
            var r = new Rectangle(0, 0, Width - 1, Height - 1);
            using (GraphicsPath path = Ui.Round(r, 8))
            using (var b = new SolidBrush(Ui.Card))
            using (var p = new Pen(Ui.CardBorder))
            {
                g.FillPath(b, path);
                g.DrawPath(p, path);
            }
        }
    }

    /// <summary>Flat, rounded button: accent-filled when primary, subtle otherwise, with hover/press states.</summary>
    internal sealed class ModernButton : Button
    {
        public bool Primary;
        private bool _hover, _down;

        public ModernButton(bool primary = false)
        {
            Primary = primary;
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
            Cursor = Cursors.Hand;
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = _down = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { _down = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { _down = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Parent != null ? Parent.BackColor : Ui.Window);
            var r = new Rectangle(0, 0, Width - 1, Height - 1);
            Color fill, fg;
            bool border = false;
            if (!Enabled) { fill = Ui.Disabled; fg = Ui.DisabledText; }
            else if (Primary) { fill = _down ? Ui.AccentDown : _hover ? Ui.AccentHover : Ui.Accent; fg = Ui.OnAccent; }
            else { fill = _down ? Ui.CtlDown : _hover ? Ui.CtlHover : Ui.Ctl; fg = Ui.TextCol; border = true; }
            using (GraphicsPath path = Ui.Round(r, 7))
            {
                using (var b = new SolidBrush(fill)) g.FillPath(b, path);
                if (border) using (var p = new Pen(Ui.CtlBorder)) g.DrawPath(p, path);
            }
            TextRenderer.DrawText(g, Text, Font, r, fg,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }
    }

    /// <summary>Owner-drawn check box with a rounded box and an accent check — the WinForms default looks dated.</summary>
    internal sealed class ModernCheck : CheckBox
    {
        private bool _hover;

        public ModernCheck(string text)
        {
            Text = text;
            AutoSize = false;
            Height = 28;
            Font = Ui.Body(10f);
            ForeColor = Ui.TextCol;
            BackColor = Ui.Card;   // solid: transparent compositing double-painted the label (bug, 2026-10-22)
            Cursor = Cursors.Hand;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnCheckedChanged(EventArgs e) { Invalidate(); base.OnCheckedChanged(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            const int bs = 20;
            var box = new Rectangle(1, (Height - bs) / 2, bs, bs);
            using (GraphicsPath path = Ui.Round(box, 5))
            {
                if (Checked && Enabled)
                {
                    using (var b = new SolidBrush(_hover ? Ui.AccentHover : Ui.Accent)) g.FillPath(b, path);
                    using (var pen = new Pen(Ui.OnAccent, 2f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    {
                        g.DrawLines(pen, new[]
                        {
                            new Point(box.X + 5, box.Y + 10),
                            new Point(box.X + 9, box.Y + 14),
                            new Point(box.X + 15, box.Y + 6)
                        });
                    }
                }
                else
                {
                    using (var b = new SolidBrush(Enabled ? Ui.Field : Ui.Disabled)) g.FillPath(b, path);
                    using (var pen = new Pen(_hover && Enabled ? Ui.Accent : Ui.FieldBorder, 1.4f)) g.DrawPath(pen, path);
                }
            }
            var textRect = new Rectangle(box.Right + 10, 0, Width - box.Right - 10, Height);
            TextRenderer.DrawText(g, Text, Font, textRect, Enabled ? ForeColor : Ui.DisabledText,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.WordEllipsis);
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
            AutoScaleDimensions = new SizeF(96f, 96f);
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = Ui.Body(9.75f);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(400, 172);
            Ui.Apply(this);

            var title = new Label
            {
                Text = "Install another version",
                Font = Ui.Display(13.5f, FontStyle.Bold),
                ForeColor = Ui.TextCol,
                AutoSize = true,
                Location = new Point(20, 18)
            };
            var label = new Label
            {
                Text = "Pick a version from GitHub to install. This replaces the version you have now.",
                Font = Ui.Body(9.5f),
                ForeColor = Ui.Subtle,
                Location = new Point(20, 48),
                MaximumSize = new Size(360, 0),
                AutoSize = true
            };
            _combo.DropDownStyle = ComboBoxStyle.DropDownList;
            _combo.FlatStyle = FlatStyle.Flat;
            _combo.Font = Ui.Body(10f);
            _combo.BackColor = Ui.Field;
            _combo.ForeColor = Ui.TextCol;
            _combo.SetBounds(20, 88, 360, 28);
            foreach (GitHub.Release r in releases)
            {
                _combo.Items.Add(r.Version == installed ? r.Version + "   (installed now)" : r.Version);
            }
            _combo.SelectedIndex = 0;

            var ok = new ModernButton(true) { Text = "Install", DialogResult = DialogResult.OK, Font = Ui.Body(10f), Bounds = new Rectangle(192, 126, 92, 34) };
            var cancel = new ModernButton() { Text = "Cancel", DialogResult = DialogResult.Cancel, Font = Ui.Body(10f), Bounds = new Rectangle(290, 126, 90, 34) };
            ok.Click += (s, e) => Selected = _releases[_combo.SelectedIndex];
            Controls.AddRange(new Control[] { title, label, _combo, ok, cancel });
            AcceptButton = ok;
            CancelButton = cancel;
        }
    }

    internal sealed class SetupForm : Form
    {
        private readonly TextBox _path = new TextBox();
        private readonly Label _status = new Label();
        private readonly ModernButton _install = new ModernButton(true);
        private readonly ModernButton _uninstall = new ModernButton();
        private readonly ModernButton _close = new ModernButton();
        private readonly ModernCheck _keepSettings = new ModernCheck("Keep my settings when uninstalling");
        private readonly ModernCheck _steamLaunch = new ModernCheck("Pressing Play in Steam opens the launcher (VR or flat)");
        private readonly ModernCheck _steamPlay = new ModernCheck("Add \"" + SteamPlayEntry.Description + "\" to Steam's Play menu");
        private readonly LinkLabel _openConfig = new LinkLabel();
        private readonly LinkLabel _advanced = new LinkLabel();
        private readonly TextBox _log = new TextBox();
        private readonly Installer _installer;

        public SetupForm()
        {
            _installer = new Installer(Log);
            Text = "Ravenfield VR Setup";
            // Declare the design DPI (96) before the font so WinForms scales from the right baseline — setting a
            // variable-metric font first made it derive a wrong factor and shrink the whole window (bug, 2026-10-22).
            AutoScaleDimensions = new SizeF(96f, 96f);
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = Ui.Body(9.75f);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(640, 516);
            Ui.Apply(this);
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); ShowIcon = true; } catch { /* no icon is fine */ }

            const int M = 24;    // outer margin
            const int W = 592;   // content width (640 - 2*M)

            var title = new Label { Text = "Ravenfield VR " + Installer.Version, Font = Ui.Display(20f, FontStyle.Bold), ForeColor = Ui.TextCol, AutoSize = true, Location = new Point(M, 22) };
            var subtitle = new Label { Text = "Plays Ravenfield in your headset through SteamVR.", Font = Ui.Body(10f), ForeColor = Ui.Subtle, AutoSize = true, Location = new Point(M, 66) };

            var folderLabel = new Label { Text = "Ravenfield folder", Font = Ui.Body(9.5f), ForeColor = Ui.Subtle, AutoSize = true, Location = new Point(M, 98) };
            _path.BorderStyle = BorderStyle.FixedSingle;
            _path.BackColor = Ui.Field;
            _path.ForeColor = Ui.TextCol;
            _path.Font = Ui.Body(10f);
            _path.SetBounds(M, 120, W - 100, 30);
            _path.TextChanged += (s, e) => RefreshState();
            var browse = new ModernButton { Text = "Browse…", Font = Ui.Body(9.75f) };
            browse.SetBounds(M + W - 88, 119, 88, 32);
            browse.Click += (s, e) => Browse();

            _status.SetBounds(M, 160, W, 34);
            _status.Font = Ui.Body(9.75f);
            _status.ForeColor = Ui.TextCol;

            // --- Options card ---
            var optCard = new Card();
            optCard.SetBounds(M, 202, W, 108);
            _steamLaunch.Checked = true;
            _steamLaunch.BackColor = Ui.Card;
            _steamLaunch.SetBounds(16, 10, W - 36, 28);
            _steamPlay.Checked = true;
            _steamPlay.BackColor = Ui.Card;
            _steamPlay.SetBounds(16, 40, W - 36, 28);
            _keepSettings.Checked = true;
            _keepSettings.BackColor = Ui.Card;
            _keepSettings.SetBounds(16, 70, W - 200, 28);
            _openConfig.Text = "Open the settings file";
            _openConfig.Font = Ui.Body(9.5f);
            _openConfig.BackColor = Ui.Card;
            _openConfig.LinkColor = Ui.Accent;
            _openConfig.ActiveLinkColor = Ui.AccentDown;
            _openConfig.LinkBehavior = LinkBehavior.HoverUnderline;
            _openConfig.AutoSize = true;
            _openConfig.Location = new Point(W - 156, 74);
            _openConfig.LinkClicked += (s, e) => OpenConfig();
            optCard.Controls.AddRange(new Control[] { _steamLaunch, _steamPlay, _keepSettings, _openConfig });

            // --- Log card (borderless text inside a rounded surface) ---
            var logCard = new Card();
            logCard.SetBounds(M, 324, W, 100);
            _log.Multiline = true;
            _log.ReadOnly = true;
            _log.BorderStyle = BorderStyle.None;
            _log.ScrollBars = ScrollBars.Vertical;
            _log.BackColor = Ui.Card;
            _log.ForeColor = Ui.Subtle;
            _log.Font = Ui.Mono(9f);
            _log.SetBounds(14, 11, W - 28, 78);
            logCard.Controls.Add(_log);

            _install.Font = Ui.Body(10f);
            _install.SetBounds(M, 440, 150, 36);
            _install.Click += async (s, e) =>
            {
                string game = Installer.GamePath(_path.Text);
                bool ok = await RunAsync(() => _installer.Install(game), "Installing…", null);
                if (ok && (_steamLaunch.Checked || _steamPlay.Checked))
                {
                    await SetSteamOptions(game, true, _steamLaunch.Checked, _steamPlay.Checked);
                }
                if (ok)
                {
                    Log(_steamUnchanged
                        ? "Done. Start Ravenfield with RavenfieldVR-Launcher.exe in the game folder, or with SteamVR running."
                        : _steamLaunch.Checked
                        ? "Done. Press Play on Ravenfield in Steam and pick " + (_steamPlay.Checked ? "\"" + SteamPlayEntry.Description + "\", or Ravenfield for VR or flat." : "VR or flat.")
                        : _steamPlay.Checked
                            ? "Done. Press Play on Ravenfield in Steam and pick \"" + SteamPlayEntry.Description + "\"."
                            : "Done. Start Ravenfield with RavenfieldVR-Launcher.exe in the game folder, or with SteamVR running.");
                }
            };
            _uninstall.Text = "Uninstall";
            _uninstall.Font = Ui.Body(10f);
            _uninstall.SetBounds(M + 158, 440, 120, 36);
            _uninstall.Click += async (s, e) =>
            {
                bool keep = _keepSettings.Checked;
                string game = Installer.GamePath(_path.Text);
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
            _close.Font = Ui.Body(10f);
            _close.SetBounds(M + W - 100, 440, 100, 36);
            _close.Click += (s, e) => Close();
            AcceptButton = _install;
            CancelButton = _close;

            // Advanced: install any published version straight from GitHub, not only the one baked into this setup.
            _advanced.Text = "Advanced: install another version from GitHub…";
            _advanced.Font = Ui.Body(9.75f);
            _advanced.LinkColor = Ui.Accent;
            _advanced.ActiveLinkColor = Ui.AccentDown;
            _advanced.LinkBehavior = LinkBehavior.HoverUnderline;
            _advanced.AutoSize = true;
            _advanced.Location = new Point(M, 486);
            _advanced.LinkClicked += async (s, e) => await UpdateFromGitHubFlow();

            Controls.AddRange(new Control[] { title, subtitle, folderLabel, _path, browse, _status, optCard, logCard, _install, _uninstall, _close, _advanced });

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
            string game = Installer.GamePath(_path.Text);
            bool valid = Installer.IsRavenfieldFolder(game);
            string installed = valid ? Installer.InstalledVersion(game) : null;
            string problem = valid ? Installer.BepInExProblem(game) : null;
            _status.ForeColor = Ui.TextCol;
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
                _status.Text = "Nothing is installed.";
                _status.ForeColor = Ui.Bad;
            }
            // Compared as versions: any other version read as an update, so an older setup run over a newer install went back a
            // version under an "Update" button (installer scout, 2026-10-23). An install that stopped part way is repaired.
            bool incomplete = installed != null && installed.IndexOf("incomplete", StringComparison.OrdinalIgnoreCase) >= 0;
            // Only a real version is compared ('unknown version' from a copy without a record, a '-rc1' suffix): compared as text,
            // they read as newer and the button said Downgrade.
            string installedNumber = installed?.Split(' ')[0].Split('-')[0];
            bool comparable = !string.IsNullOrEmpty(installedNumber) && char.IsDigit(installedNumber[0]);
            int ahead = installed == null ? 0 : comparable ? GitHub.CompareVersions(Installer.Version, installedNumber) : 1;
            if (installed != null && problem == null)
            {
                // Red: nothing installed (above). Orange: an update is suggested (an older or unfinished install). Green: up to date.
                string on = comparable ? installed : "an unknown version";
                _status.Text = incomplete ? $"Ravenfield VR {installed}: install again to finish it."
                    : ahead == 0 ? $"You are up to date! (Version {installedNumber})"
                    : ahead > 0 ? $"You are on version {on}. An Update is Suggested."
                    : $"You are up to date! (Version {installed}, newer than this setup's {Installer.Version}: installing goes back to {Installer.Version}.)";
                _status.ForeColor = incomplete || ahead > 0 ? Ui.Warn : Ui.Good;
            }
            _install.Text = installed == null ? "Install" : incomplete ? "Repair" : ahead == 0 ? "Reinstall" : ahead > 0 ? "Update" : "Downgrade";
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
            string cfg = ConfigPath(Installer.GamePath(_path.Text));
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
        /// <summary>Steam was left as it was (not closed when asked): the last message does not tell the player to use its Play entry.</summary>
        private bool _steamUnchanged;

        private async Task SetSteamOptions(string game, bool add, bool launchOption, bool playEntry)
        {
            _steamUnchanged = false;
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
                    _steamUnchanged = true;
                    CopyOption();
                    return;
                }
                Log("Closing Steam…");
                if (!await Task.Run(() => SteamLaunchOption.ShutdownSteam(TimeSpan.FromSeconds(30))))
                {
                    Log("Steam did not close in time; its Play options were not changed.");
                    _steamUnchanged = true;
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
            string game = Installer.GamePath(_path.Text);
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
            _status.ForeColor = Ui.TextCol;
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
            _status.ForeColor = Ui.TextCol; // (working: not the state's colour; RefreshState sets it again when done)
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
