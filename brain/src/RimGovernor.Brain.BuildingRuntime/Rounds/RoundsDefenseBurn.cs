namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>The native read of the defense site, which the burn survey needs.</summary>
public interface IBurnFuelSource
{
    (BridgeDefenseSite Site, BridgeResult Result) ReadDefenseSite(C.Identity identity, BridgeCellRect region, CancellationToken ct);
}

public sealed partial class RoundsDefenseLayoutPlanner
{
    internal const string DefenseFuelTier = "fuel";

    internal RoundsDefenseLayoutResult Fuel(CancellationToken call, CancellationToken epoch, StandardState goal, ControlState state, CombatBurn burn)
    {
        var (read, census) = FightCensus(call, state, BurnRegion(burn.Hive));
        if (!PolicyApi.BurnRoofed(burn.Hive, census))
        {
            return new RoundsDefenseLayoutResult { Verdict = BuildingReasonNoDeficit, Tier = DefenseFuelTier };
        }
        var items = _reviewer.ItemFacts(call, state.Snapshot);
        read.Projection.Resources.TryGetValue(out var stock);
        var stone = items.StoneBlock(stock);
        var buildings = PolicyApi.BurnBuilds(burn, census, stone.Value, items);
        if (buildings.Count == 0)
        {
            return new RoundsDefenseLayoutResult { Verdict = BuildingReasonNoDeficit, Tier = DefenseFuelTier };
        }
        return AdmitFightBuilds(call, epoch, goal, state, read, buildings, DefenseFuelTier);
    }

    internal static BridgeCellRect BurnRegion(Cell hive)
    {
        var (lo, hi) = PolicyApi.BurnRegion(hive);
        return new BridgeCellRect { Min = lo, Max = hi };
    }
}

internal static partial class BuildingRuntimePackage
{
    /// <summary>The burn site survey of the active burn; unknown while no burn is active or the site read is older than the tick.</summary>
    internal static Fact<BurnSite> CombatBurnSite(CancellationToken ct, IBurnFuelSource native, C.Identity identity, CombatMemory m, long tick, ItemFacts items)
    {
        if (m.Burn is null || !m.Burn.Active())
        {
            return Unknown<BurnSite>();
        }
        var (site, _) = native.ReadDefenseSite(identity, RoundsDefenseLayoutPlanner.BurnRegion(m.Burn.Hive), ct);
        if (site.Context.Tick < tick)
        {
            return Unknown<BurnSite>();
        }
        return Known(PolicyApi.BurnSurvey(m.Burn, DefenseFightCensus(site), items));
    }
}
