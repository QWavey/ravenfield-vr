using System;
using System.Collections.Generic;
using System.Linq;

namespace RavenfieldVR.Net
{
    /// <summary>What a player tells the others when joining.</summary>
    internal sealed class NetPlayerInfo
    {
        public string Name = "";
        /// <summary>The game's own build number: players on different builds of Ravenfield do not play together (their worlds differ).</summary>
        public int GameBuild;
        public string ModVersion = "";
        public bool Vr;
        public ulong SteamId;

        public void Write(NetWriter w)
        {
            w.String(Name, 64);
            w.VarInt(GameBuild);
            w.String(ModVersion, 32);
            w.Bool(Vr);
            w.ULong(SteamId);
        }

        public static NetPlayerInfo Read(ref NetReader r) =>
            new NetPlayerInfo { Name = r.String(64), GameBuild = r.VarInt(), ModVersion = r.String(32), Vr = r.Bool(), SteamId = r.ULong() };
    }

    /// <summary>One player of the session: the host is number 0, the others get the lowest free number as they join.</summary>
    internal sealed class NetPlayer
    {
        public byte Id;
        public NetPlayerInfo Info;
        /// <summary>The transport's address of this player (the host knows everyone's; a client only the host's).</summary>
        public ulong Peer;
        /// <summary>Round trip to this player in seconds, smoothed (the host's to each client, a client's to the host); -1 until measured.</summary>
        public double Ping = -1;
        internal double LastHeard, LastPinged;
        /// <summary>On the host: something besides a hello came from this player (he has his welcome and plays).</summary>
        internal bool Spoke;
        /// <summary>On the host: when this player was taken in.</summary>
        internal double JoinedAt;
    }

    internal enum NetState
    {
        Connecting,
        Connected,
        Closed
    }

    /// <summary>A message of the game from another player (its kind is 16 or more; the session's own kinds are below).</summary>
    internal delegate void NetMessageHandler(byte kind, byte sender, ref NetReader reader);

    /// <summary>
    /// Who is in the game and how messages get between them, over any <see cref="INetTransport"/>. A star, as RavenM's: every client
    /// talks to the host only, the host passes each message on to the others (one connection a client, and Steam's relay finds the
    /// host). Joining: the client says hello (the protocol's version, the game's build, its name), the host welcomes it with its number
    /// and the list of players, or refuses it with the reason in words. No fixed game version is built in (RavenM's EXPECTED_BUILD_NUMBER
    /// is what broke it on every update): the two builds are compared with each other, and the protocol's own version changes only
    /// when the messages do. Plain C# (tools/tests/NetTests.cs checks it over a network in memory).
    /// </summary>
    internal sealed class NetSession : IDisposable
    {
        /// <summary>Raised when the messages change shape: players on different protocols are refused with a line saying so.</summary>
        public const int ProtocolVersion = 1;

        public const byte Everyone = 255;
        /// <summary>The first kind that belongs to the game; below it the session's own.</summary>
        public const byte FirstGameKind = 16;

        private const byte Hello = 1, Welcome = 2, Reject = 3, PlayerJoined = 4, PlayerLeft = 5, Ping = 6, Pong = 7, Bye = 8;
        private const double HelloTimeout = 15.0, SilenceTimeout = 12.0, PingEvery = 1.0;

        private readonly INetTransport _transport;
        private readonly Dictionary<byte, NetPlayer> _players = new Dictionary<byte, NetPlayer>();
        private readonly Dictionary<ulong, byte> _byPeer = new Dictionary<ulong, byte>();
        private readonly NetWriter _out = new NetWriter(512);
        private readonly Action<ulong, byte[], int> _received;
        private readonly Action<ulong> _lost;
        private readonly ulong _hostPeer;
        private double _now, _startedAt = -1;
        private double _nextHello;

