namespace RimGovernor.Brain.Observation;

/// <summary>
/// Reads exact cooler buildings so the food-stores/refrigeration Need can see each cooler's rotation, target setpoint and CAS
/// token. Power state comes from the same-tick colony power census instead.
/// </summary>
public interface IRefrigerationSource
{
    Task<O.BuildingsSnapshot> ReadConstructionBuildingsAsync(C.Identity identity, IReadOnlyList<string> ids, CancellationToken cancellationToken);
}

public static class RefrigerationReads
{
    /// <summary>The most building ids one typed building read takes.</summary>
    private const int BuildingReadBatch = 256;

    /// <summary>
    /// Assembles the method's observation from a rooms routine reading (rooms with cells, planning definitions, site cells),
    /// the power census's Cooler rows and a typed building read of those exact IDs. A reading without its room census,
    /// power topology, solar-flare read or <paramref name="cooler"/> definition raises <see cref="RequiredFactException"/>; so does a cooler
    /// the building read does not return: the planner must not build a second cooler on a fact it cannot see.
    /// </summary>
    public static async Task<RefrigerationObservation> RefrigerationFacts(IRefrigerationSource source, ColonyProjection projection, string cooler, CancellationToken cancellationToken)
    {
        if (!projection.Rooms.TryGetValue(out var rooms))
        {
            throw new RequiredFactException("refrigeration rooms");
        }
        if (!projection.PowerPlanning.TryGetValue(out var topology))
        {
            throw new RequiredFactException("refrigeration power topology");
        }
        if (!topology.Blackout.TryGetValue(out var blackout))
        {
            throw new RequiredFactException("refrigeration solar flare");
        }
        if (!projection.DefinitionAvailable(cooler).TryGetValue(out var available))
        {
            throw new RequiredFactException("cooler definition");
        }
        return new RefrigerationObservation
        {
            Rooms = rooms,
            Coolers = await Coolers(source, projection.Identity, topology, cooler, cancellationToken).ConfigureAwait(false),
            Cells = EquatableArray.From(projection.Cells),
            CoolerAvailable = available,
            Blackout = blackout,
        };
    }

    /// <summary>Joins the power census's <paramref name="cooler"/> rows with typed building reads of those exact IDs, sorted by id.</summary>
    private static async Task<EquatableArray<RefrigerationCooler>> Coolers(IRefrigerationSource source, Identity expected, PowerTopology topology, string cooler, CancellationToken cancellationToken)
    {
        var identity = new C.Identity { ColonyId = expected.Colony.Value, LoadToken = expected.Load.Value, MapId = expected.Map.Value };
        var power = new Dictionary<string, PowerSite>(StringComparer.Ordinal);
        foreach (var b in topology.Buildings)
        {
            if (b.Definition == cooler)
            {
                power[b.Id] = b;
            }
        }
        var ids = power.Keys.Order(StringComparer.Ordinal).ToList();
        var coolers = new List<RefrigerationCooler>(ids.Count);
        foreach (var batch in ids.Chunk(BuildingReadBatch))
        {
            var observed = await source.ReadConstructionBuildingsAsync(identity, batch, cancellationToken).ConfigureAwait(false);
            if (observed.Buildings.Count != batch.Length)
            {
                throw new RequiredFactException("refrigeration coolers");
            }
            foreach (var row in observed.Buildings)
            {
                if (!power.TryGetValue(row.Building.Id, out var site))
                {
                    throw new RequiredFactException("refrigeration coolers");
                }
                coolers.Add(new RefrigerationCooler
                {
                    Id = site.Id,
                    Position = site.Cell,
                    Rotation = ConstructionReads.RotationOf(row.Rotation),
                    Target = row.Settings.TargetTemperatureC ?? throw new BridgeContractException($"cooler {site.Id}: the building read carries no target temperature"),
                    Connected = site.Connected,
                    PowerOn = site.Powered,
                });
            }
        }
        coolers.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
        return EquatableArray.From(coolers);
    }
}
