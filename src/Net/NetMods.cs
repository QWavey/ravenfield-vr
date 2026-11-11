using System;
using System.Collections.Generic;
using RavenfieldVR.Core;
using Steamworks;
using UnityEngine;

namespace RavenfieldVR.Net
{
    /// <summary>
    /// The host's workshop mods, told to whoever joins (the user, 2026-11-10: "People playing online in multiplayer need to download
    /// the mods from the Host if they dont have them (except the vr mod they need to have it). they should be prompted"). The host
    /// sends the Steam workshop numbers of the mods switched on on his PC; a joined PC compares them with its own, says how many it
    /// lacks, and on the player's word subscribes to them through the game's own Steam wrapper. Steam downloads them, and the game
    /// loads a workshop item by itself once it is installed (ModManager.OnItemInstalled). Nothing is sent from PC to PC but the
    /// numbers: the files come from the workshop. A mod that is no workshop item (a folder in the game's mods directory) cannot be
    /// fetched this way and is not told.
    /// </summary>
    internal static class NetMods
    {
        internal const byte Kind = 27;
        private const int PerMessage = 64, Most = 2048;

        private static readonly NetWriter Out = new NetWriter(PerMessage * 8 + 16);
        private static readonly List<ulong> Hosts = new List<ulong>();
        private static readonly HashSet<ulong> Wanted = new HashSet<ulong>();
        private static int _expected = -1;
        private static bool _said;

        /// <summary>How many workshop mods the host has switched on (0 until he told, or with none).</summary>
        internal static int HostCount => _expected >= 0 && Hosts.Count >= _expected ? Hosts.Count : 0;

        /// <summary>The player said yes: Steam was asked for the ones this PC lacks.</summary>
        internal static bool Asked { get; private set; }

        /// <summary>The workshop mods switched on on this PC.</summary>
        internal static List<ulong> Mine()
        {
            var mine = new List<ulong>();
            ModManager manager = ModManager.instance;
            if (manager == null || manager.mods == null)
            {
                return mine;
            }
            foreach (ModInformation mod in manager.mods)
            {
                if (mod != null && mod.enabled && mod.IsWorkshopItem() && mine.Count < Most)
                {
                    mine.Add(mod.workshopItemId.m_PublishedFileId);
                }
            }
            return mine;
        }

        private static float _nextLook;
        private static long _toldSum = long.MinValue;

        /// <summary>
        /// On the host, every five seconds: his mods as they are now, told again to everyone when they changed (switched on or off in
        /// the game's Mods menu, one subscribed). Told only when a player joined, a change made after it was never heard of by those
        /// already in (modded multiplayer hunt, 2026-11-11).
        /// </summary>
        internal static void Keep(NetSession session)
        {
            if (session == null || !session.IsHost || Time.unscaledTime < _nextLook)
            {
                return;
            }
            _nextLook = Time.unscaledTime + 5f;
            List<ulong> mine = Mine();
            long sum = mine.Count;
            foreach (ulong id in mine)
            {
                sum = unchecked(sum * 31 + (long)id);
            }
            if (_toldSum == long.MinValue)
            {
                _toldSum = sum; // (the first look: who joins is told at his joining)
                return;
            }
            if (sum == _toldSum)
            {
                return;
            }
            _toldSum = sum;
            foreach (NetPlayer p in session.Players)
            {
                if (p.Id != session.LocalId)
                {
                    Tell(session, p.Id);
                }
            }
        }

        /// <summary>The host tells one player (who just joined) his mods, in messages of 64 numbers.</summary>
        internal static void Tell(NetSession session, byte to)
        {
            if (session == null || !session.IsHost)
            {
                return;
            }
            try
            {
                List<ulong> mine = Mine();
                int offset = 0;
                do
                {
                    int n = Math.Min(PerMessage, mine.Count - offset);
                    Out.Reset();
                    Out.UShort((ushort)mine.Count);
                    Out.UShort((ushort)offset);
                    Out.Byte((byte)n);
                    for (int i = 0; i < n; i++)
                    {
                        Out.ULong(mine[offset + i]);
                    }
                    session.SendTo(to, Kind, Out, true);
                    offset += n;
                }
                while (offset < mine.Count);
                Log.Info($"Multiplayer: told player {to} this PC's {mine.Count} workshop mod(s).");
            }
            catch (Exception e)
            {
                Log.WarnOnce("netmods-tell", "Multiplayer: the mods could not be told: " + e);
            }
        }

