using RimGovernor.Brain.Policy;
namespace RimGovernor.Brain.BuildingRuntime;

public sealed partial class Rounder
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

public sealed partial class RoundsBuildingPlanner
{
    public static RoundsBuildingPlanner NewRoundsTemperaturePlanner(Rounder? reviewer, IRoundsBuildingSource? native)
    {
        if (reviewer is null || native is null || !reviewer.TemperatureEnabled())
        {
            throw new ControlException("NewRoundsTemperaturePlanner: reviewer == nil || native == nil || !reviewer.temperatureEnabled()");
        }
        if (native is not Observation.IRoundsSource)
        {
            throw new ControlException("NewRoundsTemperaturePlanner: !ok");
        }
        return new RoundsBuildingPlanner { Reviewer = reviewer, Native = native, Concern = ShelterFamily.Temperature.FilingKey, Need = ShelterFamily.Temperature };
    }

    internal (RoundsBuildingPlanner? Planner, Verdict Verdict) SelectTemperature(Observation.ColonyProjection facts)
    {
        // An unread room census, bed census or sleeper census is the planner's unavailable verdict, named by its field.
        if (!facts.Rooms.TryGetValue(out var rooms))
        {
            return (null, FieldUnavailable("rooms"));
        }
        if (!rooms.EligibleBeds.TryGetValue(out var eligible))
        {
            return (null, FieldUnavailable("eligible_beds"));
        }
        var cooling = TemperatureCooling(facts);
        var proposal = TemperatureMethodRules.SelectTemperatureMethod(rooms, eligible, cooling, Reviewer.Policy);
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

partial class RoundsBuildingPlanner
{
    /// <summary>
    /// Assembles the powered cooler evidence from the rooms reading: the Cooler planning definition
    /// (placeability, draw), the colony power topology, the site cells the vented-wall search walks, and the
    /// sleepers whose comfortable ranges band each room. An unread sleeper census raises.
    /// </summary>
    internal static Pol.TemperatureCooling TemperatureCooling(Observation.ColonyProjection facts)
    {
        if (!facts.Facts.Sleeping.TryGetValue(out var sleeping))
        {
            throw new RequiredFactException("sleeping census");
        }
        var heater = facts.Shapes.Furniture.Heater;
        Pol.ClimateGear cooler = new Pol.ClimateGear.NotPlaceable();
        Pol.ClimateGear heaterGear = new Pol.ClimateGear.NotPlaceable();
        foreach (var d in facts.Definitions)
        {
            if (d.Name == "Cooler")
            {
                cooler = Pol.ClimateGear.Of(d.Name, d.Available, d.PowerW);
            }
            else if (d.Name == heater)
            {
                heaterGear = Pol.ClimateGear.Of(d.Name, d.Available, d.PowerW);
            }
        }
        return new Pol.TemperatureCooling
        {
            Cooler = cooler,
            Power = Pol.ClimatePower.Of(facts.PowerPlanning),
            Cells = facts.Cells,
            HeatCampfires = HeatCampfires(facts),
            Sleepers = [.. sleeping.People, .. sleeping.Slaves],
            Heater = heater,
            HeaterGear = heaterGear,
            // Campfires burn wood: a finite stock unless a tree species can be sown.
            HeatFuelFinite = !TreePlantationRules.ResourceGrowable(RoundsFieldPlanner.CropChoices(facts), ConstructionDemandRules.ConstructionWoodLog, facts.CropClimate.Biome, TreePlantationRules.GrowerSkill(facts.WorkPawns)),
        };
    }

    /// <summary>The planning definitions the temperature planner reads: every method it can place, so availability and draw are known before one is chosen.</summary>
    internal static readonly string[] TemperatureDefinitions = ["Campfire", "PassiveCooler", "Cooler"];

}
