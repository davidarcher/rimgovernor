namespace RimGovernor.Brain.BuildingRuntime;

public interface IRoundsMonumentPreviewSource
{
    Task<BuildingPreview> PreviewBuildingAsync(DomainAction action, GenerationSnapshot snapshot, CancellationToken ct);
}

public sealed partial class JoinersRounds
{
    /// <summary>Installs, hauls or builds a quest monument; not handled when no monument work is owed.</summary>
    internal async Task<(Verdict Verdict, bool Handled)> AdmitMonumentAsync(NeedWork work, Rounds review, RoundsReading read, DateTimeOffset started, CancellationToken call)
    {
        var state = work.State;
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
            return (BuildingRuntimePackage.BuildingReasonExistingWork, true);
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
                if (!Claim(work, [], "move-building:" + move.Thing))
                {
                    return (Verdict.WaitFor(Cause.Claim, "monument_install"), true);
                }

                break;
            case "haul":
                return (Verdict.AwaitingPlan("unsupported_forecast_monument_delivery",
                    "Monument delivery requires exact destination, quantity and shared worker alternatives."), true);
            case "build":
            {
                var build = choice.Build ?? throw new ControlException("admitMonument: build choice without a building");
                action = DomainAction.NewBuildingAction(aid, build, ConstructionTier.Expand);
                target = build.ToString();
                if (reviewer.Native is not IRoundsMonumentPreviewSource native)
                {
                    return (default, false);
                }

                preview = await native.PreviewBuildingAsync(action, snapshot, call).ConfigureAwait(false);
                break;
            }

            default:
                return (default, false);
        }

        var plan = new PlanSpec(id, new PlanRevision(1), [action]);
        var method = new MethodId($"monument-{choice.Quest}-{choice.Kind}-{target}");
        if (choice.Kind != "build")
        {
            return (Commit(work, method, plan, started, "admitMonument", call), true);
        }

        var (refused, admitted) = AdmitMethod(work, method, call);
        if (!admitted)
        {
            return (refused, true);
        }

        Fence(work, started, "admitMonument", call);
        var built = preview ?? throw new ControlException("admitMonument: build without a preview");
        var decision = await AdmissionLog.AdmitMethodAsync(reviewer.Player.Journal, new BuildingMethodRequest
        {
            Owner = work.Owner,
            Method = method,
            Plan = plan,
            Current = snapshot,
            Tick = read.Projection.Identity.Tick,
            Bounds = Known(read.Projection.Bounds),
            Stock = built.Stock,
            Previews = [built.Preview],
            Purpose = Purpose.Rounds,
        }, call).ConfigureAwait(false);
        return (decision.Admitted ? BuildingRuntimePackage.BuildingReasonAdmitted : AdmissionLog.AdmissionRefused(decision), true);
    }
}
