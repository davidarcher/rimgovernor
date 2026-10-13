namespace RimGovernor.Brain.Observation;

public static partial class ObservationPackage
{
    internal static Fact<HomeCoverageObservation> ColonyHomeCoverage(O.ColonyFactsSnapshot v)
    {
        var u = v.Upkeep?.Observed;
        if (u is null || HasIssue(u.Issues, "home_coverage"))
        {
            return Unknown<HomeCoverageObservation>();
        }
        var h = u.HomeCoverage?.Observed;
        if (h is null)
        {
            return Unknown<HomeCoverageObservation>();
        }
        var targets = new List<HomeCoverageTarget>(h.Targets.Count);
        var r = new HomeCoverageObservation { Revision = (long)h.Revision };
        if (u.HasAutoHomeArea && !HasIssue(u.Issues, "auto_home_area"))
        {
            r = r with { AutoHome = Known(u.AutoHomeArea) };
        }
        if (!HasIssue(u.Issues, "home_cells"))
        {
            r = r with { Home = Known<IReadOnlyList<Cell>>(CellsOf(u.HomeCells)) };
        }
        foreach (var row in h.Targets)
        {
            var t = new HomeCoverageTarget
            {
                Id = row.Id,
                Shape = Optional(row.HasShapeToken, row.ShapeToken),
                Blocker = row.Blocker,
                Cells = CellsOf(row.Cells),
            };
            if (row.HasMissingCells)
            {
                t = t with { Missing = Known((long)row.MissingCells) };
            }
            if (row.HasExcludedCells)
            {
                t = t with { Excluded = Known((long)row.ExcludedCells) };
            }
            if (row.ExtentGeometry is { } geometry)
            {
                t = t with
                {
                    ExtentGeometry = Known(new HomeExtentGeometry
                    {
                        EnclosedInterior = EquatableArray.From(CellsOf(geometry.EnclosedInterior)),
                        Corridor = EquatableArray.From(CellsOf(geometry.Corridor)),
                        Zone = EquatableArray.From(CellsOf(geometry.Zone)),
                    }),
                };
            }
            targets.Add(t);
        }
        return Known(r with { Targets = targets });
    }

    internal static Fact<EquatableArray<StoneStructure>> ColonyStoneStructures(O.ColonyFactsSnapshot v, Buildings buildings, DefinitionCatalog? catalog)
    {
        var u = v.Upkeep?.Observed;
        if (u is null || HasIssue(u.Issues, "structures"))
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
                flammability = catalog.PlannerStatValue(head.DefName, row.Stuff, StatFlammability);
            }
            catch (DomainException e)
            {
                throw new BridgeContractException($"stone structure {r.Building.Id}: {e.Message}");
            }
            rows.Add(new StoneStructure { Id = r.Building.Id, Definition = new Resource(head.DefName), Flammability = Known((double)(float)flammability) });
        }
        return Known(EquatableArray.From(rows));
    }
}
