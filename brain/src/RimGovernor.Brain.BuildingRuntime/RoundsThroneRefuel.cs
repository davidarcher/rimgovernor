namespace RimGovernor.Brain.BuildingRuntime;

internal static partial class BuildingRuntimePackage
{
    /// <summary>
    /// One game hour: a refuel order for a light stands that long before another is admitted for it, so a
    /// hauler on its way is not stacked with more orders and a refused one is retried.
    /// </summary>
    internal const int ThroneRefuelSpacingTicks = 2500;

    internal static string ThroneRefuelPrefix(string lamp) => "throne-refuel-" + lamp + "-";

    /// <summary>Whether an order for lamp was admitted within the spacing before tick.</summary>
    internal static bool ThroneRefuelRecent(IReadOnlyList<Method> history, string lamp, Tick tick)
    {
        var prefix = ThroneRefuelPrefix(lamp);
        foreach (var m in history)
        {
            var name = m.MethodId.Value;
            if (!name.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }
            if (long.TryParse(name.AsSpan(prefix.Length), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var at)
                && tick.Value - at < ThroneRefuelSpacingTicks)
            {
                return true;
            }
        }
        return false;
    }
}

public sealed partial class RoundsSleepingUpkeepPlanner
{
    /// <summary>
    /// Admits one forced refuel order for an unlit, empty throne room light as a recovery_service action under
    /// the housing goal: the same native work-giver job the defense rearm issues, whose CAS token and pawn
    /// eligibility Hands re-check at dispatch.
    /// </summary>
    internal RoundsBuildingResult RefuelThrone(
        StepArbiter? arbiter,
        ControlState state,
        Store.Rounds review,
        Store.StandardState goal,
        Observation.RoundsReading reading,
        Pol.ThroneStep step, CancellationToken call, CancellationToken epoch)
    {
        var p = Reviewer.Player;
        var tick = reading.Projection.Identity.Tick;
        var history = p.Journal.LoadOwnerMethods(goal, call);
        if (ThroneRefuelRecent(history, step.Lamp, tick))
        {
            return new RoundsBuildingResult { Verdict = WaitFor(Pol.Cause.MethodUsed, "throne_refuel") };
        }
        if (arbiter is null || !arbiter.TryClaim([step.Pawn], "throne-refuel:" + step.Lamp))
        {
            return new RoundsBuildingResult { Verdict = WaitFor(Pol.Cause.MethodUsed, "throne_refuel_claim") };
        }
        var method = new MethodId(string.Create(CultureInfo.InvariantCulture, $"{ThroneRefuelPrefix(step.Lamp)}{tick.Value}"));
        var service = new RecoveryService(new Domain.PawnId(step.Pawn.Value), step.Lamp, Domain.RecoveryMethod.Refuel);
        var id = PlanIds.MintPlanId();
        var action = DomainAction.NewRecoveryServiceAction(new ActionId($"{id}-0"), service);
        var plan = new PlanSpec(id, new PlanRevision(1), [action]);
        p.Current(call, epoch);
        if (p.Session.State() != state)
        {
            throw new ControlException("refuelThrone: p.session.State() != state");
        }
        var latest = p.Journal.LoadRounds(call);
        if (latest.Revision != review.Revision || !latest.Enabled)
        {
            throw new ControlException("refuelThrone: latest.Revision != review.Revision || !latest.Enabled");
        }
        p.Journal.CommitOwnerMethod(goal, method, "", plan, call);
        return new RoundsBuildingResult { Verdict = BuildingReasonAdmitted };
    }
}
