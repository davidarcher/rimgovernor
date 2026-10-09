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
    /// One diagnostic line from the mod. <see cref="ModLog"/> keeps them in a bounded ring until the
    /// host publishes them on the <c>rimgovernor.log</c> GABP channel and the controller records each
    /// as a flight row.
    /// </summary>
    public sealed class ModLogEntry
    {
        public long Seq;
        public ModLogLevel Level;
        public string Component;
        /// <summary>Game tick when written, -1 when no game was running.</summary>
        public long Tick = -1;
        /// <summary>Controller trace "&lt;trace_id&gt;/&lt;span_id&gt;", or null.</summary>
        public string Trace;
        public string Message;
        public long AtUnixMs;
        /// <summary>Written while nothing was subscribed: held in the ring and replayed on subscribe.</summary>
        public bool Late;
        /// <summary>Lines from the same call site the rate limit dropped since this site last logged.</summary>
        public long Suppressed;
    }

    /// <summary>
    /// The mod's diagnostic log. <c>flight.jsonl</c> is the only log, so the helper never writes to
    /// Unity's log and never uses <c>Log.Error</c> (RimWorld pauses the game on it). A call is a lock,
    /// a ring write and a pulse: it never touches the socket and never waits on the publisher, so it is
    /// safe on the game thread.
    /// <list type="bullet">
    /// <item>The ring holds <see cref="RingCapacity"/> unpublished entries. While nothing is subscribed
    /// they wait (and publish as late); on overflow the oldest are dropped and counted, and the
    /// publisher reports the count once as an overflow summary.</item>
    /// <item>A call site (or explicit key) may log <see cref="RateBurst"/> lines per
    /// <see cref="RateWindowMs"/>; the rest are counted into the next line it logs.</item>
    /// <item>A write made while this thread is already inside the helper or the publisher is dropped,
    /// so a sink that logs cannot recurse.</item>
    /// </list>
    /// </summary>
    public static class ModLog
    {
        public const int RingCapacity = 512;
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
        private static readonly Queue<ModLogEntry> Ring = new Queue<ModLogEntry>();
        private static readonly Dictionary<string, Site> Sites = new Dictionary<string, Site>(StringComparer.Ordinal);
        private static long nextSeq;
        private static long dropped;
        private static bool subscribed;
        private static bool signalled;

        [ThreadStatic] private static bool inside;
        [ThreadStatic] private static string currentTrace;

        /// <summary>The game tick, or -1 when none. Set by the mod; read on whatever thread logs.</summary>
        public static Func<long> TickSource;

        /// <summary>Monotonic milliseconds for the rate limit. Replaceable so a probe needs no wall-clock wait.</summary>
        internal static Func<long> ClockMs = () => Stopwatch.GetTimestamp() * 1000 / Stopwatch.Frequency;

        /// <summary>Wall-clock Unix milliseconds stamped on each entry.</summary>
        internal static Func<long> UnixMs = () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

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
                lock (Gate)
                {
                    if (!Admit(siteKey, now, out var suppressed)) return;
                    var entry = new ModLogEntry
                    {
                        Seq = ++nextSeq,
                        Level = level,
                        Component = component ?? "",
                        Tick = tick,
                        Trace = trace ?? currentTrace,
                        Message = text,
                        AtUnixMs = UnixMs(),
                        Late = !subscribed,
                        Suppressed = suppressed
                    };
                    if (Ring.Count >= RingCapacity)
                    {
                        Ring.Dequeue();
                        dropped++;
                    }
                    Ring.Enqueue(entry);
                    signalled = true;
                    Monitor.Pulse(Gate);
                }
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

        /// <summary>Marks whether a subscriber exists, so entries written meanwhile are late, and wakes the publisher.</summary>
        public static void SetSubscribed(bool value)
        {
            lock (Gate)
            {
                subscribed = value;
                signalled = true;
                Monitor.Pulse(Gate);
            }
        }

        /// <summary>Wakes <see cref="Wait"/> without a new entry (a subscriber arrived).</summary>
        public static void Poke()
        {
            lock (Gate)
            {
                signalled = true;
                Monitor.Pulse(Gate);
            }
        }

        /// <summary>Blocks the publisher until an entry or a poke arrives, or the timeout passes.</summary>
        public static void Wait(int timeoutMs)
        {
            lock (Gate)
            {
                if (!signalled) Monitor.Wait(Gate, timeoutMs);
                signalled = false;
            }
        }

        /// <summary>
        /// Takes every pending entry in order. With no subscriber nothing is taken: the entries stay
        /// in the ring (late). <paramref name="overflow"/> is the number of entries dropped since the
        /// last drain and <paramref name="overflowSeq"/> the sequence number the summary takes.
        /// </summary>
        public static List<ModLogEntry> Drain(bool hasSubscribers, out long overflow, out long overflowSeq, out long overflowTick, out long overflowAtUnixMs)
        {
            lock (Gate)
            {
                subscribed = hasSubscribers;
                overflow = 0;
                overflowSeq = 0;
                overflowTick = -1;
                overflowAtUnixMs = 0;
                if (!hasSubscribers) return new List<ModLogEntry>();
                var batch = new List<ModLogEntry>(Ring);
                Ring.Clear();
                if (dropped > 0)
                {
                    overflow = dropped;
                    dropped = 0;
                    // The summary sits where the dropped lines were: just before the oldest survivor
                    // (an overflow means the ring was full, so a survivor exists).
                    overflowSeq = batch[0].Seq - 1;
                    overflowTick = batch[0].Tick;
                    overflowAtUnixMs = batch[0].AtUnixMs;
                }
                return batch;
            }
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
                Ring.Clear();
                Sites.Clear();
                nextSeq = 0;
                dropped = 0;
                subscribed = false;
                signalled = false;
                ReentrantDrops = 0;
            }
        }
    }
}
