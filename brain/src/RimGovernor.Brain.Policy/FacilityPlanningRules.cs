namespace RimGovernor.Brain.Policy;

/// <summary>Pure facility eligibility and replacement choices over the observed colony frame.</summary>
public static partial class FacilityPlanningRules
{
    public static bool ButcherBenchDefinition(string definition) =>
        definition == KitchenSeparationRules.ButcherTableDefinition || definition == Rules.ButcherSpotDefinition;

    public static List<HeatCampfire> HeatCampfires(ReviewFacts facts, IReadOnlySet<string> heaterDefinitions)
    {
        var claims = facts.ConstructionClaims;
        var census = facts.CurrentConstruction;
        var heat = claims.Where(claim => claim.Concern == ShelterFamily.Temperature.FilingKey
            && claim.Building is { } building && heaterDefinitions.Contains(building.Definition)).SelectMany(claim => claim.Cells).ToHashSet();
        if (census is null || heat.Count == 0)
        {
            return [];
        }
        var output = new List<HeatCampfire>();
        foreach (var b in census.Buildings)
        {
            if (!heaterDefinitions.Contains(b.Building.Definition) || b.Cells.Count == 0 || !heat.Contains(b.Cells[0]))
            {
                continue;
            }
            if (b.Observed.Service.Fuel is not RimGovernor.Brain.GameFacts.Observations.BuildingFuel.Refuelable fuel)
                throw new RequiredFactException("heating building fuel:" + b.Id);
            foreach (var room in facts.Upkeep.Rooms.Rooms)
            {
                if (facts.Upkeep.Rooms.CellsOf(room).Contains(b.Cells[0]))
                {
                    if (HeatCampfire.Of(b.Id, room.Id, fuel.AutoRefuel) is { } campfire)
                    {
                        output.Add(campfire);
                    }
                }
            }
        }
        return output;
    }

    /// <summary>Whether a heat campfire owes its refuel switch or an unheated sleeping room sits cold; false while the room census is unread.</summary>
    public static bool TemperatureOwed(ReviewFacts facts) =>
        TemperatureMethodRules.TemperatureOwed(facts.Upkeep.Rooms, facts.Sleeping,
            HeatCampfires(facts, facts.Stations.Temperature.Where(definition => definition.Heats && definition.Fuels.Count > 0)
                .Select(definition => definition.Name).ToHashSet(StringComparer.Ordinal)));

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
}
