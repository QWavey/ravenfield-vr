using System;
using System.Collections.Generic;
using System.Linq;
using RavenfieldVR.Input;
using RavenfieldVR.Setup;

namespace RavenfieldVR.Tests
{
    /// <summary>
    /// Checks of the code that runs without the game (run-tests.ps1 compiles this with the installer's sources and BindingsWriter):
    /// the installer's path containment, the Steam config reader, the left-handed bindings. The mod's own checks need Ravenfield
    /// (-rfvr-autotest, -rfvr-selftest). Audit, issue #18.
    /// </summary>
    internal static class OfflineTests
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
            InsideTests();
            VdfTests();
            BindingsTests();
            PlayEntryTests();
            LauncherTests();
            SetupUninstallTests();
            SetupKeepAndRestoreTests();
            SetupInstallTests();
            SetupForeignLoaderTests();
            SetupMovedGameTests();
            SetupBadPayloadTests();
            SetupInterruptedInstallTests();
            GamePathTests();
            SetupMissingLoaderTests();
            VersionCompareTests();
            ModTests(args);
            Console.WriteLine($"{_passed} passed, {_failed} failed");
            return _failed == 0 ? 0 : 1;
        }

        /// <summary>
        /// The mod's own pure logic, through the built RavenfieldVR.dll (args: the game's Managed folder, the DLL): only vector
        /// maths runs here, Unity's native calls (Quaternion.Inverse, LookRotation...) need the game.
        /// </summary>
        private static void ModTests(string[] args)
        {
            if (args.Length < 2 || !System.IO.File.Exists(args[1]) || !System.IO.Directory.Exists(args[0]))
            {
                Console.WriteLine("Mod checks skipped (no built RavenfieldVR.dll, or the game's Managed folder not found)");
                return;
            }
            string managed = args[0], built = System.IO.Path.GetDirectoryName(args[1]), core = args.Length > 2 ? args[2] : managed;
            AppDomain.CurrentDomain.AssemblyResolve += (sender, e) =>
            {
                string file = new System.Reflection.AssemblyName(e.Name).Name + ".dll";
                foreach (string dir in new[] { built, managed, core })
                {
                    string path = System.IO.Path.Combine(dir, file);
                    if (System.IO.File.Exists(path))
                    {
                        return System.Reflection.Assembly.LoadFrom(path);
                    }
                }
                return null;
            };
            // In methods of their own: Unity's types are resolved when they are compiled, after the handler is in place.
            System.Reflection.Assembly mod = System.Reflection.Assembly.LoadFrom(args[1]);
            SteeringTests(mod);
            DeadzoneTests(mod);
            ConfigTests(mod);
            JsonTests(mod);
            LensToneTests(mod);
            ScriptKeyRuleTests(mod);
            PoseTests(mod);
            InputParsingTests(mod);
            HandednessTests(mod);
            EventGuardTests(mod);
            PresetTests(mod);
            InstallCheckTests(mod);
            ArmNameTests(mod);
            BlowLabelTests(mod);
            BodyImpactTests(mod);
            KeyNameTests(mod);
            FrameBudgetTests(mod);
            TestHostTests(mod);
            ShellSizeTests(mod);
            PoseToOpenVrTests(mod);
            ShapeFitTests(mod);
            GloveSweepTests(mod);
            HapticPriorityTests(mod);
            VolumeRotationTests(mod);
            NoticeWrapTests(mod);
            ToolClassTests(mod);
            RackPointFileTests(mod);
        }

        /// <summary>
        /// The snapped rack points' cache file (BoltPiece.SnappedRackPoints): a point written and read back is the same to the bit,
        /// on a German Windows too (a comma for the decimal point broke such files before: the frames file of tools/flashfilm.py).
        /// </summary>
        private static void RackPointFileTests(System.Reflection.Assembly mod)
        {
            Type points = mod.GetType("RavenfieldVR.Weapons.SnappedRackPoints");
            const System.Reflection.BindingFlags any = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static;
            System.Reflection.MethodInfo format = points?.GetMethod("Format", any), parse = points?.GetMethod("Parse", any);
            Check(format != null && parse != null, "Rack points file: Format and Parse found");
            if (format == null || parse == null)
            {
                return;
            }
            System.Globalization.CultureInfo before = System.Threading.Thread.CurrentThread.CurrentCulture;
            try
            {
                System.Threading.Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
                var rng = new Random(7);
                bool same = true;
                string seen = "";
                for (int i = 0; i < 200 && same; i++)
                {
                    var v = new UnityEngine.Vector3((float)(rng.NextDouble() - 0.5) * 0.7f, (float)(rng.NextDouble() - 0.5) * 0.3f, (float)rng.NextDouble() * 1e-3f);
                    string text = (string)format.Invoke(null, new object[] { v });
                    object[] args = { text, null };
                    bool ok = (bool)parse.Invoke(null, args);
                    var back = (UnityEngine.Vector3)args[1];
                    same = ok && BitConverter.ToInt32(BitConverter.GetBytes(back.x), 0) == BitConverter.ToInt32(BitConverter.GetBytes(v.x), 0)
                           && BitConverter.ToInt32(BitConverter.GetBytes(back.y), 0) == BitConverter.ToInt32(BitConverter.GetBytes(v.y), 0)
                           && BitConverter.ToInt32(BitConverter.GetBytes(back.z), 0) == BitConverter.ToInt32(BitConverter.GetBytes(v.z), 0);
                    seen = text;
                }
                Check(same && !seen.Contains(","), $"Rack points file: 200 points read back to the bit under de-DE, written with points not commas (last '{seen}')");
                object[] bad = { "0.1 0.2", null };
                Check(!(bool)parse.Invoke(null, bad), "Rack points file: a line with two numbers is no point");
            }
            finally
            {
                System.Threading.Thread.CurrentThread.CurrentCulture = before;
            }
        }

        /// <summary>
        /// The headset notice's text is broken into short lines by hand (a TextMesh does not wrap): no line past the width unless
        /// one word is, every word kept in order, and a break of the message's own starting the count again.
        /// </summary>
        private static void NoticeWrapTests(System.Reflection.Assembly mod)
        {
            System.Reflection.MethodInfo wrap = mod.GetType("RavenfieldVR.UI.HeadNotice")?.GetMethod("Wrap",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            Check(wrap != null, "Head notice: HeadNotice.Wrap found");
            if (wrap == null)
            {
                return;
            }
            string W(string s, int width) => (string)wrap.Invoke(null, new object[] { s, width });
            const string message = "Controller input did not start: SteamVR rejected the controller bindings (InvalidParam). Restart SteamVR, then the game.";
            string wrapped = W(message, 48);
            bool short_ = true;
            foreach (string line in wrapped.Split('\n'))
            {
                short_ &= line.Length <= 48;
            }
            Check(short_ && wrapped.Contains("\n"), "Head notice: a long message is broken into lines of at most 48 characters");
            Check(wrapped.Replace('\n', ' ') == message, "Head notice: every word kept, in order, one space or break between");
            Check(W("abcdefghijklmnopqrstuvwxyz", 10) == "abcdefghijklmnopqrstuvwxyz", "Head notice: a word longer than the width stays whole");
            Check(W("one two\nthree four five", 12) == "one two\nthree four\nfive", $"Head notice: the message's own break starts the count again (got {W("one two\nthree four five", 12).Replace("\n", "|")})");
        }

        /// <summary>
        /// An infinite-magazine gun (the game's ammo -1) is classed NoAmmo with the tools but is no tool: it is aimed down, measured
        /// and its shape fitted as a gun (round 4 of 2026-10-28).
        /// </summary>
        private static void ToolClassTests(System.Reflection.Assembly mod)
        {
            Type dataType = mod.GetType("RavenfieldVR.Weapons.WeaponRigData");
            Type actionType = mod.GetType("RavenfieldVR.Weapons.ActionType");
            System.Reflection.PropertyInfo isTool = dataType?.GetProperty("IsTool");
            Check(isTool != null && actionType != null, "Tools: WeaponRigData.IsTool found");
            if (isTool == null || actionType == null)
            {
                return;
            }
            bool Tool(string action, bool infinite)
            {
                object d = Activator.CreateInstance(dataType);
                dataType.GetField("Action").SetValue(d, Enum.Parse(actionType, action));
                dataType.GetField("InfiniteAmmo").SetValue(d, infinite);
                return (bool)isTool.GetValue(d);
            }
            Check(Tool("NoAmmo", false) && Tool("Melee", false) && Tool("Throwable", false), "Tools: a tool with no ammo, a blade and a throwable are tools");
            Check(!Tool("NoAmmo", true), "Tools: an infinite-magazine gun (NoAmmo, ammo -1) is not a tool");
            Check(!Tool("SelfLoading", false) && !Tool("Manual", false), "Tools: guns with magazines are not tools");
        }

        /// <summary>
        /// GunVolume keeps each view's rotation as a matrix (GunVolume.Rot3) and turns points with it inline: it must land on the
        /// very float bits Unity's Quaternion * Vector3 gives, or the solid tests (gloves, magazines) would change by a texel at
        /// the edges. 2000 random rotations and points, every component compared bit for bit.
        /// </summary>
        private static void VolumeRotationTests(System.Reflection.Assembly mod)
        {
            const System.Reflection.BindingFlags all = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
                                                      | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Instance;
            Type rot = mod.GetType("RavenfieldVR.Weapons.GunVolume")?.GetNestedType("Rot3", all);
            System.Reflection.MethodInfo of = rot?.GetMethod("Of", all);
            Check(of != null, "Volume rotation: GunVolume.Rot3.Of found");
            if (of == null)
            {
                return;
            }
            float F(object m, string name) => (float)rot.GetField(name, all).GetValue(m);
            var rng = new Random(5);
            float R() => (float)(rng.NextDouble() * 2 - 1);
            int differ = 0;
            for (int i = 0; i < 2000; i++)
            {
                float x = R(), y = R(), z = R(), w = R(), n = (float)Math.Sqrt(x * x + y * y + z * z + w * w);
                var q = new UnityEngine.Quaternion(x / n, y / n, z / n, w / n);
                var p = new UnityEngine.Vector3(R() * 0.3f, R() * 0.3f, R() * 0.3f);
                object m = of.Invoke(null, new object[] { q });
                float px = F(m, "M00") * p.x + F(m, "M01") * p.y + F(m, "M02") * p.z;
                float py = F(m, "M10") * p.x + F(m, "M11") * p.y + F(m, "M12") * p.z;
                float pz = F(m, "M20") * p.x + F(m, "M21") * p.y + F(m, "M22") * p.z;
                UnityEngine.Vector3 u = q * p;
                if (BitConverter.ToInt32(BitConverter.GetBytes(px), 0) != BitConverter.ToInt32(BitConverter.GetBytes(u.x), 0)
                    || BitConverter.ToInt32(BitConverter.GetBytes(py), 0) != BitConverter.ToInt32(BitConverter.GetBytes(u.y), 0)
                    || BitConverter.ToInt32(BitConverter.GetBytes(pz), 0) != BitConverter.ToInt32(BitConverter.GetBytes(u.z), 0))
                {
                    differ++;
                }
            }
            Check(differ == 0, $"Volume rotation: the matrix turns points to Quaternion * Vector3's own float bits ({differ} of 2000 differ)");
        }

        /// <summary>
        /// Haptics keep the strongest pulse playing (VRInput.HapticAllowed, Boneworks' haptic stack): a 1 cm rack tick does not
        /// cut the slide's click short, a stronger pulse takes over, and once a pulse is over anything plays.
        /// </summary>
        private static void HapticPriorityTests(System.Reflection.Assembly mod)
        {
            System.Reflection.MethodInfo allowed = mod.GetType("RavenfieldVR.Input.VRInput")?.GetMethod("HapticAllowed",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            Check(allowed != null, "Haptic priority: VRInput.HapticAllowed found");
            if (allowed == null)
            {
                return;
            }
            bool Allowed(float now, float amp, float until, float running) => (bool)allowed.Invoke(null, new object[] { now, amp, until, running });
            Check(!Allowed(1.00f, 0.35f, 1.03f, 0.6f), "Haptic priority: a rack tick does not cut the slide's click short");
            Check(Allowed(1.00f, 0.9f, 1.03f, 0.6f), "Haptic priority: a stronger pulse takes over a weaker one");
            Check(Allowed(1.05f, 0.15f, 1.03f, 0.6f), "Haptic priority: once a pulse is over, a weak one plays");
            Check(Allowed(1.00f, 0.35f, 1.04f, 0.35f), "Haptic priority: rack ticks of the same strength follow each other");

            // One controller dropping out alone (VRInput.HandButton): its buttons go through the gap as the whole input's do.
            System.Reflection.MethodInfo button = mod.GetType("RavenfieldVR.Input.VRInput")?.GetMethod("HandButton",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            Check(button != null, "Hand dropout: VRInput.HandButton found");
            if (button == null)
            {
                return;
            }
            bool Button(bool alive, bool raw, bool was, float since, bool suppress, out bool pressed, out bool released)
            {
                object[] a = { alive, raw, was, since, suppress, false, false };
                bool held = (bool)button.Invoke(null, a);
                pressed = (bool)a[5];
                released = (bool)a[6];
                return held;
            }
            bool h = Button(true, true, false, 0f, false, out bool p, out bool r);
            Check(h && p && !r, "Hand dropout: a button pressed on a live hand is held and pressed");
            h = Button(true, false, true, 0f, false, out p, out r);
            Check(!h && !p && r, "Hand dropout: let go on a live hand it is released");
            h = Button(false, false, true, 0.2f, false, out p, out r);
            Check(h && !p && !r, "Hand dropout: a held grip is kept through a 0.2 s dropout (the gun is not dropped)");
            h = Button(false, false, true, 0.6f, false, out p, out r);
            Check(!h && !p && !r, "Hand dropout: after 0.6 s it counts as let go, with no release edge");
            h = Button(true, true, false, 0f, true, out p, out r);
            Check(h && !p && !r, "Hand dropout: still down when the hand is back, it is not pressed again");
            h = Button(true, true, false, 0f, false, out p, out r);
            Check(h && p, "Hand dropout: pressed anew after the hand is back, it is a press");
            h = Button(false, false, false, 0.2f, false, out p, out r);
            Check(!h && !p && !r, "Hand dropout: a button up stays up through a dropout");
        }

        /// <summary>
        /// The drawn hand's contact with the gun (GloveSweep.Shift, Boneworks' physics hand): a 3 cm ball for the glove against
        /// boxes for the gun, shifts from where the tracked hand puts it. A swipe through a thin receiver in one frame stays on the
        /// near side, a hand pushed 10 cm deep stays at the surface, a clear hand is followed exactly, a diagonal push slides up
        /// the wall, fingers closed a few millimetres into the gun are freed on their own side, and a hand far past the gun lets
        /// the glove go to it (user, 2026-10-23: hands glitch through the gun with enough force, or on a double-magazine reload).
        /// </summary>
        private static void GloveSweepTests(System.Reflection.Assembly mod)
        {
            System.Reflection.MethodInfo shift = mod.GetType("RavenfieldVR.Rig.GloveSweep")?.GetMethod("Shift",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            Check(shift != null, "Glove sweep: GloveSweep.Shift found");
            if (shift == null)
            {
                return;
            }
            const float R = 0.03f;
            var axes = new[] { new UnityEngine.Vector3(1f, 0f, 0f), new UnityEngine.Vector3(0f, 1f, 0f), new UnityEngine.Vector3(0f, 0f, 1f) };
            // The glove (a ball round the tracked hand plus the shift) against a box min..max.
            Func<UnityEngine.Vector3, bool> Ball(UnityEngine.Vector3 tracked, UnityEngine.Vector3 min, UnityEngine.Vector3 max) => s =>
            {
                UnityEngine.Vector3 c = tracked + s;
                float dx = Math.Max(min.x - c.x, Math.Max(0f, c.x - max.x));
                float dy = Math.Max(min.y - c.y, Math.Max(0f, c.y - max.y));
                float dz = Math.Max(min.z - c.z, Math.Max(0f, c.z - max.z));
                return dx * dx + dy * dy + dz * dz < R * R;
            };
            UnityEngine.Vector3? Run(Func<UnityEngine.Vector3, bool> blocked, UnityEngine.Vector3 from)
                => (UnityEngine.Vector3?)shift.Invoke(null, new object[] { blocked, from, UnityEngine.Vector3.zero, axes, 0.005f, 0.15f, 0.03f });
            var slabMin = new UnityEngine.Vector3(0.05f, -0.05f, -0.3f);
            var slabMax = new UnityEngine.Vector3(0.09f, 0.05f, 0.3f); // a 4 cm receiver

            // A swipe from 2 cm before the receiver to 6 cm past it in one frame: the tracked hand lands clear on the far side.
            var far = new UnityEngine.Vector3(0.15f, 0f, 0f);
            Func<UnityEngine.Vector3, bool> slab = Ball(far, slabMin, slabMax);
            UnityEngine.Vector3? r = Run(slab, new UnityEngine.Vector3(-0.02f, 0f, 0f) - far);
            UnityEngine.Vector3 glove = far + r.GetValueOrDefault();
            Check(r.HasValue && !slab(r.Value) && glove.x < slabMin.x - R + 0.006f,
                  $"Glove sweep: a fast swipe through a 4 cm receiver stays on the near side (glove x {glove.x * 100f:0.0} cm, wall at {(slabMin.x - R) * 100f:0.0})");

            // The same swipe on 30 cm past it: the real hand is far beyond the gun, the glove is let go to it.
            var wayPast = new UnityEngine.Vector3(0.30f, 0f, 0f);
            r = Run(Ball(wayPast, slabMin, slabMax), new UnityEngine.Vector3(-0.02f, 0f, 0f) - wayPast);
            Check(r.HasValue && r.Value.sqrMagnitude < 1e-8f, "Glove sweep: a hand 30 cm past the gun takes the glove with it (released)");

            // Pushed 10 cm deep into a 20 cm block: the glove stays at the surface, never inside.
            var blockMax = new UnityEngine.Vector3(0.25f, 0.05f, 0.3f);
            var deep = new UnityEngine.Vector3(0.15f, 0f, 0f);
            Func<UnityEngine.Vector3, bool> block = Ball(deep, slabMin, blockMax);
            r = Run(block, new UnityEngine.Vector3(-0.02f, 0f, 0f) - deep);
            glove = deep + r.GetValueOrDefault();
            Check(r.HasValue && !block(r.Value) && Math.Abs(glove.x - (slabMin.x - R)) < 0.006f,
                  $"Glove sweep: a hand pushed 10 cm into the gun keeps the glove at its surface (glove x {glove.x * 100f:0.0} cm)");

            // Nothing in the way: the glove is drawn exactly at the hand.
            r = Run(Ball(new UnityEngine.Vector3(-0.2f, 0f, 0f), slabMin, slabMax), new UnityEngine.Vector3(-0.05f, 0f, 0f));
            Check(r.HasValue && r.Value.sqrMagnitude < 1e-8f, "Glove sweep: a hand clear of the gun is followed exactly");

            // Pushed diagonally into a wall: the glove stops at it and slides up to the hand's height.
            var wallMax = new UnityEngine.Vector3(0.5f, 1f, 1f);
            var wallMin = new UnityEngine.Vector3(0.05f, -1f, -1f);
            var diag = new UnityEngine.Vector3(0.1f, 0.1f, 0f);
            Func<UnityEngine.Vector3, bool> wall = Ball(diag, wallMin, wallMax);
            r = Run(wall, new UnityEngine.Vector3(0.01f, 0f, 0f) - diag);
            glove = diag + r.GetValueOrDefault();
            Check(r.HasValue && !wall(r.Value) && Math.Abs(glove.y - 0.1f) < 0.006f && glove.x < wallMin.x - R + 0.006f,
                  $"Glove sweep: pushed diagonally into a wall the glove slides along it to the hand's height (glove {glove.x * 100f:0.0}, {glove.y * 100f:0.0} cm)");

            // Fingers closed 5 mm into the receiver: freed on their own side, not pushed through.
            var inside = new UnityEngine.Vector3(0.1f, 0f, 0f);
            Func<UnityEngine.Vector3, bool> grip = Ball(inside, slabMin, blockMax);
            r = Run(grip, new UnityEngine.Vector3(0.025f, 0f, 0f) - inside);
            glove = inside + r.GetValueOrDefault();
            Check(r.HasValue && !grip(r.Value) && glove.x < slabMin.x - R + 0.006f,
                  $"Glove sweep: fingers a few millimetres in are freed on their own side (glove x {glove.x * 100f:0.0} cm)");

            // Its last place deep in the gun (the gun swung onto the hand): no contact to keep; the caller searches instead.
            r = Run(grip, new UnityEngine.Vector3(0.15f, 0f, 0f) - inside);
            Check(!r.HasValue, "Glove sweep: a last place deep in the gun is left to the caller's search");
        }

        /// <summary>
        /// The physics pieces fitted to a model's points (ShapeFit.FitCloud: what a gun and a magazine collide and are touched
        /// with). A gun-like cloud (a receiver, a round barrel ahead of it, a raked grip under it) is covered: every point lies
        /// inside some piece (its hull tested along 98 directions, 3 mm of play), in at most 16 pieces. A flat cloud (a sight's
        /// glass), points on a line and one point repeated give no throw and no NaN.
        /// </summary>
        private static void ShapeFitTests(System.Reflection.Assembly mod)
        {
            System.Reflection.MethodInfo fit = mod.GetType("RavenfieldVR.Weapons.ShapeFit")?.GetMethod("FitCloud");
            Check(fit != null, "Shape fit: ShapeFit.FitCloud found");
            if (fit == null)
            {
                return;
            }
            List<UnityEngine.Vector3[]> Fit(List<UnityEngine.Vector3> cloud)
            {
                var pieces = (Array)fit.Invoke(null, new object[] { cloud, 0.03f, 16 });
                var hulls = new List<UnityEngine.Vector3[]>();
                foreach (object piece in pieces)
                {
                    hulls.Add((UnityEngine.Vector3[])piece.GetType().GetField("Points").GetValue(piece));
                }
                return hulls;
            }
            var rng = new Random(7);
            float R(float a, float b) => a + (float)rng.NextDouble() * (b - a);
            var gun = new List<UnityEngine.Vector3>();
            for (int i = 0; i < 1200; i++)
            {
                // The receiver's surface: 30 x 5 x 8 cm.
                float x = R(-0.025f, 0.025f), y = R(-0.04f, 0.04f), z = R(-0.15f, 0.15f);
                int face = rng.Next(3);
                if (face == 0) x = rng.Next(2) == 0 ? -0.025f : 0.025f;
                else if (face == 1) y = rng.Next(2) == 0 ? -0.04f : 0.04f;
                else z = rng.Next(2) == 0 ? -0.15f : 0.15f;
                gun.Add(new UnityEngine.Vector3(x, y, z));
            }
            for (int i = 0; i < 900; i++)
            {
                // The barrel: 1.2 cm round, 40 cm ahead of the receiver.
                double t = rng.NextDouble() * Math.PI * 2;
                gun.Add(new UnityEngine.Vector3(0.012f * (float)Math.Cos(t), 0.02f + 0.012f * (float)Math.Sin(t), R(0.15f, 0.55f)));
            }
            for (int i = 0; i < 500; i++)
            {
                // The grip: 3 x 3 x 11 cm under the receiver, raked back 20 degrees.
                float d = R(0f, 0.11f), u = R(-0.015f, 0.015f), v = R(-0.015f, 0.015f);
                gun.Add(new UnityEngine.Vector3(u, -0.04f - d * 0.94f, -0.08f - d * 0.34f + v));
            }
            List<UnityEngine.Vector3[]> pieces0 = Fit(gun);
            var timing = System.Diagnostics.Stopwatch.StartNew();
            for (int k = 0; k < 20; k++)
            {
                Fit(gun);
            }
            Console.WriteLine($"TIMING shape fit: {timing.Elapsed.TotalMilliseconds / 20:0.00} ms a fit of {gun.Count} points; hull points {pieces0.Sum(h => h.Length)}");
            // A thin part (a 1 mm strip 60 cm long, 3 cm wide): made solid it is doubled out along its thin axis, and a hull past
            // 255 points does not cook (it became a box).
            var plate = new List<UnityEngine.Vector3>();
            for (int i = 0; i < 4000; i++)
            {
                plate.Add(new UnityEngine.Vector3(R(-0.015f, 0.015f), R(-0.0005f, 0.0005f), R(-0.3f, 0.3f)));
            }
            List<UnityEngine.Vector3[]> plateHulls = Fit(plate);
            Check(plateHulls.Count > 0 && plateHulls.All(h => h.Length <= 255),
                  $"Shape fit: a thin plate's pieces stay at or under 255 hull points ({string.Join("/", plateHulls.Select(h => h.Length))})");
            var dirs = new List<UnityEngine.Vector3>();
            for (int i = 0; i < 98; i++)
            {
                // A Fibonacci sphere.
                double zz = 1 - 2 * (i + 0.5) / 98, rr = Math.Sqrt(1 - zz * zz), ph = i * Math.PI * (3 - Math.Sqrt(5));
                dirs.Add(new UnityEngine.Vector3((float)(rr * Math.Cos(ph)), (float)(rr * Math.Sin(ph)), (float)zz));
            }
            // How far a point is outside a piece's hull (0 inside), along the worst of the directions.
            float Outside(UnityEngine.Vector3 p0, UnityEngine.Vector3[] hull)
            {
                float worst = 0f;
                foreach (UnityEngine.Vector3 d in dirs)
                {
                    float most = float.MinValue;
                    foreach (UnityEngine.Vector3 q in hull)
                    {
                        most = Math.Max(most, d.x * q.x + d.y * q.y + d.z * q.z);
                    }
                    worst = Math.Max(worst, d.x * p0.x + d.y * p0.y + d.z * p0.z - most);
                }
                return worst;
            }
            var parts = new[] { "receiver", "barrel", "grip" };
            var missed = new int[3];
            var far = new float[3];
            for (int i = 0; i < gun.Count; i++)
            {
                float best = float.MaxValue;
                foreach (UnityEngine.Vector3[] h in pieces0)
                {
                    best = Math.Min(best, Outside(gun[i], h));
                }
                int part = i < 1200 ? 0 : i < 2100 ? 1 : 2;
                if (best > 0.003f)
                {
                    missed[part]++;
                    far[part] = Math.Max(far[part], best);
                }
            }
            int uncovered = missed[0] + missed[1] + missed[2];
            string where = string.Join(", ", new[] { 0, 1, 2 }.Where(k => missed[k] > 0).Select(k => $"{parts[k]} {missed[k]} (up to {far[k] * 1000f:0} mm)").ToArray());
            Check(pieces0.Count >= 1 && pieces0.Count <= 16 && uncovered == 0,
                $"Shape fit: a gun-like cloud of {gun.Count} points in {pieces0.Count} piece(s), {uncovered} point(s) outside every piece{(uncovered > 0 ? ": " + where : "")}");
            bool Clean(List<UnityEngine.Vector3> cloud, out string what)
            {
                try
                {
                    foreach (UnityEngine.Vector3[] hull in Fit(cloud))
                    {
                        foreach (UnityEngine.Vector3 q in hull)
                        {
                            if (float.IsNaN(q.x + q.y + q.z) || float.IsInfinity(q.x + q.y + q.z))
                            {
                                what = "a NaN or infinite point";
                                return false;
                            }
                        }
                    }
                    what = "clean";
                    return true;
                }
                catch (System.Reflection.TargetInvocationException e)
                {
                    what = e.InnerException?.GetType().Name + ": " + e.InnerException?.Message;
                    return false;
                }
            }
            var flat = new List<UnityEngine.Vector3>();
            var line = new List<UnityEngine.Vector3>();
            var same = new List<UnityEngine.Vector3>();
            for (int i = 0; i < 200; i++)
            {
                flat.Add(new UnityEngine.Vector3(R(-0.02f, 0.02f), R(-0.015f, 0.015f), 0f));
                line.Add(new UnityEngine.Vector3(0f, 0f, R(0f, 0.3f)));
                same.Add(new UnityEngine.Vector3(0.01f, 0.02f, 0.03f));
            }
            Check(Clean(flat, out string flatWhat), "Shape fit: a flat cloud (a sight's glass): " + flatWhat);
            Check(Clean(line, out string lineWhat), "Shape fit: points on a line: " + lineWhat);
            Check(Clean(same, out string sameWhat), "Shape fit: one point repeated: " + sameWhat);
        }

        /// <summary>
        /// A Unity pose as OpenVR's matrix (the loading screen's overlay is placed with it): the inverse of OpenVRMath.ToUnity,
        /// the Z axis mirrored. Identity and a position, a quarter turn to the right (Unity's forward onto +x: OpenVR's forward,
        /// its -z column, onto +x too), and a rotation part that stays a rotation (orthonormal, not mirrored).
        /// </summary>
        private static void PoseToOpenVrTests(System.Reflection.Assembly mod)
        {
            System.Reflection.MethodInfo from = mod.GetType("RavenfieldVR.Runtime.OpenVRMath")?.GetMethod("FromUnity");
            Check(from != null, "Pose to OpenVR: OpenVRMath.FromUnity found");
            if (from == null)
            {
                return;
            }
            float[] M(UnityEngine.Vector3 p, UnityEngine.Quaternion q)
            {
                object r = from.Invoke(null, new object[] { p, q });
                var f = new float[12];
                for (int k = 0; k < 12; k++)
                {
                    f[k] = (float)r.GetType().GetField("m" + k).GetValue(r);
                }
                return f;
            }
            bool Near(float a, float b) => Math.Abs(a - b) < 1e-5f;
            float[] m = M(new UnityEngine.Vector3(1f, 2f, 3f), UnityEngine.Quaternion.identity);
            Check(Near(m[0], 1f) && Near(m[5], 1f) && Near(m[10], 1f) && Near(m[1], 0f) && Near(m[2], 0f) && Near(m[6], 0f)
                  && Near(m[3], 1f) && Near(m[7], 2f) && Near(m[11], -3f), "Pose to OpenVR: identity at (1, 2, 3) is identity at (1, 2, -3)");
            float h = (float)Math.Sqrt(0.5);
            m = M(UnityEngine.Vector3.zero, new UnityEngine.Quaternion(0f, h, 0f, h)); // 90 degrees about up
            Check(Near(-m[2], 1f) && Near(-m[6], 0f) && Near(-m[10], 0f) && Near(m[0], 0f) && Near(m[8], 1f),
                "Pose to OpenVR: a quarter turn to the right looks along +x (forward column " + (-m[2]) + ", " + (-m[6]) + ", " + (-m[10]) + ")");
            var q = new UnityEngine.Quaternion(0.1f, 0.7f, -0.3f, 0.64f);
            float n = (float)Math.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w);
            m = M(UnityEngine.Vector3.zero, new UnityEngine.Quaternion(q.x / n, q.y / n, q.z / n, q.w / n));
            float[] c0 = { m[0], m[4], m[8] }, c1 = { m[1], m[5], m[9] }, c2 = { m[2], m[6], m[10] };
            float Dot(float[] a, float[] b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];
            float det = c0[0] * (c1[1] * c2[2] - c1[2] * c2[1]) - c1[0] * (c0[1] * c2[2] - c0[2] * c2[1]) + c2[0] * (c0[1] * c1[2] - c0[2] * c1[1]);
            Check(Near(Dot(c0, c0), 1f) && Near(Dot(c1, c1), 1f) && Near(Dot(c2, c2), 1f) && Near(Dot(c0, c1), 0f) && Near(Dot(c1, c2), 0f)
                  && Near(det, 1f), $"Pose to OpenVR: any turn stays a rotation (det {det:0.000})");
        }

        /// <summary>
        /// What counts as a shell (the shotgun's own shell copied into the hand, its loading port and chambers found from it): the
        /// reload shells of the exported shotguns (rig.json's magBoundsSize, 2026-10-19), and not a rifle magazine, a pistol
        /// magazine, an RPG's rocket or a pistol round.
        /// </summary>
        private static void ShellSizeTests(System.Reflection.Assembly mod)
        {
            var shellSized = mod.GetType("RavenfieldVR.Weapons.WeaponAnalyzer")?.GetMethod("ShellSized", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            Check(shellSized != null, "Shell sizes: WeaponAnalyzer.ShellSized found");
            if (shellSized == null)
            {
                return;
            }
            bool Shell(float x, float y, float z) => (bool)shellSized.Invoke(null, new object[] { new UnityEngine.Bounds(UnityEngine.Vector3.zero, new UnityEngine.Vector3(x, y, z)) });
            var wrong = new List<string>();
            var shells = new Dictionary<string, UnityEngine.Vector3>
            {
                { "R870", new UnityEngine.Vector3(0.022f, 0.058f, 0.022f) }, { "M500", new UnityEngine.Vector3(0.022f, 0.059f, 0.022f) },
                { "76 EAGLE", new UnityEngine.Vector3(0.023f, 0.064f, 0.027f) }, { "M1014", new UnityEngine.Vector3(0.021f, 0.070f, 0.021f) },
                { "DOUBLE-BARREL", new UnityEngine.Vector3(0.032f, 0.071f, 0.032f) }, { "M1878", new UnityEngine.Vector3(0.035f, 0.108f, 0.035f) },
                { "mw-m870", new UnityEngine.Vector3(0.023f, 0.061f, 0.023f) },
            };
            foreach (KeyValuePair<string, UnityEngine.Vector3> kv in shells)
            {
                if (!Shell(kv.Value.x, kv.Value.y, kv.Value.z))
                {
                    wrong.Add(kv.Key + "'s shell (not taken)");
                }
            }
            var not = new Dictionary<string, UnityEngine.Vector3>
            {
                { "an HK416 magazine", new UnityEngine.Vector3(0.03f, 0.09f, 0.20f) }, { "a P220 magazine", new UnityEngine.Vector3(0.02f, 0.07f, 0.12f) },
                { "an RPG-7 rocket", new UnityEngine.Vector3(0.07f, 0.79f, 0.07f) }, { "a 9 mm round", new UnityEngine.Vector3(0.01f, 0.02f, 0.01f) },
                { "a drum", new UnityEngine.Vector3(0.09f, 0.10f, 0.09f) }, { "a 7.62x51 round", new UnityEngine.Vector3(0.012f, 0.071f, 0.012f) },
                { "a .357 round", new UnityEngine.Vector3(0.010f, 0.040f, 0.010f) },
            };
            foreach (KeyValuePair<string, UnityEngine.Vector3> kv in not)
            {
                if (Shell(kv.Value.x, kv.Value.y, kv.Value.z))
                {
                    wrong.Add(kv.Key + " (taken for a shell)");
                }
            }
            Check(wrong.Count == 0, "Shell sizes: the shotguns' own shells are shells, magazines, rockets and pistol rounds are not" + (wrong.Count > 0 ? ": " + string.Join(", ", wrong.ToArray()) : ""));
        }

        /// <summary>A held thing's blow names: one per PropKind in its order, then the grenade, then the fist (indexed by the kind's number).</summary>
        private static void BlowLabelTests(System.Reflection.Assembly mod)
        {
            const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static;
            Type melee = mod.GetType("RavenfieldVR.Weapons.MeleeHandler");
            var labels = melee?.GetField("BlowLabels", flags)?.GetValue(null) as string[];
            var keys = melee?.GetField("BlowKeys", flags)?.GetValue(null) as string[];
            Type kind = mod.GetType("RavenfieldVR.Weapons.PropKind");
            Check(labels != null && keys != null && kind != null, "Blow labels: MeleeHandler.BlowLabels, BlowKeys and PropKind found");
            if (labels == null || keys == null || kind == null)
            {
                return;
            }
            var names = new List<string>();
            foreach (object k in Enum.GetValues(kind))
            {
                names.Add(k.ToString().ToLowerInvariant());
                Check((int)k == names.Count - 1, $"Blow labels: PropKind.{k} numbered by its place");
            }
            names.Add("grenade");
            bool match = labels.Length == names.Count + 1 && keys.Length == names.Count + 1;
            for (int i = 0; match && i < names.Count; i++)
            {
                match = labels[i] == "a held " + names[i] && keys[i] == "blow:" + names[i];
            }
            match &= labels[labels.Length - 1] == "a fist" && keys[keys.Length - 1] == "blow:fist";
            Check(match, "Blow labels: one label and key per PropKind in its order, then the grenade, then the fist");
        }

        /// <summary>
        /// A held or thrown soldier's impacts (BodyImpacts, after Boneworks' blunt impact damage): nothing below the threshold, then
        /// more the faster, never past the cap.
        /// </summary>
        private static void BodyImpactTests(System.Reflection.Assembly mod)
        {
            Type impacts = mod.GetType("RavenfieldVR.Weapons.BodyImpacts");
            System.Reflection.MethodInfo damage = impacts?.GetMethod("ImpactDamage", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            Check(damage != null, "Body impacts: BodyImpacts.ImpactDamage found");
            if (damage == null)
            {
                return;
            }
            float D(float speed) => (float)damage.Invoke(null, new object[] { speed });
            Check(D(0f) == 0f && D(3f) == 0f && D(4f) > 0f, "Body impacts: a body dropped or dragged (3 m/s or less) is not hurt, a slam at 4 is");
            Check(D(6f) > 0f && D(6f) < D(9f), "Body impacts: past the threshold, harder hurts more");
            Check(D(9f) - D(8f) > D(7f) - D(6f), "Body impacts: by the speed squared (Boneworks' impact energy), each m/s more hurts more");
            Check(D(20f) == D(60f) && D(60f) < 50f, $"Body impacts: one impact is capped below half a soldier's health ({D(60f):0})");
        }

        /// <summary>
        /// The fast test mode's mod filter (-rfvr-mods=): tokens parsed, a mod kept by its workshop id or a word of its title,
        /// "none" keeping no mod, no option keeping every mod.
        /// </summary>
        private static void TestHostTests(System.Reflection.Assembly mod)
        {
            Type host = mod.GetType("RavenfieldVR.Core.TestHost");
            var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static;
            System.Reflection.MethodInfo parse = host?.GetMethod("ParseMods", flags);
            System.Reflection.MethodInfo keeps = host?.GetMethod("Keeps", flags, null, new[] { typeof(string[]), typeof(string), typeof(string), typeof(string) }, null);
            Check(parse != null && keeps != null, "Fast test mode: ParseMods and Keeps found");
            if (parse == null || keeps == null)
            {
                return;
            }
            var tokens = (string[])parse.Invoke(null, new object[] { new[] { "ravenfield.exe", "-vr", "-rfvr-mods= 2900000001 , Shotguns ,," } });
            Check(tokens != null && string.Join("|", tokens) == "2900000001|shotguns", $"Fast test mode: tokens parsed ({(tokens == null ? "null" : string.Join("|", tokens))})");
            Check(parse.Invoke(null, new object[] { new[] { "ravenfield.exe", "-vr" } }) == null, "Fast test mode: no option, no filter");
            bool K(string[] t, string id, string folder, string title) => (bool)keeps.Invoke(null, new object[] { t, id, folder, title });
            Check(K(tokens, "2900000001", "2900000001", "Modern Rifles Pack") && K(tokens, "2900000002", "2900000002", "Shotguns Expansion")
                  && !K(tokens, "2900000003", "2900000003", "Vehicles Pack"), "Fast test mode: a mod kept by its workshop id or a word of its title, others not");
            Check(!K(new[] { "none" }, "2900000001", "2900000001", "Modern Rifles Pack") && K(null, "1", "1", "Any"),
                  "Fast test mode: none keeps no mod, no filter keeps every mod");
        }

        /// <summary>
        /// The frame-rate log's percentiles from its 0.25 ms buckets: the upper edge of the bucket the share falls in.
        /// </summary>
        private static void FrameBudgetTests(System.Reflection.Assembly mod)
        {
            Type budget = mod.GetType("RavenfieldVR.Core.FrameBudget");
            var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static;
            var buckets = budget?.GetField("Buckets", flags)?.GetValue(null) as int[];
            System.Reflection.FieldInfo frames = budget?.GetField("_frames", flags);
            System.Reflection.MethodInfo percentile = budget?.GetMethod("Percentile", flags);
            Check(buckets != null && frames != null && percentile != null, "Frame budget: buckets, count and percentile found");
            if (buckets == null || frames == null || percentile == null)
            {
                return;
            }
            Array.Clear(buckets, 0, buckets.Length);
            buckets[40] = 50; // 10.00-10.25 ms
            buckets[44] = 45; // 11.00-11.25 ms
            buckets[100] = 5; // 25.00-25.25 ms
            frames.SetValue(null, 100);
            // (The buckets and count are passed in: the GPU times have a set of their own.)
            float P(float share) => (float)percentile.Invoke(null, new object[] { buckets, 100, share });
            string got = string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0:0.00} {1:0.00} {2:0.00}", P(0.5f), P(0.95f), P(0.99f));
            Check(got == "10.25 11.25 25.25", $"Frame budget: median, 95th and 99th from the buckets ({got})");
            Array.Clear(buckets, 0, buckets.Length);
            frames.SetValue(null, 0);
        }

        /// <summary>
        /// The names a script's key polls are matched by (Input.GetKey(string)'s): letters, digits, function keys, keypad, mouse,
        /// named keys, and a code past the cached range still named.
        /// </summary>
        private static void KeyNameTests(System.Reflection.Assembly mod)
        {
            System.Reflection.MethodInfo name = null;
            Type keys = mod.GetType("RavenfieldVR.Input.ScriptKeys");
            foreach (System.Reflection.MethodInfo m in keys != null ? keys.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static) : new System.Reflection.MethodInfo[0])
            {
                if (m.Name == "Name" && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType.IsEnum)
                {
                    name = m;
                }
            }
            Check(name != null, "Key names: ScriptKeys.Name(KeyCode) found");
            if (name == null)
            {
                return;
            }
            Type keyCode = name.GetParameters()[0].ParameterType;
            string N(int code) => (string)name.Invoke(null, new[] { Enum.ToObject(keyCode, code) });
            // KeyCode: A 97, Alpha3 51, F5 286, Keypad7 263, Mouse1 324, LeftShift 304.
            string got = $"{N(97)}|{N(51)}|{N(286)}|{N(263)}|{N(324)}|{N(304)}|{N(97)}";
            Check(got == "a|3|f5|[7]|mouse 1|left shift|a", $"Key names: as Input.GetKey(string) names them, cached or not ({got})");
            Check(!string.IsNullOrEmpty(N(700)), "Key names: a code past the cache still gets a name");
        }

        /// <summary>
        /// The grenade hand's and the dud copy's arm-bone test: an arm's bones are arms, a gun's or grenade's handles, handguards
        /// and arming levers are not (the names from the exported weapons; a stick grenade's "Handle" bone lost its mesh).
        /// </summary>
        private static void ArmNameTests(System.Reflection.Assembly mod)
        {
            var arm = mod.GetType("RavenfieldVR.Weapons.ThrowableHandler")?.GetField("ArmName", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
                ?.GetValue(null) as System.Text.RegularExpressions.Regex;
            Check(arm != null, "Arm names: ThrowableHandler.ArmName found");
            if (arm == null)
            {
                return;
            }
            var wrong = new List<string>();
            foreach (string name in new[] { "Hand.R", "hand_l", "LeftHand", "RightForeArm", "UpperArm_L", "arm_R", "Arm", "Finger_01", "Thumb.L", "Wrist", "Palm", "Elbow", "Shoulder.R", "Clavicle" })
            {
                if (!arm.IsMatch(name))
                {
                    wrong.Add(name + " (an arm, not taken)");
                }
            }
            foreach (string name in new[] { "Charging Handle", "HandGuard", "handguard_ar15_kac_ris_LOD0_0", "Bolt Handle", "Handle", "QJB201_CatchHandle", "Handrail",
                                            "Arming_Lever", "Armed", "Armour", "Armor", "Armature", "Spoon", "Pin", "Grenade_Body" })
            {
                if (arm.IsMatch(name))
                {
                    wrong.Add(name + " (not an arm, taken)");
                }
            }
            Check(wrong.Count == 0, "Arm names: arm bones taken, handles, handguards and arming levers not" + (wrong.Count > 0 ? ": " + string.Join(", ", wrong) : ""));
        }

        /// <summary>
        /// The graphics presets: each one at least the one below it (resolution, shadows, detail, view distance; a finer terrain
        /// error), and the launcher's own copy of their render scales the same as the mod's (picking a preset in the launcher and
        /// in the headset must give the same resolution).
        /// </summary>
        private static void PresetTests(System.Reflection.Assembly mod)
        {
            Type presets = mod.GetType("RavenfieldVR.Core.GraphicsPresets"), presetEnum = mod.GetType("RavenfieldVR.Core.GraphicsPreset");
            var profiles = presets?.GetField("Profiles", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)?.GetValue(null) as System.Collections.IDictionary;
            Check(profiles != null && presetEnum != null, "Presets: GraphicsPresets.Profiles found");
            if (profiles == null || presetEnum == null)
            {
                return;
            }
            string[] order = { "Low", "Medium", "High", "Ultra" };
            float Field(string preset, string field)
            {
                object p = profiles[Enum.Parse(presetEnum, preset)];
                object v = p.GetType().GetField(field).GetValue(p);
                return Convert.ToSingle(v is Enum ? (object)(int)v : v);
            }
            var wrong = new List<string>();
            string[] rising = { "RenderScale", "Msaa", "ShadowDistance", "Cascades", "ShadowResolution", "LodBias", "PixelLights", "DetailDensity", "DetailDistance", "TreeDistance", "FarClip" };
            for (int i = 1; i < order.Length; i++)
            {
                foreach (string f in rising)
                {
                    if (Field(order[i], f) < Field(order[i - 1], f))
                    {
                        wrong.Add($"{order[i]}.{f} {Field(order[i], f)} under {order[i - 1]}'s {Field(order[i - 1], f)}");
                    }
                }
                if (Field(order[i], "MinPixelError") > Field(order[i - 1], "MinPixelError") || Field(order[i], "TextureLimit") > Field(order[i - 1], "TextureLimit"))
                {
                    wrong.Add($"{order[i]}'s terrain error or texture limit coarser than {order[i - 1]}'s");
                }
            }
            Check(wrong.Count == 0, "Presets: each one at least the one below it" + (wrong.Count > 0 ? ": " + string.Join("; ", wrong) : ""));
            var launcher = typeof(RavenfieldVR.Launcher.PluginConfig).GetField("PresetRenderScale", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
                ?.GetValue(null) as Dictionary<string, string>;
            var differ = new List<string>();
            foreach (string preset in order)
            {
                string scale = launcher != null && launcher.TryGetValue(preset, out string s) ? s : "none";
                if (scale == "none" || Math.Abs(float.Parse(scale, System.Globalization.CultureInfo.InvariantCulture) - Field(preset, "RenderScale")) > 1e-4f)
                {
                    differ.Add($"{preset}: launcher {scale}, mod {Field(preset, "RenderScale")}");
                }
            }
            Check(launcher != null && differ.Count == 0, "Presets: the launcher's render scale for each preset is the mod's" + (differ.Count > 0 ? ": " + string.Join("; ", differ) : ""));
        }

        /// <summary>
        /// The runtime self-repair (plugins-only installs): with nothing to copy from it says what is missing instead of asking
        /// for a restart that would change nothing; with the release's copies it puts them in place; once there it is quiet.
        /// </summary>
        private static void InstallCheckTests(System.Reflection.Assembly mod)
        {
            Type check = mod.GetType("RavenfieldVR.Core.InstallCheck"), plugin = mod.GetType("RavenfieldVR.Core.VRPlugin");
            System.Reflection.MethodInfo run = check?.GetMethod("Run");
            System.Reflection.PropertyInfo dir = plugin?.GetProperty("PluginDir");
            Check(run != null && dir != null, "Install check: InstallCheck.Run and VRPlugin.PluginDir found");
            if (run == null || dir == null)
            {
                return;
            }
            string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "rfvr-installcheck-" + Guid.NewGuid().ToString("N"));
            string game = System.IO.Path.Combine(root, "game"), pluginDir = System.IO.Path.Combine(root, "plugin");
            string runtime = System.IO.Path.Combine(pluginDir, "runtime");
            object before = dir.GetValue(null);
            try
            {
                System.IO.Directory.CreateDirectory(game);
                System.IO.Directory.CreateDirectory(runtime);
                dir.SetValue(null, pluginDir);
                string empty = (string)run.Invoke(null, new object[] { game });
                Check(empty != null && empty.Contains("is missing") && !empty.Contains("restart"),
                    $"Install check: an empty runtime folder says what is missing, not restart ({empty})");
                // The release's copies, from the last packaged build when there is one.
                string shipped = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(mod.Location), "..", "dist", "RavenfieldVR", "BepInEx", "plugins", "RavenfieldVR", "runtime");
                if (System.IO.Directory.Exists(shipped))
                {
                    foreach (string f in new[] { "XRSDKOpenVR.dll.bin", "openvr_api.dll.bin", "UnitySubsystemsManifest.json" })
                    {
                        System.IO.File.Copy(System.IO.Path.Combine(shipped, f), System.IO.Path.Combine(runtime, f), true);
                    }
                    string installed = (string)run.Invoke(null, new object[] { game });
                    string again = (string)run.Invoke(null, new object[] { game });
                    Check(installed != null && installed.StartsWith("Installed") && again == null,
                        $"Install check: the release's copies are put in place, then nothing to do ({installed} / {again ?? "null"})");
                }
            }
            finally
            {
                dir.SetValue(null, before);
                try
                {
                    System.IO.Directory.Delete(root, true);
                }
                catch (System.IO.IOException)
                {
                }
            }
        }

        /// <summary>
        /// A dashboard handler that throws is logged, not thrown out of the event pump (which skipped the mod's whole frame), and
        /// the dashboard state still follows SteamVR.
        /// </summary>
        private static void EventGuardTests(System.Reflection.Assembly mod)
        {
            const System.Reflection.BindingFlags any = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static;
            Type rt = mod.GetType("RavenfieldVR.Runtime.OpenVRRuntime"), log = mod.GetType("RavenfieldVR.Core.Log");
            System.Reflection.MethodInfo set = rt?.GetMethod("SetDashboard", any);
            System.Reflection.EventInfo changed = rt?.GetEvent("DashboardChanged"), logged = log?.GetEvent("ExceptionLogged");
            Check(set != null && changed != null && logged != null, "Event guard: OpenVRRuntime.SetDashboard and its event found");
            if (set == null || changed == null || logged == null)
            {
                return;
            }
            int logs = 0;
            Action<bool> thrower = open => throw new InvalidOperationException("handler");
            Action<string, Exception> counter = (context, e) => logs++;
            changed.AddEventHandler(null, thrower);
            logged.AddEventHandler(null, counter);
            bool threw = false;
            try
            {
                set.Invoke(null, new object[] { true });
            }
            catch (System.Reflection.TargetInvocationException)
            {
                threw = true;
            }
            bool open = (bool)rt.GetProperty("DashboardOpen").GetValue(null);
            changed.RemoveEventHandler(null, thrower);
            set.Invoke(null, new object[] { false });
            logged.RemoveEventHandler(null, counter);
            Check(!threw && logs == 1 && open, $"Event guard: a throwing dashboard handler is logged, not thrown (threw {threw}, logged {logs}, open {open})");
        }

        /// <summary>
        /// SteamVR's right-handed space into Unity's left-handed one: positions flip z, angular velocities (axial vectors) flip
        /// x and y. They must agree with each other: a point p of a hand spinning at w moves at w x p, and after the conversion the
        /// same must hold with Unity's cross product. Wrong, a thrown magazine or grenade spins the wrong way.
        /// </summary>
        private static void HandednessTests(System.Reflection.Assembly mod)
        {
            Type math = mod.GetType("RavenfieldVR.Runtime.OpenVRMath"), vec = mod.GetType("Valve.VR.HmdVector3_t");
            System.Reflection.MethodInfo toVec = math?.GetMethod("ToUnityVector"), toAng = math?.GetMethod("ToUnityAngular");
            Check(toVec != null && toAng != null && vec != null, "Handedness: OpenVRMath's vector conversions found");
            if (toVec == null || toAng == null || vec == null)
            {
                return;
            }
            object V(float a, float b, float c)
            {
                object v = Activator.CreateInstance(vec);
                vec.GetField("v0").SetValue(v, a);
                vec.GetField("v1").SetValue(v, b);
                vec.GetField("v2").SetValue(v, c);
                return v;
            }
            UnityEngine.Vector3 Conv(System.Reflection.MethodInfo m, object v) => (UnityEngine.Vector3)m.Invoke(null, new[] { v });
            var p0 = Conv(toVec, V(1f, 2f, 3f));
            Check(p0 == new UnityEngine.Vector3(1f, 2f, -3f), $"Handedness: a position flips z ({p0})");
            var rng = new Random(11);
            float R() => (float)(rng.NextDouble() * 4 - 2);
            double worst = 0;
            for (int n = 0; n < 1000; n++)
            {
                float wx = R(), wy = R(), wz = R(), px = R(), py = R(), pz = R();
                // w x p in SteamVR's space (the same component formula as Unity's Cross).
                float vx = wy * pz - wz * py, vy = wz * px - wx * pz, vz = wx * py - wy * px;
                UnityEngine.Vector3 expected = Conv(toVec, V(vx, vy, vz));
                UnityEngine.Vector3 got = UnityEngine.Vector3.Cross(Conv(toAng, V(wx, wy, wz)), Conv(toVec, V(px, py, pz)));
                worst = Math.Max(worst, (expected - got).magnitude);
            }
            Check(worst < 1e-4, $"Handedness: a spinning hand's point moves the same way in both spaces (1000 cases, worst {worst:0.0e0})");
        }

        /// <summary>
        /// The two parsers that guard the game from what a player types: the VR keyboard's field filter (an int too big for an
        /// int threw in the game's int.Parse, task 213) and the config's vectors (a NaN offset hid the hand and the gun).
        /// </summary>
        private static void InputParsingTests(System.Reflection.Assembly mod)
        {
            const System.Reflection.BindingFlags any = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static;
            System.Reflection.MethodInfo filter = mod.GetType("RavenfieldVR.UI.VRKeyboard")?.GetMethod("Filter", any),
                parse = mod.GetType("RavenfieldVR.Core.VRConfig")?.GetMethod("TryParseVector", any),
                format = mod.GetType("RavenfieldVR.Core.VRConfig")?.GetMethod("FormatVector", any);
            Check(filter != null && parse != null && format != null, "Parsing: VRKeyboard.Filter and VRConfig's vectors found");
            if (filter == null || parse == null || format == null)
            {
                return;
            }
            string F(string text, bool integer, bool dec, bool alnum, int limit, bool signed = true) =>
                (string)filter.Invoke(null, new object[] { text, integer, dec, alnum, limit, signed });
            var cases = new (string got, string want, string what)[]
            {
                (F(" 42 ", true, false, false, 0), "42", "an int, trimmed"),
                (F("4a2", true, false, false, 0), "42", "an int keeps only digits"),
                (F("--5", true, false, false, 0), "-5", "one leading minus"),
                (F("-5", true, false, false, 0, false), "5", "no minus on a digits-only field"),
                (F("99999999999", true, false, false, 0), null, "an int too big for an int is refused"),
                (F("-", true, false, false, 0), null, "a lone minus is no number"),
                (F("1,5", false, true, false, 0), "1.5", "a decimal comma becomes a point"),
                (F("1.2.3", false, true, false, 0), "1.23", "only the first point counts"),
                (F("-.", false, true, false, 0), null, "a minus and a point are no number"),
                (F("12345", true, false, false, 3), "123", "cut to the field's limit"),
                (F("A b-1", false, false, true, 0), "Ab1", "alphanumeric keeps letters and digits"),
                (F(null, true, false, false, 0), null, "nothing typed"),
            };
            var wrong = new List<string>();
            foreach (var (got, want, what) in cases)
            {
                if (got != want)
                {
                    wrong.Add($"{what}: {(got == null ? "null" : Show(got))}, not {(want == null ? "null" : Show(want))}");
                }
            }
            Check(wrong.Count == 0, $"Keyboard filter: {cases.Length} cases" + (wrong.Count > 0 ? ": " + string.Join("; ", wrong) : ""));
            bool P(string text, out UnityEngine.Vector3 v)
            {
                object[] a = { text, null };
                bool ok = (bool)parse.Invoke(null, a);
                v = (UnityEngine.Vector3)a[1];
                return ok;
            }
            bool round = P((string)format.Invoke(null, new object[] { new UnityEngine.Vector3(-0.0125f, 1.5f, 3f) }), out UnityEngine.Vector3 back)
                         && Math.Abs(back.x + 0.013f) < 0.0006f && back.y == 1.5f && back.z == 3f;
            Check(round, "Config vectors: written and read back (to 3 decimals, invariant culture)");
            Check(P("1;2 3", out UnityEngine.Vector3 sep) && sep.z == 3f && !P("1, 2", out _) && !P("NaN, 1, 2", out _) && !P("1e40, 0, 0", out _) && !P("", out _),
                  "Config vectors: ',' ';' or ' ' between them; two numbers, NaN, an overflow or nothing refused");
        }

        /// <summary>
        /// RigidPose's inverse operations (the hands, the keep-out, the guns' frames): a point taken into a pose and out again comes
        /// back, a pose times its inverse is no move. They ran Quaternion.Inverse, a native call, and could not run here at all.
        /// </summary>
        private static void PoseTests(System.Reflection.Assembly mod)
        {
            Type pose = mod.GetType("RavenfieldVR.Runtime.RigidPose");
            Check(pose != null, "Poses: RigidPose found");
            if (pose == null)
            {
                return;
            }
            System.Reflection.MethodInfo into = pose.GetMethod("InverseTransformPoint"), outOf = pose.GetMethod("TransformPoint"),
                inverse = pose.GetMethod("Inverse"), multiply = pose.GetMethod("Multiply"), dirIn = pose.GetMethod("InverseTransformDirection");
            var rng = new Random(3);
            float R() => (float)(rng.NextDouble() * 2 - 1);
            double worstPoint = 0, worstIdentity = 0, worstDir = 0;
            try
            {
                for (int n = 0; n < 2000; n++)
                {
                    var q = new UnityEngine.Quaternion(R(), R(), R(), R());
                    float len = (float)Math.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w);
                    q = new UnityEngine.Quaternion(q.x / len, q.y / len, q.z / len, q.w / len);
                    object p = Activator.CreateInstance(pose, new UnityEngine.Vector3(R() * 50f, R() * 50f, R() * 50f), q);
                    var v = new UnityEngine.Vector3(R() * 3f, R() * 3f, R() * 3f);
                    var back = (UnityEngine.Vector3)outOf.Invoke(p, new object[] { into.Invoke(p, new object[] { v }) });
                    worstPoint = Math.Max(worstPoint, (back - v).magnitude);
                    object id = multiply.Invoke(p, new[] { inverse.Invoke(p, null) });
                    var idPos = (UnityEngine.Vector3)pose.GetField("position").GetValue(id);
                    var idRot = (UnityEngine.Quaternion)pose.GetField("rotation").GetValue(id);
                    worstIdentity = Math.Max(worstIdentity, idPos.magnitude + Math.Abs(Math.Abs(idRot.w) - 1));
                    var d = (UnityEngine.Vector3)dirIn.Invoke(p, new object[] { q * v });
                    worstDir = Math.Max(worstDir, (d - v).magnitude);
                }
                Check(worstPoint < 1e-4 && worstDir < 1e-4, $"Poses: a point (and a direction) into a pose and out again comes back ({worstPoint:0.0e0}, {worstDir:0.0e0})");
                Check(worstIdentity < 1e-4, $"Poses: a pose times its inverse is no move ({worstIdentity:0.0e0})");
                System.Reflection.MethodInfo contains = mod.GetType("RavenfieldVR.Runtime.BoundsMath")?.GetMethod("Contains");
                if (contains == null)
                {
                    Check(false, "Bounds: BoundsMath.Contains found");
                    return;
                }
                var box = new UnityEngine.Bounds(new UnityEngine.Vector3(1f, -2f, 0.5f), new UnityEngine.Vector3(0.4f, 1f, 0.2f));
                bool In(float x, float y, float z) => (bool)contains.Invoke(null, new object[] { box, new UnityEngine.Vector3(x, y, z) });
                Check(In(1f, -2f, 0.5f) && In(1.2f, -1.5f, 0.6f) && In(0.8f, -2.5f, 0.4f) && !In(1.2001f, -2f, 0.5f) && !In(1f, -2.5001f, 0.5f)
                      && !In(1f, -2f, 0.3999f), "Bounds: inside and on the faces counts, a hair outside does not (as Unity's)");
            }
            catch (System.Reflection.TargetInvocationException e)
            {
                Check(false, "Poses: threw " + e.InnerException?.Message);
            }
        }

        /// <summary>
        /// The purposes the weapons' own script keys get from their behaviour names, on names from the workshop scan
        /// (tools/export_checks/script_keys_scan.py): what A, the hand and the support trigger do on those guns follows them.
        /// </summary>
        private static void ScriptKeyRuleTests(System.Reflection.Assembly mod)
        {
            System.Reflection.MethodInfo first = mod.GetType("RavenfieldVR.Input.ScriptKeys")?.GetMethod("FirstRule",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            Check(first != null, "Script keys: FirstRule found");
            if (first == null)
            {
                return;
            }
            string Purpose(string name)
            {
                object rule = first.Invoke(null, new object[] { name });
                return rule == null ? "none" : rule.GetType().GetField("Purpose").GetValue(rule).ToString();
            }
            var expected = new[]
            {
                ("fireMode", "FireMode"), ("FRfiremode", "FireMode"), ("KLVWeaponModeChange", "FireMode"), ("KLVLaser", "Laser"),
                ("KLVFlash", "Light"), ("LaserSight", "Laser"), ("LaserScope", "Magnifier"), ("KLVRereBipod", "Bipod"),
                ("NefWeaponInspect", "Inspect"), ("TacticalSprint", "Sprint"), ("BFV_KillFeed", "Hud"),
                ("MuzzleFlashSuppressor", "Suppressor"), ("FlashHider", "none"), ("ReWeaponSystem", "none"),
            };
            var wrong = new List<string>();
            foreach (var (name, want) in expected)
            {
                string got = Purpose(name);
                if (got != want)
                {
                    wrong.Add($"{name}: {got}, not {want}");
                }
            }
            Check(wrong.Count == 0, $"Script keys: {expected.Length} behaviour names from the workshop take their purpose" + (wrong.Count > 0 ? ": " + string.Join("; ", wrong) : ""));
        }

        /// <summary>
        /// The on-screen effects' tone curves and their exp table (LensEffects, computed on a worker thread): a NaN strength
        /// passed both guards and indexed the table at int.MinValue, which threw.
        /// </summary>
        private static void LensToneTests(System.Reflection.Assembly mod)
        {
            Type lens = mod.GetType("RavenfieldVR.Rig.LensEffects");
            const System.Reflection.BindingFlags any = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static;
            System.Reflection.MethodInfo flash = lens?.GetMethod("ToneFlash", any), se = lens?.GetMethod("ToneSe", any), exp = lens?.GetMethod("ExpNeg", any),
                tables = lens?.GetMethod("MakeTables", any);
            Check(flash != null && se != null && exp != null && tables != null, "Lens tones: LensEffects' curves found");
            if (flash == null || se == null || exp == null || tables == null)
            {
                return;
            }
            float Call(System.Reflection.MethodInfo m, float v)
            {
                try
                {
                    return (float)m.Invoke(null, new object[] { v });
                }
                catch (System.Reflection.TargetInvocationException e)
                {
                    Check(false, $"Lens tones: {m.Name}({v}) threw {e.InnerException?.GetType().Name}");
                    return float.NaN;
                }
            }
            Check(Call(flash, float.NaN) == 0f && Call(se, float.NaN) == 0f, "Lens tones: a NaN strength shows nothing");
            tables.Invoke(null, null);
            double worst = 0;
            for (float q = 0f; q < 26f; q += 0.0137f)
            {
                worst = Math.Max(worst, Math.Abs(Call(exp, q) - Math.Exp(-q)));
            }
            Check(worst < 2e-5, $"Lens tones: the exp table within 2e-5 of exp(-q) over 0-26 (worst {worst:0.0e0})");
            Check(Call(flash, 100f) <= 0.94f && Call(flash, 100f) > 0.93f && Call(se, 0.001f) > 0f && Call(se, 0.001f) < 0.01f,
                  "Lens tones: the flash rolls off to 94%, a faint effect stays faint");
        }

        /// <summary>
        /// The mod's JSON reader on what the sandbox (Python's json module) writes: a non-finite number comes out as NaN or
        /// Infinity there, and one such value failed the whole pose file, so every hand pose of that gun was dropped.
        /// </summary>
        private static void JsonTests(System.Reflection.Assembly mod)
        {
            Type json = mod.GetType("RavenfieldVR.Core.MiniJson");
            const System.Reflection.BindingFlags any = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static;
            System.Reflection.MethodInfo parse = json?.GetMethod("Parse", any), floats = json?.GetMethod("Floats", any), num = json?.GetMethod("Num", any), str = json?.GetMethod("Str", any);
            Check(parse != null && floats != null && num != null && str != null, "JSON: MiniJson found");
            if (parse == null || floats == null || num == null || str == null)
            {
                return;
            }
            object doc = null;
            try
            {
                doc = parse.Invoke(null, new object[] { "{\"hand\": {\"position\": [0.1, -2.5e-1, 3], \"rotation\": [0, 0, NaN, 1], \"curl\": Infinity, \"spread\": -Infinity,"
                                                       + " \"huge\": 1e39, \"far\": [0, 1e39, 0], \"name\": \"M4A1 \\u00e4\\\"x\\\"\"}}" });
            }
            catch (System.Reflection.TargetInvocationException e)
            {
                Check(false, "JSON: Python's NaN / Infinity / -Infinity read (threw " + e.InnerException?.Message + ")");
            }
            if (doc == null)
            {
                return;
            }
            object hand = ((System.Collections.Generic.Dictionary<string, object>)doc)["hand"];
            var pos = (float[])floats.Invoke(null, new object[] { hand, "position", 3 });
            Check(pos != null && Math.Abs(pos[1] + 0.25f) < 1e-6f && pos[2] == 3f, "JSON: numbers with exponents and signs");
            Check(floats.Invoke(null, new object[] { hand, "rotation", 4 }) == null, "JSON: an array with a NaN in it counts as missing (that field only)");
            Check((float)num.Invoke(null, new object[] { hand, "curl", 0.5f }) == 0.5f && (float)num.Invoke(null, new object[] { hand, "spread", 0.25f }) == 0.25f,
                  "JSON: a non-finite number takes the fallback");
            Check((float)num.Invoke(null, new object[] { hand, "huge", 0.5f }) == 0.5f && floats.Invoke(null, new object[] { hand, "far", 3 }) == null,
                  "JSON: a number past a float's range (finite as a double, Infinity as a float) counts as missing");
            Check((string)str.Invoke(null, new object[] { hand, "name" }) == "M4A1 ä\"x\"", "JSON: \\u and quote escapes");
            // Nesting: a deep file is refused with a format error (a stack overflow closes the game, no catch stops it); real
            // nesting still reads.
            bool deepRefused;
            try
            {
                parse.Invoke(null, new object[] { new string('[', 100000) + new string(']', 100000) });
                deepRefused = false;
            }
            catch (System.Reflection.TargetInvocationException e)
            {
                deepRefused = e.InnerException is FormatException;
            }
            object nested = parse.Invoke(null, new object[] { new string('[', 200) + "1" + new string(']', 200) });
            Check(deepRefused && nested != null, "JSON: 100000 levels of nesting refused as a format error, 200 read");
        }

        /// <summary>
        /// Every setting bound into a fresh file: its default is one its own range accepts, and the range is not empty (a default
        /// out of range is clamped silently on the first load, so the documented default was never the one played).
        /// </summary>
        private static void ConfigTests(System.Reflection.Assembly mod)
        {
            System.Reflection.MethodInfo bind = mod.GetType("RavenfieldVR.Core.VRConfig")?.GetMethod("Bind",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            Check(bind != null, "Settings: VRConfig.Bind found");
            if (bind == null)
            {
                return;
            }
            string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "rfvr-config-check-" + Guid.NewGuid().ToString("N") + ".cfg");
            try
            {
                var cfg = new BepInEx.Configuration.ConfigFile(path, true);
                bind.Invoke(null, new object[] { cfg });
                int count = 0, ranged = 0;
                var bad = new List<string>();
                foreach (KeyValuePair<BepInEx.Configuration.ConfigDefinition, BepInEx.Configuration.ConfigEntryBase> kv in cfg)
                {
                    count++;
                    BepInEx.Configuration.ConfigEntryBase entry = kv.Value;
                    BepInEx.Configuration.AcceptableValueBase accept = entry.Description?.AcceptableValues;
                    // Every number has a range: a hand-typed NaN or negative in an unranged one went straight into the game
                    // (WeaponReturnSeconds: NaN never returned a dropped weapon). The internal revision counter is the exception.
                    if (accept == null && (entry.SettingType == typeof(float) || entry.SettingType == typeof(int)) && kv.Key.Key != "ConfigRevision")
                    {
                        bad.Add($"{kv.Key.Section}.{kv.Key.Key} is a number without a range");
                    }
                    if (accept == null)
                    {
                        continue;
                    }
                    ranged++;
                    if (!accept.IsValid(entry.DefaultValue))
                    {
                        bad.Add($"{kv.Key.Section}.{kv.Key.Key} default {entry.DefaultValue} not in {accept.ToDescriptionString()}");
                    }
                    Type t = accept.GetType();
                    if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(BepInEx.Configuration.AcceptableValueRange<>)
                        && t.GetProperty("MinValue").GetValue(accept) is IComparable min && min.CompareTo(t.GetProperty("MaxValue").GetValue(accept)) >= 0)
                    {
                        bad.Add($"{kv.Key.Section}.{kv.Key.Key} has an empty range");
                    }
                }
                Check(count > 50 && bad.Count == 0, $"Settings: {count} bound, {ranged} with a range, every default inside it"
                      + (bad.Count > 0 ? ": " + string.Join("; ", bad) : ""));
            }
            catch (System.Reflection.TargetInvocationException e)
            {
                Check(false, "Settings: binding threw " + e.InnerException);
            }
            finally
            {
                System.IO.File.Delete(path);
            }
        }

        private static void DeadzoneTests(System.Reflection.Assembly mod)
        {
            Type input = mod.GetType("RavenfieldVR.Input.VRGameInput");
            System.Reflection.MethodInfo round = input?.GetMethod("ApplyDeadzone"), square = input?.GetMethod("ApplyDeadzoneSquare");
            Check(round != null && square != null, "Dead zones: ApplyDeadzone and ApplyDeadzoneSquare found");
            if (round == null || square == null)
            {
                return;
            }
            UnityEngine.Vector2 Round(float x, float y, float dz) => (UnityEngine.Vector2)round.Invoke(null, new object[] { new UnityEngine.Vector2(x, y), dz });
            UnityEngine.Vector2 Square(float x, float y, float dz) => (UnityEngine.Vector2)square.Invoke(null, new object[] { new UnityEngine.Vector2(x, y), dz });
            bool Near(UnityEngine.Vector2 v, float x, float y) => Math.Abs(v.x - x) < 0.002f && Math.Abs(v.y - y) < 0.002f;
            // A thumbstick (a round gate): nothing in the zone, full at the edge, starting from nothing after the zone.
            Check(Near(Round(0.05f, 0f, 0.1f), 0f, 0f) && Near(Round(1f, 0f, 0.1f), 1f, 0f) && Near(Round(0.55f, 0f, 0.1f), 0.5f, 0f)
                  && Round(0.101f, 0f, 0.1f).x < 0.01f, "Dead zone (thumbstick): none in the zone, full at the edge, continuous after it");
            UnityEngine.Vector2 diag = Round(0.7071f, 0.7071f, 0.1f);
            Check(Math.Abs(diag.magnitude - 1f) < 0.002f, $"Dead zone (thumbstick): a full diagonal stays on the circle ({diag.x:0.000}, {diag.y:0.000})");
            // The flight stick (a square gate, each axis its own throw): a corner is full roll and full pitch.
            Check(Near(Square(1f, 1f, 0.075f), 1f, 1f), $"Dead zone (flight stick): a full corner is full on both axes ({Square(1f, 1f, 0.075f).x:0.000})");
            Check(Near(Square(-1f, 0f, 0.075f), -1f, 0f) && Near(Square(0.05f, 0.05f, 0.075f), 0f, 0f) && Square(0.076f, 0f, 0.075f).x < 0.01f,
                  "Dead zone (flight stick): full on one axis, none in the zone, continuous after it");
            UnityEngine.Vector2 half = Square(0.5f, 0.5f, 0.075f);
            Check(half.x > 0.4f && half.x < 0.75f && Math.Abs(half.x - half.y) < 1e-4f, $"Dead zone (flight stick): half a corner is in between ({half.x:0.000})");
        }

        private static void SteeringTests(System.Reflection.Assembly mod)
        {
            System.Reflection.MethodInfo addTurn = mod.GetType("RavenfieldVR.Rig.CockpitControls")?.GetMethod("AddTurn",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            Check(addTurn != null, "Steering: CockpitControls.AddTurn found");
            if (addTurn == null)
            {
                return;
            }
            float Turn(float turned, float fromDeg, float toDeg, float steerAtTake, float maxRotation) =>
                (float)addTurn.Invoke(null, new object[] { turned, Rim(fromDeg), Rim(toDeg), UnityEngine.Vector3.forward, steerAtTake, maxRotation });
            // A hand on the rim turned clockwise as the driver sees it (the wheel facing -forward), in 10 degree steps.
            float HandOverHand(float degrees, float steerAtTake, float maxRotation)
            {
                float turned = 0f;
                for (float a = 10f; a <= degrees + 0.01f; a += 10f)
                {
                    turned = Turn(turned, a - 10f, a, steerAtTake, maxRotation);
                }
                return turned;
            }
            float t270 = HandOverHand(270f, 0f, 360f);
            Check(Math.Abs(t270 + 270f) < 0.5f, $"Steering: 270 degrees hand over hand add up past half a turn ({t270:0.0}, not wrapped)");
            float locked = HandOverHand(270f, 0f, 120f);
            Check(Math.Abs(locked + 120f) < 0.01f, $"Steering: held at full lock past it ({locked:0.0})");
            float back = Turn(Turn(0f, 0f, 30f, 0f, 120f), 30f, 0f, 0f, 120f);
            Check(Math.Abs(back) < 0.01f, $"Steering: turned and back is straight ({back:0.00})");
            // Taken at half lock (steer 0.5, 100 degrees to lock): half a lock is left one way (+50), one and a half the other (-150).
            float ccw = Turn(0f, 0f, -80f, 0.5f, 100f), cw = HandOverHand(120f, 0.5f, 100f);
            Check(Math.Abs(ccw - 50f) < 0.01f && Math.Abs(cw + 120f) < 0.5f, $"Steering: the lock counts from where the wheel was taken ({ccw:0.0} one way, {cw:0.0} the other)");
        }

        /// <summary>A rim point <paramref name="deg"/> clockwise from the top, as the driver sees a wheel facing -forward.</summary>
        private static UnityEngine.Vector3 Rim(float deg)
        {
            float r = deg * (float)Math.PI / 180f;
            return new UnityEngine.Vector3((float)Math.Sin(r), (float)Math.Cos(r), 0f);
        }

        private static void InsideTests()
        {
            const string game = @"C:\Games\Ravenfield";
            Check(Installer.Inside(game, @"BepInEx\plugins\RavenfieldVR\RavenfieldVR.dll") == game + @"\BepInEx\plugins\RavenfieldVR\RavenfieldVR.dll", "Inside: a plain path");
            Check(Installer.Inside(game, @"ravenfield_Data\Plugins\..\x.dll") == game + @"\ravenfield_Data\x.dll", "Inside: '..' that stays inside");
            Check(Installer.Inside(game, @"ravenfield_Data\..\..\x") == null, "Inside: zip-slip out of the folder");
            Check(Installer.Inside(game, @"..\Ravenfield2\x") == null, "Inside: a sibling folder with the same prefix");
            Check(Installer.Inside(game, @"C:\Windows\x") == null, "Inside: a rooted path");
            Check(Installer.Inside(game + @"\", @"BepInEx\x") == game + @"\BepInEx\x", "Inside: a root with a trailing separator");
            Check(Installer.Inside(game, "") == null && Installer.Inside(game, null) == null, "Inside: empty");
        }

        [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CommandLineToArgvW(string cmdLine, out int argc);

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr mem);

        /// <summary>A command line split the way the game's C runtime reads it (CommandLineToArgvW), the program name dropped.</summary>
        private static string[] SplitLikeWindows(string args)
        {
            IntPtr argv = CommandLineToArgvW("x.exe " + args, out int argc);
            var parts = new string[Math.Max(0, argc - 1)];
            for (int i = 1; i < argc; i++)
            {
                parts[i - 1] = System.Runtime.InteropServices.Marshal.PtrToStringUni(System.Runtime.InteropServices.Marshal.ReadIntPtr(argv, i * IntPtr.Size));
            }
            LocalFree(argv);
            return parts;
        }

        /// <summary>
        /// The Steam launcher, run on every launch: the graphics preset written into the mod's config in its own section with the
        /// rest of the file kept, its render scale set only when the preset changes, the game's arguments passed on unchanged
        /// (Windows' own splitting reads back exactly what was given) and -vr / -novr taken out as the choice.
        /// </summary>
        private static void LauncherTests()
        {
            string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "rfvr-launcher-" + Guid.NewGuid().ToString("N"));
            string cfgDir = System.IO.Path.Combine(dir, "BepInEx", "config"), cfg = System.IO.Path.Combine(cfgDir, "com.qwavey.ravenfieldvr.cfg");
            try
            {
                // No config yet: the section and both entries are made.
                RavenfieldVR.Launcher.PluginConfig.SetGraphicsPreset(dir, "High");
                string made = System.IO.File.ReadAllText(cfg);
                Check(RavenfieldVR.Launcher.PluginConfig.GetGraphicsPreset(dir) == "High" && made.Contains("[Rendering]") && made.Contains("RenderScale = 1" + Environment.NewLine),
                      "Launcher: a missing config gets the preset and its render scale (" + made.Replace("\r\n", " | ") + ")");
                // A config BepInEx wrote: only the preset's two lines change, the comments, other sections and same-named keys elsewhere stay.
                string[] original =
                {
                    "## Settings file was created by plugin Ravenfield VR v1.0.0", "", "[Comfort]", "", "## Degrees per snap turn.", "SnapTurnAngle = 30", "",
                    "[Rendering]", "", "## Eye texture resolution multiplier", "# Setting type: Single", "RenderScale = 0.95", "", "GraphicsPreset = Medium",
                    "MirrorView = Right", "", "[Weapons]", "", "GraphicsPresetNote = keep", "RenderScale = 7"
                };
                System.IO.File.WriteAllLines(cfg, original);
                RavenfieldVR.Launcher.PluginConfig.SetGraphicsPreset(dir, "Ultra");
                string[] after = System.IO.File.ReadAllLines(cfg);
                var changed = new List<string>();
                for (int i = 0; i < Math.Max(original.Length, after.Length); i++)
                {
                    string a = i < original.Length ? original[i] : "(none)", b = i < after.Length ? after[i] : "(none)";
                    if (a != b)
                    {
                        changed.Add(a + " -> " + b);
                    }
                }
                Check(changed.Count == 2 && changed.Contains("RenderScale = 0.95 -> RenderScale = 1.2") && changed.Contains("GraphicsPreset = Medium -> GraphicsPreset = Ultra"),
                      "Launcher: a new preset changes only its own line and the render scale in [Rendering] (" + string.Join("; ", changed) + ")");
                // The same preset again: a render scale adjusted by hand stays.
                string[] tuned = (string[])after.Clone();
                tuned[Array.IndexOf(tuned, "RenderScale = 1.2")] = "RenderScale = 1.33";
                System.IO.File.WriteAllLines(cfg, tuned);
                RavenfieldVR.Launcher.PluginConfig.SetGraphicsPreset(dir, "Ultra");
                Check(Array.IndexOf(System.IO.File.ReadAllLines(cfg), "RenderScale = 1.33") >= 0, "Launcher: the same preset again keeps a render scale set by hand");

                // The game's arguments: quoted so Windows reads back each one as it was (spaces, quotes, trailing backslashes, empty).
                System.Reflection.MethodInfo quote = typeof(RavenfieldVR.Launcher.GameCommand).GetMethod("Quote", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
                string[] given = { "-map", "island 2023", @"D:\Mods\", @"C:\a b\c\\", "say \"hi\"", "", @"x\""y", "-screen-width", "1920", @"\\server\share\" };
                string line = string.Join(" ", Array.ConvertAll(given, g => (string)quote.Invoke(null, new object[] { g })));
                string[] back = SplitLikeWindows(line);
                bool same = back.Length == given.Length;
                for (int i = 0; same && i < given.Length; i++)
                {
                    same = back[i] == given[i];
                }
                Check(same, "Launcher: the game's arguments read back by Windows exactly as given (" + line + " -> " + string.Join(" | ", back) + ")");
                // The choice: -vr / -novr taken out of what goes on to the game, any case.
                string exe = System.IO.Path.Combine(dir, "ravenfield.exe");
                System.IO.File.WriteAllText(exe, "");
                var cmd = RavenfieldVR.Launcher.GameCommand.FromArgs(new[] { exe, "-NOVR", "-map", "x" });
                Check(cmd.Requested == RavenfieldVR.Launcher.PlayMode.Flat && cmd.Args.Count == 2 && cmd.Args[0] == "-map" && cmd.GameDir == dir,
                      "Launcher: -novr is the choice, not passed on; Steam's own exe path is the game's folder");
                // The remembered choice: saved and read back as it was; a damaged file (a preset that is none, stray lines) keeps
                // what it can and asks again rather than throwing.
                var saved = RavenfieldVR.Launcher.LauncherSettings.Load(dir);
                saved.Mode = RavenfieldVR.Launcher.PlayMode.Flat;
                saved.Preset = "Low";
                saved.Remember = true;
                saved.Save();
                var read = RavenfieldVR.Launcher.LauncherSettings.Load(dir);
                // The preset shown is the mod's config's (Ultra, set above): one picked in the headset since is the newer choice, and the
                // launcher's own copy put it back on the next start (round 7, 2026-10-28). Its copy counts only with no config.
                Check(read.Mode == RavenfieldVR.Launcher.PlayMode.Flat && read.Preset == "Ultra" && read.Remember,
                      $"Launcher: the remembered choice reads back, with the config's preset (mode {read.Mode}, preset {read.Preset}, remember {read.Remember})");
                string cfgKept = System.IO.File.ReadAllText(cfg);
                System.IO.File.Delete(cfg);
                var noConfig = RavenfieldVR.Launcher.LauncherSettings.Load(dir);
                Check(noConfig.Preset == "Low", $"Launcher: with no config yet, its own remembered preset (got {noConfig.Preset})");
                System.IO.File.WriteAllText(System.IO.Path.Combine(cfgDir, "RavenfieldVR.launcher.ini"), "garbage\n=x\nmode=VR\npreset=Insane\nremember=yes\n");
                var damaged = RavenfieldVR.Launcher.LauncherSettings.Load(dir);
                Check(damaged.Mode == RavenfieldVR.Launcher.PlayMode.VR && damaged.Preset == "Medium" && !damaged.Remember,
                      $"Launcher: a damaged choice file keeps the defaults it cannot read (mode {damaged.Mode}, preset {damaged.Preset}, remember {damaged.Remember})");
                System.IO.File.WriteAllText(cfg, cfgKept);
            }
            catch (Exception e)
            {
                Check(false, "Launcher: threw " + e);
            }
            finally
            {
                try
                {
                    System.IO.Directory.Delete(dir, true);
                }
                catch (System.IO.IOException)
                {
                }
            }
        }

        /// <summary>
        /// The setup's uninstall on a made-up install in a temporary folder, with a made-up Steam: the launcher kept while a
        /// launch option still starts it and removed when none does; the plugin folder, the launcher's log and (settings not
        /// kept) the config, overrides and launcher choice removed; another plugin's config and a BepInEx it did not install kept.
        /// </summary>
        private static void SetupUninstallTests()
        {
            foreach (bool inUse in new[] { true, false })
            {
                string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "rfvr-setup-uninstall-" + Guid.NewGuid().ToString("N"));
                string game = System.IO.Path.Combine(root, "game"), steam = System.IO.Path.Combine(root, "steam");
                try
                {
                    void Write(string rel, string text)
                    {
                        string p = System.IO.Path.Combine(game, rel);
                        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(p));
                        System.IO.File.WriteAllText(p, text);
                    }
                    Write("ravenfield.exe", "");
                    Write(Installer.LauncherName, "launcher");
                    Write(@"BepInEx\plugins\RavenfieldVR\RavenfieldVR.dll", "mod");
                    Write(@"BepInEx\core\BepInEx.dll", "loader");
                    Write(@"BepInEx\RavenfieldVR-launcher.log", "log");
                    Write(@"BepInEx\config\com.qwavey.ravenfieldvr.cfg", "cfg");
                    Write(@"BepInEx\config\RavenfieldVR.weapons.json", "{}");
                    Write(@"BepInEx\config\RavenfieldVR.launcher.ini", "mode=VR");
                    Write(@"BepInEx\config\other.plugin.cfg", "keep");
                    Write(Installer.ManifestRelative, "version=test\ninstalledBepInEx=False\nbackup=\nadded=" + Installer.LauncherName
                                                      + "\nadded=BepInEx\\plugins\\RavenfieldVR\\RavenfieldVR.dll\n");
                    string cfgDir = System.IO.Path.Combine(steam, "userdata", "12345", "config");
                    System.IO.Directory.CreateDirectory(cfgDir);
                    string option = inUse ? "\\\"" + game.Replace("\\", "\\\\") + "\\\\" + Installer.LauncherName + "\\\" %command%" : "";
                    System.IO.File.WriteAllText(System.IO.Path.Combine(cfgDir, "localconfig.vdf"),
                        "\"UserLocalConfigStore\"\n{\n\t\"Software\"\n\t{\n\t\t\"Valve\"\n\t\t{\n\t\t\t\"Steam\"\n\t\t\t{\n\t\t\t\t\"apps\"\n\t\t\t\t{\n\t\t\t\t\t\"636480\"\n\t\t\t\t\t{\n"
                        + "\t\t\t\t\t\t\"LaunchOptions\"\t\t\"" + option + "\"\n\t\t\t\t\t}\n\t\t\t\t}\n\t\t\t}\n\t\t}\n\t}\n}\n");
                    Installer.SteamDirOverride = steam;
                    var log = new List<string>();
                    new Installer(log.Add).Uninstall(game, keepSettings: false);
                    bool Has(string rel) => System.IO.File.Exists(System.IO.Path.Combine(game, rel)) || System.IO.Directory.Exists(System.IO.Path.Combine(game, rel));
                    Check(Has(Installer.LauncherName) == inUse && !Has(@"BepInEx\plugins\RavenfieldVR") && !Has(@"BepInEx\RavenfieldVR-launcher.log")
                          && !Has(@"BepInEx\config\com.qwavey.ravenfieldvr.cfg") && !Has(@"BepInEx\config\RavenfieldVR.weapons.json")
                          && !Has(@"BepInEx\config\RavenfieldVR.launcher.ini") && Has(@"BepInEx\config\other.plugin.cfg") && Has(@"BepInEx\core\BepInEx.dll"),
                          $"Setup uninstall (launch option {(inUse ? "set" : "not set")}): launcher {(Has(Installer.LauncherName) ? "kept" : "removed")}, the mod's files and settings gone, "
                          + "another plugin's config and BepInEx it did not install kept (" + string.Join(" | ", log) + ")");
                }
                catch (Exception e)
                {
                    Check(false, "Setup uninstall: threw " + e);
                }
                finally
                {
                    Installer.SteamDirOverride = null;
                    try
                    {
                        System.IO.Directory.Delete(root, true);
                    }
                    catch (System.IO.IOException)
                    {
                    }
                }
            }
        }

        /// <summary>
        /// Round 7 (2026-10-28): "Keep my settings" keeps BepInEx when the game never wrote the mod's config (the launcher's choice
        /// went with it), a Steam config that cannot be read keeps the launcher (it may still start it), and a backup copy put back
        /// is deleted with its emptied folders (each conflict left another folder of them).
        /// </summary>
        private static void SetupKeepAndRestoreTests()
        {
            string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "rfvr-setup-keep-" + Guid.NewGuid().ToString("N"));
            string game = System.IO.Path.Combine(root, "game"), steam = System.IO.Path.Combine(root, "steam");
            try
            {
                void Write(string rel, string text)
                {
                    string p = System.IO.Path.Combine(game, rel);
                    System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(p));
                    System.IO.File.WriteAllText(p, text);
                }
                Write("ravenfield.exe", "");
                Write(Installer.LauncherName, "launcher");
                Write(@"BepInEx\plugins\RavenfieldVR\RavenfieldVR.dll", "mod");
                Write(@"BepInEx\core\BepInEx.dll", "loader");
                Write(@"BepInEx\config\RavenfieldVR.launcher.ini", "mode=VR"); // (no com.qwavey.ravenfieldvr.cfg: the game never ran)
                Write(@"ravenfield_Data\keep.dat", "modded");
                Write(@"RavenfieldVR-backup\20261028-000000\ravenfield_Data\keep.dat", "original");
                Write(Installer.ManifestRelative, "version=test\ninstalledBepInEx=True\nbackup=RavenfieldVR-backup\\20261028-000000\nadded=" + Installer.LauncherName
                                                  + "\nadded=BepInEx\\core\\BepInEx.dll\nadded=BepInEx\\plugins\\RavenfieldVR\\RavenfieldVR.dll\nreplaced=ravenfield_Data\\keep.dat\n");
                string cfgDir = System.IO.Path.Combine(steam, "userdata", "12345", "config");
                System.IO.Directory.CreateDirectory(cfgDir);
                // Cut short mid-write (Steam stopped while writing it): it cannot be parsed.
                System.IO.File.WriteAllText(System.IO.Path.Combine(cfgDir, "localconfig.vdf"), "\"UserLocalConfigStore\"\n{\n\t\"Software\"\n\t{\n\t\t\"Val");
                Installer.SteamDirOverride = steam;
                var log = new List<string>();
                new Installer(log.Add).Uninstall(game, keepSettings: true);
                bool Has(string rel) => System.IO.File.Exists(System.IO.Path.Combine(game, rel)) || System.IO.Directory.Exists(System.IO.Path.Combine(game, rel));
                string all = string.Join(" | ", log);
                Check(Has(@"BepInEx\core\BepInEx.dll") && Has(@"BepInEx\config\RavenfieldVR.launcher.ini"),
                      "Setup uninstall, settings kept with no mod config yet: BepInEx and the launcher's choice kept (" + all + ")");
                Check(Has(Installer.LauncherName), "Setup uninstall: an unreadable Steam config keeps the launcher (" + all + ")");
                Check(Has(@"ravenfield_Data\keep.dat") && System.IO.File.ReadAllText(System.IO.Path.Combine(game, @"ravenfield_Data\keep.dat")) == "original"
                      && !Has(@"RavenfieldVR-backup"), "Setup uninstall: the original put back and its backup copy and emptied folders gone (" + all + ")");
            }
            catch (Exception e)
            {
                Check(false, "Setup keep and restore: threw " + e);
            }
            finally
            {
                Installer.SteamDirOverride = null;
                try
                {
                    System.IO.Directory.Delete(root, true);
                }
                catch (System.IO.IOException)
                {
                }
            }
        }

        /// <summary>
        /// A BepInEx 5 whose loader beside the game is gone: the install adds the loader (else nothing loads) and keeps that
        /// BepInEx's core; Uninstall leaves the loader for it.
        /// </summary>
        private static void SetupMissingLoaderTests()
        {
            string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "rfvr-setup-noloader-" + Guid.NewGuid().ToString("N"));
            string game = System.IO.Path.Combine(root, "game"), steam = System.IO.Path.Combine(root, "steam");
            try
            {
                System.IO.Directory.CreateDirectory(System.IO.Path.Combine(game, @"BepInEx\core"));
                System.IO.Directory.CreateDirectory(steam);
                System.IO.File.WriteAllText(System.IO.Path.Combine(game, "ravenfield.exe"), "");
                string core = System.IO.Path.Combine(game, @"BepInEx\core\BepInEx.dll"), winhttp = System.IO.Path.Combine(game, "winhttp.dll");
                System.IO.File.WriteAllText(core, "the user's BepInEx");
                Installer.SteamDirOverride = steam;
                var log = new List<string>();
                var installer = new Installer(log.Add);
                installer.Install(game);
                string record = System.IO.File.ReadAllText(System.IO.Path.Combine(game, Installer.ManifestRelative));
                Check(System.IO.File.Exists(winhttp) && System.IO.File.ReadAllText(core) == "the user's BepInEx" && record.Contains("installedBepInEx=False"),
                      "Setup missing loader: the loader is added and that BepInEx's core kept (" + string.Join(" | ", log) + ")");
                log.Clear();
                installer.Install(game); // an update or repair over it: the loader it added stays (it was removed as no longer in the release)
                Check(System.IO.File.Exists(winhttp) && System.IO.File.ReadAllText(System.IO.Path.Combine(game, Installer.ManifestRelative)).Contains("added=winhttp.dll"),
                      "Setup missing loader: installing again keeps the loader it added, and its record (" + string.Join(" | ", log) + ")");
                installer.Uninstall(game, keepSettings: false);
                Check(System.IO.File.Exists(winhttp) && System.IO.File.Exists(core), "Setup missing loader: Uninstall leaves BepInEx and the loader it now needs");
            }
            catch (Exception e)
            {
                Check(false, "Setup missing loader: threw " + e.Message);
            }
            finally
            {
                Installer.SteamDirOverride = null;
                try
                {
                    System.IO.Directory.Delete(root, true);
                }
                catch (System.IO.IOException)
                {
                }
            }
        }

        /// <summary>Release versions compare as numbers part by part (the setup's Update / Downgrade label and the version list's order).</summary>
        private static void VersionCompareTests()
        {
            foreach ((string a, string b, int sign) in new[]
            {
                ("1.11.0", "1.10.2", 1), ("1.10.0", "1.9.9", 1), ("1.9.0", "1.10.0", -1), ("1.11", "1.11.0", 0),
                ("2.0.0", "1.99.99", 1), ("1.0.0", "1.0.0", 0), ("1.10.10", "1.10.9", 1),
            })
            {
                int got = Math.Sign(GitHub.CompareVersions(a, b));
                Check(got == sign, $"Versions: {a} vs {b} compares {got} (want {sign})");
            }
        }

        /// <summary>A game folder as typed or pasted: Explorer's "Copy as path" quotes, spaces and a trailing separator come off; a relative one is made absolute.</summary>
        private static void GamePathTests()
        {
            string game = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "rfvr game " + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(game);
            try
            {
                System.IO.File.WriteAllText(System.IO.Path.Combine(game, "ravenfield.exe"), "");
                foreach (string typed in new[] { "\"" + game + "\"", "  " + game + "\\ ", " \"" + game + "\\\" " })
                {
                    string got = Installer.GamePath(typed);
                    Check(got == game && Installer.IsRavenfieldFolder(got), $"Game path: {Show(typed)} -> {Show(got)}");
                }
                Check(System.IO.Path.IsPathRooted(Installer.GamePath(@"some\relative")), "Game path: a relative folder is made absolute");
                Check(Installer.GamePath(@"C:\") == @"C:\", "Game path: a drive's root keeps its separator");
                Check(!Installer.IsRavenfieldFolder("a\"b<c") && Installer.GamePath("a\"b<c") == "a\"b<c", "Game path: invalid characters are no game folder, and do not throw");
            }
            finally
            {
                System.IO.Directory.Delete(game, true);
            }
        }

        /// <summary>
        /// A game that had another tool's own winhttp.dll (no BepInEx): installed over, then uninstalled. With BepInEx kept (the
        /// settings kept) BepInEx's loader stays, so BepInEx still loads; with BepInEx removed the tool's original comes back and
        /// is not deleted with BepInEx's loader.
        /// </summary>
        private static void SetupForeignLoaderTests()
        {
            foreach (bool keep in new[] { true, false })
            {
                string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "rfvr-setup-loader-" + Guid.NewGuid().ToString("N"));
                string game = System.IO.Path.Combine(root, "game"), steam = System.IO.Path.Combine(root, "steam");
                try
                {
                    System.IO.Directory.CreateDirectory(game);
                    System.IO.Directory.CreateDirectory(steam);
                    System.IO.File.WriteAllText(System.IO.Path.Combine(game, "ravenfield.exe"), "");
                    string winhttp = System.IO.Path.Combine(game, "winhttp.dll");
                    System.IO.File.WriteAllText(winhttp, "other tool");
                    Installer.SteamDirOverride = steam;
                    var log = new List<string>();
                    var installer = new Installer(log.Add);
                    installer.Install(game);
                    string record = System.IO.File.ReadAllText(System.IO.Path.Combine(game, Installer.ManifestRelative));
                    Check(record.Contains("replaced=winhttp.dll") && System.IO.File.ReadAllText(winhttp) == "doorstop",
                          $"Setup loader ({(keep ? "kept" : "removed")}): the other tool's winhttp.dll is backed up and replaced by BepInEx's");
                    if (keep)
                    {
                        System.IO.Directory.CreateDirectory(System.IO.Path.Combine(game, @"BepInEx\config"));
                        System.IO.File.WriteAllText(System.IO.Path.Combine(game, @"BepInEx\config\com.qwavey.ravenfieldvr.cfg"), "cfg");
                    }
                    log.Clear();
                    installer.Uninstall(game, keepSettings: keep);
                    string now = System.IO.File.Exists(winhttp) ? System.IO.File.ReadAllText(winhttp) : "(none)";
                    Check(keep ? now == "doorstop" && System.IO.File.Exists(System.IO.Path.Combine(game, @"BepInEx\core\BepInEx.dll"))
                               : now == "other tool" && !System.IO.Directory.Exists(System.IO.Path.Combine(game, "BepInEx")),
                          keep ? $"Setup loader (BepInEx kept): BepInEx's winhttp.dll stays so BepInEx still loads (now '{now}'; {string.Join(" | ", log)})"
                               : $"Setup loader (BepInEx removed): the other tool's winhttp.dll is back, not deleted (now '{now}'; {string.Join(" | ", log)})");
                }
                catch (Exception e)
                {
                    Check(false, "Setup loader: threw " + e);
                }
                finally
                {
                    Installer.SteamDirOverride = null;
                    try
                    {
                        System.IO.Directory.Delete(root, true);
                    }
                    catch (System.IO.IOException)
                    {
                    }
                }
            }
        }

        /// <summary>
        /// A first install that stops part way (an original it replaces is locked by another program): it leaves a record of
        /// what it changed, so Uninstall then puts the game back, and installing again records the files the failed attempt had
        /// already written (identical to the release's, they were taken for the game's own and never removed).
        /// </summary>
        private static void SetupInterruptedInstallTests()
        {
            foreach (bool retry in new[] { false, true })
            {
                string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "rfvr-setup-stopped-" + Guid.NewGuid().ToString("N"));
                string game = System.IO.Path.Combine(root, "game"), steam = System.IO.Path.Combine(root, "steam");
                string what = retry ? "installed again" : "uninstalled";
                try
                {
                    System.IO.Directory.CreateDirectory(game);
                    System.IO.Directory.CreateDirectory(steam);
                    System.IO.File.WriteAllText(System.IO.Path.Combine(game, "ravenfield.exe"), "");
                    string winhttp = System.IO.Path.Combine(game, "winhttp.dll");
                    System.IO.File.WriteAllText(winhttp, "other tool");
                    Installer.SteamDirOverride = steam;
                    var log = new List<string>();
                    var installer = new Installer(log.Add);
                    bool stopped = false;
                    using (new System.IO.FileStream(winhttp, System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.Read))
                    {
                        try
                        {
                            installer.Install(game);
                        }
                        catch (System.IO.IOException)
                        {
                            stopped = true;
                        }
                    }
                    Check(stopped, $"Setup stopped install ({what}): the install stops at the locked winhttp.dll");
                    string recordPath = System.IO.Path.Combine(game, Installer.ManifestRelative);
                    log.Clear();
                    if (retry)
                    {
                        installer.Install(game);
                        string record = System.IO.File.Exists(recordPath) ? System.IO.File.ReadAllText(recordPath) : "";
                        Check(record.Contains(@"added=BepInEx\plugins\RavenfieldVR\RavenfieldVR.dll") && record.Contains("replaced=winhttp.dll")
                              && !record.Contains("incomplete"),
                              $"Setup stopped install (installed again): the record has the files the failed attempt wrote and the original it backed up ({string.Join(" | ", log)})");
                    }
                    else
                    {
                        installer.Uninstall(game, keepSettings: false);
                        string now = System.IO.File.Exists(winhttp) ? System.IO.File.ReadAllText(winhttp) : "(none)";
                        Check(now == "other tool" && !System.IO.File.Exists(System.IO.Path.Combine(game, @"BepInEx\plugins\RavenfieldVR\RavenfieldVR.dll")),
                              $"Setup stopped install (uninstalled): Uninstall puts the game back (winhttp.dll '{now}'; {string.Join(" | ", log)})");
                    }
                }
                catch (Exception e)
                {
                    Check(false, $"Setup stopped install ({what}): threw " + e.Message);
                }
                finally
                {
                    Installer.SteamDirOverride = null;
                    try
                    {
                        System.IO.Directory.Delete(root, true);
                    }
                    catch (System.IO.IOException)
                    {
                    }
                }
            }
        }

        /// <summary>
        /// An update from a payload that is no whole release (bytes that are no zip, the release zip cut off half way, a zip
        /// without the plugin): refused before anything changes, the installed mod and its record left as they were.
        /// </summary>
        private static void SetupBadPayloadTests()
        {
            string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "rfvr-setup-payload-" + Guid.NewGuid().ToString("N"));
            string game = System.IO.Path.Combine(root, "game");
            try
            {
                System.IO.Directory.CreateDirectory(game);
                System.IO.File.WriteAllText(System.IO.Path.Combine(game, "ravenfield.exe"), "");
                var installer = new Installer(_ => { });
                installer.Install(game);
                byte[] release;
                using (System.IO.Stream s = typeof(Installer).Assembly.GetManifestResourceStream("RavenfieldVR.Setup.payload.zip"))
                using (var m = new System.IO.MemoryStream())
                {
                    s.CopyTo(m);
                    release = m.ToArray();
                }
                byte[] empty;
                using (var m = new System.IO.MemoryStream())
                {
                    using (var z = new System.IO.Compression.ZipArchive(m, System.IO.Compression.ZipArchiveMode.Create, true))
                    {
                        using (var w = new System.IO.StreamWriter(z.CreateEntry("readme.txt").Open()))
                        {
                            w.Write("no plugin");
                        }
                    }
                    empty = m.ToArray();
                }
                string plugin = System.IO.Path.Combine(game, @"BepInEx\plugins\RavenfieldVR\RavenfieldVR.dll");
                string recordPath = System.IO.Path.Combine(game, Installer.ManifestRelative);
                string record = System.IO.File.ReadAllText(recordPath);
                var bad = new[]
                {
                    ("bytes that are no zip", System.Text.Encoding.ASCII.GetBytes("<html>404 Not Found</html>")),
                    ("the release cut off half way", release.Take(release.Length / 2).ToArray()),
                    ("a zip without the plugin", empty),
                };
                foreach ((string what, byte[] bytes) in bad)
                {
                    string error = null;
                    try
                    {
                        installer.InstallFrom(game, () => new System.IO.MemoryStream(bytes), "9.9.9");
                    }
                    catch (InvalidOperationException e)
                    {
                        error = e.Message;
                    }
                    Check(error != null && error.Contains("Nothing was changed") && System.IO.File.Exists(plugin) && System.IO.File.ReadAllText(recordPath) == record,
                          $"Setup payload ({what}): refused before the installed mod is touched ({error ?? "installed"})");
                }
            }
            catch (Exception e)
            {
                Check(false, "Setup payload: threw " + e);
            }
            finally
            {
                try
                {
                    System.IO.Directory.Delete(root, true);
                }
                catch (System.IO.IOException)
                {
                }
            }
        }

        /// <summary>
        /// A game moved after the install (Steam's "Move install folder"): Uninstall from the new place still puts the original it
        /// replaced back. The record keeps its backup folder relative to the game; an older record's absolute path is followed to
        /// the same folder under the moved game.
        /// </summary>
        private static void SetupMovedGameTests()
        {
            foreach (bool legacy in new[] { false, true })
            {
                string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "rfvr-setup-moved-" + Guid.NewGuid().ToString("N"));
                string game = System.IO.Path.Combine(root, "game"), moved = System.IO.Path.Combine(root, "moved"), steam = System.IO.Path.Combine(root, "steam");
                string what = legacy ? "an older record's absolute backup path" : "the record's relative backup path";
                try
                {
                    System.IO.Directory.CreateDirectory(game);
                    System.IO.Directory.CreateDirectory(steam);
                    System.IO.File.WriteAllText(System.IO.Path.Combine(game, "ravenfield.exe"), "");
                    System.IO.File.WriteAllText(System.IO.Path.Combine(game, "winhttp.dll"), "other tool");
                    Installer.SteamDirOverride = steam;
                    var log = new List<string>();
                    var installer = new Installer(log.Add);
                    installer.Install(game);
                    string recordPath = System.IO.Path.Combine(game, Installer.ManifestRelative);
                    string[] record = System.IO.File.ReadAllLines(recordPath);
                    string backupLine = record.FirstOrDefault(l => l.StartsWith("backup=")) ?? "";
                    if (!legacy)
                    {
                        Check(backupLine.StartsWith(@"backup=RavenfieldVR-backup\"), "Setup moved game: the record keeps its backup folder relative to the game (" + backupLine + ")");
                    }
                    else
                    {
                        System.IO.File.WriteAllLines(recordPath, record.Select(l => l.StartsWith("backup=") ? "backup=" + System.IO.Path.Combine(game, l.Substring(7)) : l));
                    }
                    System.IO.Directory.Move(game, moved);
                    log.Clear();
                    installer.Uninstall(moved, keepSettings: false);
                    string winhttp = System.IO.Path.Combine(moved, "winhttp.dll");
                    string now = System.IO.File.Exists(winhttp) ? System.IO.File.ReadAllText(winhttp) : "(none)";
                    Check(now == "other tool", $"Setup moved game ({what}): Uninstall puts the original back (now '{now}'; {string.Join(" | ", log)})");
                }
                catch (Exception e)
                {
                    Check(false, $"Setup moved game ({what}): threw " + e);
                }
                finally
                {
                    Installer.SteamDirOverride = null;
                    try
                    {
                        System.IO.Directory.Delete(root, true);
                    }
                    catch (System.IO.IOException)
                    {
                    }
                }
            }
        }

        /// <summary>
        /// The setup's install of its embedded release (here run-tests.ps1's small payload) into a temporary folder: a fresh install
        /// writes the plugin, BepInEx, the launcher and the record, and a zip entry that climbs out of the game folder is skipped,
        /// never written beside it (zip slip); an upgrade over it removes a file the earlier install added that this one has not.
        /// </summary>
        private static void SetupInstallTests()
        {
            string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "rfvr-setup-install-" + Guid.NewGuid().ToString("N"));
            string game = System.IO.Path.Combine(root, "game");
            try
            {
                System.IO.Directory.CreateDirectory(game);
                System.IO.File.WriteAllText(System.IO.Path.Combine(game, "ravenfield.exe"), "");
                var log = new List<string>();
                var installer = new Installer(log.Add);
                installer.Install(game);
                bool Has(string rel) => System.IO.File.Exists(System.IO.Path.Combine(game, rel));
                string record = System.IO.File.ReadAllText(System.IO.Path.Combine(game, Installer.ManifestRelative));
                Check(Has(@"BepInEx\plugins\RavenfieldVR\RavenfieldVR.dll") && Has(@"BepInEx\core\BepInEx.dll") && Has(Installer.LauncherName)
                      && record.Contains("installedBepInEx=True") && record.Contains("added=" + Installer.LauncherName),
                      "Setup install: a fresh install writes the plugin, BepInEx, the launcher and its record (" + string.Join(" | ", log) + ")");
                Check(!System.IO.File.Exists(System.IO.Path.Combine(root, "outside.txt")) && log.Exists(l => l.Contains("outside the game folder")),
                      "Setup install: a zip entry that climbs out of the game folder is skipped, not written beside it");
                // An upgrade: the record names a file this release no longer has; it goes.
                System.IO.File.WriteAllText(System.IO.Path.Combine(game, "old_part.txt"), "old");
                System.IO.File.AppendAllText(System.IO.Path.Combine(game, Installer.ManifestRelative), "added=old_part.txt\n");
                log.Clear();
                installer.Install(game);
                Check(!Has("old_part.txt") && Has(@"BepInEx\plugins\RavenfieldVR\RavenfieldVR.dll") && log.Exists(l => l.StartsWith("Updating")),
                      "Setup install: an upgrade removes what the earlier install added and this one has not (" + string.Join(" | ", log) + ")");
            }
            catch (Exception e)
            {
                Check(false, "Setup install: threw " + e);
            }
            finally
            {
                try
                {
                    System.IO.Directory.Delete(root, true);
                }
                catch (System.IO.IOException)
                {
                }
            }
        }

        private static void VdfTests()
        {
            const string text = "\"UserLocalConfigStore\"\n{\n\t\"Software\"\n\t{\n\t\t\"LaunchOptions\"\t\t\"\\\"C:\\\\x\\\\RavenfieldVR-Launcher.exe\\\" %command%\"\n\t}\n}\n";
            VdfNode root = Vdf.Parse(text);
            VdfNode store = root.Child("UserLocalConfigStore");
            VdfNode option = store?.Child("Software")?.Child("LaunchOptions");
            Check(option != null && Vdf.Unescape(option.RawValue) == "\"C:\\x\\RavenfieldVR-Launcher.exe\" %command%", "Vdf: an escaped value read back");
            Check(Vdf.Parse(Vdf.Serialize(root)).Child("UserLocalConfigStore")?.Child("Software")?.Child("LaunchOptions")?.RawValue == option?.RawValue,
                  "Vdf: a round trip keeps the value");
            // A file cut short (Steam stopped mid-write) is a FormatException, never an index error.
            // A '}' outside any block too: parsed as the end, the rest of the file was dropped when it was written back.
            foreach (string cut in new[] { "\"a\" \"b", "\"a\" \"b\\", "\"a\"", "\"a\" {", "\"", "\"a\" { \"b\" \"c\"", "\"a\" { } } \"rest\" { \"b\" \"c\" }" })
            {
                Type thrown = null;
                try
                {
                    Vdf.Parse(cut);
                }
                catch (Exception e)
                {
                    thrown = e.GetType();
                }
                Check(thrown == typeof(FormatException), $"Vdf: truncated {Show(cut)} gives a FormatException (got {(thrown != null ? thrown.Name : "none")})");
            }
        }

        private static void BindingsTests()
        {
            const string right = "{ \"name\": \"Ravenfield VR default\", \"sources\": [ { \"path\": \"/user/hand/right/input/a\" }, { \"path\": \"/user/hand/left/input/trigger\" }, { \"path\": \"/user/hand/left/input/y\" } ] }";
            string touch = BindingsWriter.MirrorToLeftHanded(right, "oculus_touch");
            Check(touch.Contains("\"path\": \"/user/hand/left/input/x\""), "Bindings: Touch's A on the right becomes X on the left");
            Check(touch.Contains("\"path\": \"/user/hand/right/input/trigger\""), "Bindings: the left trigger becomes the right one");
            Check(touch.Contains("\"path\": \"/user/hand/right/input/b\""), "Bindings: Touch's Y on the left becomes B on the right");
            Check(touch.Contains("Ravenfield VR left-handed") && !touch.Contains("Ravenfield VR default"), "Bindings: the name says left-handed");
            string index = BindingsWriter.MirrorToLeftHanded(right, "knuckles");
            Check(index.Contains("\"path\": \"/user/hand/left/input/a\""), "Bindings: an Index controller's A stays A (no letter faces)");
            const string described = "{ \"name\": \"Ravenfield VR default\", \"description\": \"Right hand shoots, left hand moves. B: drop magazine. A: slide release. X: squad orders. Y: map.\", \"sources\": [] }";
            string touchText = BindingsWriter.MirrorToLeftHanded(described, "oculus_touch");
            Check(touchText.Contains("Y: drop magazine. X: slide release. A: squad orders. B: map."), "Bindings: Touch's description names the mirrored buttons");
            Check(touchText.Contains("Left hand shoots, right hand moves"), "Bindings: the description says which hand shoots");
            Check(BindingsWriter.MirrorToLeftHanded(described, "knuckles").Contains("B: drop magazine. A: slide release."), "Bindings: an Index description keeps its letters");
            const string hands = "{ \"name\": \"Ravenfield VR default\", \"description\": \"Right B: drop magazine, right A: slide. Left stick hold: scoreboard. LEFT menu.\", \"sources\": [ { \"path\": \"/user/hand/left/input/trackpad\" } ] }";
            string indexText = BindingsWriter.MirrorToLeftHanded(hands, "knuckles");
            Check(indexText.Contains("Left B: drop magazine, left A: slide. Right stick hold: scoreboard. RIGHT menu."), "Bindings: every controller's description names the mirrored hands, in their case");
            Check(indexText.Contains("\"path\": \"/user/hand/right/input/trackpad\""), "Bindings: the hand words' swap leaves the mirrored paths as they are");
        }

        /// <summary>
        /// The Play-list entry in Steam's binary app cache, on a copy of the user's appinfo.vdf (never the file itself): changed and
        /// changed back, the file must load at each step, and an entry added and removed must leave it byte for byte as it was
        /// (SteamPlayEntry rewrites a record's size and its SHA-1; a slip there corrupts Steam's cache).
        /// </summary>
        private static void PlayEntryTests()
        {
            // A damaged cache (a record whose size reads 0xFFFFFFF8): refused as a bad file, in time. The walk over the records
            // stepped by that size unchecked and never ended.
            string damaged = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "rfvr-test-appinfo-damaged-" + Guid.NewGuid().ToString("N") + ".vdf");
            var bytes = new List<byte>();
            bytes.AddRange(BitConverter.GetBytes(0x07564428u)); // version 28
            bytes.AddRange(BitConverter.GetBytes(1u));          // universe
            bytes.AddRange(BitConverter.GetBytes(636480u));     // an app record
            bytes.AddRange(BitConverter.GetBytes(0xFFFFFFF8u)); // its damaged size
            bytes.AddRange(new byte[80]);
            System.IO.File.WriteAllBytes(damaged, bytes.ToArray());
            string outcome = "still running after 5 s (the walk does not end)";
            var load = System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    SteamPlayEntry.IsPresent(damaged);
                    outcome = "read as fine";
                }
                catch (System.IO.InvalidDataException)
                {
                    outcome = "refused";
                }
                catch (Exception e)
                {
                    outcome = e.GetType().Name;
                }
            });
            load.Wait(5000);
            Check(outcome == "refused", "Play entry: a damaged app cache is refused, not walked forever (" + outcome + ")");
            try
            {
                System.IO.File.Delete(damaged);
            }
            catch (System.IO.IOException)
            {
            }

            string real = SteamPlayEntry.CachePath();
            if (!System.IO.File.Exists(real))
            {
                Console.WriteLine("skip Play entry: no appinfo.vdf at " + real);
                return;
            }
            string copy = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "rfvr-test-appinfo-" + Guid.NewGuid().ToString("N") + ".vdf");
            System.IO.File.Copy(real, copy);
            try
            {
                byte[] original = System.IO.File.ReadAllBytes(copy);
                bool? present = SteamPlayEntry.IsPresent(copy);
                if (present == null)
                {
                    Console.WriteLine("skip Play entry: the cache has no app info for Ravenfield");
                    return;
                }
                var log = new List<string>();
                bool first = SteamPlayEntry.Change(copy, !present.Value, log.Add);
                Check(first && SteamPlayEntry.IsPresent(copy) == !present.Value, "Play entry: " + (present.Value ? "removed" : "added") + " on a copy (" + string.Join(" / ", log) + ")");
                bool back = SteamPlayEntry.Change(copy, present.Value, log.Add);
                Check(back && SteamPlayEntry.IsPresent(copy) == present.Value, "Play entry: put back as it was");
                if (!present.Value)
                {
                    Check(SameBytes(original, System.IO.File.ReadAllBytes(copy)), "Play entry: the cache is byte for byte as before after add + remove");
                }
            }
            finally
            {
                System.IO.File.Delete(copy);
            }
        }

        private static bool SameBytes(byte[] a, byte[] b)
        {
            if (a.Length != b.Length)
            {
                return false;
            }
            for (int i = 0; i < a.Length; i++)
            {
                if (a[i] != b[i])
                {
                    return false;
                }
            }
            return true;
        }

        private static string Show(string s) => "'" + s.Replace("\n", "\\n") + "'";
    }
}
