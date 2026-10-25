namespace RimGovernor.Brain.Runtime;

public static class RefrigerationReads
{
    /// <summary>The most building ids one typed building read takes.</summary>
    private const int BuildingReadBatch = 256;

    /// <summary>
    /// Assembles the method's observation from a rooms routine reading (rooms with cells, planning definitions, site cells),
    /// the power census's Cooler rows and a typed building read of those exact IDs. A reading without its room census,
    /// solar-flare read or <paramref name="cooler"/> definition raises <see cref="RequiredFactException"/>; so does a cooler
    /// the building read does not return: the planner must not build a second cooler on a fact it cannot see.
    /// </summary>
    public static async Task<RefrigerationObservation> RefrigerationFacts(IRefrigerationSource source, ColonyReading projection, string cooler, CancellationToken cancellationToken)
    {
        var rooms = projection.Facts.Upkeep.Rooms;
        var topology = projection.Facts.EssentialInputs.Power;
        var available = ColonyPlanningRules.DefinitionAvailable(projection.Facts, cooler);
        return new RefrigerationObservation
        {
            Rooms = rooms,
            Coolers = await Coolers(source, projection.Identity, topology, cooler, cancellationToken).ConfigureAwait(false),
            Cells = EquatableArray.From(projection.PlanningWindow.Cells),
            CoolerAvailable = available,
            Blackout = topology.Blackout,
        };
    }

    /// <summary>Joins the power census's <paramref name="cooler"/> rows with typed building reads of those exact IDs, sorted by id.</summary>
    private static async Task<EquatableArray<RefrigerationCooler>> Coolers(IRefrigerationSource source, Identity expected, PowerTopology topology, string cooler, CancellationToken cancellationToken)
    {
        var identity = new RimGovernor.Brain.GameFacts.Common.Identity { ColonyId = expected.Colony.Value, LoadToken = expected.Load.Value, MapId = expected.Map.Value };
        var power = new HashSet<string>(StringComparer.Ordinal);
        foreach (var b in topology.Buildings)
        {
            if (b.Definition == cooler)
            {
                power.Add(b.Id);
            }
        }
        var ids = power.Order(StringComparer.Ordinal).ToList();
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
                if (!power.Contains(row.Building.Id))
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
