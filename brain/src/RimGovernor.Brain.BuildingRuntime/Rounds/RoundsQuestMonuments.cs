namespace RimGovernor.Brain.BuildingRuntime;

public interface IRoundsMonumentPreviewSource
{
    (BuildingPreview Preview, BridgeResult Result) PreviewBuilding(CancellationToken ct, DomainAction action, GenerationSnapshot snapshot);
}

public sealed partial class RoundsPopulationJoinerPlanner
{
    /// <summary>Installs, hauls or builds a quest monument; not handled when no monument work is owed.</summary>
    internal (RoundsPopulationJoinerResult Result, bool Handled) AdmitMonument(CancellationToken call, CancellationToken epoch, ControlState state, StandardState goal, Rounds review, RoundsReading read, StepArbiter arbiter, DateTimeOffset started)
    {
        var cells = reviewer.FieldProtected(call, state, review, read.Projection, Unknown<IReadOnlyList<ConstructionClaim>>());
        var protectedRects = cells.Select(c => new Rectangle(c.X, c.Z, 1, 1)).ToList();
        var stock = new Dictionary<Resource, long>();
        if (read.Projection.Facts.Resources.TryGetValue(out var rows))
        {
            foreach (var row in rows)
            {
                stock[row.Resource] = stock.GetValueOrDefault(row.Resource) + row.Count;
            }
        }

        var choice = PolicyFunctions.SelectQuestMonument(read.Projection.Facts.QuestOffers, read.Projection.Identity.Map, stock, protectedRects);
        if (choice.Quest == "")
        {
            return (default, false);
        }

        if (choice.Reason != "")
        {
            reviewer.LogQuestSkip(call, new QuestSkip { Quest = choice.Quest, Reason = new QuestSkipReason(choice.Reason) });
            return (default, false);
        }

        if (choice.Kind is "protect" or "wait")
        {
            return (new RoundsPopulationJoinerResult { Verdict = BuildingRuntimePackage.BuildingReasonExistingWork, NativeWorkTicks = BuildingRuntimePackage.StockWaitTicks }, true);
        }

        var id = PlanIds.MintPlanId();
        var snapshot = state.Snapshot with { Plan = id, Revision = new PlanRevision(1) };
        var aid = new ActionId(id.Value + "-0");
        DomainAction action;
        BuildingPreview preview = default;
        switch (choice.Kind)
        {
            case "install":
                action = DomainAction.NewMoveBuildingAction(aid, choice.Move);
                if (!arbiter.TryClaim([], "move-building:" + choice.Move.Thing))
                {
                    return (new RoundsPopulationJoinerResult { Verdict = BuildingRuntimePackage.WaitFor(Cause.Claim, "monument_install") }, true);
                }

                break;
            case "haul":
                action = DomainAction.NewHaulAction(aid, choice.Haul);
                if (!arbiter.TryClaim([choice.Haul.Pawn], "haul-item:" + choice.Haul.Thing))
                {
                    return (new RoundsPopulationJoinerResult { Verdict = BuildingRuntimePackage.WaitFor(Cause.Claim, "monument_haul") }, true);
                }

                break;
            case "build":
            {
                action = DomainAction.NewBuildingAction(aid, choice.Build, Tier.Expand);
                if (reviewer.Native is not IRoundsMonumentPreviewSource native)
                {
                    return (default, false);
                }

                (preview, _) = native.PreviewBuilding(call, action, snapshot);
                break;
            }

            default:
                return (default, false);
        }

        var plan = PlanSpec.NewPlan(id, new PlanRevision(1), [action]);
        var target = choice.Kind switch
        {
            "haul" => choice.Haul.Thing,
            "build" => choice.Build.ToString(),
            _ => choice.Move.Thing,
        };
        var prefix = $"monument-{choice.Quest}-{choice.Kind}-{target}-";
        var (method, verdict, admitted) = BuildingRuntimePackage.AdmitStandardMethod(call, reviewer.Player.Journal, goal, prefix, state.Snapshot);
        if (!admitted)
        {
            return (new RoundsPopulationJoinerResult { Verdict = verdict }, true);
        }

        reviewer.Player.Current(call, epoch);
        var elapsed = reviewer.Clock.Now() - started;
        if (reviewer.Player.Session.State() != state || elapsed < TimeSpan.Zero || elapsed > reviewer.MaxAge)
        {
            throw new ControlException("admitMonument: session state changed or the read took too long");
        }

        if (choice.Kind == "build")
        {
            var decision = BuildingRuntimePackage.AdmitMethod(call, reviewer.Player.Journal, new BuildingMethodRequest
            {
                Owner = goal,
                Method = method,
                Plan = plan,
                Current = snapshot,
                Tick = read.Projection.Identity.Tick,
                Bounds = Known(read.Projection.Bounds),
                Stock = preview.Stock,
                Previews = [preview.Preview],
                Purpose = Purpose.Rounds,
            });
            if (!decision.Admitted)
            {
                return (new RoundsPopulationJoinerResult { Verdict = BuildingRuntimePackage.AdmissionRefused(decision) }, true);
            }
        }
        else
        {
            reviewer.Player.Journal.CommitMethod(call, goal.Standard.Id, goal.Revision, method, plan);
        }

        return (new RoundsPopulationJoinerResult { Verdict = BuildingRuntimePackage.BuildingReasonAdmitted, Plan = id, NativeWorkTicks = BuildingRuntimePackage.StockWaitTicks }, true);
    }
}