        public bool IsHost { get; }
        public byte LocalId { get; private set; }
        public NetState State { get; private set; }
        /// <summary>Why the session closed, in words for the player ("the host left", "refused: the lobby is full").</summary>
        public string CloseReason { get; private set; }
        public NetPlayerInfo LocalInfo { get; }
        public int MaxPlayers = 16;

        public IEnumerable<NetPlayer> Players => _players.Values;
        public NetPlayer Player(byte id) => _players.TryGetValue(id, out NetPlayer p) ? p : null;
        public int PlayerCount => _players.Count;

        public event Action<NetPlayer> Joined, Left;
        public event Action Connected;
        public event Action<string> Closed;
        public event NetMessageHandler Message;

#pragma warning disable 0649 // (set by the offline tests alone so far, from outside this assembly)
        /// <summary>The host's own check of a newcomer (null: welcome): a reason in words refuses it.</summary>
        public Func<NetPlayerInfo, ulong, string> Admit;
#pragma warning restore 0649

        private NetSession(INetTransport transport, NetPlayerInfo info, bool host, ulong hostPeer)
        {
            _transport = transport;
            LocalInfo = info;
            IsHost = host;
            _hostPeer = hostPeer;
            _received = OnReceived;
            _lost = OnLost;
            if (host)
            {
                LocalId = 0;
                _players[0] = new NetPlayer { Id = 0, Info = info, Peer = transport.LocalId };
                State = NetState.Connected;
            }
            else
            {
                LocalId = Everyone; // (not yet given)
                State = NetState.Connecting;
            }
        }

        public static NetSession Host(INetTransport transport, NetPlayerInfo info) => new NetSession(transport, info, true, 0);

        public static NetSession Join(INetTransport transport, ulong hostPeer, NetPlayerInfo info) => new NetSession(transport, info, false, hostPeer);

        /// <summary>Once a frame: the transport pumped, hello and pings sent, silent players dropped.</summary>
        public void Update(double now)
        {
            if (State == NetState.Closed)
            {
                return;
            }
            _now = now;
            if (_startedAt < 0)
            {
                _startedAt = now;
            }
            // Said again every two seconds until the host answers: over Steam the host takes a line only from a member of his
            // lobby, and a hello that came before Steam had told him of the new member was left unanswered for good (the join
            // then ended "no answer from the host"). A hello from someone already in is ignored on his side.
            if (!IsHost && State == NetState.Connecting && now >= _nextHello)
            {
                _nextHello = now + 2.0;
                _out.Reset();
                _out.Byte(Hello);
                _out.VarInt(ProtocolVersion);
                LocalInfo.Write(_out);
                _transport.Send(_hostPeer, _out.Buffer, _out.Length, true);
            }
            _transport.Poll(now, _received, _lost);
            if (State == NetState.Closed)
            {
                return;
            }
            if (State == NetState.Connecting)
            {
                if (now - _startedAt > HelloTimeout)
                {
                    Close("no answer from the host");
                }
                return;
            }
            foreach (NetPlayer p in _players.Values.ToArray())
            {
                if (p.Id == LocalId || !IsHost && p.Id != 0)
                {
                    continue; // (a client measures and watches the host only)
                }
                if (now - p.LastPinged >= PingEvery)
                {
                    p.LastPinged = now;
                    _out.Reset();
                    _out.Byte(Ping);
                    _out.Float((float)(now % 100000.0));
                    _transport.Send(p.Peer, _out.Buffer, _out.Length, false);
                }
                if (now - p.LastHeard > SilenceTimeout)
                {
                    if (IsHost)
                    {
                        Drop(p, "timed out");
                    }
                    else
                    {
                        Close("the connection to the host was lost");
                        return;
                    }
                }
            }
        }

        /// <summary>A message of the game to everyone else (a client's goes through the host).</summary>
        public void Send(byte kind, NetWriter payload, bool reliable) => SendTo(Everyone, kind, payload, reliable);

