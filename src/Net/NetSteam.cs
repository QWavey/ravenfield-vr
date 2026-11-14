using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using RavenfieldVR.Core;
using Steamworks;

namespace RavenfieldVR.Net
{
    /// <summary>
    /// Messages between players over Steam (SteamNetworkingMessages: peer to peer where the routers let it, Valve's relays where
    /// not, no port to open, no address shown). A peer is a Steam id. Steam keeps a reliable message whole and in order by itself.
    /// Only who <see cref="Accept"/> lets in may open a line to this player (the lobby's members): anyone else's request is left
    /// unanswered. The game runs Steam's callbacks each frame (SteamworksWrapper.Update); this adds none of its own.
    /// </summary>
    internal sealed class SteamTransport : INetTransport
    {
        /// <summary>The mod's own channel of Steam's messages (the game uses none; another mod's traffic stays apart).</summary>
        private const int Channel = 7133;

        private readonly Callback<SteamNetworkingMessagesSessionRequest_t> _request;
        private readonly Callback<SteamNetworkingMessagesSessionFailed_t> _failed;
        private readonly IntPtr[] _incoming = new IntPtr[64];
        private readonly HashSet<ulong> _peers = new HashSet<ulong>();
        private readonly List<ulong> _lost = new List<ulong>();
        private byte[] _buffer = new byte[4096];
        private bool _disposed;

        /// <summary>The self-check's: a message to this account itself is sent (Steam hands it straight back), to try the sending and receiving with one account.</summary>
        internal bool AllowSelf;

        /// <summary>What Steam said to the last send (the check's).</summary>
        internal EResult LastSend { get; private set; } = EResult.k_EResultNone;

        /// <summary>Who may open a line to this player (by Steam id).</summary>
        public Func<ulong, bool> Accept;

        public SteamTransport()
        {
            LocalId = SteamUser.GetSteamID().m_SteamID;
            _request = Callback<SteamNetworkingMessagesSessionRequest_t>.Create(OnRequest);
            _failed = Callback<SteamNetworkingMessagesSessionFailed_t>.Create(OnFailed);
        }

        public ulong LocalId { get; }

        // (Steam splits a longer unreliable message itself, up to 512 KB; kept to one packet so a lost one costs one state, not several.)
        public int MaxUnreliable => 1200;

        private void OnRequest(SteamNetworkingMessagesSessionRequest_t request)
        {
            SteamNetworkingIdentity from = request.m_identityRemote;
            ulong id = from.GetSteamID64();
            if (!_disposed && id != 0 && (Accept == null || Accept(id)))
            {
                SteamNetworkingMessages.AcceptSessionWithUser(ref from);
                _peers.Add(id);
            }
        }

        private void OnFailed(SteamNetworkingMessagesSessionFailed_t failed)
        {
            ulong id = failed.m_info.m_identityRemote.GetSteamID64();
            if (!_disposed && _peers.Remove(id))
            {
                _lost.Add(id);
            }
        }

        public void Send(ulong peer, byte[] data, int length, bool reliable)
        {
            if (_disposed || peer == 0 || (peer == LocalId && !AllowSelf))
            {
                return;
            }
            var to = new SteamNetworkingIdentity();
            to.SetSteamID64(peer);
            _peers.Add(peer);
            // (A session that dropped is opened again by the next message: a player back from a short loss of line plays on.)
            int flags = (reliable ? Constants.k_nSteamNetworkingSend_Reliable : Constants.k_nSteamNetworkingSend_UnreliableNoDelay)
                        | Constants.k_nSteamNetworkingSend_AutoRestartBrokenSession;
            GCHandle pin = GCHandle.Alloc(data, GCHandleType.Pinned);
            try
            {
                LastSend = SteamNetworkingMessages.SendMessageToUser(ref to, pin.AddrOfPinnedObject(), (uint)length, flags, Channel);
            }
            catch (InvalidOperationException)
            {
                // Steam already shut down (the game closing: the goodbye comes after it); nothing to send with. It threw out of the
                // session's leaving and the log closed on an error (the user's friend's log, 2026-11-11).
            }
            finally
            {
                pin.Free();
            }
        }

