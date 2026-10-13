namespace RimGovernor.Brain.BuildingRuntime;

public interface IRoundsMonumentPreviewSource
{
    Task<(BuildingPreview Preview, BridgeResult Result)> PreviewBuildingAsync(DomainAction action, GenerationSnapshot snapshot, CancellationToken ct);
}

public sealed partial class RoundsPopulationJoinerPlanner
{
    /// <summary>Installs, hauls or builds a quest monument; not handled when no monument work is owed.</summary>
    internal async Task<(RoundsPopulationJoinerResult Result, bool Handled)> AdmitMonumentAsync(ControlState state, StandardState goal, Rounds review, RoundsReading read, StepArbiter arbiter, DateTimeOffset started, CancellationToken call, CancellationToken epoch)
    {
        var cells = reviewer.FieldProtected(state, review, read.Projection, Unknown<EquatableArray<ConstructionClaim>>(), call);
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
        if (choice.Quest.IsEmpty)
        {
            return (default, false);
        }

        if (choice.Reason != "")
        {
            reviewer.LogQuestSkip(new QuestSkip(choice.Quest, "", new QuestSkipReason(choice.Reason), ""), call);
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
        BuildingPreview? preview = null;
        string target;
        switch (choice.Kind)
        {
            case "install":
                var move = choice.Move ?? throw new ControlException("admitMonument: install choice without a move");
                action = DomainAction.NewMoveBuildingAction(aid, move);
                target = move.Thing;
                if (!arbiter.TryClaim([], "move-building:" + move.Thing))
                {
                    return (new RoundsPopulationJoinerResult { Verdict = Verdict.WaitFor(Cause.Claim, "monument_install") }, true);
                }

                break;
            case "haul":
                var haul = choice.Haul ?? throw new ControlException("admitMonument: haul choice without a haul");
                action = DomainAction.NewHaulAction(aid, haul);
                target = haul.Thing;
                if (!arbiter.TryClaim([(PawnId)haul.Pawn], "haul-item:" + haul.Thing))
                {
                    return (new RoundsPopulationJoinerResult { Verdict = Verdict.WaitFor(Cause.Claim, "monument_haul") }, true);
                }

                break;
            case "build":
            {
                var build = choice.Build ?? throw new ControlException("admitMonument: build choice without a building");
                action = DomainAction.NewBuildingAction(aid, build, ConstructionTier.Expand);
                target = build.ToString();
                if (reviewer.Native is not IRoundsMonumentPreviewSource native)
                {
                    return (default, false);
                }

                (preview, _) = await native.PreviewBuildingAsync(action, snapshot, call).ConfigureAwait(false);
                break;
            }

            default:
                return (default, false);
        }

        var plan = new PlanSpec(id, new PlanRevision(1), [action]);
        var prefix = $"monument-{choice.Quest}-{choice.Kind}-{target}-";
        var (method, verdict, admitted) = BuildingRuntimePackage.AdmitStandardMethod(reviewer.Player.Journal, goal, prefix, state.Snapshot, call);
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
            var built = preview ?? throw new ControlException("admitMonument: build without a preview");
            var decision = await AdmissionLog.AdmitMethodAsync(reviewer.Player.Journal, new BuildingMethodRequest
            {
                Owner = goal,
                Method = method,
                Plan = plan,
                Current = snapshot,
                Tick = read.Projection.Identity.Tick,
                Bounds = Known(read.Projection.Bounds),
                Stock = built.Stock,
                Previews = [built.Preview],
                Purpose = Purpose.Rounds,
            }, call).ConfigureAwait(false);
            if (!decision.Admitted)
            {
                return (new RoundsPopulationJoinerResult { Verdict = AdmissionLog.AdmissionRefused(decision) }, true);
            }
        }
        else
        {
            reviewer.Player.Journal.CommitMethod(goal.Standard.Id, goal.Revision, method, plan, call);
        }

        return (new RoundsPopulationJoinerResult { Verdict = BuildingRuntimePackage.BuildingReasonAdmitted, Plan = id, NativeWorkTicks = BuildingRuntimePackage.StockWaitTicks }, true);
    }
}