        /// <summary>A message of the game to one player (or <see cref="Everyone"/>).</summary>
        public void SendTo(byte target, byte kind, NetWriter payload, bool reliable)
        {
            if (State != NetState.Connected || kind < FirstGameKind || target == LocalId)
            {
                return;
            }
            _out.Reset();
            _out.Byte(kind);
            _out.Byte(LocalId);
            _out.Byte(target);
            if (payload != null && payload.Length > 0)
            {
                _out.Bytes(payload.Buffer, 0, payload.Length);
            }
            if (!IsHost)
            {
                _transport.Send(_hostPeer, _out.Buffer, _out.Length, reliable);
                return;
            }
            foreach (NetPlayer p in _players.Values)
            {
                if (p.Id != 0 && (target == Everyone || target == p.Id))
                {
                    _transport.Send(p.Peer, _out.Buffer, _out.Length, reliable);
                }
            }
        }

        private void OnLost(ulong peer)
        {
            if (!IsHost)
            {
                if (peer == _hostPeer && State != NetState.Connecting)
                {
                    // (While joining, a failed line is not the end: the hello goes again every two seconds and reopens it, and the
                    // hello's own wait says when it is over; a first hello before the host had admitted the joiner ended the join
                    // at once. Steam scout, 2026-11-12.)
                    Close("the line to the host was lost");
                }
                return;
            }
            if (_byPeer.TryGetValue(peer, out byte id) && _players.TryGetValue(id, out NetPlayer p))
            {
                Drop(p, "lost");
            }
        }

        private void OnReceived(ulong from, byte[] data, int length)
        {
            if (State == NetState.Closed || length < 1)
            {
                return;
            }
            try
            {
                var r = new NetReader(data, 0, length);
                byte kind = r.Byte();
                if (IsHost)
                {
                    FromClient(from, kind, ref r, data, length);
                }
                else if (from == _hostPeer)
                {
                    FromHost(kind, ref r);
                }
            }
            catch (NetFormatException)
            {
                // A message that does not read is dropped; the player who sent it stays (one bad packet is not a reason to throw them out).
            }
        }

