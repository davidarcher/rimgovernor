namespace RimGovernor.Brain.Runtime;

public static class RefrigerationReads
{
    /// <summary>The most building ids one typed building read takes.</summary>
    private const int BuildingReadBatch = 256;

    /// <summary>
    /// Assembles the method's observation from a rooms routine reading (rooms with cells, planning definitions, site cells),
    /// the power census's supported wall-cooler rows and a typed building read of those exact IDs. Missing required
    /// candidate facts raise <see cref="RequiredFactException"/>; so does a cooler
    /// the building read does not return: the planner must not build a second cooler on a fact it cannot see.
    /// </summary>
    public static async Task<RefrigerationObservation> RefrigerationFacts(IRefrigerationSource source, ColonyReading projection, CancellationToken cancellationToken)
    {
        var rooms = projection.Facts.Upkeep.Rooms;
        var topology = projection.Facts.EssentialInputs.Power;
        var candidates = RefrigerationRules.CoolerDefinitions(projection.Facts.Stations);
        return new RefrigerationObservation
        {
            Rooms = rooms,
            Coolers = await Coolers(source, projection.Identity, topology, candidates.Select(candidate => candidate.Name).ToHashSet(StringComparer.Ordinal), cancellationToken).ConfigureAwait(false),
            Cells = EquatableArray.From(projection.PlanningWindow.Cells),
            Candidates = candidates, Definitions = projection.Facts.Definitions, Builders = projection.Facts.ForecastObserved.WorkPawns.Pawns,
            Blackout = topology.Blackout,
        };
    }

    /// <summary>Joins supported cooler rows with typed building reads of those exact IDs, sorted by id.</summary>
    private static async Task<EquatableArray<RefrigerationCooler>> Coolers(IRefrigerationSource source, Identity expected, PowerTopology topology, HashSet<string> definitions, CancellationToken cancellationToken)
    {
        var identity = new RimGovernor.Brain.GameFacts.Common.Identity { ColonyId = expected.Colony.Value, LoadToken = expected.Load.Value, MapId = expected.Map.Value };
        var power = new HashSet<string>(StringComparer.Ordinal);
        foreach (var b in topology.Buildings)
        {
            if (definitions.Contains(b.Definition))
            {
                power.Add(b.Id);
            }
        }
        var ids = power.Order(StringComparer.Ordinal).ToList();
        var coolers = new List<RefrigerationCooler>(ids.Count);
        foreach (var batch in ids.Chunk(BuildingReadBatch))
        {
            var observed = await source.ReadConstructionBuildingsAsync(identity, batch, cancellationToken).ConfigureAwait(false);
            if (observed.Buildings.Count != batch.Length || observed.Buildings.Select(row => row.Building.Id).Distinct(StringComparer.Ordinal).Count() != batch.Length)
            {
                throw new RequiredFactException("refrigeration coolers");
            }
            foreach (var row in observed.Buildings)
            {
                if (!batch.Contains(row.Building.Id, StringComparer.Ordinal) || !definitions.Contains(row.Building.DefName))
                {
                    throw new RequiredFactException("refrigeration coolers");
                }
                coolers.Add(new RefrigerationCooler { Observed = row });
            }
        }
        coolers.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
        return EquatableArray.From(coolers);
    }
}
