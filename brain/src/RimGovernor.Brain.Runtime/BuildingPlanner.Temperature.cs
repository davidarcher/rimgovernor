using RimGovernor.Brain.Policy;
namespace RimGovernor.Brain.Runtime;

public sealed partial class ColonyReview
{
    internal bool TemperatureEnabled() => Needs.Contains(ShelterFamily.Temperature);

    /// <summary>Whether the shelter family composes its housing Need (sleeping).</summary>
    internal bool HousingEnabled() => Needs.Contains(ShelterFamily.Sleeping);

    /// <summary>
    /// Whether any composed family reads the typed room census inside the review bracket: temperature and
    /// refrigeration plans need room heat, comfort plans need each facility's hosting room role and cleaning
    /// plans need each room's measured cleanliness.
    /// </summary>
    internal bool RoomsEnabled() =>
        // Housing: suite claims and the plan's suite wing read the census's standing bedrooms.
        // shelter/safe-area: the Safe area covers the census's enclosed roofed rooms.
        TemperatureEnabled() || Needs.Contains(ShelterFamily.SafeArea) || HousingEnabled() || Needs.Contains(RoomsFamily.Comfort) || Needs.Contains(FoodStoresRules.RefrigerationNeed) || Needs.Contains(RoomsFamily.Lighting) || Needs.Contains(RoomsFamily.Flooring) || Needs.Contains(LayoutFamily.Routes) || Needs.Contains(AdminFamily.Burial) || Needs.Contains(LayoutFamily.ClimateEnvelope) || Needs.Contains(MilitaryFamily.Training) || Needs.Contains(AdminFamily.Stockpiles);

}

public sealed partial class BuildingPlanner
{
    public static BuildingPlanner NewReviewTemperaturePlanner(ColonyReview? reviewer, IReviewBuildingSource? native)
    {
        if (reviewer is null || native is null || !reviewer.TemperatureEnabled())
        {
            throw new ControlException("NewReviewTemperaturePlanner: reviewer == nil || native == nil || !reviewer.temperatureEnabled()");
        }
        if (native is not IReviewSource)
        {
            throw new ControlException("NewReviewTemperaturePlanner: !ok");
        }
        return new BuildingPlanner { Reviewer = reviewer, Native = native, Concern = ShelterFamily.Temperature.FilingKey, Need = ShelterFamily.Temperature };
    }

    internal (BuildingPlanner? Planner, Verdict Verdict) SelectTemperature(RimGovernor.Brain.GameAccess.ColonyReading facts)
    {
        // An unread room census, bed census or sleeper census is the planner's unavailable verdict, named by its field.
        var rooms = facts.Facts.Upkeep.Rooms;
        var cooling = TemperatureCooling(facts);
        var proposal = TemperatureMethodRules.SelectTemperatureMethod(rooms, rooms.EligibleBeds, cooling, Reviewer.Policy);
        switch (proposal.Method)
        {
            case Pol.TemperatureMethod.Heat or Pol.TemperatureMethod.HeatElectric or Pol.TemperatureMethod.Cool or Pol.TemperatureMethod.CoolPowered or Pol.TemperatureMethod.RefuelOff or Pol.TemperatureMethod.RefuelOn or Pol.TemperatureMethod.SetTarget:
                var resolved = Copy();
                resolved.Temperature = proposal;
                resolved.Definition = proposal.Definition;
                resolved.Environment = Pol.PlacementEnvironment.Anywhere;
                return (resolved, new Verdict());
            case Pol.TemperatureMethod.BedsUnroomed:
                return (null, FieldUnavailable("room_temperatures"));
            case Pol.TemperatureMethod.NoMethod:
                return (null, BuildingReasonNoDeficit);
            case Pol.TemperatureMethod.Wait:
                return (null, BuildingReasonTemperatureWait);
            case Pol.TemperatureMethod.ShelterNeeded:
                return (null, AwaitingPlan("enclosed_sleeping_room", ""));
        }
        throw new ControlException("SelectTemperature: unhandled method " + proposal.Method);
    }
}

partial class BuildingPlanner
{
    internal async Task<(IReadOnlyList<Pol.Preview>? Previews, Pol.StockObservation Stock, Verdict Verdict)> PreviewTemperatureAsync(
        GenerationSnapshot snapshot, ColonyReading facts, IReadOnlyList<Cell> protectedCells, Action check, CancellationToken call)
    {
        var proposal = Temperature ?? throw new ControlException("temperature preview without proposal");
        var stock = new StockObservation { Snapshot = snapshot, Tick = facts.Identity.Tick };
        foreach (var option in proposal.Options)
        {
            if (option.Wall)
            {
                foreach (var site in proposal.WallSites)
                {
                    var (previews, observed, verdict) = await PreviewWallBuildingAsync(snapshot, facts, protectedCells, check,
                        option.Name, site.Cell, site.Rotation, false, call).ConfigureAwait(false);
                    if (verdict.IsZero) return (previews, observed, verdict);
                }
                continue;
            }
            foreach (var cell in proposal.Cells)
            {
                if (protectedCells.Contains(cell)) continue;
                check();
                var (preview, observed, verdict) = await PreviewPlannedBuildingAsync(snapshot, facts,
                    new Building(option.Name, cell, Rotation.North, ""), 0, false, false, call).ConfigureAwait(false);
                if (verdict.IsZero) return (preview, observed, verdict);
            }
        }
        return (null, stock, NoSpace("temperature_cell"));
    }

    /// <summary>
    /// Supplies the shared catalog, measured services, construction eligibility, fuel renewability,
    /// site geometry and sleeper comfort to pure temperature policy.
    /// </summary>
    internal static Pol.TemperatureCooling TemperatureCooling(RimGovernor.Brain.GameAccess.ColonyReading facts)
    {
        var candidates = facts.Facts.Stations.Temperature;
        var fuels = candidates.SelectMany(candidate => candidate.Fuels).Distinct().ToList();
        var renewable = fuels.Where(fuel => TreePlantationRules.ResourceGrowable(FieldPlanner.CropChoices(facts), fuel,
            facts.Facts.CropClimate.Biome, TreePlantationRules.GrowerSkill(facts.Facts.ForecastObserved.WorkPawns))).ToHashSet();
        return new Pol.TemperatureCooling
        {
            Candidates = candidates, Definitions = facts.Facts.Definitions,
            Buildings = (facts.Facts.CurrentConstruction ?? throw new RequiredFactException("temperature construction")).Buildings,
            Builders = facts.Facts.ForecastObserved.WorkPawns.Pawns, Stock = ResourceStockRules.Count(facts.Snapshot.Resources),
            RenewableFuels = renewable, Power = Pol.ClimatePower.Of(facts.Facts.EssentialInputs.Power),
            Cells = facts.PlanningWindow.Cells,
            HeatCampfires = FacilityPlanningRules.HeatCampfires(facts.Facts, candidates.Where(candidate => candidate.Heats && candidate.Fuels.Count > 0)
                .Select(candidate => candidate.Name).ToHashSet(StringComparer.Ordinal)),
            Sleepers = [.. facts.Facts.Sleeping.People, .. facts.Facts.Sleeping.Slaves],
        };
    }
}
