#nullable enable

using System;
using System.Collections.Generic;

namespace HomeBridge.BridgeTools
{
    // The process's clock events, in memory only: the session journal lives
    // in Go's SQLite (persistence-contracts.md), so native keeps a bounded
    // buffer for the controller to page. Cursors start past Base, the
    // process's start time in Unix milliseconds, so a restarted game's
    // cursors exceed any a controller holds from an earlier process. A page
    // reports Base + 1 as its oldest cursor and a read from below Base starts
    // at Base with no loss (those rows died with their process). Rows evicted
    // from the buffer within this process read as lost, one per scanned
    // position.
    internal sealed class ClockEventJournal
    {
        // Rows kept for paging. Startup replays only the newest page, so a
        // controller this far behind has lost the rows below the floor.
        internal const int RetainRows = 8192;

        internal readonly long Base;
        internal long Newest { get; private set; }
        private readonly Dictionary<long, Dictionary<string, object?>> rows = new Dictionary<long, Dictionary<string, object?>>();

        internal ClockEventJournal()
        {
            Base = Math.Max(1L, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            Newest = Base;
        }

        // Lowest cursor whose row is still held, minus one.
        private long Floor { get { return Math.Max(Base, Newest - RetainRows); } }

        internal void Append(Dictionary<string, object?> row)
        {
            var cursor = Convert.ToInt64(row["cursor"]);
            if (cursor != Newest + 1) throw new InvalidOperationException("Native clock cursor is not consecutive.");
            // A copy, immune to later edits of `row`.
            rows[cursor] = new Dictionary<string, object?>(row);
            Newest = cursor;
            rows.Remove(Floor);
        }

        internal sealed class Window
        {
            internal readonly List<Dictionary<string, object?>> Rows = new List<Dictionary<string, object?>>();
            internal long Next;
            internal ulong Lost;
        }

        internal Window ReadWindow(long after, int limit)
        {
            if (after < 0 || after > Newest || limit < 1 || limit > 128)
                throw new ArgumentOutOfRangeException(nameof(after));
            var result = new Window { Next = Math.Max(after, Base) };
            for (int scanned = 0; result.Next < Newest && scanned < limit; scanned++)
            {
                result.Next++;
                if (rows.TryGetValue(result.Next, out var row)) result.Rows.Add(row);
                else result.Lost++;
            }
            return result;
        }
    }
}
