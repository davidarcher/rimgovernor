#nullable enable

using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Verse;
using Obs = RimGovernor.Protocol.Observations;

namespace HomeBridge.BridgeTools
{
    // Per-map, per-query last-changed ticks and tombstones behind the
    // changed_since_tick of the zones, buildings and bills list reads
    // (issue #358). The native keeps no per-client state: one tracker per
    // (map, query shape) remembers, for every entity the shape lists, a
    // digest of the row it last emitted and the tick that digest last
    // changed, plus the ids it stopped listing with the tick they went,
    // kept for TombstoneWindow ticks and at most MaxTombstones ids.
    //
    // Change detection is by digest at read time rather than by hooks: a
    // list read projects every entity anyway (the rows are what it
    // emits), so comparing each projected row against the shadow of the
    // last visit costs nothing extra and misses no field the row carries,
    // where a hook list would have to name every game path that can change
    // a zone's settings, a bench's bills or a building's service state.
    // The stamp is the first read that saw the change, at or after the
    // change itself, so an ask since the caller's own last read (which is
    // at or before any change it has not seen) never omits it. A snapshot
    // token's context (the read's tick) is stripped before digesting so a
    // row that says the same thing at a later tick is unchanged.
    //
    // A removed entity is one a complete enumeration no longer lists;
    // reads narrowed by ids, a region, a name or a bench never enumerate
    // completely and refuse the ask. An ask the tombstones cannot answer
    // (older than the window, or than a tombstone the count cap dropped)
    // gets a full reply inline, without as_of_tick, so the caller replaces
    // what it holds in the same round trip (#795).
    internal sealed class EntityTracking
    {
        // One game day: a controller that reviews at least daily always
        // gets a delta.
        internal const int TombstoneWindow = 60000;

        // The count bound on one tracker's tombstones. A building placement
        // retires a blueprint and a frame id, so a heavy building day is a
        // few hundred to a thousand tombstones; 4096 ids of ~40 bytes keep
        // a tracker under ~0.5 MB while leaving that day several times over.
        internal const int MaxTombstones = 4096;

        private static readonly ConditionalWeakTable<Map, Dictionary<string, EntityTracking>> Maps = new ConditionalWeakTable<Map, Dictionary<string, EntityTracking>>();

        private struct Entry { public ulong Digest; public int LastChanged; }

        private readonly Dictionary<string, Entry> entries = new Dictionary<string, Entry>();
        private readonly Dictionary<string, int> removed = new Dictionary<string, int>();
        private int forgotThrough = int.MinValue; // newest tombstone the cap dropped

        // For is the tracker of one query shape on map (the tool name plus
        // the options that shape its rows), created on first use.
        internal static EntityTracking For(Map map, string shape)
        {
            var table = Maps.GetValue(map, _ => new Dictionary<string, EntityTracking>());
            if (!table.TryGetValue(shape, out var tracking)) table[shape] = tracking = new EntityTracking();
            return tracking;
        }

        // Covers reports an ask the tombstones can answer: within the
        // window before now and newer than any tombstone the cap dropped.
        // It enforces the cap first, so the answer holds for this read.
        internal bool Covers(long since)
        {
            if (removed.Count > MaxTombstones)
                foreach (var pair in removed.OrderBy(pair => pair.Value).Take(removed.Count - MaxTombstones).ToList())
                {
                    removed.Remove(pair.Key);
                    if (pair.Value > forgotThrough) forgotThrough = pair.Value;
                }
            return Find.TickManager.TicksGame - since <= TombstoneWindow && since > forgotThrough;
        }

        // Note records row as the entity's current state and returns the
        // tick it last changed: now when the entity is new to the tracker
        // or its row differs from the last visit's, else the earlier stamp.
        internal int Note(string id, IMessage row)
        {
            int now = Find.TickManager.TicksGame;
            var digest = Digest(row);
            removed.Remove(id);
            if (entries.TryGetValue(id, out var entry) && entry.Digest == digest) return entry.LastChanged;
            entries[id] = new Entry { Digest = digest, LastChanged = now };
            return now;
        }

        // Sweep, after a complete enumeration, marks every tracked entity
        // the enumeration did not list as removed now, and forgets the
        // tombstones older than the window (the count cap is Covers').
        internal void Sweep(HashSet<string> listed)
        {
            int now = Find.TickManager.TicksGame;
            foreach (var id in entries.Keys.Where(id => !listed.Contains(id)).ToList())
            {
                entries.Remove(id);
                removed[id] = now;
            }
            foreach (var id in removed.Where(pair => now - pair.Value > TombstoneWindow).Select(pair => pair.Key).ToList()) removed.Remove(id);
        }

        // RemovedSince lists the ids removed at or after since, in id order.
        internal IEnumerable<string> RemovedSince(long since) =>
            removed.Where(pair => pair.Value >= since).Select(pair => pair.Key).OrderBy(id => id, System.StringComparer.Ordinal);

        // Digest is a 64-bit FNV-1a over the row's wire bytes with every
        // snapshot token's context (the read's tick) stripped.
        internal static ulong Digest(IMessage row)
        {
            var copy = row.Descriptor.Parser.ParseFrom(row.ToByteArray());
            Strip(copy);
            ulong hash = 14695981039346656037UL;
            foreach (var b in copy.ToByteArray()) { hash ^= b; hash *= 1099511628211UL; }
            return hash;
        }

        private static void Strip(IMessage message)
        {
            if (message is Obs.SnapshotRef reference) { reference.Context = null; return; }
            foreach (var field in message.Descriptor.Fields.InDeclarationOrder())
            {
                if (field.FieldType != FieldType.Message || field.IsMap) continue;
                if (field.IsRepeated)
                {
                    foreach (var item in (IEnumerable)field.Accessor.GetValue(message)) if (item is IMessage nested) Strip(nested);
                }
                else if (field.Accessor.GetValue(message) is IMessage nested) Strip(nested);
            }
        }
    }
}
