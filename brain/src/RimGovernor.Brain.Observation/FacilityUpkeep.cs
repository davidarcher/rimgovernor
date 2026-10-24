namespace RimGovernor.Brain.Observation;

public static class FacilityUpkeepReads
{
    internal static Fact<EquatableArray<StoneStructure>> ColonyStoneStructures(O.ColonyFactsSnapshot v, Buildings buildings, DefinitionCatalog? catalog)
    {
        var u = v.Upkeep?.Outcome;
        if (u is null || ColonyDecodeReads.HasIssue(u.Issues, "structures"))
        {
            return Unknown<EquatableArray<StoneStructure>>();
        }
        var rows = new List<StoneStructure>();
        foreach (var r in u.Structures)
        {
            if (!buildings.Table.TryGetValue(r.Building?.Id ?? "", out var row) || row.Building is not { } head)
            {
                return Unknown<EquatableArray<StoneStructure>>();
            }
            if (catalog is null)
            {
                throw new BridgeContractException("stone structures: no definition catalog was loaded");
            }
            // The flammability of the building as made: its def with its stuff.
            double flammability;
            try
            {
                flammability = catalog.PlannerStatValue((head.DefName ?? ""), (row.Stuff ?? ""), CatalogStats.Flammability);
            }
            catch (DomainException e)
            {
                throw new BridgeContractException($"stone structure {r.Building?.Id}: {e.Message}");
            }
            rows.Add(new StoneStructure { Id = r.Building?.Id ?? "", Definition = new Resource((head.DefName ?? "")), Flammability = Known((double)(float)flammability) });
        }
        return Known(EquatableArray.From(rows));
    }
}
