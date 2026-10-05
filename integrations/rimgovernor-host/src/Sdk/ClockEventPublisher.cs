using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace RimGovernor.Host.Sdk
{
    /// <summary>
    /// Announces clock journal advances on the <see cref="Channel"/> GABP channel (#2070). The
    /// journal stays the source of truth: an event says "the journal now ends at cursor N" and
    /// carries no rows, so the controller reads the page after its own cursor with an unheld
    /// clock_read_events and gap/loss evidence is exactly what that read proves. Advances are
    /// coalesced (one frame carries the newest cursor seen), the journal writer never waits on the
    /// socket (one background thread emits), and a new subscriber is told the current newest cursor
    /// at once, so a (re)connect always ends in a tail read.
    /// </summary>
    public sealed class ClockEventPublisher
    {
        public const string Channel = "rimgovernor.clock";
        public const string Description = "Clock journal advances: {type:advance,newest:<cursor>}; read the page with rimgovernor/clock_read_events.";

        private static readonly object Gate = new object();
        private static long newest = -1;
        private static bool signalled;

        /// <summary>
        /// Called by the journal's only writer after each appended row, on any thread. Never blocks
        /// on the connection.
        /// </summary>
        public static void Advanced(long newestCursor)
        {
            lock (Gate)
            {
                newest = newestCursor;
                signalled = true;
                Monitor.Pulse(Gate);
            }
        }

        /// <summary>Wakes the publisher without a new row (a subscriber arrived).</summary>
        public static void Poke()
        {
            lock (Gate)
            {
                signalled = true;
                Monitor.Pulse(Gate);
            }
        }

        private readonly Func<string, object, DateTimeOffset?, Task> emit;
        private readonly Func<bool> hasSubscribers;
        private Thread thread;
        private volatile bool stopping;
        private long announced = -1;

        public ClockEventPublisher(Func<string, object, DateTimeOffset?, Task> emit, Func<bool> hasSubscribers)
        {
            this.emit = emit ?? throw new ArgumentNullException(nameof(emit));
            this.hasSubscribers = hasSubscribers ?? throw new ArgumentNullException(nameof(hasSubscribers));
        }

        public void Start()
        {
            if (thread != null) return;
            thread = new Thread(Run) { IsBackground = true, Name = "RimGovernor.ClockEvents" };
            thread.Start();
        }

        public void Stop()
        {
            stopping = true;
            Poke();
        }

        /// <summary>A subscriber arrived: re-announce the current newest cursor even if it was announced before.</summary>
        public void Resubscribed()
        {
            lock (Gate)
            {
                announced = -1;
                signalled = true;
                Monitor.Pulse(Gate);
            }
        }

        private void Run()
        {
            while (!stopping)
            {
                lock (Gate)
                {
                    if (!signalled) Monitor.Wait(Gate, 1000);
                    signalled = false;
                }
                try { PumpOnce(); }
                catch (Exception) { /* a failed send is covered by the controller's bounded re-read */ }
            }
        }

        /// <summary>Announces the newest cursor if it moved and anyone is subscribed. Returns the events sent.</summary>
        internal int PumpOnce()
        {
            long current;
            lock (Gate) current = newest;
            if (current < 0 || current == announced || !hasSubscribers()) return 0;
            var payload = new Dictionary<string, object>
            {
                ["type"] = "advance",
                ["newest"] = current,
                ["at_unix_ms"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };
            try { emit(Channel, payload, DateTimeOffset.UtcNow).GetAwaiter().GetResult(); }
            catch (Exception) { return 0; }
            announced = current;
            return 1;
        }
    }
}
