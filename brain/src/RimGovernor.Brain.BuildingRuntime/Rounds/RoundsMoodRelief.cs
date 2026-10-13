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

    internal RoundsMoodReliefResult Step(CancellationToken call, CancellationToken epoch, StepArbiter arbiter)
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

        var review = p.Journal.LoadRounds(call);
        if (!review.Enabled || !review.Snapshot.Matches(sessionState.Snapshot))
        {
            return new RoundsMoodReliefResult { Verdict = BuildingRuntimePackage.BuildingReasonNoReview };
        }

        if (review.Mood is null || review.Mood.States.Count == 0)
        {
            return new RoundsMoodReliefResult { Verdict = BuildingRuntimePackage.WaitFor(Cause.MethodUsed, "mood_states") };
        }

        var statesByPawn = new Dictionary<PawnId, RoundsMoodState>();
        foreach (var s in review.Mood.States)
        {
            statesByPawn[new PawnId(s.Pawn.Id)] = s;
        }

        var activeOwner = new Dictionary<ConcernId, bool>();
        foreach (var binding in review.Standards)
        {
            if (!PolicyFunctions.MoodProvisionConcern(binding.Concern))
            {
                continue;
            }

            var goal = p.Journal.LoadStandard(call, binding.Standard);
            activeOwner[binding.Concern] = goal.Standard.Status == StandardStatus.Open && goal.Standard.Finding == StandardFinding.Unmet;
        }

        var started = reviewer.Clock.Now();
        foreach (var binding in review.SubjectIncidents(ConcernId.EnsureMood))
        {
            if (binding.Situation != Situation.Active)
            {
                continue;
            }

            var incident = p.Journal.LoadIncident(call, binding.Incident);
            if (review.VetoIncident(incident.Incident) != "")
            {
                continue;
            }

            if (BuildingRuntimePackage.IncidentOpenWork(call, p.Journal, incident))
            {
                return new RoundsMoodReliefResult { Verdict = BuildingRuntimePackage.BuildingReasonExistingWork };
            }

            if (!statesByPawn.TryGetValue(binding.Subject, out var moodState))
            {
                continue;
            }

            var policyState = MoodReliefPolicyState(moodState);
            var used = MoodReliefUsedNeeds(call, p.Journal, incident.Methods, new PawnId(moodState.Pawn.Id), sessionState.Snapshot);
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
                    var (castId, cast) = CastMoodRelief(call, epoch, arbiter, incident, new PawnId(moodState.Pawn.Id), sessionState.Snapshot);
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

            var prefix = $"mood-{proposal.Need}-{moodState.Pawn.Id}-";
            if (!arbiter.TryClaim([new PawnId(moodState.Pawn.Id)]))
            {
                continue;
            }

            var relief = MoodRelief.NewMoodRelief(new PawnId(moodState.Pawn.Id), need);
            var method = BuildingRuntimePackage.NextMethodId(prefix, BuildingRuntimePackage.IncidentMethodIds(incident.Methods));
            var id = PlanIds.MintPlanId();
            var action = DomainAction.NewMoodReliefAction(new ActionId($"{id}-0"), relief);
            var plan = PlanSpec.NewPlan(id, new PlanRevision(1), [action]);
            p.Current(call, epoch);
            var elapsed = reviewer.Clock.Now() - started;
            if (p.Session.State() != sessionState || elapsed < TimeSpan.Zero || elapsed > reviewer.MaxAge)
            {
                throw new ControlException("step: session state changed or the read took too long");
            }

            p.Journal.CommitIncidentMethod(call, incident.Incident.Id, method, "", plan);
            return new RoundsMoodReliefResult { Verdict = BuildingRuntimePackage.BuildingReasonAdmitted, Plan = id };
        }

        return new RoundsMoodReliefResult { Verdict = BuildingRuntimePackage.WaitFor(Cause.MethodUsed, "mood_relief_method") };
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

    internal static IReadOnlyList<MoodNeed> MoodReliefUsedNeeds(CancellationToken ct, Store.Store journal, IReadOnlyList<IncidentMethod> methods, PawnId pawn, GenerationSnapshot world)
    {
        var used = new List<MoodNeed>();
        foreach (var need in new[] { MoodNeed.Food, MoodNeed.Rest, MoodNeed.Joy })
        {
            var prefix = $"mood-{need}-{pawn}-";
            var (_, ok) = BuildingRuntimePackage.AdmitSubject(ct, journal, prefix, BuildingRuntimePackage.IncidentPlans(methods, prefix), world);
            if (!ok)
            {
                used.Add(need);
            }
        }

        return used;
    }
}

public readonly record struct RoundsMoodReliefResult
{
    public Verdict Verdict { get; init; }

    public PlanId Plan { get; init; }
}
