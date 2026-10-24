#nullable enable
using System.Linq;
using Verse;
using Common = RimGovernor.Contracts.Wire.Common;
using Obs = RimGovernor.Contracts.Wire.Observations;

namespace HomeBridge.BridgeTools
{
    internal static class NativeRecoveryFacts
    {
        // Pure census: reading recovery never acquires or repairs area leases. A read that cannot make it fails the
        // colony read.
        internal static Obs.RecoverySnapshot Read(Map map, Common.ObservationContext context) => new Obs.RecoverySnapshot
        {
            // Each building's hit points, fuel and breakdown state is its row in the bundle's building table.
            Buildings = [.. map.listerBuildings.allBuildingsColonist.Where(b => !b.Position.Fogged(map)).OrderBy(b => b.thingIDNumber)
                .Select(b => NativeBuildingObservationTools.Ref(b))],
            Restrictions = [.. map.mapPawns.FreeColonistsSpawned.OrderBy(p => p.thingIDNumber).Select(pawn => new Obs.RecoveryRestriction
            {
                Pawn = NativePawnObservationTools.Ref(pawn),
                AreaId = pawn.playerSettings?.AreaRestrictionInPawnCurrentMap?.GetUniqueLoadID(),
            })],
        };
    }
}
