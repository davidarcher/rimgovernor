namespace RimGovernor.Brain.BuildingRuntime;

public interface IRoundsCustodySource : IRoundsRescueSource, IRoundsTendSource
{
    /// <summary>The emergency census; one read serves both the rescue and tend halves, so the inherited twins are hidden.</summary>
    new Task<(EmergencyObservation Emergency, BridgeResult Result)> ReadEmergencyAsync(C.Identity identity, CancellationToken ct);
}

public sealed partial class RoundsPopulationCustodyPlanner
{
    /// <summary>Containment upkeep: close doors, enable bioferrite harvest, advance the monolith, then tend the held entity.</summary>
    internal async Task<RoundsPopulationCustodyResult> StepContainmentAsync(Player p, ControlState state, DateTimeOffset started, StandardState goal, RoundsFacts facts, StepArbiter arbiter, CancellationToken call, CancellationToken epoch)
    {
        var containment = facts.Containment;
        var research = facts.Research;
        var upkeep = PolicyFunctions.ContainmentDoorUpkeep(containment);
        foreach (var issue in upkeep.Issues)
        {
            BuildingRuntimePackage.DefenseAction("routine-population-custody", TelemetryLevel.Warn, "refused", "containment_upkeep_issue", $"{issue.Cell.X},{issue.Cell.Z}", new Dictionary<string, object?> { ["x"] = issue.Cell.X, ["z"] = issue.Cell.Z, ["detail"] = issue.Reason }, call);
        }

        if (upkeep.CloseDoors.Count > 0)
        {
            var cell = upkeep.CloseDoors[0];
            var door = new DoorControl(cell, false);
            var prefix = $"population-door-{cell.X}-{cell.Z}-";
            var (method, verdict, ok) = BuildingRuntimePackage.AdmitStandardMethod(p.Journal, goal, prefix, state.Snapshot, call);
            if (!ok)
            {
                return new RoundsPopulationCustodyResult { Verdict = verdict };
            }

            var id = PlanIds.MintPlanId();
            var action = DomainAction.NewDoorControlAction(new ActionId($"{id}-0"), door);
            return Commit(p, state, started, goal, method, id, action, call, epoch);
        }

        var harvest = PolicyFunctions.BioferriteHarvestOwed(containment, research);
        foreach (var reason in harvest.Issues)
        {
            BuildingRuntimePackage.DefenseAction("routine-population-custody", TelemetryLevel.Warn, "refused", "bioferrite_harvest_unread", "bioferrite", new Dictionary<string, object?> { ["detail"] = reason }, call);
        }

        if (harvest.Enable.Count > 0)
        {
            var entity = harvest.Enable[0];
            var prefix = $"population-bioferrite-{entity}-";
            var (method, verdict, ok) = BuildingRuntimePackage.AdmitStandardMethod(p.Journal, goal, prefix, state.Snapshot, call);
            if (!ok)
            {
                return new RoundsPopulationCustodyResult { Verdict = verdict };
            }

            var setting = PawnSettings.NewExtractBioferriteSetting(entity, true);
            var id = PlanIds.MintPlanId();
            var action = DomainAction.NewPawnSettingsAction(new ActionId($"{id}-0"), setting);
            return Commit(p, state, started, goal, method, id, action, call, epoch);
        }

        var (monolith, handled) = await StepMonolithAsync(p, state, started, goal, facts, arbiter, call, epoch).ConfigureAwait(false);
        if (handled)
        {
            return monolith;
        }

        if (PolicyFunctions.EntityTendTarget(containment) is not { } patient)
        {
            return new RoundsPopulationCustodyResult { Verdict = Verdict.WaitFor(Cause.MethodUsed, "entity_tend_target") };
        }

        var review = p.Journal.LoadRounds(call);
        var identity = Boundary.Identity(state.Snapshot);
        var (emergency, _) = await native.ReadEmergencyAsync(identity, call).ConfigureAwait(false);
        try
        {
            Boundary.Context(emergency.Context, state.Snapshot);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            throw new ControlException("stepContainment: emergency read outside the review", e);
        }

        if (emergency.Context is not { } emergencyContext || emergencyContext.Tick.GetValueOrDefault() < review.Tick.Value)
        {
            throw new ControlException("stepContainment: emergency read outside the review");
        }

        if (!emergency.Facts.ColonistsComplete.TryGetValue(out var complete) || !complete || emergency.Facts.Colonists.Count == 0)
        {
            return new RoundsPopulationCustodyResult { Verdict = Verdict.WaitFor(Cause.MethodUsed, "colonists_complete") };
        }

        var ids = new List<string>(emergency.Facts.Colonists.Count + 1);
        foreach (var pawn in emergency.Facts.Colonists)
        {
            if (pawn.Id.Value != patient.Value)
            {
                ids.Add(pawn.Id.Value);
            }
        }

        ids.Add(patient.Value);
        var (reply, _) = await native.ReadTendPawnsAsync(identity, ids, call).ConfigureAwait(false);
        var observed = reply.Outcome?.Observed ?? throw new ControlException("stepContainment: observed == nil");
        try
        {
            Boundary.Context(observed.Context, state.Snapshot);
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
            if (row is null || row.Pawn is null || !seen.Add((row.Pawn.Id ?? "")))
            {
                throw new ControlException("stepContainment: tend row missing or repeated");
            }

            var pawn = new PawnId((row.Pawn.Id ?? ""));
            if (pawn == (PawnId)patient)
            {
                patients.Add(TendFacts.NewTendPatientFacts(pawn, row, ""));
                continue;
            }

            doctors.Add(TendFacts.NewTendDoctorFacts(pawn, row, ""));
        }

        var (doctor, target, ok2) = PolicyFunctions.SelectTend(doctors, patients, TendFacts.TendReachabilityOf(observed.Pawns));
        if (ok2 && !arbiter.TryClaim([doctor, target]))
        {
            return new RoundsPopulationCustodyResult { Verdict = Verdict.WaitFor(Cause.MethodUsed, "pawn_claim") };
        }

        if (!ok2)
        {
            BuildingRuntimePackage.DefenseAction("routine-population-custody", TelemetryLevel.Warn, "waiting", "entity_tend_unavailable", patient.Value, null, call);
            return new RoundsPopulationCustodyResult { Verdict = Verdict.WaitFor(Cause.MethodUsed, "entity_tend_doctor") };
        }

        var value = new Domain.Tend(new Domain.PawnId(doctor.Value), new Domain.PawnId(target.Value));
        var tendPrefix = $"population-tend-{target}-";
        var (tendMethod, tendVerdict, tendOk) = BuildingRuntimePackage.AdmitStandardMethod(p.Journal, goal, tendPrefix, state.Snapshot, call);
        if (!tendOk)
        {
            return new RoundsPopulationCustodyResult { Verdict = tendVerdict };
        }

        var tendId = PlanIds.MintPlanId();
        var tendAction = DomainAction.NewTendAction(new ActionId($"{tendId}-0"), value);
        return Commit(p, state, started, goal, tendMethod, tendId, tendAction, call, epoch);
    }
}
