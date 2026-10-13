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

    internal RoundsPopulationJoinerResult Step(CancellationToken call, CancellationToken epoch, StepArbiter arbiter)
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

        var (goal, workable) = p.Journal.Workable(call, review, ConcernId.MaintainPopulation);
        if (!workable)
        {
            return new RoundsPopulationJoinerResult { Verdict = BuildingRuntimePackage.BuildingReasonNoDeficit };
        }

        foreach (var method0 in goal.Methods)
        {
            var open = p.Journal.LoadPlan(call, method0.Plan);
            if (StorePackage.PlanOpen(open))
            {
                return new RoundsPopulationJoinerResult { Verdict = BuildingRuntimePackage.BuildingReasonExistingWork };
            }
        }

        var expected = BuildingRuntimePackage.StepScope(call, reviewer.Native);
        if (!BuildingRuntimePackage.RoundsBuildingBoundary(expected, state.Snapshot, review.Tick))
        {
            throw new ControlException("step: boundary moved");
        }

        var started = reviewer.Clock.Now();
        var read = reviewer.ObserveOwned(call, reviewer.Native, expected, Unknown<IReadOnlyList<ConstructionClaim>>());
        var facts = read.Projection.Facts;
        var (siteWork, siteHandled) = AdmitSiteWork(call, epoch, state, goal, read, arbiter, started);
        if (siteHandled)
        {
            return siteWork;
        }

        var (ideology, ideologyHandled) = AdmitIdeologyQuestWork(call, epoch, state, goal, read, arbiter, started);
        if (ideologyHandled)
        {
            return ideology;
        }

        var (ceremony, hasCeremony) = PolicyFunctions.CeremonyStartOf(facts.Royalty);
        if (hasCeremony)
        {
            return AdmitCeremonyStart(call, epoch, state, goal, ceremony, started);
        }

        var (letter, hasLetter) = PolicyFunctions.SelectJoinerLetter(facts.JoinerLetters, PolicyFunctions.JoinerCapacity(facts.JoinerCapacity()));
        if (hasLetter)
        {
            return AdmitLetter(call, epoch, state, goal, letter, started);
        }

        var (inspection, inspecting) = AdmitGravInspection(call, epoch, state, goal, read, arbiter, started);
        if (inspecting)
        {
            return inspection;
        }

        var (refugee, refugeeHandled) = AdmitRefugeeTend(call, epoch, state, goal, review, read, arbiter, started);
        if (refugeeHandled)
        {
            return refugee;
        }

        var (monument, monumentHandled) = AdmitMonument(call, epoch, state, goal, review, read, arbiter, started);
        if (monumentHandled)
        {
            return monument;
        }

        var (decree, decreeHandled) = AdmitDecree(call, epoch, state, goal, review, read, arbiter, started);
        if (decreeHandled)
        {
            return decree;
        }

        var (departure, departed) = AdmitDeparture(call, epoch, state, goal, read, arbiter, started);
        if (departed)
        {
            return departure;
        }

        var (expedition, expeditionHandled) = AdmitExpedition(call, epoch, state, goal, read, arbiter, started);
        if (expeditionHandled)
        {
            return expedition;
        }

        var (hosting, hosted) = AdmitHospitality(call, epoch, state, goal, review, read, arbiter, started);
        if (hosted)
        {
            return hosting;
        }

        var choice = PolicyFunctions.SelectJoinerMethod(facts.QuestOffers, PolicyFunctions.JoinerCapacity(facts.JoinerCapacity()), facts);
        var prefix = "joiner";
        if (choice.Reason is JoinerReason.NoOffer or JoinerReason.NoCapacity)
        {
            choice = PolicyFunctions.SelectQuestMethod(facts);
            prefix = "quest";
        }

        switch (choice.Reason)
        {
            case JoinerReason.NoOffer:
            case JoinerReason.NoCapacity:
            case JoinerReason.QuestNoOffer:
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

                return new RoundsPopulationJoinerResult { Verdict = BuildingRuntimePackage.WaitFor(Cause.MethodUsed, "joiner_offers") };
            case JoinerReason.CensusUnknown:
                return new RoundsPopulationJoinerResult { Verdict = BuildingRuntimePackage.FieldUnavailable("joiner_census") };
        }

        prefix = $"{prefix}-{choice.Quest}-";
        var (method, verdict, ok) = BuildingRuntimePackage.AdmitStandardMethod(call, p.Journal, goal, prefix, state.Snapshot);
        if (!ok)
        {
            return new RoundsPopulationJoinerResult { Verdict = verdict };
        }

        var accept = QuestAccept.NewQuestAccept(choice.Quest, choice.Accepter, choice.RewardChoice);
        var id = PlanIds.MintPlanId();
        var action = DomainAction.NewQuestAcceptAction(new ActionId($"{id}-0"), accept);
        return CommitSingle(call, epoch, state, started, goal, method, id, action);
    }

    private RoundsPopulationJoinerResult CommitSingle(CancellationToken call, CancellationToken epoch, ControlState state, DateTimeOffset started, StandardState goal, MethodId method, PlanId id, DomainAction action)
    {
        var p = reviewer.Player;
        var plan = PlanSpec.NewPlan(id, new PlanRevision(1), [action]);
        p.Current(call, epoch);
        var elapsed = reviewer.Clock.Now() - started;
        if (p.Session.State() != state || elapsed < TimeSpan.Zero || elapsed > reviewer.MaxAge)
        {
            throw new ControlException("commit: session state changed or the read took too long");
        }

        p.Journal.CommitMethod(call, goal.Standard.Id, goal.Revision, method, plan);
        return new RoundsPopulationJoinerResult { Verdict = BuildingRuntimePackage.BuildingReasonAdmitted, Plan = id };
    }

    internal RoundsPopulationJoinerResult AdmitCeremonyStart(CancellationToken call, CancellationToken epoch, ControlState state, StandardState goal, BestowingCeremony ceremony, DateTimeOffset started)
    {
        var p = reviewer.Player;
        var prefix = $"ritual-start-{ceremony.Pawn}-";
        var (method, verdict, ok) = BuildingRuntimePackage.AdmitStandardMethod(call, p.Journal, goal, prefix, state.Snapshot);
        if (!ok)
        {
            return new RoundsPopulationJoinerResult { Verdict = verdict };
        }

        var ritual = Ritual.NewRitual(new PawnId(ceremony.Pawn), RitualKind.Bestowing, RitualStage.Start);
        var id = PlanIds.MintPlanId();
        var action = DomainAction.NewRitualAction(new ActionId($"{id}-0"), ritual);
        return CommitSingle(call, epoch, state, started, goal, method, id, action);
    }

    internal RoundsPopulationJoinerResult AdmitLetter(CancellationToken call, CancellationToken epoch, ControlState state, StandardState goal, JoinerLetterOffer letter, DateTimeOffset started)
    {
        var p = reviewer.Player;
        var prefix = $"joiner-letter-{letter.Id}-";
        var (method, verdict, ok) = BuildingRuntimePackage.AdmitStandardMethod(call, p.Journal, goal, prefix, state.Snapshot);
        if (!ok)
        {
            return new RoundsPopulationJoinerResult { Verdict = verdict };
        }

        var id = PlanIds.MintPlanId();
        var value = JoinerLetterAnswer.NewJoinerLetterAnswer(letter.Id, letter.Label, letter.Token);
        var action = DomainAction.NewDialogAnswerAction(new ActionId($"{id}-0"), value);
        return CommitSingle(call, epoch, state, started, goal, method, id, action);
    }
}

public readonly record struct RoundsPopulationJoinerResult
{
    public Verdict Verdict { get; init; }

    public PlanId Plan { get; init; }

    public uint NativeWorkTicks { get; init; }
}
