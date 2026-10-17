#nullable enable

using System;
using System.Collections.Generic;
using Clock = RimGovernor.Contracts.Wire.Clock;

namespace HomeBridge.BridgeTools
{
    // The process's clock events, in memory only: nothing keeps a durable
    // copy (the brain reads the ring live and persists no clock events,
    // persistence-contracts.md); native keeps a bounded ring to page. An
    // event's cursor is its position in a plain sequence from 1; the brain is
    // hosted in this process, so no cursor it holds predates the journal.
    // Events overwritten in the ring are skipped, one per scanned position.
    internal sealed class ClockEventJournal
    {
        // Events kept for paging. A reader this far behind has lost the
        // events below the floor.
        internal const int RetainRows = 8192;

        private readonly Clock.ClockEvent[] ring = new Clock.ClockEvent[RetainRows];
        internal long Newest { get; private set; }

        // Lowest cursor whose event is still held, minus one.
        private long Floor => Math.Max(0, Newest - RetainRows);

        internal void Append(Clock.ClockEvent value)
        {
            var cursor = checked(Newest + 1);
            ring[cursor % RetainRows] = value;
            Newest = cursor;
        }

        internal sealed class Window
        {
            internal readonly List<Clock.ClockEvent> Events = new List<Clock.ClockEvent>();
            internal long Next;
        }

        internal Window ReadWindow(long after, int limit)
        {
            if (after < 0 || after > Newest || limit < 1 || limit > 128)
                throw new ArgumentOutOfRangeException(nameof(after));
            var result = new Window { Next = after };
            var floor = Floor;
            for (int scanned = 0; result.Next < Newest && scanned < limit; scanned++)
            {
                result.Next++;
                if (result.Next > floor) result.Events.Add(ring[result.Next % RetainRows]);
            }
            return result;
        }
    }
}
