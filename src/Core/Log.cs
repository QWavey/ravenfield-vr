using System;
using System.Collections.Generic;
using BepInEx.Logging;

namespace RavenfieldVR.Core
{
    /// <summary>Thin wrapper over the BepInEx log source with once-only and throttled variants.</summary>
    public static class Log
    {
        private static ManualLogSource _source;
        private static readonly HashSet<string> Once = new HashSet<string>();

        public static void Init(ManualLogSource source) => _source = source;

        public static void Info(string message) => _source?.LogInfo(message);
        public static void Warn(string message) => _source?.LogWarning(message);
        public static void Error(string message) => _source?.LogError(message);
        /// <summary>Whether debug lines are written: asked before building one that runs often (its text is built before Debug checks).</summary>
        internal static bool DebugOn => VRConfig.DebugLogging != null && VRConfig.DebugLogging.Value;

        public static void Debug(string message)
        {
            if (VRConfig.DebugLogging != null && VRConfig.DebugLogging.Value)
            {
                _source?.LogInfo("[debug] " + message);
            }
        }

        /// <summary>Raised for every logged exception (the autotest collects them).</summary>
        public static event Action<string, Exception> ExceptionLogged;

        public static void Exception(string context, Exception e)
        {
            _source?.LogError($"{context}: {e}");
            ExceptionLogged?.Invoke(context, e);
        }

        /// <summary>Logs an exception the first time it is seen in <paramref name="context"/> (same type and message).</summary>
        public static void ExceptionOnce(string context, Exception e)
        {
            bool first;
            // (By the context and the exception's type: with its message, one that names a per-frame value added a key every frame to a set
            // never emptied; util scout, 2026-11-25. The first one's message is still logged.)
            string key = "guard:" + context + ":" + e.GetType().Name;
            lock (Once)
            {
                first = Once.Add(key);
                if (!first)
                {
                    Repeats.TryGetValue(key, out int n);
                    Repeats[key] = n + 1;
                }
            }
            if (first)
            {
                Exception(context, e);
            }
        }

        /// <summary>
        /// How often each exception logged once has come again since the last report. One line stood for every one: the 1.16.11 log showed a
        /// single NullReferenceException where a sound threw every 30 s and stopped what it sounded for each time (2026-11-08).
        /// </summary>
        private static readonly Dictionary<string, int> Repeats = new Dictionary<string, int>();

        /// <summary>Writes how often the exceptions logged once came again (and forgets the counts); nothing when none did. Once a minute, with the frame rate line.</summary>
        public static void ReportRepeats()
        {
            List<KeyValuePair<string, int>> seen;
            lock (Once)
            {
                if (Repeats.Count == 0)
                {
                    return;
                }
                seen = new List<KeyValuePair<string, int>>(Repeats);
                Repeats.Clear();
            }
            foreach (KeyValuePair<string, int> kv in seen)
            {
                Warn($"Thrown again {kv.Value} time(s) since the last report: {kv.Key.Substring("guard:".Length)}");
            }
        }

        /// <summary>Logs a message only the first time a given key is seen (per session).</summary>
        public static void WarnOnce(string key, string message)
        {
            bool first;
            lock (Once)
            {
                first = Once.Add(key); // also called from worker threads (shape measuring)
            }
            if (first)
            {
                Warn(message);
            }
        }

        /// <summary>Whether a once-only message under <paramref name="key"/> went out already (skip building a costly one).</summary>
        public static bool Logged(string key)
        {
            lock (Once)
            {
                return Once.Contains(key);
            }
        }

        public static void InfoOnce(string key, string message)
        {
            bool first;
            lock (Once)
            {
                first = Once.Add(key);
            }
            if (first)
            {
                Info(message);
            }
        }

        /// <summary>
        /// Runs <paramref name="action"/> and logs (once per context) instead of propagating exceptions.
        /// Used around per-frame code so one broken weapon mod cannot spam the log or stop the frame.
        /// </summary>
        public static void Guard(string context, Action action)
        {
            try
            {
                // (Inside the guard: a probe that threw here went past it, out of the frame it was to protect; util scout.)
                if (PerfProbe.Enabled)
                {
                    PerfProbe.Begin(context);
                }
                else
                {
                    FramePhases.SectionBegin(context); // (time only, always: the frame rate line's costliest mod sections)
                }
                action();
            }
            catch (Exception e)
            {
                // Under the lock like every other use of the once-set: worker threads (shape measuring) add to it too, and
                // an unlocked add here could meet one of theirs and break the set.
                ExceptionOnce(context, e);
            }
            finally
            {
                if (PerfProbe.Enabled)
                {
                    PerfProbe.End(context);
                }
                else
                {
                    FramePhases.SectionEnd(context);
                }
            }
        }
    }
}
