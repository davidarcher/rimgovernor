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

        reviewer.MoodCasts = true;
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

        var started = reviewer.Clock.Now();
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
            var used = MoodReliefUsedNeeds(p.Journal, incident.Methods, moodState.Pawn.Id, sessionState.Snapshot, call);
            var proposal = PolicyFunctions.SelectMoodMethod(policyState, used);
            var provisioning = false;
            foreach (var owner in policyState.Provision)
            {
                provisioning = provisioning || (activeOwner.TryGetValue(owner.Concern, out var active) && active);
            }

            if (proposal.Reason == MoodMethodReason.Provisioned && !provisioning)
            {
                proposal = PolicyFunctions.SelectMoodMethod(policyState.WithoutProvision(), used);
            }

            if (proposal.Reason != MoodMethodReason.Relief)
            {
                if (BuildingRuntimePackage.MoodCastReason(proposal.Reason))
                {
                    var (castId, cast) = CastMoodRelief(arbiter, incident, moodState.Pawn.Id, sessionState.Snapshot, call, epoch);
                    if (cast)
                    {
                        return new RoundsMoodReliefResult { Verdict = BuildingRuntimePackage.BuildingReasonAdmitted, Plan = castId };
                    }
                }

                continue;
            }

            var (need, ok) = DomainMoodReliefNeed(proposal.Need);
            if (!ok)
            {
                throw new ControlException("step: unmapped mood need");
            }

            var prefix = $"mood-{Wire.Name(proposal.Need)}-{moodState.Pawn.Id}-";
            if (!arbiter.TryClaim([moodState.Pawn.Id]))
            {
                continue;
            }

            var relief = new MoodRelief((Domain.PawnId)moodState.Pawn.Id, need);
            var method = BuildingRuntimePackage.NextMethodId(prefix, BuildingRuntimePackage.IncidentMethodIds(incident.Methods));
            var id = PlanIds.MintPlanId();
            var action = DomainAction.NewMoodReliefAction(new ActionId($"{id}-0"), relief);
            var plan = new PlanSpec(id, new PlanRevision(1), [action]);
            p.Current(call, epoch);
            var elapsed = reviewer.Clock.Now() - started;
            if (p.Session.State() != sessionState || elapsed < TimeSpan.Zero || elapsed > reviewer.MaxAge)
            {
                throw new ControlException("step: session state changed or the read took too long");
            }

            await p.Journal.CommitIncidentMethodAsync(incident.Incident.Id, method, "", plan, call).ConfigureAwait(false);
            return new RoundsMoodReliefResult { Verdict = BuildingRuntimePackage.BuildingReasonAdmitted, Plan = id };
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

    internal static (MoodReliefNeed Need, bool Ok) DomainMoodReliefNeed(MoodNeed need) => need switch
    {
        MoodNeed.Food => (MoodReliefNeed.Food, true),
        MoodNeed.Rest => (MoodReliefNeed.Rest, true),
        MoodNeed.Joy => (MoodReliefNeed.Joy, true),
        _ => (default, false),
    };

    internal static IReadOnlyList<MoodNeed> MoodReliefUsedNeeds(StoreJournal journal, IReadOnlyList<IncidentMethod> methods, PawnId pawn, GenerationSnapshot world, CancellationToken ct)
    {
        var used = new List<MoodNeed>();
        foreach (var need in new[] { MoodNeed.Food, MoodNeed.Rest, MoodNeed.Joy })
        {
            var prefix = $"mood-{Wire.Name(need)}-{pawn}-";
            var (_, ok) = BuildingRuntimePackage.AdmitSubject(journal, prefix, BuildingRuntimePackage.IncidentPlans(methods, prefix), world, ct);
            if (!ok)
            {
                used.Add(need);
            }
        }

        return used;
    }
}

public readonly record struct RoundsMoodReliefResult : IPlannerMethod
{
    public Verdict Verdict { get; init; }

    public PlanId Plan { get; init; }
}