        private void FromClient(ulong from, byte kind, ref NetReader r, byte[] data, int length)
        {
            if (kind == Hello)
            {
                // Read first: a short or broken hello from a player's address threw only after he had been dropped (transport scout,
                // 2026-11-12); now it throws before, and he stays.
                int protocol = r.VarInt();
                NetPlayerInfo info = NetPlayerInfo.Read(ref r);
                if (_byPeer.TryGetValue(from, out byte known) && _players.TryGetValue(known, out NetPlayer was))
                {
                    if (!was.Spoke && _now - was.JoinedAt < 4.0) // (his hellos come every 2 s until the welcome reaches him)
                    {
                        return; // (a hello twice: already in, his welcome on its way)
                    }
                    // A hello from one who was already playing: he left (his goodbye lost as his game closed the line) and joins again.
                    // Ignored, he waited until the old one timed out, 12 s, or gave up first ("no answer from the host"; scout,
                    // 2026-11-11). The old one goes now and the new one is taken in below.
                    Drop(was, "joined again", close: false); // (his line stays open: the same address is the newcomer's)
                }
                string refuse = protocol != ProtocolVersion ? $"the host plays protocol {ProtocolVersion}, you have {protocol}: both need the same version of the mod"
                    : info.GameBuild != LocalInfo.GameBuild ? $"the host runs game build {LocalInfo.GameBuild}, you run {info.GameBuild}: update the game on both PCs"
                    // (The mod's own version: sent since the start and never looked at. Two versions agree on the wire's format, but not on
                    // the game's rules (a slide caught or not, a round counted or not), and each PC is the judge of its own soldiers; a
                    // shared match on two versions went its own way on each PC with nothing said. 2026-11-11.)
                    : !string.IsNullOrEmpty(LocalInfo.ModVersion) && !string.IsNullOrEmpty(info.ModVersion) && info.ModVersion != LocalInfo.ModVersion
                        ? $"the host has Ravenfield VR {LocalInfo.ModVersion}, you have {info.ModVersion}: install the same version on both PCs"
                    : _players.Count >= MaxPlayers ? "the game is full"
                    : Admit?.Invoke(info, from);
                if (refuse != null)
                {
                    _out.Reset();
                    _out.Byte(Reject);
                    _out.String(refuse, 256);
                    _transport.Send(from, _out.Buffer, _out.Length, true);
                    return;
                }
                byte id = 1;
                while (_players.ContainsKey(id))
                {
                    id++;
                }
                var player = new NetPlayer { Id = id, Info = info, Peer = from, LastHeard = _now, LastPinged = _now, JoinedAt = _now };
                // The others first learn of the newcomer, then the newcomer of everyone.
                _out.Reset();
                _out.Byte(PlayerJoined);
                _out.Byte(id);
                info.Write(_out);
                foreach (NetPlayer p in _players.Values)
                {
                    if (p.Id != 0)
                    {
                        _transport.Send(p.Peer, _out.Buffer, _out.Length, true);
                    }
                }
                _players[id] = player;
                _byPeer[from] = id;
                _out.Reset();
                _out.Byte(Welcome);
                _out.Byte(id);
                _out.VarUInt((uint)_players.Count);
                foreach (NetPlayer p in _players.Values)
                {
                    _out.Byte(p.Id);
                    p.Info.Write(_out);
                }
                _transport.Send(from, _out.Buffer, _out.Length, true);
                Joined?.Invoke(player);
                return;
            }
            if (!_byPeer.TryGetValue(from, out byte sender) || !_players.TryGetValue(sender, out NetPlayer who))
            {
                return; // (not a player: nothing of theirs is taken)
            }
            who.LastHeard = _now;
            who.Spoke = true;
            switch (kind)
            {
                case Ping:
                    data[0] = Pong;
                    _transport.Send(from, data, length, false);
                    data[0] = Ping;
                    return;
                case Pong:
                    Measure(who, r.Float());
                    return;
                case Bye:
                    Drop(who, "left");
                    return;
            }
            if (kind < FirstGameKind)
            {
                return;
            }
            r.Byte(); // (the sender it claims: the host knows who it is)
            byte target = r.Byte();
            data[1] = sender;
            if (target != 0)
            {
                foreach (NetPlayer p in _players.Values)
                {
                    if (p.Id != 0 && p.Id != sender && (target == Everyone || target == p.Id))
                    {
                        // (As it came: reliable kinds are sent reliable by their senders; the host passes on by the kind's own rule.)
                        _transport.Send(p.Peer, data, length, ReliableKind == null || ReliableKind(kind));
                    }
                }
            }
            if (target == Everyone || target == 0)
            {
                Message?.Invoke(kind, sender, ref r);
            }
        }

        /// <summary>
        /// Which kinds the host passes on reliably (the game says: states that are sent over and over are not; events are). Null:
        /// everything reliably.
        /// </summary>
        public Func<byte, bool> ReliableKind { get; set; }

