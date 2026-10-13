using System.Globalization;

namespace RimGovernor.Brain.BuildingRuntime;

public sealed partial class RoundsBuildingPlanner
{
    /// <summary>Commits the auto-refuel switch of a heat campfire as one owner method.</summary>
    internal RoundsBuildingResult CommitCampfireRefuel(CancellationToken call, StandardState goal, Action check)
    {
        var p = Reviewer.Player;
        var proposal = Temperature;
        if (proposal is null || (proposal.Method != PolicyApi.TemperatureRefuelOff && proposal.Method != PolicyApi.TemperatureRefuelOn))
        {
            throw new ControlException("commitCampfireRefuel: not a refuel proposal");
        }
        try
        {
            p.Journal.LoadOwnerMethod(goal, proposal.Key, call);
            return new RoundsBuildingResult { Verdict = WaitFor(PolicyApi.CauseMethodUsed, "campfire_refuel_method") };
        }
        catch (NotFoundException)
        {
        }
        var value = new AutoRefuel(proposal.Thing, proposal.Method == PolicyApi.TemperatureRefuelOn);
        var id = PlanIds.MintPlanId();
        var action = DomainAction.NewAutoRefuelAction(new ActionId($"{id}-0"), value);
        var spec = new PlanSpec(id, new PlanRevision(1), [action]);
        check();
        p.Journal.CommitOwnerMethod(goal, proposal.Key, "", spec, call);
        return new RoundsBuildingResult { Verdict = BuildingReasonAdmitted };
    }

    internal RoundsBuildingResult RetireCampfire(CancellationToken call, CancellationToken epoch, ControlState state, RoundsReview review, StandardState goal, ColonyReading reading, CurrentBuilding campfire) =>
        RetireBuilding(call, epoch, state, review, goal, reading, campfire.Id, campfire.Building.Definition, campfire.Cells[0], "campfire-retire", "building_retire_method");

    /// <summary>Deconstructs one standing building through an excavation-style admission, once per building.</summary>
    internal RoundsBuildingResult RetireBuilding(CancellationToken call, CancellationToken epoch, ControlState state, RoundsReview review, StandardState goal, ColonyReading reading, string id, string def, Cell cell, string prefix, string reason)
    {
        var p = Reviewer.Player;
        var method = RetireMethodId(prefix, id);
        try
        {
            p.Journal.LoadOwnerMethod(goal, method, call);
            return new RoundsBuildingResult { Verdict = WaitFor(PolicyApi.CauseMethodUsed, reason) };
        }
        catch (NotFoundException)
        {
        }
        var snapshot = state.Snapshot with { Plan = PlanIds.MintPlanId(), Revision = new PlanRevision(1) };

        void Check()
        {
            p.Current(call, epoch);
            if (p.Session.State() != state)
            {
                throw new ControlException("retireBuilding: p.session.State() != state");
            }
        }

        Check();
        var value = new Deconstruction(id, def, cell);
        var action = DomainAction.NewDeconstructionAction(new ActionId($"{snapshot.Plan}-0"), value);
        var spec = new PlanSpec(snapshot.Plan, new PlanRevision(1), [action]);
        TelemetryApi.Decide(call, new Decision
        {
            Kind = "layout_edit",
            Component = "building",
            Verdict = "admitted",
            Reason = "building_retire",
            Target = id,
            Attrs = new Dictionary<string, object?> { ["family"] = "building", ["owner"] = goal.Standard.Id.Value, ["x"] = cell.X, ["z"] = cell.Z },
        });
        var facts = reading.Projection;
        return AdmitExcavation(call, epoch, new ExcavationStep { State = state, Review = review, Owner = goal, Facts = facts, Read = reading }, snapshot, method, spec, null, new StockObservation { Snapshot = snapshot, Tick = facts.Identity.Tick }, Check);
    }
}

internal static partial class BuildingRuntimePackage
{
    /// <summary>The standing campfire the colony no longer needs once a usable stove stands in a kitchen, excluding the heat campfires.</summary>
    internal static (CurrentBuilding Building, bool Retire) CampfireRetirement(ColonyProjection facts, IReadOnlyList<ConstructionClaim> claims)
    {
        var bk = facts.CookingBenches.TryGetValue(out var benches);
        var rk = facts.Rooms.TryGetValue(out var rooms);
        var ck = facts.Facts.CurrentConstruction.TryGetValue(out var census);
        if (!bk || !rk || !ck)
        {
            return (default!, false);
        }
        var heat = HeatCampfireCells(claims);
        var stove = false;
        foreach (var bench in benches!)
        {
            if (bench.Definition != "FueledStove" && bench.Definition != "ElectricStove")
            {
                continue;
            }
            var uk = bench.Usable.TryGetValue(out var usable);
            var ik = bench.Room.TryGetValue(out var id);
            var (room, found) = rooms!.Room(id);
            var ok = found && room.Role.TryGetValue(out var role);
            role = ok ? room.Role.TryGetValue(out var r2) ? r2 : default : default;
            stove = stove || (uk && usable && ik && found && ok && role == PolicyApi.RoomRoleKitchen);
        }
        foreach (var bench in benches)
        {
            if (bench.Definition != "Campfire" || !stove)
            {
                continue;
            }
            foreach (var b in census!.Buildings)
            {
                if (b.Id == bench.Id && b.Cells.Count > 0 && !heat.Contains(b.Cells[0]))
                {
                    return (b, true);
                }
            }
        }
        return (default!, false);
    }

    internal static HashSet<Cell> HeatCampfireCells(IReadOnlyList<ConstructionClaim> claims)
    {
        var heat = new HashSet<Cell>();
        foreach (var claim in claims)
        {
            if (claim.Concern == PolicyApi.EnsureTemperatureSafety && claim.Building.Definition == "Campfire")
            {
                foreach (var c in claim.Cells)
                {
                    heat.Add(c);
                }
            }
        }
        return heat;
    }

    internal static List<HeatCampfire> HeatCampfires(ColonyProjection facts)
    {
        facts.Facts.ConstructionClaims.TryGetValue(out var claims);
        var bk = facts.CookingBenches.TryGetValue(out var benches);
        var ck = facts.Facts.CurrentConstruction.TryGetValue(out var census);
        var heat = HeatCampfireCells(claims ?? []);
        if (!bk || !ck || heat.Count == 0)
        {
            return [];
        }
        var output = new List<HeatCampfire>();
        foreach (var bench in benches!)
        {
            if (bench.Definition != "Campfire")
            {
                continue;
            }
            foreach (var b in census!.Buildings)
            {
                if (b.Id == bench.Id && b.Cells.Count > 0 && heat.Contains(b.Cells[0]))
                {
                    output.Add(new HeatCampfire { Id = bench.Id, Room = bench.Room, AutoRefuel = bench.AutoRefuel });
                }
            }
        }
        return output;
    }

    internal static Fact<bool> TemperatureOwed(ColonyProjection facts) =>
        Known(PolicyApi.TemperatureOwed(facts.Rooms, TemperatureCooling(facts)));

    internal static Fact<bool> CampfireRetireOwed(ColonyProjection facts)
    {
        facts.Facts.ConstructionClaims.TryGetValue(out var claims);
        if (!facts.CookingBenches.IsKnown || !facts.Rooms.IsKnown || !facts.Facts.CurrentConstruction.IsKnown)
        {
            return Unknown<bool>();
        }
        return Known(CampfireRetirement(facts, claims ?? []).Retire);
    }
}
