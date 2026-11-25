using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using RavenfieldVR.Net;
using UnityEngine;

namespace RavenfieldVR.Tests
{
    /// <summary>
    /// The multiplayer's own layers without the game (run-tests.ps1 compiles this with src/Net's plain files): the wire format, the
    /// reliable channel on a bad line, the session (joining, refusing, passing on, leaving) over a network in memory, and the UDP
    /// transport over this PC's loopback. Exit code 0 when all pass.
    /// </summary>
    internal static class NetTests
    {
        private static int _failed, _passed;

        private static void Check(bool ok, string what)
        {
            if (ok)
            {
                _passed++;
            }
            else
            {
                _failed++;
                Console.WriteLine("FAIL " + what);
            }
        }

        private static int Main(string[] args)
        {
            // Unity's managed core (its vector types only) from the game's folder.
            string managed = args.Length > 0 ? args[0] : "";
            AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
            {
                string file = Path.Combine(managed, new AssemblyName(e.Name).Name + ".dll");
                return File.Exists(file) ? Assembly.LoadFrom(file) : null;
            };
            Run(WireTests, "wire");
            Run(ReliableTests, "reliable");
            Run(SessionTests, "session");
            Run(UdpTests, "udp");
            Run(VoiceCodecTests, "voice codec");
            Console.WriteLine($"net: {_passed} passed, {_failed} failed");
            return _failed == 0 ? 0 : 1;
        }

        private static void Run(Action test, string name)
        {
            try
            {
                test();
            }
            catch (Exception e)
            {
                _failed++;
                Console.WriteLine($"FAIL {name}: threw {e}");
            }
        }

