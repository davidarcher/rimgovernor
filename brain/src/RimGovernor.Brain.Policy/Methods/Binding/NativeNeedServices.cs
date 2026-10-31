using RimGovernor.Brain.GameFacts.Observations;

namespace RimGovernor.Brain.Policy.Methods.Binding;

/// <summary>Current service evidence only. Neither a legal target nor ongoing service promises future recovery.</summary>
internal enum NativeNeedService
{
    Unobserved,
    Ineligible,
    NoObservedService,
    Available,
    /// <summary>A legal target is available, the need is at its zero floor and the pawn's current native job serves another
    /// need or none (<see cref="NeedsForecastPawn.ServedNeed"/>): native operation is observed not answering the need now.</summary>
    Unanswered,
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
            return pawn.Needs.FirstOrDefault(n => n.Kind == kind) is { Level: <= 0 } && pawn.ServedNeed != kind
                ? NativeNeedService.Unanswered : NativeNeedService.Available;
        // Ground rest and spontaneous recreation need no captured service object. Absence is not proof of failure.
        return NativeNeedService.NoObservedService;
    }

    /// <summary>Whether the comparison keeps the conditional forecast of a pawn's need: an ineligible pawn's own owners govern
    /// it, and an unanswered need has no native response to defer to.</summary>
    internal static bool ConditionalKept(NativeNeedService response) => response is NativeNeedService.Ineligible or NativeNeedService.Unanswered;

    /// <summary>Ordinary self-service is a dispatch boundary even when current access is blocked or unread.
    /// A what-if binding has no intervention authority, regardless of its forecasted benefit.</summary>
    internal static bool RoutineTask(string activity, string pawn, ForecastCoupledContext context)
    {
        return context.Bindings.TryGetValue((activity, pawn), out var task) && Routine(task.Kind);
    }

    internal static bool Routine(PawnActivityKind activity) =>
        activity is PawnActivityKind.Eat or PawnActivityKind.Sleep or PawnActivityKind.Recreation;

    /// <summary>The need an ordinary self-service activity serves: food, rest or joy.</summary>
    internal static string NeedOf(PawnActivityKind activity) => activity == PawnActivityKind.Eat ? "food"
        : activity == PawnActivityKind.Sleep ? "rest"
        : activity == PawnActivityKind.Recreation ? "joy" : throw new DomainException("not a self-service activity: " + activity);

    /// <summary>A pure native-service portfolio supplies no independent intervention premise.</summary>
    internal static bool RoutineOnly(ForecastActivityPlan plan, ForecastCoupledContext context) =>
        plan.Allocations.Length != 0 && plan.Allocations.All(a => RoutineTask(a.Activity, a.Pawn, context));
}
