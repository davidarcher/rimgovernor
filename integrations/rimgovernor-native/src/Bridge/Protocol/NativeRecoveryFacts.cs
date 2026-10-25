using System.Linq;
using Verse;

namespace RimGovernor.Native
{
    internal static class NativeRecoveryFacts
    {
        // Pure census: reading recovery never acquires or repairs area leases. A read that cannot make it fails the
        // colony read.
        internal static RimGovernor.Brain.GameFacts.Observations.RecoverySnapshot Read(Map map, RimGovernor.Brain.GameFacts.Common.ObservationContext context) => new RimGovernor.Brain.GameFacts.Observations.RecoverySnapshot
        {
            // Each building's hit points, fuel and breakdown state is its row in the bundle's building table.
            Buildings = [.. map.listerBuildings.allBuildingsColonist.Where(b => !b.Position.Fogged(map)).OrderBy(b => b.thingIDNumber)
                .Select(b => NativeBuildingObservationTools.Ref(b))],
            Restrictions = [.. map.mapPawns.FreeColonistsSpawned.OrderBy(p => p.thingIDNumber).Select(pawn => new RimGovernor.Brain.GameFacts.Observations.RecoveryRestriction
            {
                Pawn = NativePawnObservationTools.Ref(pawn),
                AreaId = pawn.playerSettings?.AreaRestrictionInPawnCurrentMap?.GetUniqueLoadID(),
            })],
        };
    }
}
