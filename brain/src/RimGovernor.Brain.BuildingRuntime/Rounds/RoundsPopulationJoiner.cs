namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>Composes MaintainPopulation's joiner methods: quest acceptance, letters, ceremonies and the quest-site work around them.</summary>
public sealed partial class RoundsPopulationJoinerPlanner
{
    private readonly Rounder reviewer;
    private readonly object mu = new();
    private bool decreeWork;

    private RoundsPopulationJoinerPlanner(Rounder reviewer) => this.reviewer = reviewer;

    public static RoundsPopulationJoinerPlanner NewRoundsPopulationJoinerPlanner(Rounder reviewer)
    {
        if (reviewer is null)
        {
            throw new ControlException("NewRoundsPopulationJoinerPlanner: reviewer == null");
        }

        return new RoundsPopulationJoinerPlanner(reviewer);
    }

    internal async Task<RoundsPopulationJoinerResult> StepAsync(StepArbiter arbiter, CancellationToken call, CancellationToken epoch)
    {
        var p = reviewer.Player;
        var state = p.Session.State();
        if (!state.Enabled)
        {
            return new RoundsPopulationJoinerResult { Verdict = BuildingRuntimePackage.BuildingReasonDisabled };
        }

        if (!state.ObservationKnown || !state.Snapshot.IsValid)
        {
            throw new ControlException("step: observation unknown or snapshot invalid");
        }

        var review = p.Journal.LoadRounds(call);
        if (!review.Enabled || !review.Snapshot.Matches(state.Snapshot))
        {
            return new RoundsPopulationJoinerResult { Verdict = BuildingRuntimePackage.BuildingReasonNoReview };
        }

        var (goal, workable) = p.Journal.Workable(review, PolicyConstants.MaintainPopulation, call);
        if (!workable || goal is null)
        {
            return new RoundsPopulationJoinerResult { Verdict = BuildingRuntimePackage.BuildingReasonNoDeficit };
        }

        foreach (var method0 in goal.Methods)
        {
            var open = p.Journal.LoadPlan(method0.Plan, call);
            if (StorePackage.PlanOpen(open))
            {
                return new RoundsPopulationJoinerResult { Verdict = BuildingRuntimePackage.BuildingReasonExistingWork };
            }
        }

        var expected = await BuildingRuntimePackage.StepScopeAsync(reviewer.Native, call).ConfigureAwait(false);
        if (!BuildingRuntimePackage.RoundsBuildingBoundary(expected, state.Snapshot, review.Tick))
        {
            throw new ControlException("step: boundary moved");
        }

        var started = reviewer.Clock.Now();
        var read = await reviewer.ObserveOwnedAsync(reviewer.Native, expected, Unknown<EquatableArray<ConstructionClaim>>(), call).ConfigureAwait(false);
        var facts = read.Projection.Facts;
        var (siteWork, siteHandled) = AdmitSiteWork(state, goal, read, arbiter, started, call, epoch);
        if (siteHandled)
        {
            return siteWork;
        }

        var (ideology, ideologyHandled) = AdmitIdeologyQuestWork(state, goal, read, arbiter, started, call, epoch);
        if (ideologyHandled)
        {
            return ideology;
        }

        if (PolicyFunctions.CeremonyStartOf(facts.Royalty) is { } ceremony)
        {
            return AdmitCeremonyStart(state, goal, ceremony, started, call, epoch);
        }

        var (letter, hasLetter) = PolicyFunctions.SelectJoinerLetter(facts.JoinerLetters, PolicyFunctions.JoinerCapacity(facts.JoinerCapacity()));
        if (hasLetter)
        {
            return AdmitLetter(state, goal, letter, started, call, epoch);
        }

        var (inspection, inspecting) = AdmitGravInspection(state, goal, read, arbiter, started, call, epoch);
        if (inspecting)
        {
            return inspection;
        }

        var (refugee, refugeeHandled) = await AdmitRefugeeTendAsync(state, goal, review, read, arbiter, started, call, epoch).ConfigureAwait(false);
        if (refugeeHandled)
        {
            return refugee;
        }

        var (monument, monumentHandled) = await AdmitMonumentAsync(state, goal, review, read, arbiter, started, call, epoch).ConfigureAwait(false);
        if (monumentHandled)
        {
            return monument;
        }

        var (decree, decreeHandled) = await AdmitDecreeAsync(state, goal, review, read, arbiter, started, call, epoch).ConfigureAwait(false);
        if (decreeHandled)
        {
            return decree;
        }

        var (departure, departed) = AdmitDeparture(state, goal, read, arbiter, started, call, epoch);
        if (departed)
        {
            return departure;
        }

        var (expedition, expeditionHandled) = AdmitExpedition(state, goal, read, arbiter, started, call, epoch);
        if (expeditionHandled)
        {
            return expedition;
        }

        var (hosting, hosted) = AdmitHospitality(state, goal, review, read, arbiter, started, call, epoch);
        if (hosted)
        {
            return hosting;
        }

        var choice = PolicyFunctions.SelectJoinerMethod(facts.QuestOffers, PolicyFunctions.JoinerCapacity(facts.JoinerCapacity()), facts);
        var prefix = "joiner";
        if (choice.Reason is JoinerPlanReason.NoOffer or JoinerPlanReason.NoCapacity)
        {
            choice = PolicyFunctions.SelectQuestMethod(facts);
            prefix = "quest";
        }

        switch (choice.Reason)
        {
            case JoinerPlanReason.NoOffer:
            case JoinerPlanReason.NoCapacity:
            case JoinerPlanReason.QuestNoOffer:
                if (expedition.NativeWorkTicks > 0)
                {
                    return expedition;
                }

                if (inspection.NativeWorkTicks > 0)
                {
                    return inspection;
                }

                if (departure.NativeWorkTicks > 0)
                {
                    return departure;
                }

                if (refugee.NativeWorkTicks > 0)
                {
                    return refugee;
                }

                if (hosting.NativeWorkTicks > 0)
                {
                    return hosting;
                }

                return new RoundsPopulationJoinerResult { Verdict = Verdict.WaitFor(Cause.MethodUsed, "joiner_offers") };
            case JoinerPlanReason.CensusUnknown:
                return new RoundsPopulationJoinerResult { Verdict = Verdict.FieldUnavailable("joiner_census") };
        }

        prefix = $"{prefix}-{choice.Quest}-";
        var (method, verdict, ok) = BuildingRuntimePackage.AdmitStandardMethod(p.Journal, goal, prefix, state.Snapshot, call);
        if (!ok)
        {
            return new RoundsPopulationJoinerResult { Verdict = verdict };
        }

        var accept = new QuestAccept(choice.Quest, new Domain.PawnId(choice.Accepter.Value), choice.RewardChoice);
        var id = PlanIds.MintPlanId();
        var action = DomainAction.NewQuestAcceptAction(new ActionId($"{id}-0"), accept);
        return CommitSingle(state, started, goal, method, id, action, call, epoch);
    }