        private static void WireTests()
        {
            var random = new System.Random(7);
            var w = new NetWriter(4);
            var numbers = new List<int>();
            for (int i = 0; i < 2000; i++)
            {
                int v = i < 40 ? new[] { 0, 1, -1, 127, 128, -128, 16383, 16384, int.MaxValue, int.MinValue }[i % 10] : random.Next(int.MinValue, int.MaxValue) >> random.Next(0, 31);
                numbers.Add(v);
                w.VarInt(v);
            }
            w.Byte(200);
            w.Bool(true);
            w.UShort(54321);
            w.UInt32(0xDEADBEEF);
            w.ULong(0x0123456789ABCDEFul);
            w.Float(-123.456f);
            w.Ranged(0.25f, -1f, 1f);
            w.Vector(new Vector3(1.5f, -2000.25f, 3e6f));
            w.SmallVector(new Vector3(0.123f, -0.456f, 1.789f));
            w.String("Hällo, wörld ☃");
            w.String("");
            w.String(null);
            var r = new NetReader(w.Buffer, 0, w.Length);
            bool same = true;
            foreach (int v in numbers)
            {
                same &= r.VarInt() == v;
            }
            Check(same, "varints come back as written (2000 of them, the edges too)");
            Check(r.Byte() == 200 && r.Bool() && r.UShort() == 54321 && r.UInt32() == 0xDEADBEEF && r.ULong() == 0x0123456789ABCDEFul, "bytes, shorts, ints and longs come back");
            Check(r.Float() == -123.456f, "a float comes back bit for bit");
            Check(Math.Abs(r.Ranged(-1f, 1f) - 0.25f) < 1e-4f, "a ranged value within its 16 bits");
            Vector3 v3 = r.Vector();
            Check(v3.x == 1.5f && v3.y == -2000.25f && v3.z == 3e6f, "a vector comes back");
            Vector3 small = r.SmallVector();
            Check(Math.Abs(small.x - 0.123f) < 6e-4f && Math.Abs(small.y + 0.456f) < 6e-4f && Math.Abs(small.z - 1.789f) < 6e-4f, "a small vector within a millimetre");
            Check(r.String() == "Hällo, wörld ☃" && r.String() == "" && r.String() == "", "texts come back (an empty and a null one as empty)");
            Check(r.Remaining == 0, "nothing left over");

            // A text cut to what its reader takes (2026-11-11): at a whole character, and one that fits is left alone.
            var capped = new NetWriter(64);
            capped.String(new string('ä', 40), 64); // 80 bytes of two-byte characters
            capped.String(new string('☃', 30), 64); // 90 bytes of three-byte characters: 21 fit, 63 bytes
            capped.String("short", 64);
            var cr = new NetReader(capped.Buffer, 0, capped.Length);
            string two = cr.String(64), three = cr.String(64), shortOne = cr.String(64);
            Check(two == new string('ä', 32) && three == new string('☃', 21) && shortOne == "short" && cr.Remaining == 0,
                  $"a text longer than its reader takes is cut at a whole character ({two.Length} and {three.Length} characters)");

            // Rotations: the angle between what went in and what came out, over many random ones and the axes.
            double worst = 0;
            for (int i = 0; i < 5000; i++)
            {
                Quaternion q = i < 8
                    ? new[] { new Quaternion(0, 0, 0, 1), new Quaternion(1, 0, 0, 0), new Quaternion(0, 1, 0, 0), new Quaternion(0, 0, 1, 0), new Quaternion(0, 0, 0, -1),
                              new Quaternion(0.5f, 0.5f, 0.5f, 0.5f), new Quaternion(-0.5f, 0.5f, -0.5f, 0.5f), new Quaternion(0.7071f, 0, 0.7071f, 0) }[i]
                    : Unit(random);
                var one = new NetWriter(8);
                one.Rotation(q);
                var back = new NetReader(one.Buffer, 0, one.Length).Rotation();
                // (Against the input at unit length: the list's 0.7071s are a little short of it, which read as half a degree.)
                double norm = Math.Sqrt((double)q.x * q.x + (double)q.y * q.y + (double)q.z * q.z + (double)q.w * q.w);
                double dot = Math.Abs((double)q.x * back.x + (double)q.y * back.y + (double)q.z * back.z + (double)q.w * back.w) / norm;
                worst = Math.Max(worst, 2 * Math.Acos(Math.Min(1.0, dot)) * 180 / Math.PI);
                Check(one.Length == 4, "a rotation is 4 bytes");
            }
            Check(worst < 0.3, $"a rotation comes back within 0.3 degrees (worst {worst:0.000})");
            // A broken word: every small part at its largest (no sender packs that). It comes back a whole turn, not scaled.
            var broken = new NetWriter(8);
            broken.UInt32(0xFFFFFFFFu);
            Quaternion junk = new NetReader(broken.Buffer, 0, broken.Length).Rotation();
            double junkNorm = Math.Sqrt((double)junk.x * junk.x + (double)junk.y * junk.y + (double)junk.z * junk.z + (double)junk.w * junk.w);
            Check(Math.Abs(junkNorm - 1.0) < 1e-4, $"a broken rotation word comes back of unit length ({junkNorm:0.0000})");

            // A message cut short is refused, wherever it is cut.
            int refused = 0;
            for (int cut = 0; cut < w.Length; cut += 97)
            {
                try
                {
                    var c = new NetReader(w.Buffer, 0, cut);
                    for (int i = 0; i < numbers.Count; i++)
                    {
                        c.VarInt();
                    }
                    c.ULong();
                    c.Vector();
                    c.Vector();
                    c.String();
                    c.String();
                    c.String();
                    c.String();
                }
                catch (NetFormatException)
                {
                    refused++;
                }
            }
            Check(refused == (w.Length + 96) / 97, "a message cut short is refused at every cut");
            try
            {
                var big = new NetWriter();
                big.VarUInt(1000000);
                new NetReader(big.Buffer, 0, big.Length).String(100);
                Check(false, "a text longer than allowed is refused");
            }
            catch (NetFormatException)
            {
                Check(true, "a text longer than allowed is refused");
            }
        }

        private static Quaternion Unit(System.Random random)
        {
            double x = random.NextDouble() * 2 - 1, y = random.NextDouble() * 2 - 1, z = random.NextDouble() * 2 - 1, w = random.NextDouble() * 2 - 1;
            double n = Math.Sqrt(x * x + y * y + z * z + w * w);
            return n < 1e-3 ? new Quaternion(0, 0, 0, 1) : new Quaternion((float)(x / n), (float)(y / n), (float)(z / n), (float)(w / n));
        }

        /// <summary>A line between two reliable channels that loses, doubles and delays packets by a seeded dice.</summary>
        private sealed class BadLine
        {
            public readonly List<(double due, byte[] packet, bool toB)> Flying = new List<(double, byte[], bool)>();
            public double Now, Loss = 0.3, Duplicate = 0.1, Jitter = 0.15;
            private readonly System.Random _dice = new System.Random(11);
            public int Sent;

