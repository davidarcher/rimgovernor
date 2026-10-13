namespace RimGovernor.Brain.BuildingRuntime;

public interface IRoundsCustodySource : IRoundsRescueSource, IRoundsTendSource
{
}

public sealed partial class RoundsPopulationCustodyPlanner
{
    /// <summary>Containment upkeep: close doors, enable bioferrite harvest, advance the monolith, then tend the held entity.</summary>
    internal RoundsPopulationCustodyResult StepContainment(CancellationToken call, CancellationToken epoch, Player p, ControlState state, DateTimeOffset started, StandardState goal, RoundsFacts facts, StepArbiter arbiter)
    {
        var containment = facts.Containment;
        var research = facts.Research;
        var upkeep = PolicyFunctions.ContainmentDoorUpkeep(containment);
        foreach (var issue in upkeep.Issues)
        {
            BuildingRuntimePackage.DefenseAction(call, "routine-population-custody", DecisionLevel.Warn, "refused", "containment_upkeep_issue", $"{issue.Cell.X},{issue.Cell.Z}", new Dictionary<string, object?> { ["x"] = issue.Cell.X, ["z"] = issue.Cell.Z, ["detail"] = issue.Reason });
        }

        if (upkeep.CloseDoors.Count > 0)
        {
            var cell = upkeep.CloseDoors[0];
            var door = DoorControl.NewDoorControl(cell, false);
            var prefix = $"population-door-{cell.X}-{cell.Z}-";
            var (method, verdict, ok) = BuildingRuntimePackage.AdmitStandardMethod(call, p.Journal, goal, prefix, state.Snapshot);
            if (!ok)
            {
                return new RoundsPopulationCustodyResult { Verdict = verdict };
            }

            var id = PlanIds.MintPlanId();
            var action = DomainAction.NewDoorControlAction(new ActionId($"{id}-0"), door);
            return Commit(call, epoch, p, state, started, goal, method, id, action);
        }

        var harvest = PolicyFunctions.BioferriteHarvestOwed(containment, research);
        foreach (var reason in harvest.Issues)
        {
            BuildingRuntimePackage.DefenseAction(call, "routine-population-custody", DecisionLevel.Warn, "refused", "bioferrite_harvest_unread", "bioferrite", new Dictionary<string, object?> { ["detail"] = reason });
        }

        if (harvest.Enable.Count > 0)
        {
            var entity = harvest.Enable[0];
            var prefix = $"population-bioferrite-{entity}-";
            var (method, verdict, ok) = BuildingRuntimePackage.AdmitStandardMethod(call, p.Journal, goal, prefix, state.Snapshot);
            if (!ok)
            {
                return new RoundsPopulationCustodyResult { Verdict = verdict };
            }

            var setting = PawnSetting.NewExtractBioferriteSetting(entity, true);
            var id = PlanIds.MintPlanId();
            var action = DomainAction.NewPawnSettingsAction(new ActionId($"{id}-0"), setting);
            return Commit(call, epoch, p, state, started, goal, method, id, action);
        }

        var (monolith, handled) = StepMonolith(call, epoch, p, state, started, goal, facts, arbiter);
        if (handled)
        {
            return monolith;
        }

        var (patient, found) = PolicyFunctions.EntityTendTarget(containment);
        if (!found)
        {
            return new RoundsPopulationCustodyResult { Verdict = BuildingRuntimePackage.WaitFor(Cause.MethodUsed, "entity_tend_target") };
        }

        var review = p.Journal.LoadRounds(call);
        var identity = BoundaryPackage.Identity(state.Snapshot);
        var (emergency, _) = native.ReadEmergency(call, identity);
        try
        {
            BoundaryPackage.Context(emergency.Context, state.Snapshot);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            throw new ControlException("stepContainment: emergency read outside the review", e);
        }

        if (emergency.Context.Tick < review.Tick.Value)
        {
            throw new ControlException("stepContainment: emergency read outside the review");
        }

        if (!emergency.Facts.ColonistsComplete.TryGetValue(out var complete) || !complete || emergency.Facts.Colonists.Count == 0)
        {
            return new RoundsPopulationCustodyResult { Verdict = BuildingRuntimePackage.WaitFor(Cause.MethodUsed, "colonists_complete") };
        }

        var ids = new List<string>(emergency.Facts.Colonists.Count + 1);
        foreach (var pawn in emergency.Facts.Colonists)
        {
            if (new PawnId(pawn.Id.Value) != patient)
            {
                ids.Add(pawn.Id.Value);
            }
        }

        ids.Add(patient.Value);
        var (reply, _) = native.ReadTendPawns(call, identity, ids);
        var observed = reply.Observed ?? throw new ControlException("stepContainment: observed == nil");
        try
        {
            BoundaryPackage.Context(observed.Context, state.Snapshot);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            throw new ControlException("stepContainment: tend read outside the world", e);
        }

        if (observed.Pawns.Count != ids.Count)
        {
            throw new ControlException($"stepContainment: {observed.Pawns.Count} tend rows for {ids.Count} pawns");
        }

        var doctors = new List<TendDoctorFacts>();
        var patients = new List<TendPatientFacts>();
        var seen = new HashSet<string>();
        foreach (var row in observed.Pawns)
        {
            if (row is null || row.Pawn is null || !seen.Add(row.Pawn.Id))
            {
                throw new ControlException("stepContainment: tend row missing or repeated");
            }

            var pawn = new PawnId(row.Pawn.Id);
            if (pawn == patient)
            {
                patients.Add(TendFacts.NewTendPatientFacts(pawn, row, ""));
                continue;
            }

            doctors.Add(TendFacts.NewTendDoctorFacts(pawn, row, ""));
        }

        var (doctor, target, ok2) = PolicyFunctions.SelectTend(doctors, patients, TendFacts.TendReachability(observed.Pawns));
        if (ok2 && !arbiter.TryClaim([doctor, target]))
        {
            return new RoundsPopulationCustodyResult { Verdict = BuildingRuntimePackage.WaitFor(Cause.MethodUsed, "pawn_claim") };
        }

        if (!ok2)
        {
            BuildingRuntimePackage.DefenseAction(call, "routine-population-custody", DecisionLevel.Warn, "waiting", "entity_tend_unavailable", patient.Value, null);
            return new RoundsPopulationCustodyResult { Verdict = BuildingRuntimePackage.WaitFor(Cause.MethodUsed, "entity_tend_doctor") };
        }

        var value = Tend.NewTend(doctor, target);
        var tendPrefix = $"population-tend-{target}-";
        var (tendMethod, tendVerdict, tendOk) = BuildingRuntimePackage.AdmitStandardMethod(call, p.Journal, goal, tendPrefix, state.Snapshot);
        if (!tendOk)
        {
            return new RoundsPopulationCustodyResult { Verdict = tendVerdict };
        }

        var tendId = PlanIds.MintPlanId();
        var tendAction = DomainAction.NewTendAction(new ActionId($"{tendId}-0"), value);
        return Commit(call, epoch, p, state, started, goal, tendMethod, tendId, tendAction);
    }
}
