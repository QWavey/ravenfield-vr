// Adds / removes a "Ravenfield VR" entry in the list Steam shows when you press Play on Ravenfield
// ("Ravenfield", "Ravenfield (No Mods)", "Ravenfield (Safe Mode)", ...).
// Those entries are not a user setting: they come from Valve's servers with the game's app info, and Steam caches
// them in appcache\appinfo.vdf (binary KeyValues; app 636480 > appinfo > config > launch). Each app record there
// carries a SHA-1 over its key-value bytes that Steam checks, so the record's size and that checksum are rewritten
// with the entry; the record's other checksum (Valve's, of the text form) is left as Valve sent it, as it is for
// most records anyway. Steam replaces the whole record when Ravenfield's app info changes on Valve's side (a game
// update), which drops the entry; running Setup again adds it back. Written only while Steam is closed: Steam keeps
// the cache in memory and writes it back over the file.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace RavenfieldVR.Setup
{
    internal static class SteamPlayEntry
    {
        public const string Description = "Ravenfield VR";
        private const uint AppId = 636480;

        // The entry: ravenfield.exe -vr on Windows. With the launcher in Ravenfield's launch options, Steam runs
        // "RavenfieldVR-Launcher.exe" "...\ravenfield.exe" -vr, and the launcher starts VR straight away.
        private static readonly string[][] Values =
        {
            new[] { "executable", "ravenfield.exe" },
            new[] { "arguments", "-vr" },
            new[] { "description", Description },
            new[] { "type", "option3" },
        };

        public static string CachePath() => Path.Combine(Installer.SteamDir(), "appcache", "appinfo.vdf");

        public static bool Apply(Action<string> log) => Change(CachePath(), true, log);

        public static bool Remove(Action<string> log) => Change(CachePath(), false, log);

        /// <summary>
        /// Whether the entry is there once <see cref="Change"/> has been asked to add it: it returns whether the file changed, so an entry already
        /// in the list read as a failure, and every Setup run after the first ended with "start the launcher by hand" (installer scout, 2026-11-07).
        /// </summary>
        public static bool Ensure(string file, Action<string> log)
        {
            if (Change(file, true, log))
            {
                return true;
            }
            try
            {
                return File.Exists(file) && IsPresent(file) == true;
            }
            catch (Exception e) when (e is IOException || e is InvalidDataException || e is UnauthorizedAccessException)
            {
                return false;
            }
        }

        /// <summary>Whether the cache holds the entry; null when Ravenfield's app info is not in it.</summary>
        public static bool? IsPresent(string file)
        {
            var info = AppInfoFile.Load(file);
            KvNode launch = info.LaunchBlock(AppId, out _, out _);
            return launch == null ? (bool?)null : Ours(launch).Any();
        }

        /// <summary>Adds or removes the entry in an appinfo.vdf. Returns whether the file was changed.</summary>
        public static bool Change(string file, bool add, Action<string> log)
        {
            if (!File.Exists(file))
            {
                log("Steam's app cache was not found (" + file + "); the Ravenfield VR entry was not " + (add ? "added." : "removed."));
                return false;
            }
            if (string.Equals(Path.GetFullPath(file), Path.GetFullPath(CachePath()), StringComparison.OrdinalIgnoreCase) && SteamLaunchOption.SteamRunning())
            {
                log("Steam is running: the Ravenfield VR entry in Steam's Play list was not changed (Steam writes its cache back when it exits).");
                return false;
            }
            var info = AppInfoFile.Load(file);
            KvNode launch = info.LaunchBlock(AppId, out int recordAt, out int recordEnd);
            if (launch == null)
            {
                log(add ? "Steam has no app info for Ravenfield yet (start Ravenfield once from Steam, then run Setup again); the Play list entry was not added." : "Steam's cache has no app info for Ravenfield; nothing to remove.");
                return false;
            }
            List<KvNode> ours = Ours(launch).ToList();
            byte[] body = info.Body(recordAt, recordEnd);
            int bodyAt = recordAt + AppInfoFile.HeaderSize;
            byte[] updated;
            if (add)
            {
                if (ours.Count > 0)
                {
                    log("Steam's Play list already has the Ravenfield VR entry.");
                    return false;
                }
                int next = launch.Children.Select(c => int.TryParse(c.Key, out int n) ? n + 1 : 0).DefaultIfEmpty(0).Max();
                byte[] entry = EncodeEntry(info, next.ToString());
                // Before the launch block's end marker.
                int at = launch.End - 1 - bodyAt;
                updated = body.Take(at).Concat(entry).Concat(body.Skip(at)).ToArray();
            }
            else
            {
                if (ours.Count == 0)
                {
                    return false;
                }
                var keep = new List<byte>(body.Length);
                int from = 0;
                foreach (KvNode node in ours.OrderBy(n => n.Start))
                {
                    keep.AddRange(body.Skip(from).Take(node.Start - bodyAt - from));
                    from = node.End - bodyAt;
                }
                keep.AddRange(body.Skip(from));
                updated = keep.ToArray();
            }
            byte[] result = info.WithRecordBody(recordAt, recordEnd, updated);

            // The new file has to read back whole, with the entry present (or gone), before it replaces Steam's.
            string temp = file + ".rfvr-tmp";
            File.WriteAllBytes(temp, result);
            bool? check;
            try
            {
                check = IsPresent(temp);
            }
            catch (Exception e)
            {
                File.Delete(temp);
                throw new InvalidDataException("the changed Steam app cache did not read back (" + e.Message + "); Steam's file was left as it was.");
            }
            if (check != add)
            {
                File.Delete(temp);
                throw new InvalidDataException("the changed Steam app cache did not have the expected entry; Steam's file was left as it was.");
            }
            try
            {
                // (The first copy is the one to keep, as with Steam's own config: written over at every change, the copy of
                // Steam's file as it was before the setup ever touched it was gone after the second; scout, 2026-11-11.)
                if (!File.Exists(file + ".rfvr-backup"))
                {
                    File.Copy(file, file + ".rfvr-backup", false);
                }
                SteamLaunchOption.ReplaceFile(temp, file);
            }
            finally
            {
                // (A locked or read-only cache threw here and left a whole copy of Steam's cache beside it.)
                if (File.Exists(temp))
                {
                    File.Delete(temp);
                }
            }
            log(add ? "Added \"Ravenfield VR\" to the list Steam shows when you press Play on Ravenfield." : "Removed \"Ravenfield VR\" from Steam's Play list.");
            return true;
        }

        private static IEnumerable<KvNode> Ours(KvNode launch) =>
            launch.Children.Where(c => c.Children != null && c.Child("description")?.Text == Description && (c.Child("arguments")?.Text ?? "").Contains("-vr"));

        private static byte[] EncodeEntry(AppInfoFile info, string key)
        {
            var b = new List<byte>();
            b.Add(0x00);
            info.WriteKey(b, key);
            foreach (string[] kv in Values)
            {
                b.Add(0x01);
                info.WriteKey(b, kv[0]);
                b.AddRange(Encoding.UTF8.GetBytes(kv[1]));
                b.Add(0);
            }
            b.Add(0x00);
            info.WriteKey(b, "config");
            b.Add(0x01);
            info.WriteKey(b, "oslist");
            b.AddRange(Encoding.UTF8.GetBytes("windows"));
            b.Add(0);
            b.Add(0x08);
            b.Add(0x08);
            return b.ToArray();
        }
    }

    /// <summary>One key-value of a binary KeyValues block, with where it sits in the file.</summary>
    internal sealed class KvNode
    {
        public string Key;
        /// <summary>A string value (null for a block or a number).</summary>
        public string Text;
        /// <summary>The children of a block (null for a value).</summary>
        public List<KvNode> Children;
        /// <summary>From the type byte to just after the value (a block: after its end marker).</summary>
        public int Start, End;

        public KvNode Child(string key) => Children?.FirstOrDefault(c => string.Equals(c.Key, key, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Steam's appcache\appinfo.vdf, versions 28 and 29: a header, app records, an app id of 0, and (29) a table of
    /// the key strings that the records refer to by index. A record: app id, size (of the rest), info state, last
    /// update, PICS token, text SHA-1, change number, SHA-1 of the key-value bytes, then the key-values.
    /// </summary>
    internal sealed class AppInfoFile
    {
        public const int HeaderSize = 68;
        private const uint V28 = 0x07564428, V29 = 0x07564429;

        private byte[] _data;
        private bool _v29;
        private int _recordsAt;
        private int _tableAt;
        private readonly List<string> _strings = new List<string>();
        private readonly Dictionary<string, int> _index = new Dictionary<string, int>();
        private readonly List<string> _added = new List<string>();

        public static AppInfoFile Load(string file)
        {
            var f = new AppInfoFile { _data = File.ReadAllBytes(file) };
            uint magic = BitConverter.ToUInt32(f._data, 0);
            if (magic != V28 && magic != V29)
            {
                throw new InvalidDataException($"Steam's app cache has a format this setup does not know (0x{magic:x8}).");
            }
            f._v29 = magic == V29;
            f._recordsAt = f._v29 ? 16 : 8;
            if (f._v29)
            {
                long table = BitConverter.ToInt64(f._data, 8);
                if (table < f._recordsAt || table + 4 > f._data.Length)
                {
                    throw new InvalidDataException("Steam's app cache has a broken string table offset.");
                }
                f._tableAt = (int)table;
                int count = BitConverter.ToInt32(f._data, f._tableAt);
                int p = f._tableAt + 4;
                for (int i = 0; i < count; i++)
                {
                    int e = Array.IndexOf(f._data, (byte)0, p);
                    if (e < 0)
                    {
                        throw new InvalidDataException("Steam's app cache string table ends early.");
                    }
                    string s = Encoding.UTF8.GetString(f._data, p, e - p);
                    f._strings.Add(s);
                    if (!f._index.ContainsKey(s))
                    {
                        f._index[s] = i;
                    }
                    p = e + 1;
                }
            }
            // Every record has to chain up to the end marker (and, in 29, the string table).
            int at = f._recordsAt;
            while (true)
            {
                if (at + 4 > f._data.Length)
                {
                    throw new InvalidDataException("Steam's app cache ends inside a record.");
                }
                if (BitConverter.ToUInt32(f._data, at) == 0)
                {
                    break;
                }
                at = f.RecordEnd(at);
            }
            if (f._v29 && at + 4 != f._tableAt)
            {
                throw new InvalidDataException("Steam's app cache records do not end at its string table.");
            }
            return f;
        }

        /// <summary>The appinfo > config > launch block of one app, and where its record starts and ends; null if absent.</summary>
        public KvNode LaunchBlock(uint appId, out int recordAt, out int recordEnd)
        {
            recordAt = recordEnd = -1;
            int at = _recordsAt;
            while (true)
            {
                uint id = BitConverter.ToUInt32(_data, at);
                if (id == 0)
                {
                    return null;
                }
                int end = RecordEnd(at);
                if (id == appId)
                {
                    int p = at + HeaderSize;
                    KvNode root = ReadBlock(ref p, end);
                    if (p != end)
                    {
                        throw new InvalidDataException("Ravenfield's record in Steam's app cache has bytes after its key-values.");
                    }
                    byte[] sha;
                    using (var sha1 = SHA1.Create())
                    {
                        sha = sha1.ComputeHash(_data, at + HeaderSize, end - at - HeaderSize);
                    }
                    if (!sha.SequenceEqual(_data.Skip(at + 48).Take(20)))
                    {
                        throw new InvalidDataException("Ravenfield's record in Steam's app cache fails its checksum.");
                    }
                    recordAt = at;
                    recordEnd = end;
                    return root.Child("appinfo")?.Child("config")?.Child("launch");
                }
                at = end;
            }
        }

        /// <summary>
        /// Where the record at <paramref name="at"/> ends, by its size field, which must leave a whole header and stay inside the
        /// file. Unchecked, a damaged size (0xFFFFFFF8 read as -8) stepped back or not at all and the walk never ended: the setup
        /// hung on a damaged cache (bug hunt, 2026-10-22).
        /// </summary>
        private int RecordEnd(int at)
        {
            uint size = BitConverter.ToUInt32(_data, at + 4);
            if (size < HeaderSize - 8 || size > (uint)(_data.Length - at - 8))
            {
                throw new InvalidDataException($"a record of Steam's app cache has a size that does not fit it ({size} bytes at {at}).");
            }
            return at + 8 + (int)size;
        }

        public byte[] Body(int recordAt, int recordEnd) => _data.Skip(recordAt + HeaderSize).Take(recordEnd - recordAt - HeaderSize).ToArray();

        /// <summary>The whole file with one record's key-values replaced: its size and binary SHA-1 redone, and the string table (29) moved and extended.</summary>
        public byte[] WithRecordBody(int recordAt, int recordEnd, byte[] body)
        {
            var header = new byte[HeaderSize];
            Array.Copy(_data, recordAt, header, 0, HeaderSize);
            Array.Copy(BitConverter.GetBytes((uint)(body.Length + HeaderSize - 8)), 0, header, 4, 4);
            using (var sha1 = SHA1.Create())
            {
                Array.Copy(sha1.ComputeHash(body), 0, header, 48, 20);
            }
            var o = new List<byte>(_data.Length + body.Length);
            o.AddRange(_data.Take(recordAt));
            o.AddRange(header);
            o.AddRange(body);
            int restEnd = _v29 ? _tableAt : _data.Length;
            o.AddRange(_data.Skip(recordEnd).Take(restEnd - recordEnd));
            if (_v29)
            {
                int tableAt = o.Count;
                o.AddRange(BitConverter.GetBytes(_strings.Count + _added.Count));
                o.AddRange(_data.Skip(_tableAt + 4));
                foreach (string s in _added)
                {
                    o.AddRange(Encoding.UTF8.GetBytes(s));
                    o.Add(0);
                }
                byte[] offset = BitConverter.GetBytes((long)tableAt);
                for (int i = 0; i < 8; i++)
                {
                    o[8 + i] = offset[i];
                }
            }
            return o.ToArray();
        }

        /// <summary>A key: (29) its index in the string table, added to the table when new; (28) the string itself.</summary>
        public void WriteKey(List<byte> b, string key)
        {
            if (!_v29)
            {
                b.AddRange(Encoding.UTF8.GetBytes(key));
                b.Add(0);
                return;
            }
            if (!_index.TryGetValue(key, out int i))
            {
                i = _strings.Count + _added.Count;
                _added.Add(key);
                _index[key] = i;
            }
            b.AddRange(BitConverter.GetBytes(i));
        }

        private string ReadKey(ref int p, int end)
        {
            if (_v29)
            {
                int i = BitConverter.ToInt32(_data, p);
                p += 4;
                if (i < 0 || i >= _strings.Count)
                {
                    throw new InvalidDataException("a key index outside Steam's string table.");
                }
                return _strings[i];
            }
            return ReadString(ref p, end);
        }

        private string ReadString(ref int p, int end)
        {
            int e = Array.IndexOf(_data, (byte)0, p, end - p);
            if (e < 0)
            {
                throw new InvalidDataException("a string runs past its record.");
            }
            string s = Encoding.UTF8.GetString(_data, p, e - p);
            p = e + 1;
            return s;
        }

        private KvNode ReadBlock(ref int p, int end)
        {
            var block = new KvNode { Children = new List<KvNode>() };
            while (true)
            {
                if (p >= end)
                {
                    throw new InvalidDataException("a key-value block runs past its record.");
                }
                int start = p;
                byte type = _data[p++];
                if (type == 0x08)
                {
                    return block;
                }
                var node = new KvNode { Start = start, Key = ReadKey(ref p, end) };
                switch (type)
                {
                    case 0x00:
                        node.Children = ReadBlock(ref p, end).Children;
                        break;
                    case 0x01:
                        node.Text = ReadString(ref p, end);
                        break;
                    case 0x02:
                    case 0x03:
                    case 0x04:
                    case 0x06:
                        p += 4;
                        break;
                    case 0x07:
                    case 0x0A:
                        p += 8;
                        break;
                    default:
                        throw new InvalidDataException($"a key-value type this setup does not know (0x{type:x2}).");
                }
                node.End = p;
                block.Children.Add(node);
            }
        }
    }
}
