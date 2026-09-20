#nullable enable
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace HomeBridge.BridgeTools
{
    // Save-persistent ownership of drills the controller built (#538). A record
    // is written once when a typed construction completes into a deep drill and
    // is evidence of identity only: removal still needs a fresh depletion read
    // and the ordinary deconstruction guards.
    internal static class NativeDrillOwnership
    {
        private const int Limit = 256;

        internal static bool IsDrill(Thing? thing) => thing is Building building && building.TryGetComp<CompDeepDrill>() != null;

        internal static void Register(Thing thing)
        {
            if (!IsDrill(thing) || thing.Map == null || Current.Game == null || thing.Faction != Faction.OfPlayerSilentFail) return;
            var drills = MiningGuard.State().BuiltDrills;
            if (drills.Any(r => r.MapId == thing.Map.uniqueID && r.ThingId == thing.ThingID) || drills.Count >= Limit) return;
            drills.Add(new BuiltDrillRecord { ThingId = thing.ThingID, Definition = thing.def.defName, MapId = thing.Map.uniqueID,
                X = thing.Position.x, Z = thing.Position.z, Built = Find.TickManager.TicksGame });
        }

        internal static bool Owned(Building building) => building.Spawned && MiningGuard.State().BuiltDrills.Any(r =>
            r.MapId == building.Map.uniqueID && r.ThingId == building.ThingID && r.Definition == building.def.defName
            && r.X == building.Position.x && r.Z == building.Position.z);

        // Records whose drill is gone from the map (deconstructed, destroyed or
        // moved) are dropped so the bounded list never fills with history.
        internal static void Prune(Map map)
        {
            var present = new HashSet<string>(map.listerBuildings.allBuildingsColonist.Where(b => b.Spawned && IsDrill(b))
                .Select(b => Key(b.ThingID, b.def.defName, b.Position.x, b.Position.z)));
            MiningGuard.State().BuiltDrills.RemoveAll(r => r.MapId == map.uniqueID && !present.Contains(Key(r.ThingId, r.Definition, r.X, r.Z)));
        }

        private static string Key(string id, string definition, int x, int z) => id + "|" + definition + "|" + x + "|" + z;
    }
}
