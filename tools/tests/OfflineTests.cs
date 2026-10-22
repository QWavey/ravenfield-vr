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
                                                       + " \"name\": \"M4A1 \\u00e4\\\"x\\\"\"}}" });
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
            Check((string)str.Invoke(null, new object[] { hand, "name" }) == "M4A1 ä\"x\"", "JSON: \\u and quote escapes");
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
