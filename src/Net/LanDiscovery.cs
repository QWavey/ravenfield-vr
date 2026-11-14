using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using RavenfieldVR.Core;
using UnityEngine;

namespace RavenfieldVR.Net
{
    /// <summary>A game heard on the local network (<see cref="LanDiscovery"/>).</summary>
    internal sealed class LanGame
    {
        public string Address;
        public string Name, Map, Mode, Version;
        public int Players, MaxPlayers, Port;
        public float SeenAt;
    }

    /// <summary>
    /// Games on the local network, found without typing an address (the user, 2026-11-14: "Local then will show LAN or Splitscreen"). A host
    /// over UDP (not over Steam) says so once a second by a broadcast on <see cref="BeaconPort"/>, to this PC too (a second game on the same
    /// PC hears it); the LAN page listens while it is open and lists what it heard in the last 4 seconds. Joining is the address join.
    /// </summary>
    internal static class LanDiscovery
    {
        public const int BeaconPort = 7134;
        private const string Tag = "RFVRLAN1";

        private static UdpClient _sender, _listener;
        private static float _nextBeacon;
        private static readonly Dictionary<string, LanGame> Heard = new Dictionary<string, LanGame>();
        private static readonly byte[] Buffer = new byte[0];

        /// <summary>The LAN page is open: listen for hosts (set by the page each frame it shows; off after 2 s without).</summary>
        public static void Listen() => _listenUntil = Time.unscaledTime + 2f;
        private static float _listenUntil;

        /// <summary>The port this PC's LAN game is hosted on (set by <see cref="Multiplayer.HostLan"/>).</summary>
        public static int HostPort;

        public static List<LanGame> Games
        {
            get
            {
                var list = new List<LanGame>();
                foreach (LanGame g in Heard.Values)
                {
                    if (Time.unscaledTime - g.SeenAt < 4f)
                    {
                        list.Add(g);
                    }
                }
                list.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
                return list;
            }
        }

        public static void Update()
        {
            try
            {
                Announce();
                Hear();
            }
            catch (Exception e)
            {
                Log.WarnOnce("lan", "LAN discovery: " + e.Message);
            }
        }

        private static void Announce()
        {
            if (!(Multiplayer.Hosting && !Multiplayer.OverSteam && HostPort > 0) || Time.unscaledTime < _nextBeacon)
            {
                return;
            }
            _nextBeacon = Time.unscaledTime + 1f;
            if (_sender == null)
            {
                _sender = new UdpClient { EnableBroadcast = true };
            }
            NetSession s = Multiplayer.Session;
            string text = string.Join("|", new[]
            {
                Tag, Clean(s.LocalInfo.Name + "'s game"), Clean(Multiplayer.Map), Clean(Multiplayer.Mode), s.PlayerCount.ToString(), s.MaxPlayers.ToString(),
                HostPort.ToString(), NetSession.ProtocolVersion.ToString(), PluginInfo.Version
            });
            byte[] data = Encoding.UTF8.GetBytes(text);
            _sender.Send(data, data.Length, new IPEndPoint(IPAddress.Broadcast, BeaconPort));
            _sender.Send(data, data.Length, new IPEndPoint(IPAddress.Loopback, BeaconPort)); // (a game on this PC hears it too)
        }

        private static string Clean(string s) => (s ?? "").Replace("|", "/");

        private static void Hear()
        {
            if (Time.unscaledTime > _listenUntil)
            {
                if (_listener != null)
                {
                    _listener.Close();
                    _listener = null;
                }
                return;
            }
            if (_listener == null)
            {
                _listener = new UdpClient();
                _listener.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                _listener.Client.Bind(new IPEndPoint(IPAddress.Any, BeaconPort));
            }
            for (int i = 0; i < 32 && _listener.Available > 0; i++)
            {
                var from = new IPEndPoint(IPAddress.Any, 0);
                byte[] data = _listener.Receive(ref from);
                string[] f = Encoding.UTF8.GetString(data).Split('|');
                if (f.Length < 9 || f[0] != Tag || !int.TryParse(f[6], out int port))
                {
                    continue;
                }
                string address = (IPAddress.IsLoopback(from.Address) ? "127.0.0.1" : from.Address.ToString()) + ":" + port;
                if (!Heard.TryGetValue(address, out LanGame g))
                {
                    g = new LanGame { Address = address };
                    Heard[address] = g;
                }
                g.Name = f[1];
                g.Map = f[2];
                g.Mode = f[3];
                int.TryParse(f[4], out g.Players);
                int.TryParse(f[5], out g.MaxPlayers);
                g.Port = port;
                g.Version = f[8];
                g.SeenAt = Time.unscaledTime;
            }
        }

        public static void Shutdown()
        {
            _sender?.Close();
            _sender = null;
            _listener?.Close();
            _listener = null;
            Heard.Clear();
        }
    }
}
