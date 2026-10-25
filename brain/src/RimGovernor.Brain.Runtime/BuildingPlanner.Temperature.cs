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
            case Pol.TemperatureMethod.Heat or Pol.TemperatureMethod.HeatElectric or Pol.TemperatureMethod.Cool or Pol.TemperatureMethod.CoolPowered or Pol.TemperatureMethod.RefuelOff or Pol.TemperatureMethod.RefuelOn:
                var resolved = Copy();
                resolved.Temperature = proposal;
                resolved.Definition = Wire.Name(proposal.Method);
                resolved.Environment = Pol.PlacementEnvironment.Anywhere;
                if (proposal.Thing != "")
                {
                    resolved.Definition = "Campfire";
                }
                if (proposal.Method == Pol.TemperatureMethod.HeatElectric)
                {
                    resolved.Definition = cooling.Heater;
                }
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
    /// <summary>
    /// Assembles the powered cooler evidence from the rooms reading: the Cooler planning definition
    /// (placeability, draw), the colony power topology, the site cells the vented-wall search walks, and the
    /// sleepers whose comfortable ranges band each room. An unread sleeper census raises.
    /// </summary>
    internal static Pol.TemperatureCooling TemperatureCooling(RimGovernor.Brain.GameAccess.ColonyReading facts)
    {
        var sleeping = facts.Facts.Sleeping;
        var heater = facts.Facts.Shapes.Furniture.Heater;
        Pol.ClimateGear cooler = new Pol.ClimateGear.NotPlaceable();
        Pol.ClimateGear heaterGear = new Pol.ClimateGear.NotPlaceable();
        foreach (var d in facts.Facts.Definitions)
        {
            if (!d.Available) continue;
            if (d.Name == "Cooler")
            {
                cooler = Pol.ClimateGear.Of(d.Name, d.Available, (d.Power ?? throw new RequiredFactException("planning Power")).PowerW);
            }
            else if (d.Name == heater)
            {
                heaterGear = Pol.ClimateGear.Of(d.Name, d.Available, (d.Power ?? throw new RequiredFactException("planning Power")).PowerW);
            }
        }
        return new Pol.TemperatureCooling
        {
            Cooler = cooler,
            Power = Pol.ClimatePower.Of(facts.Facts.EssentialInputs.Power),
            Cells = facts.PlanningWindow.Cells,
            HeatCampfires = HeatCampfires(facts),
            Sleepers = [.. sleeping.People, .. sleeping.Slaves],
            Heater = heater,
            HeaterGear = heaterGear,
            // Campfires burn wood: a finite stock unless a tree species can be sown.
            HeatFuelFinite = !TreePlantationRules.ResourceGrowable(FieldPlanner.CropChoices(facts), ConstructionDemandRules.ConstructionWoodLog, facts.Facts.CropClimate.Biome, TreePlantationRules.GrowerSkill(facts.Facts.ForecastObserved.WorkPawns)),
        };
    }

    /// <summary>The planning definitions the temperature planner reads: every method it can place, so availability and draw are known before one is chosen.</summary>
    internal static readonly string[] TemperatureDefinitions = ["Campfire", "PassiveCooler", "Cooler"];

}
