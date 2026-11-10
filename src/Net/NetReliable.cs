using System;
using System.Collections.Generic;

namespace RavenfieldVR.Net
{
    /// <summary>
    /// Reliable, ordered messages to one peer over a line that loses, doubles and reorders packets (plain UDP; Steam's own transport
    /// needs none of this). Each message goes out in pieces of at most <see cref="PieceBytes"/>, each piece numbered; the other side
    /// answers every piece with how far it has them all and which later ones it holds; a piece not answered is sent again (after
    /// 0.2 s, each time half as long again, at most 2 s). Pieces are handed on in order and joined into their messages. Sixteen seconds
    /// without an answer to a waiting piece: the peer is lost. Time is given from outside (checked without a clock in the tests).
    /// </summary>
    internal sealed class ReliableChannel
    {
        /// <summary>The first byte of every packet on a line that carries this channel.</summary>
        internal const byte KindUnreliable = 0, KindPiece = 1, KindAck = 2;

        internal const int PieceBytes = 1000;
        private const int Window = 128;
        private const double FirstResend = 0.2, MaxResend = 2.0, LostAfter = 16.0;

        private sealed class Piece
        {
            public ushort Seq;
            public byte[] Packet; // the whole packet as sent: kind, number, more-follows, the bytes
            public double SentAt, Resend, FirstSentAt;
            public bool Sent;
        }

        private readonly Action<byte[], int> _send;
        private readonly List<Piece> _waiting = new List<Piece>(); // sent or queued, not yet answered; in order of number
        private readonly Dictionary<ushort, (byte[] data, bool more)> _ahead = new Dictionary<ushort, (byte[], bool)>();
        private readonly List<byte[]> _joining = new List<byte[]>();
        private ushort _nextSeq, _expected;
        private bool _ackDue;

        /// <summary>The peer did not answer for <see cref="LostAfter"/> seconds.</summary>
        public bool Lost { get; private set; }

        /// <summary>Pieces sent again so far (the tests' and the log's measure of the line).</summary>
        public int Resent { get; private set; }

        /// <param name="send">Puts one packet on the line (the array is this channel's: sent at once, not kept).</param>
        public ReliableChannel(Action<byte[], int> send)
        {
            _send = send;
        }

        /// <summary>A message queued: its pieces go out as the window has room (at <see cref="Update"/>).</summary>
        public void Send(byte[] data, int length)
        {
            int offset = 0;
            do
            {
                int count = Math.Min(PieceBytes, length - offset);
                bool more = offset + count < length;
                var packet = new byte[4 + count];
                packet[0] = KindPiece;
                packet[1] = (byte)_nextSeq;
                packet[2] = (byte)(_nextSeq >> 8);
                packet[3] = more ? (byte)1 : (byte)0;
                Buffer.BlockCopy(data, offset, packet, 4, count);
                _waiting.Add(new Piece { Seq = _nextSeq, Packet = packet });
                _nextSeq++;
                offset += count;
            }
            while (offset < length);
        }

        /// <summary>Once a frame: pieces sent for the first time or again, the answer to what came in.</summary>
        public void Update(double now)
        {
            for (int i = 0; i < _waiting.Count && i < Window; i++)
            {
                Piece p = _waiting[i];
                if (!p.Sent)
                {
                    p.Sent = true;
                    p.FirstSentAt = p.SentAt = now;
                    p.Resend = FirstResend;
                    _send(p.Packet, p.Packet.Length);
                }
                else if (now - p.SentAt >= p.Resend)
                {
                    if (now - p.FirstSentAt >= LostAfter)
                    {
                        Lost = true;
                        return;
                    }
                    p.SentAt = now;
                    p.Resend = Math.Min(MaxResend, p.Resend * 1.5);
                    Resent++;
                    _send(p.Packet, p.Packet.Length);
                }
            }
            if (_ackDue)
            {
                _ackDue = false;
                SendAck();
            }
        }

        /// <summary>Distance from a to b going up through the wrap of 16 bits (under 32768: b is ahead of a).</summary>
        private static int Ahead(ushort a, ushort b) => (ushort)(b - a);

        private void SendAck()
        {
            // How far all pieces are here (the next one wanted), and of the 32 after it, which are held already.
            uint mask = 0;
            for (int i = 0; i < 32; i++)
            {
                if (_ahead.ContainsKey((ushort)(_expected + 1 + i)))
                {
                    mask |= 1u << i;
                }
            }
            var ack = new byte[7];
            ack[0] = KindAck;
            ack[1] = (byte)_expected;
            ack[2] = (byte)(_expected >> 8);
            ack[3] = (byte)mask;
            ack[4] = (byte)(mask >> 8);
            ack[5] = (byte)(mask >> 16);
            ack[6] = (byte)(mask >> 24);
            _send(ack, ack.Length);
        }

        /// <summary>
        /// A packet of this channel from the line (its first byte <see cref="KindPiece"/> or <see cref="KindAck"/>): whole messages
        /// that are complete with it go to <paramref name="deliver"/>, in the order they were sent.
        /// </summary>
        public void Receive(byte[] packet, int length, Action<byte[], int> deliver)
        {
            if (length < 1)
            {
                return;
            }
            if (packet[0] == KindAck)
            {
                if (length < 7)
                {
                    return;
                }
                ushort expected = (ushort)(packet[1] | packet[2] << 8);
                uint mask = (uint)(packet[3] | packet[4] << 8 | packet[5] << 16 | packet[6] << 24);
                _waiting.RemoveAll(p =>
                {
                    int ahead = Ahead(expected, p.Seq); // 0: the one it wants next; over 32768: before it, so it has it
                    return ahead >= 32768 || ahead >= 1 && ahead <= 32 && (mask & 1u << (ahead - 1)) != 0;
                });
                return;
            }
            if (packet[0] != KindPiece || length < 4)
            {
                return;
            }
            _ackDue = true; // (every piece is answered, a doubled one too: its answer may have been the one lost)
            ushort seq = (ushort)(packet[1] | packet[2] << 8);
            int distance = Ahead(_expected, seq);
            if (distance >= 32768 || distance > 4 * Window || _ahead.ContainsKey(seq))
            {
                return; // had already, or from too far ahead to be this stream's
            }
            var data = new byte[length - 4];
            Buffer.BlockCopy(packet, 4, data, 0, data.Length);
            _ahead[seq] = (data, packet[3] != 0);
            while (_ahead.TryGetValue(_expected, out (byte[] data, bool more) next))
            {
                _ahead.Remove(_expected);
                _expected++;
                if (next.more || _joining.Count > 0)
                {
                    _joining.Add(next.data);
                    if (next.more)
                    {
                        continue;
                    }
                    int total = 0;
                    foreach (byte[] part in _joining)
                    {
                        total += part.Length;
                    }
                    var whole = new byte[total];
                    int at = 0;
                    foreach (byte[] part in _joining)
                    {
                        Buffer.BlockCopy(part, 0, whole, at, part.Length);
                        at += part.Length;
                    }
                    _joining.Clear();
                    deliver(whole, whole.Length);
                }
                else
                {
                    deliver(next.data, next.data.Length);
                }
            }
        }
    }
}
