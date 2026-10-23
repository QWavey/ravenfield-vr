using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using BepInEx;
using RavenfieldVR.Core;
using UnityEngine;

namespace RavenfieldVR.Weapons
{
    /// <summary>
    /// The parked ammunition each weapon prefab's analysis found (<see cref="WeaponRigData.SparePartPaths"/>), kept on disk
    /// between sessions: BepInEx/cache/RavenfieldVR-parked-parts.txt. A dropped or holstered copy hides these parts
    /// (WeaponDrops.HideParkedAmmo), and that was the only thing the first drop of a type needed the whole analysis for: a
    /// type the menu's warm-up had not reached analysed on a soldier's death frame (the user's session of 2026-10-20 night:
    /// 235-300 ms drops of the PKP, PKM, Type 80 and BRN180s; the warm-up had reached 78 of 286 weapons). With the paths
    /// from an earlier session the copy is built without it.
    ///
    /// An entry counts only for the prefab it was taken from: its transforms' count and names, hashed (a weapon mod updated
    /// since is analysed again). Every analysis of a prefab writes its entry again, so a rule changed in a later build is
    /// carried over the next time the warm-up (or a drop) analyses that weapon.
    /// </summary>
    internal static class ParkedPartsCache
    {
        private const string Header = "# RavenfieldVR parked ammunition v1";

        public static string FilePath => Path.Combine(Paths.CachePath, "RavenfieldVR-parked-parts.txt");

        private struct Entry
        {
            public uint Sig;
            public List<int[]> Paths;
        }

        private static Dictionary<string, Entry> _entries;
        private static bool _dirty;
        private static float _nextWrite;

        /// <summary>The parked parts kept for this prefab, if its entry was taken from the same prefab.</summary>
        internal static bool TryGet(string key, Transform prefabRoot, out List<int[]> paths)
        {
            paths = null;
            if (prefabRoot == null || !Load().TryGetValue(key, out Entry e) || e.Sig != Signature(prefabRoot))
            {
                return false;
            }
            paths = e.Paths;
            return true;
        }

        /// <summary>
        /// Keeps a prefab's parked parts (an analysis of a weapon in a scene is not kept: the game and the mod add to a live
        /// weapon's transforms, so its hash would never match the prefab's).
        /// </summary>
        internal static void Put(string key, Weapon analysed, List<int[]> paths)
        {
            if (analysed == null || analysed.gameObject.scene.IsValid() || analysed.transform.parent != null || key.IndexOf('\t') >= 0
                || key.IndexOf('\n') >= 0)
            {
                return;
            }
            var kept = new List<int[]>();
            foreach (int[] p in paths)
            {
                if (p != null)
                {
                    kept.Add(p);
                }
            }
            uint sig = Signature(analysed.transform);
            Dictionary<string, Entry> entries = Load();
            if (entries.TryGetValue(key, out Entry old) && old.Sig == sig && Same(old.Paths, kept))
            {
                return;
            }
            entries[key] = new Entry { Sig = sig, Paths = kept };
            _dirty = true;
        }

        /// <summary>Writes the file when an entry changed, at most every 10 s (called from the drops' update).</summary>
        internal static void WriteIfDue()
        {
            if (!_dirty || Time.unscaledTime < _nextWrite)
            {
                return;
            }
            _nextWrite = Time.unscaledTime + 10f;
            Write();
        }

