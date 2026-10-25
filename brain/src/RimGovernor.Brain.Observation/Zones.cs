namespace RimGovernor.Brain.Observation;

public interface IZonesNative
{
    Task<O.ZonesSnapshot> ReadZoneSectionAsync(C.Identity identity, CancellationToken cancellationToken);
}

public interface IZonesSource
{
    Task<Held<O.ZonesSnapshot>> ZonesAsync(C.Identity identity, CancellationToken cancellationToken);
}

public static class ZonesReads
{
    private static readonly AsyncLocal<IZonesSource?> ZonesScope = new();

    /// <summary>Attaches <paramref name="source"/> to the current async flow; dispose the result to detach it.</summary>
    public static IDisposable WithZones(IZonesSource source)
    {
        var previous = ZonesScope.Value;
        ZonesScope.Value = source;
        return new ZonesRestore(previous);
    }

    /// <summary>
    /// Serves the scheduler's zone section, or a full census outside a scheduler step (for standalone planners
    /// and inspection).
    /// </summary>
    public static async Task<Held<O.ZonesSnapshot>> ReadZoneSection(IZonesNative? native, C.Identity id, CancellationToken cancellationToken)
    {
        if (ZonesScope.Value is { } zones)
        {
            return await zones.ZonesAsync(id, cancellationToken).ConfigureAwait(false);
        }
        if (native is null)
        {
            throw new BridgeUnavailableException();
        }
        var read = await native.ReadZoneSectionAsync(id, cancellationToken).ConfigureAwait(false);
        return new Held<O.ZonesSnapshot> { Value = read, AsOf = read.Context.Tick, Complete = true, Source = "rimgovernor/observations_list_zones" };
    }

    /// <summary>Supplies the policy facts intentionally omitted by <see cref="DecodeColony"/>.</summary>
    public static async Task<ColonyProjection> FillZones(IZonesNative? native, C.Identity id, Identity expected, ColonyProjection p, CancellationToken cancellationToken)
    {
        var held = await ReadZoneSection(native, id, cancellationToken).ConfigureAwait(false);
        var actual = DecodeReads.ContextIdentity(held.Value.Context) with { Paused = expected.Paused };
        if (!ColonyReads.SameColonyContext(actual, expected))
        {
            throw new ObservationChangedException();
        }
        return ApplyZones(p with { Zones = held }, held.Value);
    }

    private static ColonyProjection ApplyZones(ColonyProjection p, O.ZonesSnapshot read)
    {
        List<O.ZoneState.Growing> farms = [.. read.Zones.OfType<O.ZoneState.Growing>()];
        p = p with { Farms = farms };
        p = p with { Facts = ColonyProductionReads.ZoneProduction(farms, p.Definitions, p.Facts) };
        return p with
        {
            FieldCrops = ColonyProductionReads.ColonyFieldCrops(farms, p.Definitions),
            FoodFields = ColonyProductionReads.ColonyFoodFields(farms, p.Definitions, p.Facts.Calendar, p.Cells),
            FieldCapacityCrops = ColonyProductionReads.ColonyFieldCrops(farms, p.Definitions, true),
        };
    }

    private sealed class ZonesRestore(IZonesSource? previous) : IDisposable
    {
        public void Dispose() => ZonesScope.Value = previous;
    }
}
