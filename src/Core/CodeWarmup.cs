using System;
using System.Collections.Generic;
using System.Reflection;

namespace RavenfieldVR.Core
{
    /// <summary>
    /// The mod's own methods compiled ahead, in the menu (WeaponWarmup runs it before the weapons). Mono compiles a method on its
    /// first call, and the first weapon drawn in a session called hundreds for the first time in one frame: its draw, hands,
    /// reload and pouch steps took 40-64 ms there against a tenth of a millisecond on later draws (76 EAGLE, the first gun of
    /// both autotests of 2026-10-24/25). The test code is left out.
    /// Static field initializers may run when a method using them is compiled, earlier than their first use: none of the mod's
    /// reads a scene object or a game singleton (audited 2026-10-25), and none may.
    /// </summary>
    internal static class CodeWarmup
    {
        /// <summary>Types only the tests use (with everything nested in them).</summary>
        private static readonly HashSet<string> TestTypes = new HashSet<string> { "AutoTest", "Shots", "TestHost", "WeaponSelfTest" };

        private static List<RuntimeMethodHandle> _pending;
        private static int _next, _failed;
        private static double _ms;
        /// <summary>
        /// The mod's types, walked for their methods a few a step within the budget (<see cref="Collect"/>): walked whole in the first
        /// step, before its budget was counted, the reflection over every type took one frame of its own (round-6 scout).
        /// </summary>
        private static Type[] _types;
        private static int _nextType;

        internal static bool Done => _types != null && _nextType >= _types.Length && _next >= _pending.Count;

        /// <summary>What was compiled, for the autotest's results (null before the end).</summary>
        internal static string Summary { get; private set; }

        /// <summary>
        /// Compiles methods for up to <paramref name="budgetMs"/> of this frame (all that are left when negative). True once
        /// every method is compiled.
        /// </summary>
        internal static bool Step(float budgetMs)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            if (_types == null)
            {
                _types = Types();
                _pending = new List<RuntimeMethodHandle>();
                if (budgetMs >= 0f)
                {
                    _ms += watch.Elapsed.TotalMilliseconds;
                    return false; // (the assembly's type list is this step's work, unbudgeted: the walk starts next frame)
                }
            }
            if (!Collect(watch, budgetMs))
            {
                _ms += watch.Elapsed.TotalMilliseconds;
                return false; // (the types' walk took this step's budget)
            }
            while (_next < _pending.Count)
            {
                try
                {
                    _pending[_next].GetFunctionPointer(); // (Mono compiles the method to hand out its code)
                }
                catch (Exception)
                {
                    _failed++;
                }
                _next++;
                if (budgetMs >= 0f && watch.Elapsed.TotalMilliseconds >= budgetMs)
                {
                    break;
                }
            }
            _ms += watch.Elapsed.TotalMilliseconds;
            if (Done && Summary == null)
            {
                Summary = $"{_pending.Count - _failed} of the mod's methods compiled ahead in {_ms:0} ms ({_failed} could not be)";
                Log.Info(Summary + ".");
            }
            return Done;
        }

        private static Type[] Types()
        {
            try
            {
                return typeof(CodeWarmup).Assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException e)
            {
                return e.Types; // (those that loaded)
            }
        }

        /// <summary>The next types' methods into the list while the step's budget lasts; true once every type is walked.</summary>
        private static bool Collect(System.Diagnostics.Stopwatch watch, float budgetMs)
        {
            const BindingFlags declared = BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            while (_nextType < _types.Length)
            {
                Type t = _types[_nextType++];
                if (t != null && !t.ContainsGenericParameters && !Skipped(t))
                {
                    foreach (MethodInfo m in t.GetMethods(declared))
                    {
                        Add(_pending, m);
                    }
                    foreach (ConstructorInfo c in t.GetConstructors(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                    {
                        Add(_pending, c);
                    }
                }
                if (budgetMs >= 0f && watch.Elapsed.TotalMilliseconds >= budgetMs)
                {
                    return _nextType >= _types.Length;
                }
            }
            return true;
        }

        private static void Add(List<RuntimeMethodHandle> handles, MethodBase m)
        {
            // Only methods with a body of their own: not abstract or open generic ones, not a delegate's Invoke (the runtime's)
            // or a native call.
            if (m.IsAbstract || m.ContainsGenericParameters || (m.Attributes & MethodAttributes.PinvokeImpl) != 0
                || (m.GetMethodImplementationFlags() & (MethodImplAttributes.InternalCall | MethodImplAttributes.Runtime)) != 0)
            {
                return;
            }
            handles.Add(m.MethodHandle);
        }

        /// <summary>
        /// The tests' types, and Valve's OpenVR wrappers built into the mod (openvr_api.cs: those it calls run from the start
        /// of VR, compiled long before the menu's warm-up; the rest never run).
        /// </summary>
        private static bool Skipped(Type t)
        {
            if (t.Namespace != null && t.Namespace.StartsWith("Valve", StringComparison.Ordinal))
            {
                return true;
            }
            while (t.DeclaringType != null)
            {
                t = t.DeclaringType;
            }
            return TestTypes.Contains(t.Name) || t.Name.StartsWith("AutoTest", StringComparison.Ordinal) || t.Name.StartsWith("TestHost", StringComparison.Ordinal);
        }
    }
}
