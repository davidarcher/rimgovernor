namespace RimGovernor.Brain.BuildingRuntime;

public sealed partial class RoundsMoodReliefPlanner
{
    private readonly Rounder reviewer;

    private RoundsMoodReliefPlanner(Rounder reviewer) => this.reviewer = reviewer;

    public static RoundsMoodReliefPlanner NewRoundsMoodReliefPlanner(Rounder reviewer)
    {
        if (reviewer is null)
        {
            throw new ControlException("NewRoundsMoodReliefPlanner: reviewer == null");
        }

        return new RoundsMoodReliefPlanner(reviewer);
    }

    internal async Task<RoundsMoodReliefResult> StepAsync(StepArbiter arbiter, CancellationToken call, CancellationToken epoch)
    {
        var p = reviewer.Player;
        var sessionState = p.Session.State();
        if (!sessionState.Enabled)
        {
            return new RoundsMoodReliefResult { Verdict = BuildingRuntimePackage.BuildingReasonDisabled };
        }

        if (!sessionState.ObservationKnown || !sessionState.Snapshot.IsValid || sessionState.Snapshot.Native.Value == 0)
        {
            throw new ControlException("step: observation unknown, snapshot invalid or native generation unset");
        }

        var review = await p.Journal.LoadRoundsAsync(call).ConfigureAwait(false);
        if (!review.Enabled || !review.Snapshot.Matches(sessionState.Snapshot))
        {
            return new RoundsMoodReliefResult { Verdict = BuildingRuntimePackage.BuildingReasonNoReview };
        }

        if (review.Mood is null || review.Mood.States.Count == 0)
        {
            return new RoundsMoodReliefResult { Verdict = Verdict.WaitFor(Cause.MethodUsed, "mood_states") };
        }

        var statesByPawn = new Dictionary<PawnId, RoundsMoodState>();
        foreach (var s in review.Mood.States)
        {
            statesByPawn[s.Pawn.Id] = s;
        }

        var activeOwner = new Dictionary<ConcernId, bool>();
        foreach (var binding in review.Standards)
        {
            if (!PolicyFunctions.MoodProvisionConcern(binding.Concern))
            {
                continue;
            }

            var goal = await p.Journal.LoadStandardAsync(binding.Standard, call).ConfigureAwait(false);
            activeOwner[binding.Concern] = goal.Standard.Status == StandardStatus.Open && goal.Standard.Finding == Finding.Unmet;
        }


        foreach (var binding in review.SubjectIncidents(PolicyFunctions.EnsureMood))
        {
            if (binding.Situation != Situation.Active)
            {
                continue;
            }

            var incident = await p.Journal.LoadIncidentAsync(binding.Incident, call).ConfigureAwait(false);
            if (review.VetoIncident(incident.Incident) != "")
            {
                continue;
            }

            if (BuildingRuntimePackage.IncidentOpenWork(p.Journal, incident, call))
            {
                return new RoundsMoodReliefResult { Verdict = BuildingRuntimePackage.BuildingReasonExistingWork };
            }

            if (!statesByPawn.TryGetValue((PawnId)binding.Subject, out var moodState))
            {
                continue;
            }

            var policyState = MoodReliefPolicyState(moodState);
            // Supported needs are selected by the shared portfolio, never a native need giver.
            var proposal = PolicyFunctions.SelectMoodMethod(policyState, []);
            var provisioning = false;
            foreach (var owner in policyState.Provision)
            {
                provisioning = provisioning || (activeOwner.TryGetValue(owner.Concern, out var active) && active);
            }

            if (proposal.Reason == MoodMethodReason.Provisioned && !provisioning)
            {
                proposal = PolicyFunctions.SelectMoodMethod(policyState.WithoutProvision(), []);
            }

            if (proposal.Reason != MoodMethodReason.Relief)
            {
                if (proposal.Reason is MoodMethodReason.NoCause or MoodMethodReason.Exhausted or MoodMethodReason.Unowned)
                    return new RoundsMoodReliefResult { Verdict = Verdict.AwaitingPlan("unsupported_forecast_mood_cast",
                        "Preventative mood casts require shared caster time, psyfocus and mood-outcome alternatives.") };
                continue;
            }

            // No parallel food/rest/recreation order. The shared portfolio owns these causes.
        }

        return new RoundsMoodReliefResult { Verdict = Verdict.WaitFor(Cause.MethodUsed, "mood_relief_method") };
    }

    internal static Fact<T> MoodReliefValue<T>(T? v)
        where T : struct => v is null ? Unknown<T>() : Known(v.Value);

    internal static MoodState MoodReliefPolicyState(RoundsMoodState s)
    {
        var p = s.Pawn;
        var row = new MoodPawn
        {
            Id = p.Id,
            Mood = MoodReliefValue(p.Mood),
            Threshold = MoodReliefValue(p.Threshold),
            Target = MoodReliefValue(p.Target),
            Food = MoodReliefValue(p.Food),
            Rest = MoodReliefValue(p.Rest),
            Joy = MoodReliefValue(p.Joy),
            Mental = MoodReliefValue(p.Mental),
            Dead = MoodReliefValue(p.Dead),
            Downed = MoodReliefValue(p.Downed),
            Drafted = MoodReliefValue(p.Drafted),
            PlayerForced = MoodReliefValue(p.PlayerForced),
        };
        return new MoodState
        {
            Pawn = row,
            Active = s.Active,
            Missing = s.Missing,
            MentalRisk = s.MentalRisk,
            Provision = [.. s.Provision],
            Unowned = [.. s.Unowned],
            Causes = s.Causes.Select(cause => new MoodCause { Need = cause.Need, Level = MoodReliefValue(cause.Level) }).ToList(),
        };
    }

}

public readonly record struct RoundsMoodReliefResult : IPlannerMethod
{
    public Verdict Verdict { get; init; }

    public PlanId Plan { get; init; }
}