        public void Poll(double now, Action<ulong, byte[], int> received, Action<ulong> lost)
        {
            if (_disposed)
            {
                return;
            }
            if (_lost.Count > 0)
            {
                foreach (ulong peer in _lost)
                {
                    lost(peer);
                }
                _lost.Clear();
            }
            int n;
            do
            {
                try
                {
                    n = SteamNetworkingMessages.ReceiveMessagesOnChannel(Channel, _incoming, _incoming.Length);
                }
                catch (InvalidOperationException)
                {
                    return; // (Steam already shut down, the game closing: nothing to receive; Steam scout)
                }
                for (int i = 0; i < n; i++)
                {
                    IntPtr handle = _incoming[i];
                    try
                    {
                        SteamNetworkingMessage_t message = SteamNetworkingMessage_t.FromIntPtr(handle);
                        ulong from = message.m_identityPeer.GetSteamID64();
                        int size = message.m_cbSize;
                        if (size > 0 && from != 0 && _peers.Contains(from))
                        {
                            if (size > _buffer.Length)
                            {
                                _buffer = new byte[Math.Max(size, _buffer.Length * 2)];
                            }
                            Marshal.Copy(message.m_pData, _buffer, 0, size);
                            try
                            {
                                received(from, _buffer, size);
                            }
                            catch (Exception e)
                            {
                                // (One message's handler must not cost the rest of the batch their release and their turn.)
                                Log.WarnOnce("steam-received", "Multiplayer: a message's handler threw: " + e);
                            }
                        }
                    }
                    finally
                    {
                        SteamNetworkingMessage_t.Release(handle);
                    }
                }
            }
            while (n == _incoming.Length);
        }

        public void Close(ulong peer)
        {
            if (_peers.Remove(peer))
            {
                var who = new SteamNetworkingIdentity();
                who.SetSteamID64(peer);
                try
                {
                    SteamNetworkingMessages.CloseSessionWithUser(ref who);
                }
                catch (InvalidOperationException)
                {
                    // (Steam already shut down: the line is gone with it.)
                }
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            foreach (ulong peer in new List<ulong>(_peers))
            {
                Close(peer);
            }
            _request?.Dispose();
            _failed?.Dispose();
        }
    }

    /// <summary>One game open to join, as Steam lists it.</summary>
    internal sealed class LobbyInfo
    {
        public ulong Id, Host;
        public string Name = "", Map = "", Mode = "", ModVersion = "";
        public int Players, MaxPlayers, Protocol, GameBuild;
        public bool InMatch;

        /// <summary>Why this player cannot join it (null: he can).</summary>
        public string Mismatch(int gameBuild) =>
            Protocol != NetSession.ProtocolVersion ? $"another version of the mod ({ModVersion})"
            // (The mod's own version too: the host refuses another one at the door, the list showed it joinable; the user, 2026-11-14:
            // "if one pc has a older version it needs to tell the player it".)
            : !string.IsNullOrEmpty(ModVersion) && ModVersion != PluginInfo.Version ? $"Ravenfield VR {ModVersion} there, {PluginInfo.Version} here: both need the same version"
            : GameBuild != gameBuild ? $"another build of the game ({GameBuild})"
            : Players >= MaxPlayers ? "full" : null;
    }

    /// <summary>
    /// Steam's lobbies for the mod's games: one made for a hosted game (public or for friends), all open ones listed, one joined
    /// (from the list, from a friend's invitation or "Join game" in Steam's overlay). The lobby only brings the players together
    /// and tells what is played; the game itself runs over <see cref="SteamTransport"/> to the lobby's owner. Nothing here knows the
    /// game's build beyond a number put into the lobby and compared: an update of the game changes that number for everyone at
    /// once, and the mod keeps working (RavenM refused every build but the one it was made for).
    /// </summary>
    internal sealed class SteamLobbies : IDisposable
    {
        private const string Mark = "rfvr", MarkValue = "1";

        private readonly CallResult<LobbyCreated_t> _created = CallResult<LobbyCreated_t>.Create();
        private readonly CallResult<LobbyMatchList_t> _listed = CallResult<LobbyMatchList_t>.Create();
        private readonly CallResult<LobbyEnter_t> _entered = CallResult<LobbyEnter_t>.Create();
        private readonly Callback<GameLobbyJoinRequested_t> _joinRequested;
        private readonly Callback<LobbyChatUpdate_t> _chat;

        /// <summary>The lobby this player is in (0: none), and whether he made it.</summary>
        public ulong Current { get; private set; }
        public bool Owner { get; private set; }

        /// <summary>A friend's invitation taken, or "Join game" pressed in Steam: the lobby to join.</summary>
        public event Action<ulong> JoinRequested;

