using System.Linq;
using RimWorld;
using Verse;

namespace RimGovernor.Native;

/// <summary>Native currency sets. Current traders own reachability; future visits condition on gaining access.</summary>
internal static class NativeTradeSilver
{
    internal static long ForTrader(ITrader trader, Pawn negotiator) => trader.ColonyThingsWillingToBuy(negotiator)
        .Where(t => t != null && !t.Destroyed && t.def == ThingDefOf.Silver).Sum(t => (long)t.stackCount);

    internal static long Ground(Map map)
    {
        // Pawn_TraderTracker.ColonyThingsWillingToBuy (1.7, confirmed with ilspycmd):
        // the map item branch requires sellability, no fog, and Home or storage. Its
        // separate IHaulSource branch yields held items. A future trader has no position
        // yet; this forecast conditions on access, never promises future reachability.
        var items = map.listerThings.ThingsOfDef(ThingDefOf.Silver)
            .Where(t => TradeUtility.PlayerSellableNow(t, null) && !t.Position.Fogged(map)
                && (map.areaManager.Home[t.Position] || t.IsInAnyStorage()));
        var held = map.listerBuildings.AllColonistBuildingsOfType<IHaulSource>()
            .SelectMany(source => source.GetDirectlyHeldThings()).Where(t => t.def == ThingDefOf.Silver);
        return items.Concat(held).Sum(t => (long)t.stackCount);
    }
}