        internal static void Take(byte sender, ref NetReader r, bool fromHostToJoined)
        {
            int total = r.UShort(), offset = r.UShort(), n = r.Byte();
            if (!fromHostToJoined || total > Most || n > PerMessage)
            {
                return;
            }
            if (offset == 0)
            {
                Hosts.Clear();
                _expected = total;
                _said = false;
            }
            if (offset != Hosts.Count)
            {
                return; // (out of order: the reliable line keeps its order, so this is not his list)
            }
            for (int i = 0; i < n; i++)
            {
                Hosts.Add(r.ULong());
            }
            if (Hosts.Count >= _expected && !_said)
            {
                _said = true;
                int missing = Missing(out int off);
                Log.Info($"Multiplayer: the host has {Hosts.Count} workshop mod(s) on; this PC lacks {missing}, has {off} switched off.");
                // (Switched off here is said too: those mods are not loaded, so their weapons and maps are missing as surely as a mod not
                // installed, and only the log said so; modded multiplayer hunt, 2026-11-11.)
                if (missing > 0 && off > 0)
                {
                    UI.UISystem.Instance?.Notice?.Show($"The host plays with {missing} workshop mod(s) this PC does not have, and {off} that are switched off here. "
                                                       + "Multiplayer panel: Get them; switch the others on in the game's Mods menu.", 12f);
                }
                else if (missing > 0)
                {
                    UI.UISystem.Instance?.Notice?.Show($"The host plays with {missing} workshop mod(s) this PC does not have. Multiplayer panel: Get them.", 10f);
                }
                else if (off > 0)
                {
                    UI.UISystem.Instance?.Notice?.Show($"{off} of the host's workshop mod(s) are switched off on this PC: switch them on in the game's Mods menu, "
                                                       + "then start the game again.", 12f);
                }
            }
        }

        /// <summary>How many of the host's mods are not installed on this PC; <paramref name="switchedOff"/>: here, but switched off.</summary>
        internal static int Missing(out int switchedOff)
        {
            switchedOff = 0;
            if (HostCount == 0)
            {
                return 0;
            }
            var here = new Dictionary<ulong, bool>();
            ModManager manager = ModManager.instance;
            if (manager != null && manager.mods != null)
            {
                foreach (ModInformation mod in manager.mods)
                {
                    if (mod != null && mod.IsWorkshopItem())
                    {
                        here[mod.workshopItemId.m_PublishedFileId] = mod.enabled;
                    }
                }
            }
            int missing = 0;
            foreach (ulong id in Hosts)
            {
                if (!here.TryGetValue(id, out bool on) || !Installed(id))
                {
                    missing++;
                }
                else if (!on)
                {
                    switchedOff++;
                }
            }
            return missing;
        }

        private static bool Installed(ulong id)
        {
            try
            {
                return GameManager.instance != null && GameManager.instance.steamworks != null && GameManager.instance.steamworks.IsSubscribedItemInstalled(new PublishedFileId_t(id));
            }
            catch (Exception)
            {
                return true; // (Steam cannot say: the game's own list decides)
            }
        }

        /// <summary>The player's yes: Steam is asked for every mod of the host's this PC lacks. What it said.</summary>
        internal static string GetThem()
        {
            if (HostCount == 0)
            {
                return "The host told no mods.";
            }
            if (GameManager.instance == null || GameManager.instance.steamworks == null || !GameManager.IsConnectedToSteam())
            {
                return "Steam is not there: the mods cannot be fetched.";
            }
            var known = new HashSet<ulong>();
            if (ModManager.instance != null && ModManager.instance.mods != null)
            {
                foreach (ModInformation mod in ModManager.instance.mods)
                {
                    if (mod != null && mod.IsWorkshopItem())
                    {
                        known.Add(mod.workshopItemId.m_PublishedFileId);
                    }
                }
            }
            int asked = 0;
            foreach (ulong id in Hosts)
            {
                if ((!known.Contains(id) || !Installed(id)) && Wanted.Add(id))
                {
                    try
                    {
                        GameManager.instance.steamworks.SubscribeToItem(new PublishedFileId_t(id));
                        asked++;
                    }
                    catch (Exception e)
                    {
                        Log.WarnOnce("netmods-get", "Multiplayer: Steam did not take the request for a mod: " + e.Message);
                    }
                }
            }
            Asked = true;
            Log.Info($"Multiplayer: asked Steam for {asked} of the host's workshop mod(s).");
            // (The game takes a mod in only at its own start: one fetched now is on the disk, not in the match. It said "the game loads
            // each when it is there" and nothing came; modded multiplayer hunt, 2026-11-11.)
            return asked > 0 ? $"Steam is fetching {asked} mod(s). When they are downloaded, start the game again to have them." : "Nothing more to fetch.";
        }

        /// <summary>The session ended: the next host's list is his own.</summary>
        internal static void Clear()
        {
            _toldSum = long.MinValue; // (a new session: its first look is its own)
            _nextLook = 0f;
            Hosts.Clear();
            Wanted.Clear();
            _expected = -1;
            _said = false;
            Asked = false;
        }
    }
}
