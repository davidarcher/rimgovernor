namespace RimGovernor.Brain.BuildingRuntime;

public sealed partial class Rounder
{
    internal bool TemperatureEnabled() => MethodEnabled(EnsureTemperatureSafety);

    /// <summary>
    /// Whether any composed family reads the typed room census inside the review bracket: temperature and
    /// refrigeration plans need room heat, comfort plans need each facility's hosting room role and cleaning
    /// plans need each room's measured cleanliness.
    /// </summary>
    internal bool RoomsEnabled() =>
        // MaintainHousing: suite claims and the plan's suite wing read the census's standing bedrooms.
        // MaintainShelter: the Safe area covers the census's enclosed roofed rooms.
        TemperatureEnabled() || MethodEnabled(MaintainShelter) || MethodEnabled(MaintainHousing) || MethodEnabled(EnsureComfort) || MethodEnabled(MaintainRefrigeration) || MethodEnabled(MaintainLighting) || MethodEnabled(MaintainFlooring) || MethodEnabled(MaintainRoutes) || MethodEnabled(MaintainBurial) || MethodEnabled(MaintainClimateEnvelope) || MethodEnabled(MaintainTraining) || MethodEnabled(MaintainIncineration) || MethodEnabled(MaintainStockpiles);

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
        return new RoundsBuildingPlanner { Reviewer = reviewer, Native = native, Concern = EnsureTemperatureSafety };
    }

    internal (RoundsBuildingPlanner? Planner, Verdict Verdict) SelectTemperature(Observation.ColonyProjection facts, Pol.RoundsLatches latches)
    {
        var cooling = TemperatureCooling(facts);
        var proposal = SelectTemperatureMethod(facts.Rooms, cooling, Reviewer.Policy, latches);
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
        return cooling with { HeatFuelFinite = !ResourceGrowable(CropChoices(facts), new Pol.Resource(BurnFuelStuff), facts.CropClimate.Biome, GrowerSkill(facts.WorkPawns)) };
    }

    /// <summary>The planning definitions the temperature planner reads: every method it can place, so availability and draw are known before one is chosen.</summary>
    internal static readonly string[] TemperatureDefinitions = ["Campfire", "PassiveCooler", "Cooler"];

    /// <summary>Completed construction lends bounded ordinary refueling and heat-exchange time. Neither the construction receipt nor this allowance establishes safety.</summary>
    internal static uint TemperatureOutputAllowance(string heater, StoreJournal journal, Sto.StandardState goal, GenerationSnapshot current, Tick tick, CancellationToken ct)
    {
        var methods = journal.LoadOwnerMethods(goal, ct);
        uint allowance = 0;
        foreach (var method in methods)
        {
            var plan = journal.LoadPlan(method.Plan, ct);
            allowance = Math.Max(allowance, TemperatureNativeWorkTicks(heater, plan, current, tick));
        }
        return allowance;
    }

    internal static uint TemperatureNativeWorkTicks(string heater, Sto.PlanState plan, GenerationSnapshot current, Tick tick)
    {
        if (plan.Progress.Count != 1)
        {
            return 0;
        }
        var progress = plan.Progress[0];
        if (progress.Action.Building is not { } building || (building.Definition != "Campfire" && building.Definition != heater && building.Definition != "PassiveCooler" && building.Definition != "Cooler"))
        {
            return 0;
        }
        // Native order-generation drift (authority reacquired since the build) does not unbuild the structure:
        // the receipt must match the world and the plan revision it served, not the native generation.
        var v = progress.View;
        current = current with { Plan = plan.Spec.Id, Revision = plan.Spec.Revision, Native = v.Snapshot.Native };
        var known = v.Effect.TryGetValue(out var effect);
        if (v.Stage != Stage.Completed || v.Unresolved || !known || effect != Effect.Completed || !v.Snapshot.Matches(current) || tick.Value < v.Tick.Value || tick.Value - v.Tick.Value >= 10000)
        {
            return 0;
        }
        return Math.Min(120u, (uint)(10000 - (tick.Value - v.Tick.Value)));
    }
}
