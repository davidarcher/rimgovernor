namespace RimGovernor.Brain.Observation;

public interface IZonesNative
{
    Task<(ZonesRead Read, Result Receipt)> ReadZoneSectionAsync(C.Identity identity, CancellationToken cancellationToken);
}

public interface IZonesSource
{
    Task<Held<ZonesRead>> ZonesAsync(C.Identity identity, CancellationToken cancellationToken);
}

public static partial class ObservationPackage
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
    public static async Task<Held<ZonesRead>> ReadZoneSection(IZonesNative? native, C.Identity id, CancellationToken cancellationToken)
    {
        if (ZonesScope.Value is { } zones)
        {
            return await zones.ZonesAsync(id, cancellationToken).ConfigureAwait(false);
        }
        if (native is null)
        {
            throw new BridgeUnavailableException();
        }
        var (read, _) = await native.ReadZoneSectionAsync(id, cancellationToken).ConfigureAwait(false);
        return new Held<ZonesRead> { Value = read, AsOf = read.AsOf, Complete = true, Source = "rimgovernor/observations_list_zones" };
    }

    /// <summary>Supplies the policy facts intentionally omitted by <see cref="DecodeColony"/>.</summary>
    public static async Task<ColonyProjection> FillZones(IZonesNative? native, C.Identity id, Identity expected, ColonyProjection p, CancellationToken cancellationToken)
    {
        Held<ZonesRead> held;
        try
        {
            held = await ReadZoneSection(native, id, cancellationToken).ConfigureAwait(false);
        }
        catch (BridgeUnavailableException)
        {
            return p;
        }
        var actual = ContextIdentity(held.Value.Context) with { Paused = expected.Paused };
        if (!SameColonyContext(actual, expected))
        {
            throw new ObservationChangedException();
        }
        return ApplyZones(p with { Zones = held }, held.Value);
    }

    private static ColonyProjection ApplyZones(ColonyProjection p, ZonesRead read)
    {
        var farms = new List<O.FarmFacts>();
        var zoneFacts = new List<FarmZoneFact>();
        foreach (var row in read.Rows)
        {
            if (row.Farm is { } farm)
            {
                farms.Add(farm);
                zoneFacts.Add(new FarmZoneFact { ID = row.Id, Crop = farm.Crop, UsableCells = Optional(farm.HasUsableCells, farm.UsableCells) });
            }
        }
        p = p with { Farms = zoneFacts };
        p = p with { Facts = ZoneProduction(farms, p.Definitions, p.Facts) };
        return p with
        {
            FieldCrops = ColonyFieldCrops(farms, p.Definitions),
            FoodFields = ColonyFoodFields(farms, p.Definitions, p.Facts.Calendar, p.Cells),
            FieldCapacityCrops = ColonyFieldCrops(farms, p.Definitions, true),
        };
    }

    private sealed class ZonesRestore(IZonesSource? previous) : IDisposable
    {
        public void Dispose() => ZonesScope.Value = previous;
    }
}
