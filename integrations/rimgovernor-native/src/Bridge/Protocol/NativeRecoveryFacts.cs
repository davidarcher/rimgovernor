#nullable enable
using System;
using System.Linq;
using System.Collections.Generic;
using RimWorld;
using Verse;
using Common = RimGovernor.Protocol.Common;
using Obs = RimGovernor.Protocol.Observations;

namespace HomeBridge.BridgeTools
{
    internal static class NativeRecoveryFacts
    {
        // Pure census: reading recovery never acquires or repairs area leases.
        internal static Obs.RecoveryReply Read(Map map, Common.ObservationContext context)
        {
            try {
                var buildings = map.listerBuildings.allBuildingsColonist.Where(b => !b.Position.Fogged(map)).OrderBy(b => b.thingIDNumber).ToList();
                var pawns = map.mapPawns.FreeColonistsSpawned.OrderBy(p => p.thingIDNumber).ToList();
                var result = new Obs.RecoverySnapshot { Context = context };
                // Each building's hit points, fuel and breakdown state is its
                // row in the bundle's building table.
                foreach (var building in buildings) result.Buildings.Add(NativeBuildingObservationTools.Ref(building));
                foreach (var pawn in pawns) {
                    var row = new Obs.RecoveryRestriction { Pawn = NativePawnObservationTools.Ref(pawn) };
                    var area = pawn.playerSettings?.AreaRestrictionInPawnCurrentMap;
                    if (area != null) row.AreaId = area.GetUniqueLoadID();
                    result.Restrictions.Add(row);
                }
                return new Obs.RecoveryReply { Observed = result };
            } catch (Exception) {
                return new Obs.RecoveryReply { Unavailable = new Common.Unavailable { Reason = Common.UnavailableReason.ReadFailed,
                    Detail = "Complete bounded recovery census is unavailable." } };
            }
        }
        private static Common.Cell Cell(IntVec3 c) => new Common.Cell { X = c.x, Z = c.z };
        private static Obs.EntityRef Entity(Thing t) => new Obs.EntityRef { Id = t.GetUniqueLoadID(), DefName = t.def.defName, MapId = t.Map.uniqueID, Position = Cell(t.Position) };
    }
}
