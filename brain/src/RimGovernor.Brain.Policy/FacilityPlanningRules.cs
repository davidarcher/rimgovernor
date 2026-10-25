namespace RimGovernor.Brain.Policy;

/// <summary>Pure facility eligibility and replacement choices over the observed colony frame.</summary>
public static partial class FacilityPlanningRules
{
    /// <summary>Whether a campfire intent already stands: an unheated campfire in the census or an open campfire claim.</summary>
    public static bool CampfireIntentStanding(EquatableArray<ConstructionClaim> claims, CurrentConstruction? observed, string campfireDefinition)
    {
        if (observed is not { } census)
        {
            throw new RequiredFactException("construction census");
        }
        var heat = HeatCampfireCells(claims, campfireDefinition);
        foreach (var b in census.Buildings)
        {
            if (b.Building is { } building && building.Definition == campfireDefinition && b.Cells.Count > 0 && !heat.Contains(b.Cells[0]))
            {
                return true;
            }
        }
        foreach (var claim in claims)
        {
            if (claim.Building is { } campfire && campfire.Definition == campfireDefinition && ConstructionOwnershipRules.WorkOpen(campfire, observed) == BuildingWork.BuildingOpen)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Whether stock covers the butcher table's cheapest stocked stuff choice; unread without the definition.</summary>
    public static bool ButcherTableAffordable(ReviewFacts facts)
    {
        var stock = ResourceStockRules.Count(facts.Census.Resources);
        foreach (var d in facts.Definitions)
        {
            if (d.Name != KitchenSeparationRules.ButcherTableDefinition)
            {
                continue;
            }
            if (!d.Available) return false;
            StuffPrice price;
            try
            {
                price = d.StuffChoice(StuffCriterion.CheapestStuff, stock);
            }
            catch (NoStuffInStockException) { return false; }
            return price.Costs.All(a => stock.GetValueOrDefault(a.Resource) >= a.Count);
        }
        throw new RequiredFactException("butcher table build cost");
    }

    public static CurrentBuilding? StandingButcherSpot(ReviewFacts facts)
    {
        var benches = facts.EssentialInputs.Benches.Where(b => b.Butcher).ToArray();
        var census = facts.CurrentConstruction;
        var table = false;
        foreach (var bench in benches)
        {
            table = table || bench.Definition == KitchenSeparationRules.ButcherTableDefinition;
        }
        if (!table)
        {
            return null;
        }
        foreach (var bench in benches)
        {
            if (bench.Definition != Rules.ButcherSpotDefinition)
            {
                continue;
            }
            foreach (var b in census.Buildings)
            {
                if (b.Id == bench.Id && b.Cells.Count > 0)
                {
                    return b;
                }
            }
        }
        return null;
    }

    public static bool ButcherBenchDefinition(string definition) =>
        definition == KitchenSeparationRules.ButcherTableDefinition || definition == Rules.ButcherSpotDefinition;

    /// <summary>The standing campfire the colony no longer needs once a usable stove stands in a kitchen, excluding the heat campfires.</summary>
    public static (CurrentBuilding Building, bool Retire) CampfireRetirement(ReviewFacts facts, IReadOnlyList<ConstructionClaim> claims, string campfireDefinition)
    {
        var benches = facts.EssentialInputs.Benches.Where(b => !b.Butcher).ToArray();
        var rooms = facts.Upkeep.Rooms;
        var census = facts.CurrentConstruction;
        if (census is null)
        {
            return (default!, false);
        }
        var heat = HeatCampfireCells(claims, campfireDefinition);
        var stove = false;
        foreach (var bench in benches)
        {
            if (bench.Definition != "FueledStove" && bench.Definition != "ElectricStove")
            {
                continue;
            }
            var (room, found) = bench.Room is { } id ? rooms!.Room(id) : (null, false);
            stove = stove || (bench.Usable && found && room!.Role == RoomRole.Kitchen);
        }
        foreach (var bench in benches)
        {
            if (bench.Definition != campfireDefinition || !stove)
            {
                continue;
            }
            foreach (var b in census.Buildings)
            {
                if (b.Id == bench.Id && b.Cells.Count > 0 && !heat.Contains(b.Cells[0]))
                {
                    return (b, true);
                }
            }
        }
        return (default!, false);
    }

    public static HashSet<Cell> HeatCampfireCells(IReadOnlyList<ConstructionClaim> claims, string campfireDefinition)
    {
        var heat = new HashSet<Cell>();
        foreach (var claim in claims)
        {
            if (claim.Concern == ShelterFamily.Temperature.FilingKey && claim.Building?.Definition == campfireDefinition)
            {
                foreach (var c in claim.Cells)
                {
                    heat.Add(c);
                }
            }
        }
        return heat;
    }

    public static List<HeatCampfire> HeatCampfires(ReviewFacts facts, string campfireDefinition)
    {
        var claims = facts.ConstructionClaims;
        var benches = facts.EssentialInputs.Benches.Where(b => !b.Butcher).ToArray();
        var census = facts.CurrentConstruction;
        var heat = HeatCampfireCells(claims, campfireDefinition);
        if (census is null || heat.Count == 0)
        {
            return [];
        }
        var output = new List<HeatCampfire>();
        foreach (var bench in benches)
        {
            if (bench.Definition != campfireDefinition)
            {
                continue;
            }
            foreach (var b in census.Buildings)
            {
                if (b.Id == bench.Id && b.Cells.Count > 0 && heat.Contains(b.Cells[0]))
                {
                    // A campfire whose native room or auto-refuel toggle was not read owes no switch.
                    if (HeatCampfire.Of(bench.Id, bench.Room, bench.Observed.AutoRefuel) is { } campfire)
                    {
                        output.Add(campfire);
                    }
                }
            }
        }
        return output;
    }

    /// <summary>Whether a heat campfire owes its refuel switch or an unheated sleeping room sits cold; false while the room census is unread.</summary>
    public static bool TemperatureOwed(ReviewFacts facts, string campfireDefinition) =>
        TemperatureMethodRules.TemperatureOwed(facts.Upkeep.Rooms, facts.Sleeping, HeatCampfires(facts, campfireDefinition));

    public static bool CampfireRetireOwed(ReviewFacts facts, string campfireDefinition)
    {
        var claims = facts.ConstructionClaims;
        return CampfireRetirement(facts, claims, campfireDefinition).Retire;
    }

    public static bool BuilderAvailable(IReadOnlyList<WorkPawn> pawns, int minimum)
    {
        foreach (var pawn in pawns)
        {
            if (!pawn.Available || pawn.Work is not PawnWorkTable.Works { Rows: var settings })
            {
                continue;
            }
            if (minimum > 0)
            {
                var qualified = false;
                foreach (var skill in pawn.Skills)
                {
                    if (skill.DefName == "Construction" && !skill.Disabled && skill.Level >= minimum)
                    {
                        qualified = true;
                    }
                }
                if (!qualified)
                {
                    continue;
                }
            }
            foreach (var setting in settings)
            {
                if (setting.Work == WorkType.Construction && !setting.Disabled && setting.Priority > 0)
                {
                    return true;
                }
            }
        }
        return false;
    }

    /// <summary>Whether a powered network within six cells of the cell has the spare generation for a recreation building of these watts.</summary>
    public static bool PoweredRecreationCell(ReviewFacts facts, Cell cell, double watts)
    {
        var topology = facts.EssentialInputs.Power;
        if (topology.Blackout)
        {
            return false;
        }
        foreach (var b in topology.Buildings)
        {
            var dx = cell.X - b.Cell.X;
            var dz = cell.Z - b.Cell.Z;
            if (b.OutputW <= 0 || !b.Connected || (dx * dx) + (dz * dz) > 36)
            {
                continue;
            }
            foreach (var n in topology.Networks)
            {
                if (n.Id == b.Network && n.GenerationW - n.ConsumptionW >= watts)
                {
                    return true;
                }
            }
        }
        return false;
    }

    public static (string Definition, bool Owed) AdvancedLabOwed(ReviewFacts facts, IReadOnlyList<SiteCell>? cells)
    {
        var furniture = facts.Shapes.Furniture;
        if (furniture.AdvancedLab == "")
        {
            return ("", false);
        }

        var standing = new HashSet<string>();
        foreach (var c in cells ?? throw new RequiredFactException("planning window"))
        {
            var d = c.PlayerEdifice();
            if (d != "")
            {
                standing.Add(d);
            }
        }

        var next = furniture.AdvancedLab;
        if (standing.Contains(next))
        {
            next = furniture.Analyzer.Def;
        }

        if (standing.Contains(next))
        {
            return ("", false);
        }

        foreach (var d in facts.Definitions)
        {
            if (d.Name == next)
            {
                var available = d.Available;
                if (available)
                {
                    return (next, true);
                }

                return ("", false);
            }
        }

        return ("", false);
    }

    /// <summary>
    /// Whether any generator definition the power family compiles is buildable now, so a powered bench can be
    /// staged and connected; a generator row the census did not describe leaves it unknown.
    /// </summary>
    public static bool GeneratorAvailable(IReadOnlyDictionary<string, bool> available)
    {
        var result = false;
        foreach (var name in PowerRules.GeneratorDefinitions)
        {
            if (!available.TryGetValue(name, out var value))
                throw new RequiredFactException("generator definition:" + name);
            result |= value;
        }
        return result;
    }
}
