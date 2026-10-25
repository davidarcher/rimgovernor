using RimGovernor.Brain.GameFacts.Observations;

namespace RimGovernor.Brain.Policy;

public static class FreezerPrerequisites
{
    /// <summary>The freezer's cooler and missing power infrastructure are its own owed sites, never activity roots.
    /// One next researched planned generator is assumed to cover one cooler (decision R5).</summary>
    public static IReadOnlyDictionary<int, EquatableArray<PlannedBuildSite>> Of(
        ReviewFacts facts, CoupledThermalRead thermal)
    {
        var output = new Dictionary<int, EquatableArray<PlannedBuildSite>>();
        var sources = thermal.Sources.ToArray();
        var assignedGenerators = new HashSet<Cell>();
        var layout = facts.LayoutPlan;
        if (layout is null) return output;
        var construction = facts.CurrentConstruction;
        bool Built(string definition, Cell cell) => construction.Buildings.Any(b => b.Building.Definition == definition && b.Building.Cell == cell);
        PlannedBuildSite Site(string id, int room, string definition, Cell cell, Rotation rotation)
        {
            var d = facts.Definitions.FirstOrDefault(d => d.Name == definition)
                ?? throw new RequiredFactException("freezer prerequisite definition:" + definition);
            var price = d.FixedConstruction ?? throw new RequiredFactException("freezer prerequisite construction:" + definition);
            var existing = construction.Sites.FirstOrDefault(s => s.Observed.Building.DefName == definition && s.Observed.Cell == cell);
            var materials = existing is null ? price.Costs : EquatableArray.From(existing.Construction.Resources
                .Where(r => r.StillNeeded > 0).Select(r => new Amount(new Resource(r.DefName), r.StillNeeded)));
            return new(existing?.Id ?? id, room, OpKind.Build, definition, "", rotation, [cell]) { Material = materials };
        }
        foreach (var room in layout.Rooms.Where(r => r.Role == PlannedRole.Freezer).OrderBy(r => r.Id))
        {
            var index = Array.FindIndex(sources, s => s.HeatPerTick < 0 && room.Region.Contains(s.Cell));
            if (index < 0) continue; // No vented wall, or cooler research is unfinished.
            var cooler = sources[index];
            var sites = new List<PlannedBuildSite>();
            if (!cooler.Complete)
                sites.Add(Site(cooler.Target, room.Id, cooler.Definition, cooler.BuildCell, cooler.BuildRotation) with
                    { Answer = cooler.BuildEstimate ?? throw new RequiredFactException("planned cooler estimate:" + cooler.Target) });
            var links = new HashSet<ConduitRef>();
            void Link(ConduitRef from)
            {
                if (!links.Add(from)) return;
                foreach (var link in layout.Conduits.Where(l => l.From == from)) Link(link.To);
            }
            Link(ConduitRef.ForRoom(room.Id));
            foreach (var cell in layout.Conduits.Where(l => links.Contains(l.From)).SelectMany(l => l.Cells).Distinct())
                if (!facts.EssentialInputs.Power.Conduits.Contains(cell))
                    sites.Add(Site(FormattableString.Invariant($"planned/power/conduit/{cell.X}/{cell.Z}"), room.Id,
                        LayoutPowerDeriveRules.PowerConduitDefinition, cell, Rotation.North));
            if (!HeatSupplies.Supplied(cooler.Supply, out _))
            {
                PlannedBuildSite? generator = null;
                foreach (var definition in new[] { PowerRules.WindTurbineDefinition, LayoutUtilityRules.SolarDefinition, PowerRules.GeothermalDefinition })
                {
                    if (!ColonyPlanningRules.DefinitionAvailable(facts, definition)) continue;
                    var next = LayoutPlan.PlannedPowerSites(layout, definition).FirstOrDefault(s => !Built(definition, s.Cell) && !assignedGenerators.Contains(s.Cell));
                    if (next is null) continue;
                    generator = Site(FormattableString.Invariant($"planned/power/{definition}/{next.Cell.X}/{next.Cell.Z}"), room.Id, definition, next.Cell, next.Rotation);
                    break;
                }
                if (generator is null) continue; // No planned generator can supply this cooler.
                assignedGenerators.Add(generator.Positions[0]);
                sites.Add(generator);

            }
            output[room.Id] = EquatableArray.From(sites.DistinctBy(s => s.Id));
        }
        return output;
    }
}



