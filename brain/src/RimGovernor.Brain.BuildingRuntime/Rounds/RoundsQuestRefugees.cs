namespace RimGovernor.Brain.BuildingRuntime;

public sealed partial class RoundsPopulationJoinerPlanner
{
    /// <summary>Has a colonist tend an injured quest refugee once they are housed; the result carries wait ticks even when not handled.</summary>
    internal (RoundsPopulationJoinerResult Result, bool Handled) AdmitRefugeeTend(CancellationToken call, CancellationToken epoch, ControlState state, StandardState goal, Rounds review, RoundsReading read, StepArbiter arbiter, DateTimeOffset started)
    {
        var refugees = PolicyFunctions.QuestRefugeeIds(read.Projection.Facts.QuestOffers);
        if (refugees.Count == 0)
        {
            return (default, false);
        }

        var housed = new Dictionary<PawnId, bool>();
        if (read.Projection.Facts.Custody.TryGetValue(out var rows))
        {
            foreach (var row in rows)
            {
                var gk = row.Guest.TryGetValue(out var guest);
                var pk = row.Prisoner.TryGetValue(out var prisoner);
                var ak = row.Admitted.TryGetValue(out var admittedRow);
                housed[row.Pawn] = (gk && guest) || (pk && prisoner) || (ak && admittedRow);
            }
        }

        if (reviewer.Native is not IRoundsTendSource native)
        {
            return (default, false);
        }

        var identity = BoundaryPackage.Identity(state.Snapshot);
        var (emergency, _) = native.ReadEmergency(call, identity);
        try
        {
            BoundaryPackage.Context(emergency.Context, state.Snapshot);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            throw new ControlException("admitRefugeeTend: emergency context", e);
        }

        if (emergency.Context.Tick < review.Tick.Value)
        {
            throw new ControlException("admitRefugeeTend: emergency read older than the review");
        }

        if (!emergency.Facts.ColonistsComplete.TryGetValue(out var complete) || !complete)
        {
            return (default, false);
        }

        var ids = new List<string>();
        var seen = new HashSet<PawnId>();
        foreach (var pawn in emergency.Facts.Colonists)
        {
            var pawnId = new PawnId(pawn.Id);
            seen.Add(pawnId);
            ids.Add(pawnId.Value);
        }

        foreach (var refugee in refugees.Keys)
        {
            if (!seen.Contains(refugee))
            {
                ids.Add(refugee.Value);
            }
        }

        ids.Sort(StringComparer.Ordinal);
        var (reply, _) = native.ReadTendPawns(call, identity, ids);
        var observed = reply.Observed;
        if (observed is null || observed.Pawns.Count != ids.Count)
        {
            throw new ControlException("admitRefugeeTend: tend read incomplete");
        }

        BoundaryPackage.Context(observed.Context, state.Snapshot);
        var doctors = new List<TendDoctorFacts>();
        var patients = new List<TendPatientFacts>();
        foreach (var row in observed.Pawns)
        {
            if (row?.Pawn is null)
            {
                throw new ControlException("admitRefugeeTend: tend row without a pawn");
            }

            var pawnId = new PawnId(row.Pawn.Id);
            if (refugees.GetValueOrDefault(pawnId, "") != "" && housed.GetValueOrDefault(pawnId))
            {
                patients.Add(TendFacts.NewTendPatientFacts(pawnId, row, ""));
            }
            else if (seen.Contains(pawnId) && refugees.GetValueOrDefault(pawnId, "") == "")
            {
                doctors.Add(TendFacts.NewTendDoctorFacts(pawnId, row, ""));
            }
        }

        var (doctor, patient, ok) = PolicyFunctions.SelectTend(doctors, patients, TendFacts.TendReachability(observed.Pawns));
        if (!ok)
        {
            return (new RoundsPopulationJoinerResult { Verdict = BuildingRuntimePackage.WaitFor(Cause.MethodUsed, "refugee_care"), NativeWorkTicks = BuildingRuntimePackage.MedicalWaitTicks }, false);
        }

        if (!arbiter.TryClaim([doctor, patient]))
        {
            return (new RoundsPopulationJoinerResult { Verdict = BuildingRuntimePackage.WaitFor(Cause.Claim, "refugee_care") }, true);
        }

        var value = Tend.NewTend(doctor, patient);
        var prefix = $"quest-refugee-tend-{patient}-";
        var (method, verdict, admitted) = BuildingRuntimePackage.AdmitStandardMethod(call, reviewer.Player.Journal, goal, prefix, state.Snapshot);
        if (!admitted)
        {
            return (new RoundsPopulationJoinerResult { Verdict = verdict, NativeWorkTicks = BuildingRuntimePackage.MedicalWaitTicks }, false);
        }

        var id = PlanIds.MintPlanId();
        var action = DomainAction.NewTendAction(new ActionId(id.Value + "-0"), value);
        var plan = PlanSpec.NewPlan(id, new PlanRevision(1), [action]);
        reviewer.Player.Current(call, epoch);
        var elapsed = reviewer.Clock.Now() - started;
        if (reviewer.Player.Session.State() != state || elapsed < TimeSpan.Zero || elapsed > reviewer.MaxAge)
        {
            throw new ControlException("admitRefugeeTend: session state changed or the read took too long");
        }

        reviewer.Player.Journal.CommitMethod(call, goal.Standard.Id, goal.Revision, method, plan);
        return (new RoundsPopulationJoinerResult { Verdict = BuildingRuntimePackage.BuildingReasonAdmitted, Plan = id, NativeWorkTicks = BuildingRuntimePackage.MedicalWaitTicks }, true);
    }
}