            public void Put(byte[] packet, int length, bool toB)
            {
                Sent++;
                if (_dice.NextDouble() < Loss)
                {
                    return;
                }
                var copy = new byte[length];
                Buffer.BlockCopy(packet, 0, copy, 0, length);
                for (int i = _dice.NextDouble() < Duplicate ? 2 : 1; i > 0; i--)
                {
                    Flying.Add((Now + 0.01 + _dice.NextDouble() * Jitter, copy, toB));
                }
            }
        }

        private static void ReliableTests()
        {
            var line = new BadLine();
            ReliableChannel a = null, b = null;
            a = new ReliableChannel((p, n) => line.Put(p, n, true));
            b = new ReliableChannel((p, n) => line.Put(p, n, false));
            var random = new System.Random(3);
            var sentA = new List<byte[]>();
            var sentB = new List<byte[]>();
            var gotA = new List<byte[]>();
            var gotB = new List<byte[]>();
            for (int i = 0; i < 300; i++)
            {
                // Sizes round the piece's length and far over it: one piece, exactly one, many.
                int size = i % 7 == 0 ? ReliableChannel.PieceBytes * (1 + i % 4) : i % 5 == 0 ? random.Next(1001, 6000) : random.Next(1, 300);
                var m = new byte[size];
                random.NextBytes(m);
                (i % 3 == 0 ? sentB : sentA).Add(m);
            }
            int ia = 0, ib = 0;
            for (line.Now = 0; line.Now < 120 && (gotA.Count < sentB.Count || gotB.Count < sentA.Count); line.Now += 1.0 / 60)
            {
                // A few new messages a frame, as a game sends them.
                for (int k = 0; k < 3 && ia < sentA.Count; k++, ia++)
                {
                    a.Send(sentA[ia], sentA[ia].Length);
                }
                for (int k = 0; k < 2 && ib < sentB.Count; k++, ib++)
                {
                    b.Send(sentB[ib], sentB[ib].Length);
                }
                a.Update(line.Now);
                b.Update(line.Now);
                var due = line.Flying.Where(f => f.due <= line.Now).ToList();
                line.Flying.RemoveAll(f => f.due <= line.Now);
                foreach ((double _, byte[] packet, bool toB) in due)
                {
                    if (toB)
                    {
                        b.Receive(packet, packet.Length, (d, n) => gotB.Add(d.Take(n).ToArray()));
                    }
                    else
                    {
                        a.Receive(packet, packet.Length, (d, n) => gotA.Add(d.Take(n).ToArray()));
                    }
                }
            }
            Check(gotB.Count == sentA.Count && gotB.Zip(sentA, (x, y) => x.SequenceEqual(y)).All(x => x),
                  $"200 messages one way over a line losing 30% arrive once, whole and in order ({gotB.Count} of {sentA.Count}, {a.Resent} pieces sent again)");
            Check(gotA.Count == sentB.Count && gotA.Zip(sentB, (x, y) => x.SequenceEqual(y)).All(x => x),
                  $"100 the other way too ({gotA.Count} of {sentB.Count})");
            Check(!a.Lost && !b.Lost, "neither side calls the other lost on a bad but living line");
            Check(line.Now < 60, $"all of it within a minute of line time ({line.Now:0.0} s)");

            // A message of pieces with no end (2026-11-11): one of 1.5 MB is given up by its receiver (the peer counted as lost,
            // nothing delivered), while one of 300 kB comes through whole.
            foreach (int size in new[] { 300 * 1024, 1536 * 1024 })
            {
                var wide = new BadLine { Loss = 0, Duplicate = 0, Jitter = 0 };
                ReliableChannel tx = new ReliableChannel((p, n) => wide.Put(p, n, true)), rx = new ReliableChannel((p, n) => wide.Put(p, n, false));
                var bigMessage = new byte[size];
                new System.Random(7).NextBytes(bigMessage);
                tx.Send(bigMessage, bigMessage.Length);
                byte[] arrived = null;
                for (wide.Now = 0; wide.Now < 120 && arrived == null && !rx.Lost; wide.Now += 1.0 / 60)
                {
                    tx.Update(wide.Now);
                    rx.Update(wide.Now);
                    var dueNow = wide.Flying.Where(f => f.due <= wide.Now).ToList();
                    wide.Flying.RemoveAll(f => f.due <= wide.Now);
                    foreach ((double _, byte[] packet, bool toB) in dueNow)
                    {
                        if (toB)
                        {
                            rx.Receive(packet, packet.Length, (d, n) => arrived = d.Take(n).ToArray());
                        }
                        else
                        {
                            tx.Receive(packet, packet.Length, (d, n) => { });
                        }
                    }
                }
                if (size < 1024 * 1024)
                {
                    Check(arrived != null && arrived.SequenceEqual(bigMessage) && !rx.Lost, $"a message of {size / 1024} kB in pieces arrives whole");
                }
                else
                {
                    Check(arrived == null && rx.Lost, $"a message of {size / 1024} kB is not joined: its sender is given up ({(arrived != null ? "DELIVERED" : "not delivered")}, lost {rx.Lost})");
                }
            }

            // Many more than the 16-bit numbers hold (they wrap), on a clean line.
            var clean = new BadLine { Loss = 0, Duplicate = 0, Jitter = 0 };
            ReliableChannel c = new ReliableChannel((p, n) => clean.Put(p, n, true)), d2 = new ReliableChannel((p, n) => clean.Put(p, n, false));
            int next = 0, got = 0;
            bool inOrder = true;
            for (clean.Now = 0; got < 70000 && clean.Now < 400; clean.Now += 1.0 / 60)
            {
                for (int k = 0; k < 100 && next < 70000; k++, next++)
                {
                    c.Send(BitConverter.GetBytes(next), 4);
                }
                c.Update(clean.Now);
                d2.Update(clean.Now);
                var due = clean.Flying.ToList();
                clean.Flying.Clear();
                foreach ((double _, byte[] packet, bool toB) in due)
                {
                    (toB ? d2 : c).Receive(packet, packet.Length, (m, n) => inOrder &= BitConverter.ToInt32(m, 0) == got++);
                }
            }
            Check(got == 70000 && inOrder, $"70000 messages pass the wrap of the piece numbers in order ({got})");

            // No one there: lost after its sixteen seconds, not before.
            var nowhere = new ReliableChannel((p, n) => { });
            nowhere.Send(new byte[] { 1 }, 1);
            double t = 0;
            for (; t < 15.5; t += 0.1)
            {
                nowhere.Update(t);
            }
            Check(!nowhere.Lost, "a silent peer is not called lost before 16 s");
            for (; t < 20; t += 0.1)
            {
                nowhere.Update(t);
            }
            Check(nowhere.Lost, "a silent peer is called lost after 16 s");
        }

