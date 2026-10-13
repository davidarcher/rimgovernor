namespace RimGovernor.Brain.Observation;

/// <summary>
/// Reads exact cooler buildings so MaintainRefrigeration can see each cooler's rotation, target setpoint and CAS
/// token. Power state comes from the same-tick colony power census instead.
/// </summary>
public interface IRefrigerationSource
{
    Task<(O.ListBuildingsReply Reply, Result Receipt)> ReadConstructionBuildingsAsync(C.Identity identity, IReadOnlyList<string> ids, CancellationToken cancellationToken);
}

public static partial class ObservationPackage
{
    /// <summary>
    /// Joins the colony power census's Cooler rows with a typed building read of those exact IDs. A cooler the
    /// building read does not return, or returns without temperature control, makes the census unknown: the
    /// planner must not build a second cooler on a fact it cannot see.
    /// </summary>
    public static async Task<(Fact<EquatableArray<RefrigerationCooler>> Coolers, Result? Receipt)> ReadRefrigerationCoolers(
        IRefrigerationSource source, Identity expected, Fact<PowerTopology> topology, CancellationToken cancellationToken)
    {
        var unknown = Unknown<EquatableArray<RefrigerationCooler>>();
        var identity = new C.Identity { ColonyId = expected.Colony.Value, LoadToken = expected.Load.Value, MapId = expected.Map.Value };
        if (!topology.TryGetValue(out var v))
        {
            return (unknown, null);
        }
        var rows = new Dictionary<string, RefrigerationCooler>(StringComparer.Ordinal);
        var ids = new List<string>();
        foreach (var b in v.Buildings)
        {
            if (b.Definition != "Cooler")
            {
                continue;
            }
            rows[b.Id] = new RefrigerationCooler { Id = b.Id, Position = b.Cell, Connected = b.Connected, PowerOn = b.Powered };
            ids.Add(b.Id);
        }
        if (ids.Count == 0)
        {
            return (Known(EquatableArray.From(new List<RefrigerationCooler>())), null);
        }
        ids.Sort(StringComparer.Ordinal);
        if (ids.Count > 256)
        {
            return (unknown, null);
        }
        var (reply, receipt) = await source.ReadConstructionBuildingsAsync(identity, ids, cancellationToken).ConfigureAwait(false);
        var observed = reply?.Outcome?.Observed;
        if (observed is null || observed.Buildings.Count != ids.Count)
        {
            return (unknown, receipt);
        }
        var coolers = new List<RefrigerationCooler>(ids.Count);
        foreach (var row in observed.Buildings)
        {
            if (!rows.TryGetValue(row.Building?.Id ?? "", out var cooler))
            {
                return (unknown, receipt);
            }
            cooler = cooler with { Rotation = RotationOf(row.Rotation.GetValueOrDefault()) };
            if (row.Settings is { } settings)
            {
                cooler = cooler with { Target = Optional(settings.TargetTemperatureC is not null, settings.TargetTemperatureC.GetValueOrDefault()) };
            }
            coolers.Add(cooler);
        }
        coolers.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
        return (Known(EquatableArray.From(coolers)), receipt);
    }

    /// <summary>Assembles the method's observation from a rooms routine reading (rooms with cells, planning definitions, site cells) and the cooler census.</summary>
    public static Fact<RefrigerationObservation> RefrigerationFacts(ColonyProjection projection, Fact<EquatableArray<RefrigerationCooler>> coolers)
    {
        if (!projection.Rooms.TryGetValue(out var temperature) || !coolers.TryGetValue(out var rows))
        {
            return Unknown<RefrigerationObservation>();
        }
        var result = new RefrigerationObservation
        {
            Rooms = temperature.Rooms,
            Coolers = rows,
            Cells = EquatableArray.From(projection.Cells),
            CoolerAvailable = Unknown<bool>(),
            Blackout = Unknown<bool>(),
        };
        if (projection.PowerPlanning.TryGetValue(out var topology))
        {
            result = result with { Blackout = topology.Blackout };
        }
        if (projection.LayoutPlan.TryGetValue(out var plan))
        {
            if (projection.TechTier.TryGetValue(out var tier) && tier >= TechTier.Masonry)
            {
                result = result with { Planned = EquatableArray.From(PolicyFunctions.PlannedCoolerSites(plan)) };
            }
        }
        foreach (var d in projection.Definitions)
        {
            if (d.Name == "Cooler")
            {
                result = result with { CoolerAvailable = d.Available };
            }
        }
        return Known(result);
    }
}