        /// <summary>The lobby's owner left it (Steam hands the lobby to another member; the game he hosted is over).</summary>
        public event Action OwnerLeft;

        public static bool Available
        {
            get
            {
                try
                {
                    return GameManager.instance != null && GameManager.instance.steamworks != null && GameManager.instance.steamworks.isInitialized;
                }
                catch (Exception)
                {
                    return false;
                }
            }
        }

        public static string OwnName => Available ? SteamFriends.GetPersonaName() : Environment.UserName;

        public SteamLobbies()
        {
            _joinRequested = Callback<GameLobbyJoinRequested_t>.Create(r => JoinRequested?.Invoke(r.m_steamIDLobby.m_SteamID));
            _chat = Callback<LobbyChatUpdate_t>.Create(OnChat);
        }

        private void OnChat(LobbyChatUpdate_t update)
        {
            // Someone left (or was dropped): when it was the one the game runs on, the others learn it here at once.
            const uint entered = 1; // EChatMemberStateChange.k_EChatMemberStateChangeEntered
            if (update.m_ulSteamIDLobby == Current && !Owner && (update.m_rgfChatMemberStateChange & entered) == 0 && update.m_ulSteamIDUserChanged == _host)
            {
                OwnerLeft?.Invoke();
            }
        }

        private ulong _host;

        /// <summary>A lobby made for a game this player hosts. <paramref name="done"/>: the lobby, or 0 and why not.</summary>
        public void Create(int maxPlayers, bool friendsOnly, Action<ulong, string> done, bool hidden = false)
        {
            if (!Available)
            {
                done(0, "Steam is not running");
                return;
            }
            Leave();
            SteamAPICall_t call = SteamMatchmaking.CreateLobby(
                hidden ? ELobbyType.k_ELobbyTypePrivate : friendsOnly ? ELobbyType.k_ELobbyTypeFriendsOnly : ELobbyType.k_ELobbyTypePublic, maxPlayers);
            _created.Set(call, (result, ioFailure) =>
            {
                if (ioFailure || result.m_eResult != EResult.k_EResultOK)
                {
                    done(0, ioFailure ? "Steam did not answer" : "Steam refused the lobby (" + result.m_eResult.ToString().Replace("k_EResult", "") + ")");
                    return;
                }
                Current = result.m_ulSteamIDLobby;
                Owner = true;
                _host = SteamUser.GetSteamID().m_SteamID;
                Set(Mark, MarkValue);
                done(Current, null);
            });
        }

        /// <summary>A line of the hosted lobby's data (what the list shows of it).</summary>
        public void Set(string key, string value)
        {
            if (Current != 0 && Owner)
            {
                SteamMatchmaking.SetLobbyData(new CSteamID(Current), key, value ?? "");
            }
        }

        /// <summary>What a hosted game shows in the list, in one go.</summary>
        public void Describe(string name, string map, string mode, int players, int gameBuild, bool inMatch)
        {
            Set("name", name);
            Set("map", map);
            Set("mode", mode);
            Set("players", players.ToString());
            Set("proto", NetSession.ProtocolVersion.ToString());
            Set("build", gameBuild.ToString());
            Set("mod", PluginInfo.Version);
            Set("match", inMatch ? "1" : "0");
        }

        /// <summary>A line of the lobby this player is in, as Steam has it now ("" with none).</summary>
        public string Get(string key) => Current != 0 ? SteamMatchmaking.GetLobbyData(new CSteamID(Current), key) : "";

        private static int Number(CSteamID lobby, string key) => int.TryParse(SteamMatchmaking.GetLobbyData(lobby, key), out int v) ? v : 0;