        private sealed class Seat
        {
            public NetSession Session;
            public INetTransport Transport;
            public readonly List<string> Log = new List<string>();
            public readonly List<(byte kind, byte sender, string text)> Messages = new List<(byte, byte, string)>();
        }

        private static Seat Sit(NetSession session, INetTransport transport)
        {
            var seat = new Seat { Session = session, Transport = transport };
            session.Joined += p => seat.Log.Add("joined " + p.Id + " " + p.Info.Name);
            session.Left += p => seat.Log.Add("left " + p.Id);
            session.Connected += () => seat.Log.Add("connected as " + session.LocalId);
            session.Closed += why => seat.Log.Add("closed: " + why);
            session.Message += (byte kind, byte sender, ref NetReader r) => seat.Messages.Add((kind, sender, r.String()));
            return seat;
        }

        private static NetPlayerInfo Info(string name, int build = 31, string version = "test") => new NetPlayerInfo { Name = name, GameBuild = build, ModVersion = version, Vr = true, SteamId = 42 };

        private static void SessionTests()
        {
            var net = new LoopbackNetwork(5) { Delay = 0.02, Jitter = 0.02, Loss = 0.2, Duplicate = 0.1 };
            double now = 0;
            var seats = new List<Seat>();
            void Step(double seconds)
            {
                for (double end = now + seconds; now < end; now += 1.0 / 60)
                {
                    foreach (Seat s in seats.ToArray())
                    {
                        s.Session.Update(now);
                    }
                }
            }
            INetTransport ht = net.Join(100);
            Seat host = Sit(NetSession.Host(ht, Info("Host")), ht);
            seats.Add(host);
            Seat Client(ulong id, string name, int build = 31, string version = "test")
            {
                INetTransport t = net.Join(id);
                Seat s = Sit(NetSession.Join(t, 100, Info(name, build, version)), t);
                seats.Add(s);
                return s;
            }
            Seat a = Client(201, "Anna"), b = Client(202, "Ben"), c = Client(203, "Cleo");
            Step(1.0);
            Check(a.Session.State == NetState.Connected && b.Session.State == NetState.Connected && c.Session.State == NetState.Connected, "three clients are welcomed");
            var ids = new[] { a.Session.LocalId, b.Session.LocalId, c.Session.LocalId };
            Check(ids.Distinct().Count() == 3 && ids.All(i => i >= 1 && i <= 3), $"each has its own number 1-3 ({string.Join(",", ids)})");
            Check(seats.All(s => s.Session.PlayerCount == 4), "everyone knows all four players");
            Check(seats.All(s => string.Join(",", s.Session.Players.OrderBy(p => p.Id).Select(p => p.Info.Name)) == string.Join(",", host.Session.Players.OrderBy(p => p.Id).Select(p => p.Info.Name))),
                  "with the same names under the same numbers");

            // A message to everyone from a client: the host and the other clients get it once, the sender not.
            var w = new NetWriter();
            w.String("hello all");
            a.Session.Send(20, w, true);
            Step(0.5);
            Check(host.Messages.Count(m => m.kind == 20 && m.sender == a.Session.LocalId && m.text == "hello all") == 1, "the host gets a client's message once");
            Check(b.Messages.Count(m => m.text == "hello all" && m.sender == a.Session.LocalId) == 1 && c.Messages.Count(m => m.text == "hello all") == 1, "the other clients get it once, with the sender's number");
            Check(a.Messages.Count == 0, "the sender does not get its own");
            // One to a single player, client to client and host to client.
            w.Reset();
            w.String("only ben");
            a.Session.SendTo(b.Session.LocalId, 21, w, true);
            w.Reset();
            w.String("from the host to cleo");
            host.Session.SendTo(c.Session.LocalId, 22, w, true);
            w.Reset();
            w.String("to the host");
            b.Session.SendTo(0, 23, w, true);
            Step(0.5);
            Check(b.Messages.Count(m => m.text == "only ben") == 1 && c.Messages.All(m => m.text != "only ben") && host.Messages.All(m => m.text != "only ben"), "a message to one player reaches only that one");
            Check(c.Messages.Count(m => m.text == "from the host to cleo" && m.sender == 0) == 1 && a.Messages.All(m => m.text != "from the host to cleo"), "the host's to one client too");
            Check(host.Messages.Count(m => m.text == "to the host") == 1 && a.Messages.All(m => m.text != "to the host") && c.Messages.All(m => m.text != "to the host"), "a client's to the host stays with the host");
            // A client claiming to be another: the host puts the true sender in.
            Check(host.Messages.All(m => m.sender != 0) && b.Messages.Where(m => m.text == "hello all").All(m => m.sender == a.Session.LocalId), "the sender's number is the host's word, not the message's");

            // A hello that does not read, from a player's own address (one byte, the hello's kind only): he stays (he was dropped
            // before the body failed to read; transport scout, 2026-11-12).
            byte annaId = a.Session.LocalId;
            a.Transport.Send(100, new byte[] { 1 }, 1, true);
            Step(0.5);
            Check(host.Session.PlayerCount == 4 && !host.Log.Contains("left " + annaId), "a broken hello from a player's address does not drop him");

            // Unreliable states on the lossy line: most arrive, none twice is not promised (the game takes the newest).
            for (int i = 0; i < 200; i++)
            {
                w.Reset();
                w.String("state " + i);
                a.Session.Send(30, w, false);
                Step(1.0 / 60);
            }
            Step(0.5);
            int states = b.Messages.Count(m => m.kind == 30);
            Check(states > 80 && states < 320, $"unreliable messages get through a line losing 20% twice over ({states} of 200 heard by another client)");
            Step(2.0);
            Check(host.Session.Players.Where(p => p.Id != 0).All(p => p.Ping > 0.03 && p.Ping < 0.2) && a.Session.Player(0).Ping > 0.03, "the round trip is measured (40-80 ms on this line)");

            // Refused: another game build, and a full game.
            Seat old = Client(204, "Old", 30);
            Step(1.0);
            Check(old.Session.State == NetState.Closed && old.Session.CloseReason.Contains("game build 31") && old.Session.CloseReason.Contains("30"), "another game build is refused with both numbers in the reason: " + old.Session.CloseReason);
            seats.Remove(old);
            host.Session.MaxPlayers = 4;
            // Another version of the mod: refused with both versions named.
            Seat older = Client(209, "Older", version: "1.0.0");
            Step(1.0);
            Check(older.Session.State == NetState.Closed && older.Session.CloseReason.Contains("1.0.0") && older.Session.CloseReason.Contains("test"),
                  "another version of the mod is refused with both versions in the reason: " + older.Session.CloseReason);
            seats.Remove(older);
            Seat late = Client(205, "Late");
            Step(1.0);
            Check(late.Session.State == NetState.Closed && late.Session.CloseReason.Contains("full"), "a full game refuses the fifth: " + late.Session.CloseReason);
            seats.Remove(late);
            Check(host.Session.PlayerCount == 4 && a.Log.All(l => !l.Contains("Old") && !l.Contains("Late")), "the refused never showed up for the others");
            // The host's Kick: the player told why, gone for everyone, the others stay.
            byte cleoId = c.Session.LocalId;
            host.Session.Kick(cleoId);
            Step(1.5);
            Check(c.Session.State == NetState.Closed && c.Session.CloseReason.Contains("removed you"), "a kicked player's game ends with the reason: " + c.Session.CloseReason);
            Check(host.Session.Player(cleoId) == null && a.Session.Player(cleoId) == null && b.Session.Player(cleoId) == null, "he is gone for the host and the others");
            Check(a.Session.State == NetState.Connected && b.Session.State == NetState.Connected, "the others stay in");
            host.Session.Kick(0);
            Check(host.Session.State == NetState.Connected, "the host cannot kick himself");
            seats.Remove(c);
            Seat cleoAgain = Client(203, "Cleo");
            Step(1.5);
            Check(cleoAgain.Session.State == NetState.Connected, "the kicked player can join again (a kick is not a ban)");
            c = cleoAgain;
            host.Session.Admit = (info, peer) => info.Name == "Banned" ? "not welcome here" : null;
            host.Session.MaxPlayers = 16;
            Seat banned = Client(206, "Banned");
            Step(1.0);
            Check(banned.Session.CloseReason != null && banned.Session.CloseReason.Contains("not welcome"), "the host's own check can refuse");
            seats.Remove(banned);

            // A name longer than the 64 bytes a name is read with (a Steam name may be): joined all the same, the name cut.
            Seat longName = Client(208, new string('ä', 50));
            Step(1.0);
            Check(longName.Session.State == NetState.Connected && host.Session.Players.Any(pl => pl.Info.Name == new string('ä', 32)),
                  "a player with a name of 100 bytes joins, the name cut to 64: " + longName.Session.State + " " + longName.Session.CloseReason);
            longName.Session.Leave();
            seats.Remove(longName);
            Step(1.0);

            // A player whose game closed without a goodbye joins again from the same address (2026-11-11): taken in at once, not after
            // the old one timed out (12 s), and only once.
            Seat gone = Client(211, "Gone");
            Step(5.0);
            seats.Remove(gone);
            int before = host.Session.PlayerCount;
            Seat back = Client(211, "Back");
            Step(1.0);
            Check(back.Session.State == NetState.Connected && host.Session.PlayerCount == before
                  && host.Session.Players.Count(pl => pl.Info.Name == "Back") == 1 && host.Session.Players.All(pl => pl.Info.Name != "Gone"),
                  $"a player joining again from the same address is taken in at once, the old one gone ({back.Session.State}, {host.Session.PlayerCount} players)");
            back.Session.Leave();
            seats.Remove(back);
            Step(1.0);

            // A whole hello from a playing player's address that the host refuses (his game build changed): he is not dropped first,
            // only refused (session scout, 2026-11-24: dropped before the checks, a refused hello took him out of the game).
            Seat stay = Client(212, "Stay");
            Step(5.0); // (past the 4 s in which a hello again is his welcome's echo)
            byte stayId = stay.Session.LocalId;
            int withStay = host.Session.PlayerCount;
            var refusedHello = new NetWriter();
            refusedHello.Byte(1);
            refusedHello.VarInt(NetSession.ProtocolVersion);
            Info("Stay", 30).Write(refusedHello);
            stay.Transport.Send(100, refusedHello.Buffer, refusedHello.Length, true);
            Step(0.5);
            Check(host.Session.PlayerCount == withStay && host.Session.Player(stayId) != null && host.Session.Player(stayId).Info.Name == "Stay",
                  $"a refused hello from a playing player's address does not drop him ({host.Session.PlayerCount} of {withStay})");
            seats.Remove(stay);
            host.Session.Kick(stayId);
            Step(1.0);

            // A client leaves: the others hear it; its number is free for the next.
            byte bens = b.Session.LocalId;
            b.Session.Leave();
            seats.Remove(b);
            Step(1.0);
            Check(host.Session.PlayerCount == 3 && a.Log.Contains("left " + bens) && c.Log.Contains("left " + bens), "a client that leaves is gone for everyone");
            Seat dora = Client(207, "Dora");
            Step(1.0);
            Check(dora.Session.LocalId == bens && a.Log.Contains($"joined {bens} Dora"), "the next to join gets the free number, and the others hear of her");
            // A client that falls silent (its game froze): dropped after the silence, the others told.
            seats.Remove(c);
            byte cleos = c.Session.LocalId;
            Step(14.0);
            Check(host.Session.Player(cleos) == null && a.Log.Contains("left " + cleos), "a client silent for 12 s is dropped and the others are told");
            // The host ends the game: the clients close with the reason.
            host.Session.Leave();
            seats.Remove(host);
            Step(1.0);
            Check(a.Session.State == NetState.Closed && a.Session.CloseReason.Contains("host") && dora.Session.State == NetState.Closed, "the host leaving closes the clients: " + a.Session.CloseReason);
            // No host there at all.
            INetTransport lonely = net.Join(300);
            Seat alone = Sit(NetSession.Join(lonely, 999, Info("Alone")), lonely);
            seats.Clear();
            seats.Add(alone);
            Step(14.0);
            Check(alone.Session.State == NetState.Connecting, "a join waits 15 s for its host");
            Step(2.0);
            Check(alone.Session.State == NetState.Closed && alone.Session.CloseReason.Contains("no answer"), "then gives up with a reason: " + alone.Session.CloseReason);
        }

