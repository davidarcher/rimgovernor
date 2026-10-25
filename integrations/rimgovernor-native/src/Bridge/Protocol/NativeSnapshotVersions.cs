using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Common = RimGovernor.Contracts.Wire.Common;

namespace HomeBridge.BridgeTools
{
    // Per-row snapshot tokens: one monotonically increasing version per
    // (kind, world, entity). A read serializes exactly the fields its row's
    // writes guard; the version moves when those bytes differ from the last
    // read of the same row, so an unchanged row keeps its token and a write
    // based on an older read (a token the row has since moved past) is
    // refused. The counter is process-wide and never reused, so a token from
    // an earlier load, an evicted row or another entity never matches.
    //
    // Rows not read for RetainRows reads are dropped; their next read starts
    // a new version, which only refuses an outstanding older token (the safe
    // direction). Kept free of game types so the contract probes pin it.
    internal static class NativeSnapshotVersions
    {
        internal const int RetainRows = 1 << 17;

        private sealed class Row
        {
            internal byte[] Fields = Array.Empty<byte>();
            internal long Version;
            internal long Read;
        }

        private static readonly object Gate = new object();
        private static readonly Dictionary<string, Row> Rows = new Dictionary<string, Row>(StringComparer.Ordinal);
        private static long _version;
        private static long _reads;

        internal static string Token(string kind, Common.Identity? identity, string entityId, Action<BinaryWriter> write)
        {
            byte[] fields;
            using (var stream = new MemoryStream())
            {
                using (var writer = new BinaryWriter(stream, Encoding.UTF8, true)) write(writer);
                fields = stream.ToArray();
            }
            var key = string.Concat(kind, "\n", identity?.ColonyId ?? "", "\n", identity?.LoadToken ?? "", "\n",
                (identity?.MapId ?? -1).ToString(CultureInfo.InvariantCulture), "\n", entityId);
            lock (Gate)
            {
                _reads++;
                if (!Rows.TryGetValue(key, out var row))
                {
                    if (Rows.Count >= 2 * RetainRows) Sweep();
                    Rows[key] = row = new Row { Fields = fields, Version = ++_version };
                }
                else if (!row.Fields.AsSpan().SequenceEqual(fields)) { row.Fields = fields; row.Version = ++_version; }
                row.Read = _reads;
                return kind + "-" + row.Version.ToString(CultureInfo.InvariantCulture);
            }
        }

        // A row whose guarded fields are one text value.
        internal static string Token(string kind, Common.Identity? identity, string entityId, string fields) =>
            Token(kind, identity, entityId, w => w.Write(fields));

        private static void Sweep()
        {
            var floor = _reads - RetainRows;
            foreach (var stale in Rows.Where(entry => entry.Value.Read <= floor).Select(entry => entry.Key).ToList()) Rows.Remove(stale);
        }
    }
}
