using System;
using System.Collections.Generic;
using RavenfieldVR.Core;
using UnityEngine;

namespace RavenfieldVR.Net
{
    /// <summary>
    /// Multiplayer as the player sees it (the user, 2026-11-09: "players can just easily join over steam or see lobbys over the
    /// program (which pulls data from steam) and join to lobbys directly like raven m does"): a game hosted over Steam (public or
    /// for friends) or on the LAN, Steam's list of open games, one joined from the list, by a friend's invitation or by an address.
    /// One session at a time; <see cref="Update"/> pumps it each frame. What is played over it is the game layer's
    /// (<see cref="Session"/>'s messages from kind 16 on).
    /// </summary>
    internal static class Multiplayer
    {
        public const int DefaultPort = 7133;

        private static INetTransport _transport;
        private static SteamLobbies _lobbies;
        private static float _nextDescribe;
        private static bool _busy, _listening;
        private static int _request;
        private static float _nextListenTry;

        public static NetSession Session { get; private set; }

        /// <summary>The line the panel shows: what is going on, or why the last try did not work.</summary>
        public static string Status { get; private set; } = "Not in a game.";

        /// <summary>Steam's list of open games as last fetched (null before the first), and the line under it.</summary>
        public static List<LobbyInfo> Lobbies { get; private set; }
        public static string ListStatus { get; private set; } = "";
        public static bool Listing { get; private set; }

        /// <summary>Raised on every change the panel shows (players, state, the list).</summary>
        public static event Action Changed;

        /// <summary>A session began (hosted or joined and welcomed) or ended: the game layer puts its handlers on or takes them off.</summary>
        public static event Action<NetSession> Started, Ended;

        public static bool Active => Session != null && Session.State != NetState.Closed;
        public static bool Hosting => Active && Session.IsHost;
        public static bool OverSteam => _transport is SteamTransport;
        public static bool Busy => _busy;

        public static int GameBuild
        {
            get
            {
                try
                {
                    return GameManager.instance != null ? GameManager.instance.buildNumber : 0;
                }
                catch (Exception)
                {
                    return 0;
                }
            }
        }

        private static SteamLobbies Steam
        {
            get
            {
                if (_lobbies == null && SteamLobbies.Available)
                {
                    _lobbies = new SteamLobbies();
                    // A friend's invitation or "Join game" in Steam's overlay: joined at once, whatever was going on.
                    _lobbies.JoinRequested += lobby => JoinLobby(lobby);
                    _lobbies.OwnerLeft += () =>
                    {
                        if (Active && !Hosting)
                        {
                            End("The host left.");
                        }
                    };
                }
                return _lobbies;
            }
        }

        /// <summary>Called once the game's Steam is up: invitations taken from then on, also with the panel never opened.</summary>
        public static void ListenForInvitations()
        {
            _ = Steam;
        }

        private static NetPlayerInfo OwnInfo() => new NetPlayerInfo
        {
            Name = SteamLobbies.OwnName,
            GameBuild = GameBuild,
            ModVersion = PluginInfo.Version,
            Vr = VRManager.Active,
            SteamId = SteamLobbies.Available ? Steamworks.SteamUser.GetSteamID().m_SteamID : 0
        };

        private static void Say(string status)
        {
            Status = status;
            Log.Info("Multiplayer: " + status);
            Changed?.Invoke();
        }

        private static void Begin(NetSession session, INetTransport transport)
        {
            Session = session;
            _transport = transport;
            session.Joined += p =>
            {
                Say($"{p.Info.Name} joined{(p.Info.Vr ? " (VR)" : "")}.");
                _nextDescribe = 0f;
            };
            session.Left += p =>
            {
                Say($"{p.Info.Name} left.");
                _nextDescribe = 0f;
            };
            session.Closed += reason =>
            {
                if (ReferenceEquals(Session, session))
                {
                    End(reason == "left" ? "Not in a game." : "The game ended: " + reason + ".");
                }
            };
            if (session.IsHost)
            {
                Started?.Invoke(session);
            }
            else
            {
                session.Connected += () =>
                {
                    Say($"In {HostName()}'s game.");
                    Started?.Invoke(session);
                };
            }
        }