        private static void VoiceCodecTests()
        {
            // A spoken-level sine (300 Hz, a quarter of full scale) and a louder chirp, frame by frame as the voice sends them:
            // each frame decoded on its own from its header, the signal kept over 20 dB above the coding noise (a telephone voice; 4-bit ADPCM gives about 25 here).
            const int frame = 320;
            var input = new short[frame];
            var packed = new byte[frame / 2];
            var output = new short[frame];
            double signal = 0, noise = 0;
            for (int f = 0; f < 50; f++)
            {
                for (int i = 0; i < frame; i++)
                {
                    double t = (f * frame + i) / 16000.0;
                    double amp = f < 25 ? 0.25 : 0.6;
                    double hz = f < 25 ? 300 : 200 + 40 * f;
                    input[i] = (short)(amp * 32767 * Math.Sin(2 * Math.PI * hz * t));
                }
                Adpcm.Encode(input, packed, out short predictor, out byte index);
                Adpcm.Decode(packed, predictor, index, output);
                for (int i = 0; i < frame; i++)
                {
                    signal += (double)input[i] * input[i];
                    double e = input[i] - output[i];
                    noise += e * e;
                }
            }
            double snr = 10 * Math.Log10(signal / Math.Max(1, noise));
            Check(snr > 20, $"the voice codec keeps a voice clear ({snr:0.0} dB over its noise)");
            // A frame lost: the next decodes from its own header all the same.
            Adpcm.Encode(input, packed, out short p2, out byte i2);
            Adpcm.Decode(packed, p2, i2, output);
            double err = 0;
            for (int i = 0; i < frame; i++)
            {
                err += Math.Abs(input[i] - output[i]);
            }
            Check(err / frame < 2000, $"a frame decodes alone, after one lost ({err / frame:0} mean error)");
            Check(packed.Length * 2 == frame, "four bits a sample");
        }