    private RoundsPopulationJoinerResult CommitSingle(ControlState state, DateTimeOffset started, StandardState goal, MethodId method, PlanId id, DomainAction action, CancellationToken call, CancellationToken epoch)
    {
        var p = reviewer.Player;
        var plan = new PlanSpec(id, new PlanRevision(1), [action]);
        p.Current(call, epoch);
        var elapsed = reviewer.Clock.Now() - started;
        if (p.Session.State() != state || elapsed < TimeSpan.Zero || elapsed > reviewer.MaxAge)
        {
            throw new ControlException("commit: session state changed or the read took too long");
        }

        p.Journal.CommitMethod(goal.Standard.Id, goal.Revision, method, plan, call);
        return new RoundsPopulationJoinerResult { Verdict = BuildingRuntimePackage.BuildingReasonAdmitted, Plan = id };
    }

    internal RoundsPopulationJoinerResult AdmitCeremonyStart(ControlState state, StandardState goal, BestowingCeremony ceremony, DateTimeOffset started, CancellationToken call, CancellationToken epoch)
    {
        var p = reviewer.Player;
        var prefix = $"ritual-start-{ceremony.Pawn}-";
        var (method, verdict, ok) = BuildingRuntimePackage.AdmitStandardMethod(p.Journal, goal, prefix, state.Snapshot, call);
        if (!ok)
        {
            return new RoundsPopulationJoinerResult { Verdict = verdict };
        }

        var ritual = new Ritual(new Domain.PawnId(ceremony.Pawn.Value), RitualKind.Bestowing, RitualVerb.Start);
        var id = PlanIds.MintPlanId();
        var action = DomainAction.NewRitualAction(new ActionId($"{id}-0"), ritual);
        return CommitSingle(state, started, goal, method, id, action, call, epoch);
    }

    internal RoundsPopulationJoinerResult AdmitLetter(ControlState state, StandardState goal, JoinerLetterOffer letter, DateTimeOffset started, CancellationToken call, CancellationToken epoch)
    {
        var p = reviewer.Player;
        var prefix = $"joiner-letter-{letter.Id}-";
        var (method, verdict, ok) = BuildingRuntimePackage.AdmitStandardMethod(p.Journal, goal, prefix, state.Snapshot, call);
        if (!ok)
        {
            return new RoundsPopulationJoinerResult { Verdict = verdict };
        }

        var id = PlanIds.MintPlanId();
        var value = DialogAnswer.NewJoinerLetterAnswer(letter.Id, letter.Label, letter.Token);
        var action = DomainAction.NewDialogAnswerAction(new ActionId($"{id}-0"), value);
        return CommitSingle(state, started, goal, method, id, action, call, epoch);
    }
}

public readonly record struct RoundsPopulationJoinerResult : IPlannerMethod
{
    public Verdict Verdict { get; init; }

    public PlanId Plan { get; init; }

    public uint NativeWorkTicks { get; init; }
}
