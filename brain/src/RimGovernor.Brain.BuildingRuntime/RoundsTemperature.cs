namespace RimGovernor.Brain.BuildingRuntime;

public sealed partial class Rounder
{
    internal bool TemperatureEnabled() => Needs.Contains(ShelterFamily.Temperature);

    /// <summary>Whether the shelter family composes any housing Need (starter, sleeping, expansion).</summary>
    internal bool HousingEnabled() => Needs.Contains(ShelterFamily.Starter) || Needs.Contains(ShelterFamily.Sleeping) || Needs.Contains(ShelterFamily.Expansion);

    /// <summary>
    /// Whether any composed family reads the typed room census inside the review bracket: temperature and
    /// refrigeration plans need room heat, comfort plans need each facility's hosting room role and cleaning
    /// plans need each room's measured cleanliness.
    /// </summary>
    internal bool RoomsEnabled() =>
        // Housing: suite claims and the plan's suite wing read the census's standing bedrooms.
        // shelter/safe-area: the Safe area covers the census's enclosed roofed rooms.
        TemperatureEnabled() || Needs.Contains(ShelterFamily.SafeArea) || HousingEnabled() || Needs.Contains(RoomsFamily.Comfort) || Needs.Contains(RefrigerationNeed) || Needs.Contains(RoomsFamily.Lighting) || Needs.Contains(RoomsFamily.Flooring) || MethodEnabled(MaintainRoutes) || Needs.Contains(AdminFamily.Burial) || Needs.Contains(LayoutFamily.ClimateEnvelope) || Needs.Contains(MilitaryFamily.Training) || Needs.Contains(AdminFamily.Stockpiles);

    internal bool MethodEnabled(ConcernId goal)
    {
        if (!Methods.TryGetValue(out var methods))
        {
            return false;
        }
        foreach (var method in methods)
        {
            if (method == goal)
            {
                return true;
            }
        }
        return false;
    }
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
        var cooling = TemperatureCooling(facts);
        var proposal = SelectTemperatureMethod(facts.Rooms, cooling, Reviewer.Policy);
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
            case Pol.TemperatureMethod.Unknown:
                return (null, FieldUnavailable(TemperatureGap(facts.Rooms)));
            case Pol.TemperatureMethod.NoMethod:
                return (null, BuildingReasonNoDeficit);
            case Pol.TemperatureMethod.Wait:
                return (null, BuildingReasonTemperatureWait);
            case Pol.TemperatureMethod.ShelterNeeded:
                return (null, AwaitingPlan("enclosed_sleeping_room", ""));
            default:
                return (null, AwaitingMethod(proposal.Method));
        }
    }
}

internal static partial class BuildingRuntimePackage
{
    /// <summary>Names the room fact SelectTemperatureMethod found unknown.</summary>
    internal static string TemperatureGap(Fact<Pol.RoomObservation> rooms)
    {
        if (!rooms.TryGetValue(out var observed))
        {
            return "rooms";
        }
        if (!observed.EligibleBeds.TryGetValue(out _))
        {
            return "eligible_beds";
        }
        return "room_temperatures";
    }

    /// <summary>
    /// Assembles the powered cooler evidence from the rooms reading: the Cooler planning definition
    /// (availability, draw), the colony power topology, the site cells the vented-wall search walks, and the
    /// sleepers whose comfortable ranges band each room.
    /// </summary>
    internal static Pol.TemperatureCooling TemperatureCooling(Observation.ColonyProjection facts)
    {
        var cooling = new Pol.TemperatureCooling
        {
            CoolerAvailable = Unknown<bool>(),
            CoolerDrawW = Unknown<double>(),
            Power = facts.PowerPlanning,
            Cells = facts.Cells,
            HeatCampfires = HeatCampfires(facts),
            Heater = facts.Shapes.Furniture.Heater,
        };
        if (facts.Facts.Sleeping.TryGetValue(out var sleeping))
        {
            cooling = cooling with { Sleepers = [.. sleeping.People, .. sleeping.Slaves] };
        }
        var planKnown = facts.LayoutPlan.TryGetValue(out var plan);
        var shelters = new List<Pol.ThermalShelter>();
        foreach (var shelter in PlannedInteriorRooms(facts, planned => planned.Role == Pol.PlannedRole.Shelter))
        {
            shelters.Add(new Pol.ThermalShelter { Cells = RectCells(shelter.Interior), Standing = shelter.Standing, Hot = planKnown && plan!.Hot });
        }
        cooling = cooling with { PlannedShelters = [.. shelters] };
        foreach (var d in facts.Definitions)
        {
            if (d.Name == "Cooler")
            {
                cooling = cooling with { CoolerAvailable = d.Available, CoolerDrawW = d.PowerW };
            }
            else if (d.Name == cooling.Heater)
            {
                cooling = cooling with { HeaterAvailable = d.Available, HeaterDrawW = d.PowerW };
            }
        }
        // Campfires burn wood: a finite stock unless a tree species can be sown.
        return cooling with { HeatFuelFinite = !ResourceGrowable(CropChoices(facts), PolicyFunctions.ConstructionWoodLog, facts.CropClimate.Biome, GrowerSkill(facts.WorkPawns)) };
    }

    /// <summary>The planning definitions the temperature planner reads: every method it can place, so availability and draw are known before one is chosen.</summary>
    internal static readonly string[] TemperatureDefinitions = ["Campfire", "PassiveCooler", "Cooler"];

}
