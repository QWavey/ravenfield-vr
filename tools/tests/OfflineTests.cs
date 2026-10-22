using System;
using System.Collections.Generic;
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
            string managed = args[0], built = System.IO.Path.GetDirectoryName(args[1]);
            AppDomain.CurrentDomain.AssemblyResolve += (sender, e) =>
            {
                string file = new System.Reflection.AssemblyName(e.Name).Name + ".dll";
                foreach (string dir in new[] { built, managed })
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
            SteeringTests(System.Reflection.Assembly.LoadFrom(args[1]));
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
            foreach (string cut in new[] { "\"a\" \"b", "\"a\" \"b\\", "\"a\"", "\"a\" {", "\"", "\"a\" { \"b\" \"c\"" })
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
        }

        /// <summary>
        /// The Play-list entry in Steam's binary app cache, on a copy of the user's appinfo.vdf (never the file itself): changed and
        /// changed back, the file must load at each step, and an entry added and removed must leave it byte for byte as it was
        /// (SteamPlayEntry rewrites a record's size and its SHA-1; a slip there corrupts Steam's cache).
        /// </summary>
        private static void PlayEntryTests()
        {
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
