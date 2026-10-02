#nullable enable

using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using RimWorld;
using Verse;

namespace HomeBridge.BridgeTools
{
    /// <summary>
    /// The one resolver from a wire loadId to the live native object (#1339): spawned Things,
    /// the inner Thing of a spawned MinifiedThing, Zones and Bills, indexed per map through
    /// <see cref="LoadIdIndex{T}"/>. Small owned collections (a map's pawns, a pawn's apparel,
    /// areas, quests) resolve through <see cref="ById{T}"/>, which keeps the comparison here.
    /// Main thread only.
    /// </summary>
    internal static class RefIndex
    {
        private sealed class MapIndexes
        {
            internal readonly LoadIdIndex<Thing> Things;
            internal readonly LoadIdIndex<Thing> Minified;
            internal readonly LoadIdIndex<Zone> Zones;
            internal readonly LoadIdIndex<Bill> Bills;

            internal MapIndexes(Map map)
            {
                Things = new LoadIdIndex<Thing>(
                    () => map.listerThings.AllThings.Select(t => Pair(Id(t), t)),
                    Id, t => !t.Destroyed && t.Spawned && t.Map == map);
                Minified = new LoadIdIndex<Thing>(
                    () => map.listerThings.AllThings.OfType<MinifiedThing>().Where(m => m.InnerThing != null).Select(m => Pair(Id(m.InnerThing), m.InnerThing)),
                    Id, t => t.ParentHolder is MinifiedThing m && m.InnerThing == t && !m.Destroyed && m.Spawned && m.Map == map);
                Zones = new LoadIdIndex<Zone>(
                    () => map.zoneManager.AllZones.Select(z => Pair(z.GetUniqueLoadID(), z)),
                    z => z.GetUniqueLoadID(), z => map.zoneManager.AllZones.Contains(z));
                Bills = new LoadIdIndex<Bill>(
                    () => map.listerThings.AllThings.OfType<IBillGiver>().Where(g => g.BillStack != null)
                        .SelectMany(g => g.BillStack.Bills).Select(b => Pair(b.GetUniqueLoadID(), b)),
                    b => b.GetUniqueLoadID(),
                    b => b.billStack?.billGiver is Thing giver && !giver.Destroyed && giver.Spawned && giver.Map == map && b.billStack.Bills.Contains(b));
            }
        }

        private static readonly ConditionalWeakTable<Map, MapIndexes> Indexes = new ConditionalWeakTable<Map, MapIndexes>();

        private static MapIndexes For(Map map) => Indexes.GetValue(map, m => new MapIndexes(m));

        private static KeyValuePair<string, T> Pair<T>(string id, T value) => new KeyValuePair<string, T>(id, value);

        private static string Id(Thing thing) => CombatMirror.LoadId(thing);

        /// <summary>The spawned Thing on <paramref name="map"/> with this loadId: what a scan of
        /// <c>map.listerThings.AllThings</c> found.</summary>
        internal static Thing? Thing(Map? map, string? id) => map == null ? null : For(map).Things.Find(id);

        internal static T? Thing<T>(Map? map, string? id) where T : Thing => Thing(map, id) as T;

        /// <summary>A spawned Thing, else the inner Thing of a spawned MinifiedThing.</summary>
        internal static Thing? ThingOrMinified(Map? map, string? id) =>
            map == null ? null : For(map).Things.Find(id) ?? For(map).Minified.Find(id);

        internal static Zone? Zone(Map? map, string? id) => map == null ? null : For(map).Zones.Find(id);

        internal static T? Zone<T>(Map? map, string? id) where T : Zone => Zone(map, id) as T;

        /// <summary>A bill on a spawned bill giver (workbench or pawn) on <paramref name="map"/>.</summary>
        internal static Bill? Bill(Map? map, string? id) => map == null ? null : For(map).Bills.Find(id);

        /// <summary>The first member of a small collection with this loadId.</summary>
        internal static T? ById<T>(this IEnumerable<T>? source, string? id) where T : class, ILoadReferenceable
        {
            if (source == null || id == null) return null;
            foreach (var value in source)
                if (value != null && Is(value, id)) return value;
            return null;
        }

        /// <summary>Whether <paramref name="value"/> carries this loadId.</summary>
        internal static bool Is(ILoadReferenceable? value, string? id) =>
            value != null && id != null && (value is Thing thing ? Id(thing) : value.GetUniqueLoadID()) == id;
    }
}
