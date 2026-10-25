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

        internal static bool Done => _pending != null && _next >= _pending.Count;

        /// <summary>What was compiled, for the autotest's results (null before the end).</summary>
        internal static string Summary { get; private set; }

        /// <summary>
        /// Compiles methods for up to <paramref name="budgetMs"/> of this frame (all that are left when negative). True once
        /// every method is compiled.
        /// </summary>
        internal static bool Step(float budgetMs)
        {
            if (_pending == null)
            {
                _pending = Collect();
            }
            var watch = System.Diagnostics.Stopwatch.StartNew();
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

        private static List<RuntimeMethodHandle> Collect()
        {
            var handles = new List<RuntimeMethodHandle>();
            Type[] types;
            try
            {
                types = typeof(CodeWarmup).Assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException e)
            {
                types = e.Types; // (those that loaded)
            }
            const BindingFlags declared = BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            foreach (Type t in types)
            {
                if (t == null || t.ContainsGenericParameters || Skipped(t))
                {
                    continue;
                }
                foreach (MethodInfo m in t.GetMethods(declared))
                {
                    Add(handles, m);
                }
                foreach (ConstructorInfo c in t.GetConstructors(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    Add(handles, c);
                }
            }
            return handles;
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
