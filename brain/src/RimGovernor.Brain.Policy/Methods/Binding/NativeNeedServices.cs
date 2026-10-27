using RimGovernor.Brain.GameFacts.Observations;

namespace RimGovernor.Brain.Policy.Methods.Binding;

/// <summary>Current service evidence only. Neither a legal target nor ongoing service promises future recovery.</summary>
internal enum NativeNeedService
{
    Unobserved,
    Ineligible,
    NoObservedService,
    Available,
    InProgress,
}

internal static class NativeNeedServices
{
    public static NativeNeedService Observe(NeedsForecastRead read, string pawnId, string kind)
    {
        var pawn = read.Pawns.SingleOrDefault(p => p.Pawn == pawnId);
        if (pawn is null) return NativeNeedService.Unobserved;
        if (pawn.CurrentService?.Kind == kind) return NativeNeedService.InProgress;
        if (pawn.State is not ("eligible" or "unsupported_voluntary_state") || kind == "rest" && pawn.SleepBlocked)
            return NativeNeedService.Ineligible;
        // Targets have passed native legality, policy, reservation and reachability checks.
        // Unsupported describes limits of the detailed forecast; it does not revoke that current legality.
        if (pawn.Targets.Any(t => t.Kind == kind && t.AvailableCount > 0 && (t.Recovery switch
            { NeedRecovery.Ingest ingest => ingest.GainPerUnit > 0, NeedRecovery.Rate rate => rate.GainPerTick > 0,
                var invalid => throw new RequiredFactException("native need recovery:" + invalid) })))
            return NativeNeedService.Available;
        // Ground rest and spontaneous recreation need no captured service object. Absence is not proof of failure.
        return NativeNeedService.NoObservedService;
    }

    /// <summary>A pure service portfolio with current native response has no independent intervention premise.
    /// Productive/dependent portfolios still require the general execution-baseline/intervention migration.</summary>
    internal static bool RoutineOnly(ForecastActivityPlan plan, ForecastCoupledContext context)
    {
        if (plan.Allocations.Length == 0) return false;
        foreach (var allocation in plan.Allocations)
        {
            if (!context.Bindings.TryGetValue((allocation.Activity, allocation.Pawn), out var task)) return false;
            if (task.Kind is not (PawnActivityKind.Eat or PawnActivityKind.Sleep or PawnActivityKind.Recreation)) return false;
            var kind = task.Kind == PawnActivityKind.Eat ? "food" : task.Kind == PawnActivityKind.Sleep ? "rest" : "joy";
            if (!context.NeedTasks.Any(n => n.Task == allocation.Activity && n.Pawn == allocation.Pawn && n.Kind == kind)
                || Observe(context.Needs, allocation.Pawn, kind) is not (NativeNeedService.Available or NativeNeedService.InProgress)) return false;
        }
        return true;
    }

    public const string RoutineResponse = "native_service_response_without_intervention_premise";
}
