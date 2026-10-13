namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>Composes MaintainHerd's rounds step: slaughter, tame, train, master or sterilize one animal.</summary>
public sealed partial class RoundsHusbandryPlanner
{
    internal Rounder Reviewer { get; init; } = null!;

    public static RoundsHusbandryPlanner NewRoundsHusbandryPlanner(Rounder reviewer)
    {
        if (reviewer is null)
        {
            throw new ControlException("NewRoundsHusbandryPlanner: reviewer is missing");
        }

        return new RoundsHusbandryPlanner { Reviewer = reviewer };
    }
}

public readonly record struct RoundsHusbandryResult
{
    public Verdict Verdict { get; init; }

    public PlanId Plan { get; init; }

    public uint NativeWorkTicks { get; init; }
}

public sealed partial class RoundsHusbandryPlanner
{
    internal RoundsHusbandryResult Step(CancellationToken call, CancellationToken epoch, StepArbiter arbiter)
    {
        var p = Reviewer.Player;
        var state = p.Session.State();
        if (!state.Enabled)
        {
            return new RoundsHusbandryResult { Verdict = BuildingReasonDisabled };
        }

        if (!state.ObservationKnown || !state.Snapshot.IsValid)
        {
            throw new ControlException("step: observation unknown or snapshot invalid");
        }

        var review = p.Journal.LoadRounds(call);
        if (!review.Enabled || !review.Snapshot.Matches(state.Snapshot))
        {
            return new RoundsHusbandryResult { Verdict = BuildingReasonNoReview };
        }

        var expected = StepScope(call, Reviewer.Native);
        if (!RoundsBuildingBoundary(expected, state.Snapshot, review.Tick))
        {
            throw new ControlException("step: boundary moved");
        }

        var started = Reviewer.Clock.Now();
        var read = Reviewer.ObserveRooms(call, Reviewer.Native, expected, Unknown<IReadOnlyList<ConstructionClaim>>());
        var projection = read.Projection with { Facts = read.Projection.Facts with { AnimalWard = read.Projection.Facts.AnimalWard with { Ready = WardReadyOf(read.Projection) } } };
        var wait = AnimalProductWait(projection.Facts.FoodPlan);
        var herd = projection.Facts.HerdPolicy();
        var nativeWorkPending = PolicyFunctions.HerdWorkPending(projection.Facts.AnimalUpkeep.Animals, projection.Facts.AnimalUpkeep.WildAnimals, herd);
        if (nativeWorkPending)
        {
            wait = Math.Max(wait, StockWaitTicks);
        }

        var (goal, workable) = p.Journal.Workable(call, review, ConcernId.MaintainHerd);
        if (!workable)
        {
            return new RoundsHusbandryResult { Verdict = BuildingReasonNoDeficit, NativeWorkTicks = wait };
        }

        foreach (var m in goal.Methods)
        {
            var open = p.Journal.LoadPlan(call, m.Plan);
            if (StorePackage.PlanOpen(open))
            {
                return new RoundsHusbandryResult { Verdict = BuildingReasonExistingWork, NativeWorkTicks = wait };
            }
        }

        var upkeep = projection.Facts.AnimalUpkeep;
        var animals = upkeep.Animals;
        var feedShort = PolicyFunctions.HerdFeedShort(projection.Facts.AnimalFeedRunway().Projection);
        var handlers = Unknown<IReadOnlyList<PawnProfile>>();
        if (projection.WorkPawns.TryGetValue(out var pawns))
        {
            handlers = Known(PolicyFunctions.Profiles(pawns));
        }

        var choice = PolicyFunctions.ReconcileHerdRemoval(animals, herd, projection.Facts.FoodPlan);
        if (choice.Reason == HusbandryReason.NoDeficit)
        {
            choice = PolicyFunctions.SelectHusbandryMethod(animals, upkeep.WildAnimals, feedShort, herd, handlers);
        }

        if (choice.Reason == HusbandryReason.NoDeficit)
        {
            choice = PolicyFunctions.FoodSlaughterChoice(projection.Facts.FoodPlan, animals, herd);
        }

        if (choice.Reason == HusbandryReason.NoDeficit)
        {
            choice = PolicyFunctions.FoodTameChoice(projection.Facts.FoodPlan, upkeep.WildAnimals, feedShort, handlers);
        }

        if (choice.Reason == HusbandryReason.NoDeficit)
        {
            choice = PolicyFunctions.HerdMasterChoice(animals, herd, handlers);
        }

        if (choice.Reason == HusbandryReason.NoDeficit)
        {
            choice = PolicyFunctions.SterilizeChoice(animals, herd, projection.Facts.AnimalWard);
        }

        switch (choice.Reason)
        {
            case HusbandryReason.NoDeficit:
                if (nativeWorkPending)
                {
                    return new RoundsHusbandryResult { Verdict = BuildingReasonExistingWork, NativeWorkTicks = wait };
                }

                return new RoundsHusbandryResult { Verdict = BuildingReasonNoDeficit, NativeWorkTicks = wait };
            case HusbandryReason.Unknown:
                return new RoundsHusbandryResult { Verdict = FieldUnavailable("husbandry_census") };
        }

        var prefix = $"{choice.Method.WireName()}-{choice.Animal}-";
        if (choice.Method == HusbandryMethod.Train)
        {
            prefix = $"{choice.Method.WireName()}-{choice.Animal}-{choice.TrainableDef}-";
        }

        var (verdict, ok) = AdmitSubject(call, p.Journal, prefix, StandardMethodPlans(goal.History, goal.Standard.Episode, prefix), state.Snapshot);
        if (!ok)
        {
            return new RoundsHusbandryResult { Verdict = verdict, NativeWorkTicks = wait };
        }

        if (!arbiter.TryClaim([new PawnId(choice.Animal)]))
        {
            return new RoundsHusbandryResult { Verdict = ClaimHeld("animal") };
        }

        var method = NextMethodId(prefix, HistoryMethodIds(goal.History, goal.Standard.Episode));
        var argument = choice.TrainableDef;
        if (choice.Argument.Length != 0)
        {
            argument = choice.Argument;
        }

        var husbandry = Husbandry.NewHusbandry(new PawnId(choice.Animal), choice.Method, argument);
        var id = PlanIds.MintPlanId();
        var action = DomainAction.NewHusbandryAction(new ActionId($"{id}-0"), husbandry);
        var plan = PlanSpec.NewPlan(id, new PlanRevision(1), [action]);
        p.Current(call, epoch);
        var elapsed = Reviewer.Clock.Now() - started;
        if (p.Session.State() != state || elapsed < TimeSpan.Zero || elapsed > Reviewer.MaxAge)
        {
            throw new ControlException("step: session state changed or the read took too long");
        }

        p.Journal.CommitMethod(call, goal.Standard.Id, goal.Revision, method, plan);
        return new RoundsHusbandryResult { Verdict = BuildingReasonAdmitted, Plan = id };
    }
}

internal static partial class BuildingRuntimePackage
{
    internal static uint AnimalProductWait(Fact<FoodPlan> plan)
    {
        if (!plan.TryGetValue(out var v))
        {
            return 0;
        }

        foreach (var entry in v.Portfolio)
        {
            if (entry.Channel.Kind == CandidateKind.AnimalProduct && entry.Selected())
            {
                return StockWaitTicks;
            }
        }

        return 0;
    }
}