        private static string HostName()
        {
            NetPlayer host = Session != null ? Session.Player(0) : null;
            return host != null && !string.IsNullOrEmpty(host.Info.Name) ? host.Info.Name : "the host";
        }

        private static void End(string status)
        {
            NetSession session = Session;
            Session = null;
            if (session != null)
            {
                try
                {
                    Ended?.Invoke(session);
                }
                catch (Exception e)
                {
                    Log.WarnOnce("mp-ended", "Multiplayer: the game layer's end threw: " + e);
                }
                if (session.State != NetState.Closed)
                {
                    session.Leave();
                }
            }
            _transport?.Dispose();
            _transport = null;
            _lobbies?.Leave();
            _busy = false;
            _request++;
            HostLines = new string[0];
            Say(status);
        }

        /// <summary>A game hosted over Steam: a lobby the list shows (or only friends see), the players' messages relayed by Steam.</summary>
        /// <param name="friendsOnly">Only friends see the lobby and may join it.</param>
        /// <param name="maxPlayers">How many may be in the game, the host counted.</param>
        /// <param name="hidden">A lobby nobody sees or can find (the self-check's: made, read back and left again).</param>
        public static void HostSteam(bool friendsOnly, int maxPlayers = 8, bool hidden = false)
        {
            if (_busy)
            {
                return;
            }
            if (Steam == null)
            {
                Say("Steam is not running: host on the LAN, or start the game from Steam.");
                return;
            }
            End("Opening a lobby ...");
            _busy = true;
            int request = ++_request;
            Steam.Create(maxPlayers, friendsOnly, (lobby, problem) =>
            {
                if (request != _request)
                {
                    // (Left or started something else while Steam was answering: this lobby is nobody's any more.)
                    if (lobby != 0 && !Active)
                    {
                        _lobbies?.Leave();
                    }
                    return;
                }
                _busy = false;
                if (lobby == 0)
                {
                    Say("No lobby: " + problem + ".");
                    return;
                }
                var transport = new SteamTransport { Accept = id => _lobbies != null && _lobbies.IsMember(id) };
                NetSession session = NetSession.Host(transport, OwnInfo());
                session.MaxPlayers = maxPlayers;
                Begin(session, transport);
                _nextDescribe = 0f;
                Say(friendsOnly ? "Hosting for friends. Invite them, or they join from their friends list." : "Hosting: the game is in the list.");
            }, hidden);
        }

        /// <summary>A game hosted on a UDP port: for the LAN, a forwarded port, or two games on one PC.</summary>
        public static void HostLan(int port = DefaultPort, int maxPlayers = 8)
        {
            if (_busy)
            {
                return;
            }
            End("Not in a game.");
            try
            {
                var transport = new UdpTransport(port);
                NetSession session = NetSession.Host(transport, OwnInfo());
                session.MaxPlayers = maxPlayers;
                Begin(session, transport);
                Say($"Hosting on port {port} (UDP). Others join by this PC's address.");
            }
            catch (Exception e)
            {
                Say($"Port {port} could not be opened: {e.Message}");
            }
        }

        /// <summary>One of Steam's lobbies joined, then its host's game.</summary>
        public static void JoinLobby(ulong lobby)
        {
            if (_busy)
            {
                return;
            }
            if (Steam == null)
            {
                Say("Steam is not running.");
                return;
            }
            End("Joining ...");
            _busy = true;
            int request = ++_request;
            Steam.Join(lobby, (host, problem) =>
            {
                if (request != _request)
                {
                    if (host != 0 && !Active)
                    {
                        _lobbies?.Leave();
                    }
                    return;
                }
                _busy = false;
                if (host == 0)
                {
                    Say("Not joined: " + problem + ".");
                    return;
                }
                var transport = new SteamTransport { Accept = id => _lobbies != null && _lobbies.IsMember(id) };
                Begin(NetSession.Join(transport, host, OwnInfo()), transport);
                Say("Joining the host ...");
            });
        }

