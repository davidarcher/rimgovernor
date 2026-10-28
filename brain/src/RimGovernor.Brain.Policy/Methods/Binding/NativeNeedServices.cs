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

    /// <summary>Ordinary self-service is a dispatch boundary even when current access is blocked or unread.
    /// A what-if binding has no intervention authority, regardless of its forecasted benefit.</summary>
    internal static bool RoutineTask(string activity, string pawn, ForecastCoupledContext context)
    {
        return context.Bindings.TryGetValue((activity, pawn), out var task) && Routine(task.Kind);
    }

    internal static bool Routine(PawnActivityKind activity) =>
        activity is PawnActivityKind.Eat or PawnActivityKind.Sleep or PawnActivityKind.Recreation;

    /// <summary>A pure native-service portfolio supplies no independent intervention premise.</summary>
    internal static bool RoutineOnly(ForecastActivityPlan plan, ForecastCoupledContext context) =>
        plan.Allocations.Length != 0 && plan.Allocations.All(a => RoutineTask(a.Activity, a.Pawn, context));

    public const string RoutineResponse = "native_service_response_without_intervention_premise";
}