        private static void UdpTests()
        {
            using (var hostT = new UdpTransport(0))
            using (var clientT = new UdpTransport(0))
            {
                ulong hostPeer = UdpTransport.PeerOf(new IPEndPoint(IPAddress.Loopback, hostT.Port));
                Check(UdpTransport.Parse("127.0.0.1:" + hostT.Port, 1) == hostPeer && UdpTransport.Parse("127.0.0.1", hostT.Port) == hostPeer, "an address reads as its peer, with and without a port");
                Check(UdpTransport.Parse("not an address at all !", 1) == 0 && UdpTransport.Parse("", 1) == 0, "nonsense reads as no peer");
                Check(UdpTransport.EndPointOf(hostPeer).Port == hostT.Port && UdpTransport.EndPointOf(hostPeer).Address.Equals(IPAddress.Loopback), "a peer reads back as its address");
                Seat host = Sit(NetSession.Host(hostT, Info("Host")), hostT), client = Sit(NetSession.Join(clientT, hostPeer, Info("Client")), clientT);
                var clock = System.Diagnostics.Stopwatch.StartNew();
                void Pump(double seconds, Func<bool> until = null)
                {
                    for (double end = clock.Elapsed.TotalSeconds + seconds; clock.Elapsed.TotalSeconds < end && (until == null || !until());)
                    {
                        host.Session.Update(clock.Elapsed.TotalSeconds);
                        client.Session.Update(clock.Elapsed.TotalSeconds);
                        System.Threading.Thread.Sleep(1);
                    }
                }
                Pump(3, () => client.Session.State == NetState.Connected);
                Check(client.Session.State == NetState.Connected && host.Session.PlayerCount == 2, "over real UDP on this PC a client joins a host");
                var w = new NetWriter();
                host.Messages.Clear();
                for (int i = 0; i < 200; i++)
                {
                    w.Reset();
                    w.String("m" + i);
                    client.Session.Send(40, w, true);
                }
                Pump(5, () => host.Messages.Count >= 200);
                Check(host.Messages.Count == 200 && host.Messages.Select((m, i) => m.text == "m" + i).All(x => x), $"200 reliable messages arrive in order over UDP ({host.Messages.Count})");
                w.Reset();
                w.String(new string('y', 4000));
                host.Session.Send(41, w, true);
                Pump(3, () => client.Messages.Any(m => m.kind == 41));
                Check(client.Messages.Any(m => m.kind == 41 && m.text.Length == 4000 && m.text.All(ch => ch == 'y')), "a message of four pieces arrives whole");
                for (int i = 0; i < 50; i++)
                {
                    w.Reset();
                    w.String("u" + i);
                    host.Session.Send(42, w, false);
                    Pump(0.005);
                }
                Pump(0.3);
                Check(client.Messages.Count(m => m.kind == 42) >= 45, $"unreliable ones arrive too on a clean line ({client.Messages.Count(m => m.kind == 42)} of 50)");
                client.Session.Leave();
                Pump(1, () => host.Session.PlayerCount == 1);
                Check(host.Session.PlayerCount == 1, "the host hears the client leave over UDP");
            }
            // Junk from 70 addresses (a first byte that is no piece): a newcomer still gets in. Each made a reliable channel for good,
            // and from the 64th on nobody could join (transport scout, 2026-11-12).
            using (var hostT = new UdpTransport(0))
            using (var clientT = new UdpTransport(0))
            {
                ulong hostPeer = UdpTransport.PeerOf(new IPEndPoint(IPAddress.Loopback, hostT.Port));
                Seat host = Sit(NetSession.Host(hostT, Info("Host")), hostT);
                var clock = System.Diagnostics.Stopwatch.StartNew();
                var junk = new List<System.Net.Sockets.UdpClient>();
                try
                {
                    for (int i = 0; i < 70; i++)
                    {
                        var u = new System.Net.Sockets.UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
                        junk.Add(u);
                        u.Send(new byte[] { 7, 7, 7, 7, 7 }, 5, new IPEndPoint(IPAddress.Loopback, hostT.Port));
                    }
                    for (int i = 0; i < 50; i++)
                    {
                        host.Session.Update(clock.Elapsed.TotalSeconds);
                        System.Threading.Thread.Sleep(1);
                    }
                    Seat client = Sit(NetSession.Join(clientT, hostPeer, Info("Late")), clientT);
                    for (double end = clock.Elapsed.TotalSeconds + 3; clock.Elapsed.TotalSeconds < end && client.Session.State != NetState.Connected;)
                    {
                        host.Session.Update(clock.Elapsed.TotalSeconds);
                        client.Session.Update(clock.Elapsed.TotalSeconds);
                        System.Threading.Thread.Sleep(1);
                    }
                    Check(client.Session.State == NetState.Connected, $"junk from 70 addresses does not shut a newcomer out ({client.Session.State})");
                }
                finally
                {
                    foreach (var u in junk)
                    {
                        u.Close();
                    }
                }
            }
        }
    }
}
