using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace RimGovernor.Host.Sdk
{
    /// <summary>
    /// Publishes <see cref="ModLog"/> entries on the <see cref="Channel"/> GABP channel from one
    /// background thread, so a log call never waits on the socket. The host supplies the emit and
    /// subscriber-count delegates (the vendored event manager). Entries written while nothing is
    /// subscribed stay in the ring and go out, in order, with <c>late=true</c> when a subscriber
    /// arrives; entries dropped by a full ring go out first as one overflow summary.
    /// </summary>
    public sealed class ModLogPublisher
    {
        public const string Channel = "rimgovernor.log";
        public const string Description = "Mod diagnostics: ring-buffered, replayed on subscribe with late=true.";

        private readonly Func<string, object, DateTimeOffset?, Task> emit;
        private readonly Func<bool> hasSubscribers;
        private Thread thread;
        private volatile bool stopping;

        public ModLogPublisher(Func<string, object, DateTimeOffset?, Task> emit, Func<bool> hasSubscribers)
        {
            this.emit = emit ?? throw new ArgumentNullException(nameof(emit));
            this.hasSubscribers = hasSubscribers ?? throw new ArgumentNullException(nameof(hasSubscribers));
        }

        public void Start()
        {
            if (thread != null) return;
            thread = new Thread(Run) { IsBackground = true, Name = "RimGovernor.ModLog" };
            thread.Start();
        }

        public void Stop()
        {
            stopping = true;
            ModLog.Poke();
        }

        private void Run()
        {
            while (!stopping)
            {
                ModLog.Wait(1000);
                try { PumpOnce(); }
                catch (Exception) { /* a failed send loses that batch; the next pass continues */ }
            }
        }

        /// <summary>Publishes what is pending if anyone is subscribed. Returns the number of events sent.</summary>
        internal int PumpOnce()
        {
            var sent = 0;
            ModLog.Guarded(() =>
            {
                var batch = ModLog.Drain(hasSubscribers(), out var overflow, out var overflowSeq, out var overflowTick, out var overflowAt);
                if (overflow > 0)
                {
                    Send(new Dictionary<string, object>
                    {
                        ["type"] = "overflow",
                        ["seq"] = overflowSeq,
                        ["dropped"] = overflow,
                        ["tick"] = overflowTick < 0 ? (object)null : overflowTick,
                        ["at_unix_ms"] = overflowAt
                    }, overflowAt);
                    sent++;
                }
                foreach (var entry in batch)
                {
                    Send(ToPayload(entry), entry.AtUnixMs);
                    sent++;
                }
            });
            return sent;
        }

        private void Send(object payload, long atUnixMs)
        {
            try { emit(Channel, payload, DateTimeOffset.FromUnixTimeMilliseconds(atUnixMs)).GetAwaiter().GetResult(); }
            catch (Exception) { /* the connection went away mid-batch; those lines are not replayed */ }
        }

        internal static Dictionary<string, object> ToPayload(ModLogEntry entry)
        {
            var payload = new Dictionary<string, object>
            {
                ["type"] = "log",
                ["seq"] = entry.Seq,
                ["level"] = entry.Level.ToString().ToLowerInvariant(),
                ["component"] = entry.Component,
                ["tick"] = entry.Tick < 0 ? (object)null : entry.Tick,
                ["trace"] = entry.Trace,
                ["msg"] = entry.Message,
                ["at_unix_ms"] = entry.AtUnixMs,
                ["late"] = entry.Late
            };
            if (entry.Suppressed > 0) payload["suppressed"] = entry.Suppressed;
            return payload;
        }
    }
}