        internal static void Write()
        {
            if (!_dirty || _entries == null)
            {
                return;
            }
            _dirty = false;
            string tmp = null;
            try
            {
                var sb = new StringBuilder();
                sb.Append(Header).Append('\n');
                foreach (KeyValuePair<string, Entry> kv in _entries)
                {
                    sb.Append(kv.Key).Append('\t').Append(kv.Value.Sig.ToString("x8")).Append('\t');
                    for (int i = 0; i < kv.Value.Paths.Count; i++)
                    {
                        if (i > 0)
                        {
                            sb.Append(';');
                        }
                        int[] p = kv.Value.Paths[i];
                        for (int j = 0; j < p.Length; j++)
                        {
                            if (j > 0)
                            {
                                sb.Append(',');
                            }
                            sb.Append(p[j]);
                        }
                    }
                    sb.Append('\n');
                }
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                // A temp file of this process's own (two games sharing the folder), swapped in whole: a failed move after a delete
                // left no file at all.
                tmp = FilePath + "." + System.Diagnostics.Process.GetCurrentProcess().Id + ".tmp";
                File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(false));
                if (File.Exists(FilePath))
                {
                    try
                    {
                        File.Replace(tmp, FilePath, null);
                    }
                    catch (Exception) when (File.Exists(tmp))
                    {
                        File.Delete(FilePath); // (a runtime without File.Replace)
                        File.Move(tmp, FilePath);
                    }
                }
                else
                {
                    File.Move(tmp, FilePath);
                }
            }
            catch (Exception ex)
            {
                _dirty = true; // tried again at the next write (else the entries missed it for the session)
                Log.WarnOnce("parkedcache-write", "Writing the parked-ammunition cache failed: " + ex.Message);
                // Its temp file not left behind: named by the process, a new one was left at every session whose swap failed (the
                // cache held by another game).
                try
                {
                    if (tmp != null && File.Exists(tmp))
                    {
                        File.Delete(tmp);
                    }
                }
                catch (Exception)
                {
                    // (held as well: nothing more to do)
                }
            }
        }

        private static Dictionary<string, Entry> Load()
        {
            if (_entries != null)
            {
                return _entries;
            }
            _entries = new Dictionary<string, Entry>();
            try
            {
                if (!File.Exists(FilePath))
                {
                    return _entries;
                }
                string[] lines = File.ReadAllLines(FilePath, Encoding.UTF8);
                if (lines.Length == 0 || lines[0] != Header)
                {
                    return _entries; // an older format: built again from the analyses
                }
                for (int i = 1; i < lines.Length; i++)
                {
                    string[] f = lines[i].Split('\t');
                    if (f.Length != 3 || f[0].Length == 0 || !uint.TryParse(f[1], System.Globalization.NumberStyles.HexNumber, null, out uint sig)
                        || !TryPaths(f[2], out List<int[]> paths))
                    {
                        continue;
                    }
                    _entries[f[0]] = new Entry { Sig = sig, Paths = paths };
                }
                Log.Info($"Parked-ammunition cache: {_entries.Count} weapon(s) from earlier sessions.");
            }
            catch (Exception ex)
            {
                Log.WarnOnce("parkedcache-read", "Reading the parked-ammunition cache failed: " + ex.Message);
                _entries.Clear();
            }
            return _entries;
        }

        private static bool TryPaths(string s, out List<int[]> paths)
        {
            paths = new List<int[]>();
            if (s.Length == 0)
            {
                return true;
            }
            foreach (string part in s.Split(';'))
            {
                string[] n = part.Split(',');
                var p = new int[n.Length];
                for (int i = 0; i < n.Length; i++)
                {
                    if (!int.TryParse(n[i], out p[i]) || p[i] < 0)
                    {
                        return false;
                    }
                }
                paths.Add(p);
            }
            return true;
        }

        private static bool Same(List<int[]> a, List<int[]> b)
        {
            if (a.Count != b.Count)
            {
                return false;
            }
            for (int i = 0; i < a.Count; i++)
            {
                if (a[i].Length != b[i].Length)
                {
                    return false;
                }
                for (int j = 0; j < a[i].Length; j++)
                {
                    if (a[i][j] != b[i][j])
                    {
                        return false;
                    }
                }
            }
            return true;
        }

        /// <summary>The prefab's transforms, their count and names in order (FNV-1a).</summary>
        internal static uint Signature(Transform root)
        {
            uint h = 2166136261;
            Transform[] all = root.GetComponentsInChildren<Transform>(true);
            h = (h ^ (uint)all.Length) * 16777619;
            foreach (Transform t in all)
            {
                string name = t.name;
                for (int i = 0; i < name.Length; i++)
                {
                    h = (h ^ name[i]) * 16777619;
                }
                h = (h ^ (uint)t.childCount) * 16777619;
            }
            return h;
        }
    }
}
