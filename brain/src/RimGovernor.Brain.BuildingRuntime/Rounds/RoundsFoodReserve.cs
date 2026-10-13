namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>The native read of the food-reserve supplies (Go's anonymous interface on the planner source).</summary>
internal interface IFoodReserveSupplyReader
{
    (SupplyRead Read, BridgeResult Result) ReadFoodReserveSupplies(CancellationToken ct, Common.Identity identity, bool forbid);
}

public sealed partial class Rounder
{
    internal void ReviewReserve(ref ColonyProjection p)
    {
        p = p with { Facts = p.Facts with { FoodReserve = Unknown<FoodReserveReview>() } };
        var known = p.FoodSupply.TryGetValue(out var supply);
        var staged = Staged();
        if (!known || staged.FoodReserveDays == 0)
        {
            return;
        }

        try
        {
            var reserve = PolicyFunctions.ReviewFoodReserve(supply!, null, staged.FoodReserveDays, Seasonal(p.Facts).FoodMinDays, FoodDeliveryDays(p.Facts.FoodPlan));
            p = p with { Facts = p.Facts with { FoodReserve = Known(reserve) } };
        }
        catch (DomainException)
        {
            // An unreadable review leaves the reserve unknown.
        }
    }
}

internal static partial class BuildingRuntimePackage
{
    internal static Fact<IReadOnlyList<double>> FoodDeliveryDays(Fact<FoodPlan> f)
    {
        if (!f.TryGetValue(out var plan) || plan.Unknown.Count > 0)
        {
            return Unknown<IReadOnlyList<double>>();
        }

        var days = new List<double>();
        foreach (var row in plan.Portfolio)
        {
            if (row.Decision == FoodPlanDecision.Close || row.DeliveredPerDay <= 0)
            {
                continue;
            }

            if (!row.Channel.LeadDays.TryGetValue(out var lead))
            {
                return Unknown<IReadOnlyList<double>>();
            }

            days.Add(lead);
        }

        return Known<IReadOnlyList<double>>(days);
    }
}

public sealed partial class RoundsFoodStorageUpkeepPlanner
{
    /// <summary>Holds or releases the reserved food supplies: one plan of supply-allow actions, at most eight, committed under the reviewed goal.</summary>
    internal RoundsFoodStorageUpkeepResult AdmitReserve(CancellationToken ct, CancellationToken epoch, StandardState goal, Observations.ColonyFactsSnapshot observed, FoodReserveReview reserve)
    {
        var p = Reviewer.Player;
        var state = p.Session.State();
        var wanted = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var id in reserve.Hold)
        {
            wanted[id] = true;
        }

        foreach (var id in reserve.Release)
        {
            wanted[id] = false;
        }

        var method = new MethodId($"food-reserve-{goal.Revision}");
        var planId = PlanIds.MintPlanId();
        var started = Reviewer.Clock.Now();
        if (Native is not IFoodReserveSupplyReader native)
        {
            return new RoundsFoodStorageUpkeepResult { Verdict = FieldUnavailable("food_reserve_supplies") };
        }

        var actions = new List<DomainAction>();
        foreach (var forbid in new[] { false, true })
        {
            if ((forbid && reserve.Hold.Count == 0) || (!forbid && reserve.Release.Count == 0))
            {
                continue;
            }

            var (read, _) = native.ReadFoodReserveSupplies(ct, BoundaryPackage.Identity(state.Snapshot), forbid);
            try
            {
                BoundaryPackage.Context(read.Context, state.Snapshot);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                throw new ControlException("admitReserve: read context", e);
            }

            if (read.Context.Tick < observed.Context.Tick)
            {
                throw new ControlException("admitReserve: read older than the observed facts");
            }

            foreach (var target in read.Targets)
            {
                if (!wanted.TryGetValue(target.Supply.Thing, out var hold) || hold != forbid || target.Supply.Forbidden != forbid)
                {
                    continue;
                }

                actions.Add(DomainAction.NewSupplyAllowAction(new ActionId($"{planId}-{actions.Count}"), target.Supply));
                if (actions.Count == 8)
                {
                    break;
                }
            }

            if (actions.Count == 8)
            {
                break;
            }
        }

        if (actions.Count == 0)
        {
            return new RoundsFoodStorageUpkeepResult { Verdict = FieldUnavailable("food_reserve_supplies") };
        }

        var plan = PlanSpec.NewPlan(planId, new PlanRevision(1), actions);
        p.Current(ct, epoch);
        var elapsed = Reviewer.Clock.Now() - started;
        if (p.Session.State() != state || elapsed < TimeSpan.Zero || elapsed > Reviewer.MaxAge)
        {
            throw new ControlException("admitReserve: session state changed or the read took too long");
        }

        p.Journal.CommitMethod(ct, goal.Standard.Id, goal.Revision, method, plan);
        return new RoundsFoodStorageUpkeepResult { Verdict = BuildingReasonAdmitted, Plan = planId };
    }
}