        /// <summary>Every open game of the mod, worldwide (Steam's own list, nearest first). <paramref name="done"/>: the list, or null and why not.</summary>
        public void List(Action<List<LobbyInfo>, string> done)
        {
            if (!Available)
            {
                done(null, "Steam is not running");
                return;
            }
            SteamMatchmaking.AddRequestLobbyListStringFilter(Mark, MarkValue, ELobbyComparison.k_ELobbyComparisonEqual);
            SteamMatchmaking.AddRequestLobbyListDistanceFilter(ELobbyDistanceFilter.k_ELobbyDistanceFilterWorldwide);
            SteamMatchmaking.AddRequestLobbyListResultCountFilter(60);
            _listed.Set(SteamMatchmaking.RequestLobbyList(), (result, ioFailure) =>
            {
                if (ioFailure)
                {
                    done(null, "Steam did not answer");
                    return;
                }
                var list = new List<LobbyInfo>();
                for (int i = 0; i < result.m_nLobbiesMatching; i++)
                {
                    CSteamID lobby = SteamMatchmaking.GetLobbyByIndex(i);
                    if (!lobby.IsValid() || lobby.m_SteamID == Current)
                    {
                        continue;
                    }
                    list.Add(new LobbyInfo
                    {
                        Id = lobby.m_SteamID,
                        Host = SteamMatchmaking.GetLobbyOwner(lobby).m_SteamID,
                        Name = SteamMatchmaking.GetLobbyData(lobby, "name"),
                        Map = SteamMatchmaking.GetLobbyData(lobby, "map"),
                        Mode = SteamMatchmaking.GetLobbyData(lobby, "mode"),
                        ModVersion = SteamMatchmaking.GetLobbyData(lobby, "mod"),
                        Players = Math.Max(Number(lobby, "players"), SteamMatchmaking.GetNumLobbyMembers(lobby)),
                        MaxPlayers = SteamMatchmaking.GetLobbyMemberLimit(lobby),
                        Protocol = Number(lobby, "proto"),
                        GameBuild = Number(lobby, "build"),
                        InMatch = SteamMatchmaking.GetLobbyData(lobby, "match") == "1"
                    });
                }
                done(list, null);
            });
        }

        /// <summary>A lobby joined. <paramref name="done"/>: the Steam id of the player the game runs on, or 0 and why not.</summary>
        public void Join(ulong lobby, Action<ulong, string> done)
        {
            if (!Available)
            {
                done(0, "Steam is not running");
                return;
            }
            Leave();
            _entered.Set(SteamMatchmaking.JoinLobby(new CSteamID(lobby)), (result, ioFailure) =>
            {
                const uint success = 1; // EChatRoomEnterResponse.k_EChatRoomEnterResponseSuccess
                if (ioFailure || result.m_EChatRoomEnterResponse != success)
                {
                    done(0, ioFailure ? "Steam did not answer" : EnterProblem(result.m_EChatRoomEnterResponse));
                    return;
                }
                Current = result.m_ulSteamIDLobby;
                Owner = false;
                _host = SteamMatchmaking.GetLobbyOwner(new CSteamID(Current)).m_SteamID;
                if (_host == 0)
                {
                    // (Steam named no owner: the lobby was left again, not kept joined but untracked; it read "Not joined: null.")
                    Leave();
                    done(0, "Steam did not name that game's host");
                    return;
                }
                done(_host, null);
            });
        }

        private static string EnterProblem(uint response)
        {
            switch (response)
            {
                case 2: return "that game is gone";
                case 3: return "not allowed in";
                case 4: return "that game is full";
                case 6: return "banned from that game";
                case 10: return "you blocked someone in that game";
                case 11: return "someone in that game blocked you";
                default: return "Steam refused (" + response + ")";
            }
        }

        /// <summary>Whether a Steam id is in this player's lobby (who may open a line: <see cref="SteamTransport.Accept"/>).</summary>
        public bool IsMember(ulong steamId)
        {
            if (Current == 0)
            {
                return false;
            }
            var lobby = new CSteamID(Current);
            int n = SteamMatchmaking.GetNumLobbyMembers(lobby);
            for (int i = 0; i < n; i++)
            {
                if (SteamMatchmaking.GetLobbyMemberByIndex(lobby, i).m_SteamID == steamId)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>Steam's overlay with the friends list, to invite into the hosted lobby.</summary>
        public void InviteFriends()
        {
            if (Current != 0)
            {
                SteamFriends.ActivateGameOverlayInviteDialog(new CSteamID(Current));
            }
        }

        public void Leave()
        {
            if (Current != 0)
            {
                try
                {
                    SteamMatchmaking.LeaveLobby(new CSteamID(Current));
                }
                catch (InvalidOperationException)
                {
                    // (Steam already shut down: the lobby went with it, and the rest of the leaving still runs.)
                }
            }
            Current = 0;
            Owner = false;
            _host = 0;
        }

        public void Dispose()
        {
            Leave();
            _created.Dispose();
            _listed.Dispose();
            _entered.Dispose();
            _joinRequested.Dispose();
            _chat.Dispose();
        }
    }
}