        /// <summary>A game joined by its address ("192.168.1.20" or "host.name:7133").</summary>
        public static void JoinAddress(string address)
        {
            if (_busy)
            {
                return;
            }
            End("Not in a game.");
            try
            {
                ulong host = UdpTransport.Parse(address, DefaultPort);
                var transport = new UdpTransport(0);
                Begin(NetSession.Join(transport, host, OwnInfo()), transport);
                Say($"Joining {address} ...");
            }
            catch (Exception e)
            {
                Say($"\"{address}\" could not be joined: {e.Message}");
            }
        }

        public static void Leave()
        {
            if (Session != null || _busy)
            {
                End("Not in a game.");
            }
        }

        /// <summary>Steam's list fetched again (the panel shows it when it arrives).</summary>
        public static void RefreshLobbies()
        {
            if (Listing)
            {
                return;
            }
            if (Steam == null)
            {
                ListStatus = "Steam is not running: no list. A LAN game is joined by its address.";
                Changed?.Invoke();
                return;
            }
            Listing = true;
            ListStatus = "Asking Steam ...";
            Changed?.Invoke();
            Steam.List((list, problem) =>
            {
                Listing = false;
                if (list == null)
                {
                    ListStatus = "No list: " + problem + ".";
                }
                else
                {
                    Lobbies = list;
                    ListStatus = list.Count == 0 ? "No open game right now. Host one: it shows here for everyone." : $"{list.Count} open game(s).";
                }
                Changed?.Invoke();
            });
        }

        public static void InviteFriends() => _lobbies?.InviteFriends();

        /// <summary>What the host is choosing, as his game told it (for a player who joined: he follows, he does not choose).</summary>
        public static string[] HostLines { get; private set; } = new string[0];

        internal static void SetHostLines(string[] lines)
        {
            HostLines = lines ?? new string[0];
            Changed?.Invoke();
        }

        /// <summary>The Steam lobby this player is in (0: none), and a line of its data as Steam has it.</summary>
        public static ulong Lobby => _lobbies != null ? _lobbies.Current : 0;
        public static string LobbyData(string key) => _lobbies != null ? _lobbies.Get(key) : "";

        /// <summary>What the hosted game shows in Steam's list (the game layer names the map and mode; before a match: the menu).</summary>
        public static string Map = "", Mode = "";
        public static bool InMatch;

        /// <summary>Once a frame, in the menu and in a match alike.</summary>
        public static void Update()
        {
            if (!_listening && Time.unscaledTime >= _nextListenTry)
            {
                // (Steam comes up with the game's first scene: asked until it is there, then never again.)
                _nextListenTry = Time.unscaledTime + 2f;
                if (SteamLobbies.Available)
                {
                    _listening = true;
                    ListenForInvitations();
                }
            }
            NetSession session = Session;
            if (session == null)
            {
                return;
            }
            try
            {
                session.Update(Time.realtimeSinceStartupAsDouble);
                NetGame.Update();
            }
            catch (Exception e)
            {
                Log.WarnOnce("mp-update", "Multiplayer: the session's update threw: " + e);
            }
            if (Hosting && OverSteam && _lobbies != null && Time.unscaledTime >= _nextDescribe)
            {
                _nextDescribe = Time.unscaledTime + 5f;
                _lobbies.Describe(session.LocalInfo.Name + "'s game", Map, Mode, session.PlayerCount, GameBuild, InMatch);
            }
        }

        /// <summary>The plugin is going (F6 reload, the game closing): the others are told, the lobby left.</summary>
        public static void Shutdown()
        {
            Leave();
            _lobbies?.Dispose();
            _lobbies = null;
        }
    }
}
