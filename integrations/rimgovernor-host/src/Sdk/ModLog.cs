using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;

namespace RimGovernor.Host.Sdk
{
    public enum ModLogLevel
    {
        Debug,
        Info,
        Warn,
        Error
    }

    /// <summary>
    /// The mod's diagnostic log. A line goes to <see cref="Sink"/> (the host points it at Unity's log as a warning,
    /// never <c>Log.Error</c>, which pauses the game); with no sink it is dropped. A call site (or explicit key) may log
    /// <see cref="RateBurst"/> lines per <see cref="RateWindowMs"/>; the rest are counted into the next line it logs.
    /// A write made while this thread is already inside the helper is dropped, so a sink that logs cannot recurse.
    /// </summary>
    public static class ModLog
    {
        public const int RateBurst = 5;
        public const int RateWindowMs = 10000;
        private const int MaxMessageChars = 2000;
        private const int MaxKeys = 1024;

        private sealed class Site
        {
            public long WindowStartMs;
            public int Count;
            public long Suppressed;
        }

        private static readonly object Gate = new object();
        private static readonly Dictionary<string, Site> Sites = new Dictionary<string, Site>(StringComparer.Ordinal);

        [ThreadStatic] private static bool inside;
        [ThreadStatic] private static string currentTrace;

        /// <summary>The game tick, or -1 when none. Set by the mod; read on whatever thread logs.</summary>
        public static Func<long> TickSource;

        /// <summary>Receives each admitted line (level, component, tick or -1, trace or null, message, lines the rate limit dropped).</summary>
        public static Action<ModLogLevel, string, long, string, string, long> Sink;

        /// <summary>Monotonic milliseconds for the rate limit. Replaceable so a probe needs no wall-clock wait.</summary>
        internal static Func<long> ClockMs = () => Stopwatch.GetTimestamp() * 1000 / Stopwatch.Frequency;

        /// <summary>The trace new lines on this thread carry unless the call names one (set around a main-thread hop).</summary>
        public static string CurrentTrace
        {
            get { return currentTrace; }
            set { currentTrace = value; }
        }

        /// <summary>Lines dropped because the writing thread was already inside the helper.</summary>
        public static long ReentrantDrops;

        public static void Debug(string component, string message, string trace = null, string key = null, [CallerFilePath] string file = null, [CallerLineNumber] int line = 0)
            => Write(ModLogLevel.Debug, component, message, trace, key, file, line);

        public static void Info(string component, string message, string trace = null, string key = null, [CallerFilePath] string file = null, [CallerLineNumber] int line = 0)
            => Write(ModLogLevel.Info, component, message, trace, key, file, line);

        public static void Warn(string component, string message, string trace = null, string key = null, [CallerFilePath] string file = null, [CallerLineNumber] int line = 0)
            => Write(ModLogLevel.Warn, component, message, trace, key, file, line);

        public static void Error(string component, string message, string trace = null, string key = null, [CallerFilePath] string file = null, [CallerLineNumber] int line = 0)
            => Write(ModLogLevel.Error, component, message, trace, key, file, line);

        public static void Write(ModLogLevel level, string component, string message, string trace = null, string key = null, string file = null, int line = 0)
        {
            if (inside)
            {
                Interlocked.Increment(ref ReentrantDrops);
                return;
            }
            inside = true;
            try
            {
                var siteKey = key ?? (component + "@" + (file ?? "") + ":" + line);
                var tick = -1L;
                try { var source = TickSource; if (source != null) tick = source(); }
                catch (Exception) { tick = -1; } // a tick that cannot be read is not worth losing the line
                var text = message ?? "";
                if (text.Length > MaxMessageChars) text = text.Substring(0, MaxMessageChars);
                var now = ClockMs();
                long suppressed;
                lock (Gate)
                {
                    if (!Admit(siteKey, now, out suppressed)) return;
                }
                Sink?.Invoke(level, component ?? "", tick, trace ?? currentTrace, text, suppressed);
            }
            finally { inside = false; }
        }

        private static bool Admit(string key, long now, out long suppressed)
        {
            suppressed = 0;
            if (!Sites.TryGetValue(key, out var site))
            {
                if (Sites.Count >= MaxKeys) Sites.Clear();
                site = new Site { WindowStartMs = now };
                Sites[key] = site;
            }
            if (now - site.WindowStartMs >= RateWindowMs)
            {
                site.WindowStartMs = now;
                site.Count = 0;
            }
            if (site.Count >= RateBurst)
            {
                site.Suppressed++;
                return false;
            }
            site.Count++;
            suppressed = site.Suppressed;
            site.Suppressed = 0;
            return true;
        }

        /// <summary>Runs <paramref name="action"/> with the reentrancy guard held, so logging from inside it is dropped.</summary>
        public static void Guarded(Action action)
        {
            var was = inside;
            inside = true;
            try { action(); }
            finally { inside = was; }
        }

        internal static void ResetForProbe()
        {
            lock (Gate)
            {
                Sites.Clear();
                ReentrantDrops = 0;
            }
        }
    }
}
