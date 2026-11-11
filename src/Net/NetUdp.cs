using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;

namespace RavenfieldVR.Net
{
    /// <summary>
    /// Plain UDP: a LAN or a direct address without Steam, and two games on one PC for the mod's own two-player test. A peer's number
    /// is its IPv4 address and port. Unreliable messages go as one packet; reliable ones through a <see cref="ReliableChannel"/> per
    /// peer. Nothing is encrypted and anyone who can reach the port can say hello: it is for a network one trusts (Steam's transport
    /// is the one for the internet).
    /// </summary>
    internal sealed class UdpTransport : INetTransport
    {
        private readonly Socket _socket;
        private readonly Dictionary<ulong, ReliableChannel> _channels = new Dictionary<ulong, ReliableChannel>();
        private readonly byte[] _in = new byte[2048];
        private readonly byte[] _out = new byte[2048];
        private readonly byte[] _message = new byte[2048]; // (what is handed on: not the buffer a reply is built in)
        private readonly List<ulong> _lostNow = new List<ulong>();
        private Action<ulong, byte[], int> _deliverTo;
        private ulong _deliverFrom;
        private readonly Action<byte[], int> _deliver;

        public int Port { get; }

        /// <param name="port">The port to listen on (0: any free one, a client's).</param>
        public UdpTransport(int port)
        {
            _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp) { Blocking = false };
            try
            {
                // (Windows answers a send to a closed port with a reset on the next receive: not an error of this socket.)
                const int SioUdpConnReset = -1744830452;
                _socket.IOControl(SioUdpConnReset, new byte[] { 0 }, null);
            }
            catch (Exception)
            {
                // (not Windows, or not allowed: the receive loop skips the reset itself)
            }
            _socket.Bind(new IPEndPoint(IPAddress.Any, port));
            Port = ((IPEndPoint)_socket.LocalEndPoint).Port;
            _deliver = (data, length) => _deliverTo(_deliverFrom, data, length);
        }

        public ulong LocalId => PeerOf(new IPEndPoint(IPAddress.Loopback, Port));

        public int MaxUnreliable => 1200;

        /// <summary>A peer's number from its address and port (IPv4).</summary>
        internal static ulong PeerOf(IPEndPoint at)
        {
            byte[] a = at.Address.GetAddressBytes();
            return (ulong)a[0] << 40 | (ulong)a[1] << 32 | (ulong)a[2] << 24 | (ulong)a[3] << 16 | (uint)at.Port;
        }

        internal static IPEndPoint EndPointOf(ulong peer) =>
            new IPEndPoint(new IPAddress(new[] { (byte)(peer >> 40), (byte)(peer >> 32), (byte)(peer >> 24), (byte)(peer >> 16) }), (int)(peer & 0xFFFF));

        /// <summary>"192.168.1.5:27300" (or a host name) as a peer; 0 when it does not read as one.</summary>
        internal static ulong Parse(string address, int defaultPort)
        {
            if (string.IsNullOrWhiteSpace(address))
            {
                return 0;
            }
            string host = address.Trim();
            int port = defaultPort;
            int colon = host.LastIndexOf(':');
            if (colon > 0 && int.TryParse(host.Substring(colon + 1), out int p) && p > 0 && p < 65536)
            {
                port = p;
                host = host.Substring(0, colon);
            }
            if (!IPAddress.TryParse(host, out IPAddress ip))
            {
                try
                {
                    ip = Array.Find(Dns.GetHostAddresses(host), a => a.AddressFamily == AddressFamily.InterNetwork);
                }
                catch (Exception)
                {
                    ip = null;
                }
            }
            return ip == null || ip.AddressFamily != AddressFamily.InterNetwork ? 0 : PeerOf(new IPEndPoint(ip, port));
        }

        /// <summary>The most reliable channels at a time (a session has 16 players at most).</summary>
        internal const int MaxChannels = 64;
        /// <summary>Messages whose handling threw, and the last one's fault in words.</summary>
        internal int Faults;
        internal string LastFault = "";

        private ReliableChannel Channel(ulong peer)
        {
            if (!_channels.TryGetValue(peer, out ReliableChannel c))
            {
                IPEndPoint to = EndPointOf(peer);
                c = new ReliableChannel((packet, length) => Raw(to, packet, length));
                _channels[peer] = c;
            }
            return c;
        }

        private void Raw(IPEndPoint to, byte[] packet, int length)
        {
            try
            {
                _socket.SendTo(packet, 0, length, SocketFlags.None, to);
            }
            catch (SocketException)
            {
                // (a full buffer, an unreachable address: a reliable piece is sent again, an unreliable one was allowed to be lost)
            }
            catch (ObjectDisposedException)
            {
            }
        }

        public void Send(ulong peer, byte[] data, int length, bool reliable)
        {
            if (reliable)
            {
                Channel(peer).Send(data, length);
                return;
            }
            if (length + 1 > _out.Length)
            {
                return;
            }
            _out[0] = ReliableChannel.KindUnreliable;
            Buffer.BlockCopy(data, 0, _out, 1, length);
            Raw(EndPointOf(peer), _out, length + 1);
        }

        public void Poll(double now, Action<ulong, byte[], int> received, Action<ulong> lost)
        {
            EndPoint from = new IPEndPoint(IPAddress.Any, 0);
            for (int i = 0; i < 2000; i++) // (at most this many a frame: a flood does not hold the frame)
            {
                int length;
                try
                {
                    if (_socket.Available <= 0)
                    {
                        break;
                    }
                    length = _socket.ReceiveFrom(_in, ref from);
                }
                catch (SocketException e)
                {
                    if (e.SocketErrorCode == SocketError.ConnectionReset || e.SocketErrorCode == SocketError.MessageSize)
                    {
                        continue;
                    }
                    break;
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
                if (length < 1)
                {
                    continue;
                }
                ulong peer = PeerOf((IPEndPoint)from);
                // One message's fault ends that message, not the frame's poll: thrown through, the channels' resends and
                // acknowledgements of that frame were skipped, every frame it threw, until its peers counted as lost (as
                // SteamTransport guards each of its messages; scout, 2026-11-11).
                try
                {
                    if (_in[0] == ReliableChannel.KindUnreliable)
                    {
                        Buffer.BlockCopy(_in, 1, _message, 0, length - 1);
                        received(peer, _message, length - 1);
                    }
                    else if (_channels.ContainsKey(peer) || _channels.Count < MaxChannels)
                    {
                        // (No more channels than a session and some newcomers need: every address that sent one piece got a
                        // channel of its own for good.)
                        _deliverTo = received;
                        _deliverFrom = peer;
                        Channel(peer).Receive(_in, length, _deliver);
                    }
                }
                catch (Exception e)
                {
                    Faults++;
                    LastFault = e.GetType().Name + ": " + e.Message;
                }
            }
            _lostNow.Clear();
            foreach (KeyValuePair<ulong, ReliableChannel> c in _channels)
            {
                c.Value.Update(now);
                if (c.Value.Lost)
                {
                    _lostNow.Add(c.Key);
                }
            }
            foreach (ulong peer in _lostNow)
            {
                _channels.Remove(peer);
                lost(peer);
            }
        }

        /// <summary>(The channel goes; the address may say hello again later: the session takes nothing from one who is not a player.)</summary>
        public void Close(ulong peer) => _channels.Remove(peer);

        public void Dispose()
        {
            try
            {
                _socket.Close();
            }
            catch (Exception)
            {
            }
        }
    }
}
