namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>The native read of the defense site, which the burn survey needs.</summary>
public interface IBurnFuelSource
{
    Task<(BridgeDefenseSite Site, BridgeResult Result)> ReadDefenseSiteAsync(C.Identity identity, BridgeCellRect region, CancellationToken ct);
}

public sealed partial class RoundsDefenseLayoutPlanner
{
    internal const string DefenseFuelTier = "fuel";

    internal async Task<RoundsDefenseLayoutResult> FuelAsync(StandardState goal, ControlState state, CombatBurn burn, CancellationToken call, CancellationToken epoch)
    {
        var (read, census) = await FightCensusAsync(state, BurnRegion(burn.Hive), call).ConfigureAwait(false);
        if (!PolicyApi.BurnRoofed(burn.Hive, census))
        {
            return new RoundsDefenseLayoutResult { Verdict = BuildingReasonNoDeficit, Tier = DefenseFuelTier };
        }
        var items = await _reviewer.ItemFactsAsync(state.Snapshot, call).ConfigureAwait(false);
        read.Projection.Resources.TryGetValue(out var stock);
        var stone = items.StoneBlock(stock);
        var buildings = PolicyApi.BurnBuilds(burn, census, stone.Value, items);
        if (buildings.Count == 0)
        {
            return new RoundsDefenseLayoutResult { Verdict = BuildingReasonNoDeficit, Tier = DefenseFuelTier };
        }
        return await AdmitFightBuildsAsync(goal, state, read, buildings, DefenseFuelTier, call, epoch).ConfigureAwait(false);
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
    internal static async Task<Fact<BurnSite>> CombatBurnSiteAsync(IBurnFuelSource native, C.Identity identity, CombatMemory m, long tick, ItemFacts items, CancellationToken ct)
    {
        if (m.Burn is null || !m.Burn.Active())
        {
            return Unknown<BurnSite>();
        }
        var (site, _) = await native.ReadDefenseSiteAsync(identity, RoundsDefenseLayoutPlanner.BurnRegion(m.Burn.Hive), ct).ConfigureAwait(false);
        if ((site.Context?.Tick ?? 0) < tick)
        {
            return Unknown<BurnSite>();
        }
        return Known(PolicyApi.BurnSurvey(m.Burn, DefenseFightCensus(site), items));
    }
}