        private void FromHost(byte kind, ref NetReader r)
        {
            if (_players.TryGetValue(0, out NetPlayer host))
            {
                host.LastHeard = _now;
            }
            switch (kind)
            {
                case Welcome:
                {
                    if (State != NetState.Connecting)
                    {
                        return;
                    }
                    LocalId = r.Byte();
                    int count = (int)r.VarUInt();
                    for (int i = 0; i < count && i < 256; i++)
                    {
                        byte id = r.Byte();
                        NetPlayerInfo info = NetPlayerInfo.Read(ref r);
                        _players[id] = new NetPlayer { Id = id, Info = id == LocalId ? LocalInfo : info, Peer = id == 0 ? _hostPeer : 0, LastHeard = _now, LastPinged = _now };
                    }
                    State = NetState.Connected;
                    Connected?.Invoke();
                    return;
                }
                case Reject:
                    Close("refused: " + r.String(256));
                    return;
                case PlayerJoined:
                {
                    byte id = r.Byte();
                    var p = new NetPlayer { Id = id, Info = NetPlayerInfo.Read(ref r) };
                    _players[id] = p;
                    Joined?.Invoke(p);
                    return;
                }
                case PlayerLeft:
                {
                    byte id = r.Byte();
                    if (_players.TryGetValue(id, out NetPlayer p) && id != LocalId)
                    {
                        _players.Remove(id);
                        Left?.Invoke(p);
                    }
                    return;
                }
                case Ping:
                    _out.Reset();
                    _out.Byte(Pong);
                    _out.Float(r.Float());
                    _transport.Send(_hostPeer, _out.Buffer, _out.Length, false);
                    return;
                case Pong:
                    if (host != null)
                    {
                        Measure(host, r.Float());
                    }
                    return;
                case Bye:
                    Close("the host ended the game");
                    return;
            }
            if (kind < FirstGameKind || State != NetState.Connected)
            {
                return;
            }
            byte sender = r.Byte();
            r.Byte(); // (the target: it is for this player, or it would not have come)
            Message?.Invoke(kind, sender, ref r);
        }

        private void Measure(NetPlayer p, float sentAt)
        {
            double trip = _now % 100000.0 - sentAt;
            if (trip < 0 || trip > 10)
            {
                return;
            }
            p.Ping = p.Ping < 0 ? trip : p.Ping * 0.8 + trip * 0.2;
        }

        /// <summary>
        /// The host removes a player from his game: told why (his game ends the session in words, as a refusal), then taken off for
        /// everyone. Not closed at once here, or the word did not reach him first; his own game closes its side on reading it.
        /// </summary>
        public void Kick(byte id)
        {
            if (!IsHost || State == NetState.Closed || id == LocalId || !_players.TryGetValue(id, out NetPlayer p))
            {
                return;
            }
            _out.Reset();
            _out.Byte(Reject);
            _out.String("the host removed you from the game", 256);
            _transport.Send(p.Peer, _out.Buffer, _out.Length, true);
            Drop(p, "removed by the host", close: false);
        }

        private void Drop(NetPlayer p, string why, bool close = true)
        {
            if (!_players.Remove(p.Id))
            {
                return;
            }
            _byPeer.Remove(p.Peer);
            if (close)
            {
                _transport.Close(p.Peer);
            }
            _out.Reset();
            _out.Byte(PlayerLeft);
            _out.Byte(p.Id);
            foreach (NetPlayer other in _players.Values)
            {
                if (other.Id != 0)
                {
                    _transport.Send(other.Peer, _out.Buffer, _out.Length, true);
                }
            }
            Left?.Invoke(p);
        }

        private void Close(string reason)
        {
            if (State == NetState.Closed)
            {
                return;
            }
            State = NetState.Closed;
            CloseReason = reason;
            Closed?.Invoke(reason);
        }

        /// <summary>Leaves (the others are told), and the session is over. The transport is the caller's to dispose.</summary>
        public void Leave()
        {
            if (State == NetState.Closed)
            {
                return;
            }
            _out.Reset();
            _out.Byte(Bye);
            if (IsHost)
            {
                foreach (NetPlayer p in _players.Values)
                {
                    if (p.Id != 0)
                    {
                        _transport.Send(p.Peer, _out.Buffer, _out.Length, true);
                    }
                }
            }
            else
            {
                _transport.Send(_hostPeer, _out.Buffer, _out.Length, true);
            }
            // (One more pump so the goodbye leaves with this frame's packets.)
            _transport.Poll(_now, (a, b, c) => { }, a => { });
            Close("left");
        }

        public void Dispose() => Leave();
    }
}
