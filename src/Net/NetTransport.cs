using System;
using System.Collections.Generic;

namespace RavenfieldVR.Net
{
    /// <summary>
    /// How messages reach the other players: Steam's relayed peer-to-peer messages in play (<see cref="SteamTransport"/>), plain UDP
    /// for a LAN or a direct address and for two games on one PC (<see cref="UdpTransport"/>), memory in the offline tests
    /// (<see cref="LoopbackNetwork"/>). A peer is a number: a Steam id, an address and port, a test's own. Messages arrive whole;
    /// a reliable one arrives once and in the order sent, an unreliable one may be lost, doubled or late.
    /// </summary>
    internal interface INetTransport : IDisposable
    {
        ulong LocalId { get; }

        /// <summary>The most bytes one unreliable message may have (a reliable one may be longer: it is sent in pieces).</summary>
        int MaxUnreliable { get; }

        void Send(ulong peer, byte[] data, int length, bool reliable);

        /// <summary>
        /// Once a frame: what came in goes to <paramref name="received"/> (the array is the transport's own, valid during the call
        /// only), a peer that went away or could not be reached to <paramref name="lost"/>. <paramref name="now"/>: seconds, any
        /// steady clock.
        /// </summary>
        void Poll(double now, Action<ulong, byte[], int> received, Action<ulong> lost);

        /// <summary>The peer is done with (it left, it was refused): nothing more is sent to it or taken from it.</summary>
        void Close(ulong peer);
    }

    /// <summary>
    /// A network in memory for the offline tests: every transport made from it can reach every other by its id. Its line can be made
    /// bad on purpose (a seeded share of unreliable messages lost, doubled or held back) to check that what is built on it still
    /// works; reliable messages always arrive once and in order, as Steam's do (the UDP transport's own reliability is checked on a
    /// bad line through <see cref="ReliableChannel"/> directly).
    /// </summary>
    internal sealed class LoopbackNetwork
    {
        private readonly Dictionary<ulong, Endpoint> _ends = new Dictionary<ulong, Endpoint>();
        private readonly Random _random;

#pragma warning disable 0649 // (set by the offline tests alone, from outside this assembly)
        /// <summary>Of unreliable messages: the share lost, and the share delivered twice.</summary>
        public double Loss, Duplicate;

        /// <summary>Seconds a message is on its way (each unreliable one up to this much longer, so they pass each other).</summary>
        public double Delay, Jitter;
#pragma warning restore 0649

        public LoopbackNetwork(int seed = 1)
        {
            _random = new Random(seed);
        }

        public INetTransport Join(ulong id)
        {
            var end = new Endpoint(this, id);
            _ends[id] = end;
            return end;
        }

        private sealed class Endpoint : INetTransport
        {
            private readonly LoopbackNetwork _net;
            private readonly List<(double due, long order, ulong from, byte[] data)> _inbox = new List<(double, long, ulong, byte[])>();
            private readonly HashSet<ulong> _closed = new HashSet<ulong>();
            private readonly List<ulong> _gone = new List<ulong>();
            private double _now;
            private long _order;
            private bool _disposed;

            public Endpoint(LoopbackNetwork net, ulong id)
            {
                _net = net;
                LocalId = id;
            }

            public ulong LocalId { get; }

            public int MaxUnreliable => 1200;

            public void Send(ulong peer, byte[] data, int length, bool reliable)
            {
                if (_disposed || _closed.Contains(peer) || !_net._ends.TryGetValue(peer, out Endpoint to) || to._disposed)
                {
                    return;
                }
                var copy = new byte[length];
                Buffer.BlockCopy(data, 0, copy, 0, length);
                if (reliable)
                {
                    to._inbox.Add((_now + _net.Delay, to._order++, LocalId, copy));
                    return;
                }
                if (_net._random.NextDouble() < _net.Loss)
                {
                    return;
                }
                int times = _net._random.NextDouble() < _net.Duplicate ? 2 : 1;
                for (int i = 0; i < times; i++)
                {
                    to._inbox.Add((_now + _net.Delay + _net._random.NextDouble() * _net.Jitter, to._order++, LocalId, copy));
                }
            }

            public void Poll(double now, Action<ulong, byte[], int> received, Action<ulong> lost)
            {
                _now = now;
                foreach (ulong peer in _gone)
                {
                    lost(peer);
                }
                _gone.Clear();
                // What is due, in the order it became due (reliable ones keep their own order: one delay for all of them).
                _inbox.Sort((a, b) => a.due != b.due ? a.due.CompareTo(b.due) : a.order.CompareTo(b.order));
                int taken = 0;
                while (taken < _inbox.Count && _inbox[taken].due <= now)
                {
                    taken++;
                }
                if (taken == 0)
                {
                    return;
                }
                var due = _inbox.GetRange(0, taken);
                _inbox.RemoveRange(0, taken);
                foreach ((double _, long _, ulong from, byte[] data) in due)
                {
                    if (!_closed.Contains(from))
                    {
                        received(from, data, data.Length);
                    }
                }
            }

            public void Close(ulong peer) => _closed.Add(peer);

            public void Dispose()
            {
                if (_disposed)
                {
                    return;
                }
                _disposed = true;
                // The others learn it at their next poll, as a dropped connection is learned.
                foreach (Endpoint other in _net._ends.Values)
                {
                    if (!ReferenceEquals(other, this) && !other._disposed)
                    {
                        other._gone.Add(LocalId);
                    }
                }
                _net._ends.Remove(LocalId);
            }
        }
    }
}
