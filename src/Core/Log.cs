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
            if (PerfProbe.Enabled)
            {
                PerfProbe.Begin(context);
            }
            try
            {
                action();
            }
            catch (Exception e)
            {
                if (Once.Add("guard:" + context + ":" + e.GetType().Name + ":" + e.Message))
                {
                    Exception(context, e);
                }
            }
            finally
            {
                if (PerfProbe.Enabled)
                {
                    PerfProbe.End(context);
                }
            }
        }
    }
}
