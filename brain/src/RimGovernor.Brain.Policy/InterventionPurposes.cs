using RimGovernor.Brain.GameFacts.Observations;
using RimGovernor.Brain.Policy.Methods.Binding;

namespace RimGovernor.Brain.Policy;

/// <summary>One installed exact intervention's observed outcomes at a review, as <c>pawn:outcome</c>.</summary>
public sealed record InterventionReviewOutcome(PlanId Plan, EquatableArray<string> Outcomes);

/// <summary>What the native execution row says happened to one exact order (pawn-activities contract, "Observed outcome"):
/// acceptance is not completion, and a release or replacement is never read as completed work.</summary>
public enum ObservedOutcome
{
    /// <summary>Native installed or offered the order; no step has started.</summary>
    [WireName("accepted")]
    Accepted,

    /// <summary>The order's job runs, or a preparation holds the pawn at its boundary.</summary>
    [WireName("running")]
    Running,

    /// <summary>Every step of the order completed under its owner.</summary>
    [WireName("completed")]
    Completed,

    /// <summary>The owned order stopped on its own job condition, deadline or an involuntary state.</summary>
    [WireName("failed")]
    Failed,

    /// <summary>Native work took the pawn over: another owner's row, or the order's own failure with a native job takeover.</summary>
    [WireName("replaced")]
    Replaced,

    /// <summary>Native no longer owns any order for the pawn.</summary>
    [WireName("released")]
    Released,
}

/// <summary>Whether an exact intervention's purpose still stands. Neither end is ever inferred from elapsed ticks.</summary>
public enum PurposeCondition
{
    /// <summary>The purpose stands, or the reading needed to end it is not observed.</summary>
    [WireName("open")]
    Open,

    /// <summary>The observation the purpose's kind names as completion holds.</summary>
    [WireName("completed")]
    Completed,

    /// <summary>The observation the purpose's kind names as cancellation holds: the premise no longer needs this order.</summary>
    [WireName("cancelled")]
    Cancelled,
}

/// <summary>
/// The one pure place an exact intervention's purpose and observed outcome are read (RG-LABOR-005): the worker's
/// continuation and the review's coupled execution both decide from it, and the <c>coupled_observed</c> telemetry names it.
/// </summary>
public static class InterventionPurposes
{
    /// <summary>The refusal prefix of an intervention whose fresh prerequisites no longer hold (admission or continuation).</summary>
    public const string StalePrerequisitePrefix = "stale_prerequisite:";

    /// <summary>The refusal prefix of a same-pawn, same-aim intervention while one is still open.</summary>
    public const string AlreadyOpenPrefix = "intervention_already_open:";

    /// <summary>The refusal prefix of a reissue whose evidence is not newer than the end of an earlier same-aim intervention.</summary>
    public const string StaleJustificationPrefix = "stale_justification:";

    /// <summary>The release reason of a batch whose purposes all ended.</summary>
    public const string PurposeEndedReason = "intervention_purpose_ended";

    /// <summary>The native failure reason of an order a native job took over (NativePawnActivities).</summary>
    public const string NativeJobTakeover = "native_job_takeover";

    /// <summary>The purpose a justified premise gives the orders that answer it; <paramref name="evidenceTick"/> is the observation's tick.</summary>
    public static InterventionPurpose? Of(InterventionPremise premise, long evidenceTick)
    {
        if (premise is InterventionPremise.ObservedShortfall shortfall)
            return new(InterventionPurposeKind.NeedShortfall, shortfall.Need, shortfall.Pawn, evidenceTick);
        if (premise is InterventionPremise.CredibleRisk) return new(InterventionPurposeKind.FoodRisk, "food", "", evidenceTick);
        if (premise is InterventionPremise.DemonstratedStall stall)
            return new(InterventionPurposeKind.ProductionStall, stall.Stall.Product.Value, "", evidenceTick);
        if (premise is InterventionPremise.CapabilityOpportunity capability)
            return new(InterventionPurposeKind.CapabilityPractice, capability.Requirement, capability.Pawn, evidenceTick);
        // Unassessed and None premises justify nothing.
        return null;
    }

    /// <summary>The outcome one native row shows for <paramref name="order"/>.</summary>
    public static ObservedOutcome Observe(PawnActivityOrder order, ActivityOwnership row)
    {
        if (!ActivityContinuationRules.MatchesActivityOwner(order, row))
            return row.Method.Length == 0 || row.State is "unowned" or "released" ? ObservedOutcome.Released : ObservedOutcome.Replaced;
        return row.State switch
        {
            "installed" or "offered" => ObservedOutcome.Accepted,
            "completed" => ObservedOutcome.Completed,
            "failed" => row.Reason == NativeJobTakeover ? ObservedOutcome.Replaced : ObservedOutcome.Failed,
            "released" or "unowned" => ObservedOutcome.Released,
            // Started, holding at a preparation boundary, or any other live state continuation never treated as terminal.
            _ => ObservedOutcome.Running,
        };
    }

    /// <summary>The review's ownership row of one native pawn read: the same fields the continuation read carries.</summary>
    public static ActivityOwnership Ownership(WorkResourcePawn row) =>
        new(row.Pawn, row.Assignment, row.Method, row.Revision, row.State) { Reason = row.Reason };

    /// <summary>
    /// The review's observed outcomes of every installed exact intervention (accepted receipt, settled dispatch, a <c>coupled-</c>
    /// order), classified by <see cref="Observe"/> from the review's native ownership rows exactly as continuation classifies its
    /// read: a native job takeover is <see cref="ObservedOutcome.Replaced"/>, never <see cref="ObservedOutcome.Failed"/>. A pawn
    /// read other than exactly once contributes no outcome.
    /// </summary>
    public static EquatableArray<InterventionReviewOutcome> ReviewOutcomes(WorkActionCensus actions, EquatableArray<WorkResourcePawn> native)
    {
        var outcomes = new List<InterventionReviewOutcome>();
        foreach (var row in actions.Actions.OrderBy(a => a.Plan.Value, StringComparer.Ordinal).ThenBy(a => a.Action.Id.Value, StringComparer.Ordinal))
        {
            if (row.Progress.Unresolved || !row.Progress.Received(Receipt.Accepted) || row.Progress.Stage is not (Stage.Completed or Stage.Cancelled)
                || row.Action.PawnActivities is not { } batch || !batch.Orders.Any(PawnActivities.Intervention)) continue;
            var rows = batch.Orders.Where(o => !o.Release).Select(o => native.Where(p => p.Pawn == o.Pawn).ToArray())
                .Where(r => r.Length == 1).Select(r => Ownership(r[0])).ToArray();
            outcomes.Add(new(row.Plan, EquatableArray.From(Outcomes(batch, rows))));
        }
        return EquatableArray.From(outcomes);
    }

    /// <summary>The batch's observed outcomes as <c>pawn:outcome</c>, for every non-release order a row reads.</summary>
    public static string[] Outcomes(PawnActivities batch, IReadOnlyList<ActivityOwnership> observed) =>
        [.. batch.Orders.Where(o => !o.Release).SelectMany(o => observed.Where(r => r.Pawn == o.Pawn)
            .Select(r => o.Pawn + ":" + Wire.Name(Observe(o, r))))];

    /// <summary>The purposes the batch's non-release orders carry, once each.</summary>
    public static InterventionPurpose[] Purposes(PawnActivities batch) =>
        [.. batch.Orders.Where(o => !o.Release && o.Purpose is not null).Select(o => o.Purpose!).Distinct()];

    /// <summary>
    /// The condition the worker's continuation read observes: a need shortfall completes once its pawn's need reads above
    /// its native threshold, and is cancelled once native service of that need is observed for a pawn the batch does not
    /// order. The other kinds need the review's context and stay open here.
    /// </summary>
    public static PurposeCondition AtContinuation(InterventionPurpose purpose, PawnActivities batch, NeedsForecastRead? needs) =>
        purpose.Kind != InterventionPurposeKind.NeedShortfall || needs is null ? PurposeCondition.Open : NeedShortfall(purpose, batch, needs);

    /// <summary>
    /// The condition the review's context observes for every kind: a need shortfall as at continuation; a food risk completes
    /// once the food owner reviews no response needed and is cancelled once no consumer remains; a production stall completes
    /// once the reconciled calibration holds no stall of its product and is cancelled once that stall's cause is one orders
    /// cannot answer; capability practice completes once the native baseline projects no missing XP and is cancelled once
    /// the requirement is withdrawn. Unread or unobserved evidence leaves it open.
    /// </summary>
    public static PurposeCondition AtReview(InterventionPurpose purpose, PawnActivities batch, ForecastCoupledContext context,
        ForecastConsequenceVector? baseline)
    {
        if (purpose.Kind == InterventionPurposeKind.NeedShortfall) return NeedShortfall(purpose, batch, context.Needs);
        if (purpose.Kind == InterventionPurposeKind.FoodRisk)
            return context.NativeFood is NativeFoodAssessment.Reviewed { NeedsResponse: false } ? PurposeCondition.Completed
                : context.NativeFood is NativeFoodAssessment.NoConsumers ? PurposeCondition.Cancelled : PurposeCondition.Open;
        if (purpose.Kind == InterventionPurposeKind.ProductionStall)
            return context.ProductionCalibration is not ProductionCalibration.Observed(var stalls) ? PurposeCondition.Open
                : stalls.FirstOrDefault(s => s.Product.Value == purpose.Subject) is not { } stall ? PurposeCondition.Completed
                : stall.AnsweredByOrders ? PurposeCondition.Open : PurposeCondition.Cancelled;
        if (purpose.Kind != InterventionPurposeKind.CapabilityPractice) throw new DomainException("intervention purpose kind: " + purpose.Kind);
        return context.MaintenanceRequirements.All(r => r.Requirement.Id != purpose.Subject) ? PurposeCondition.Cancelled
            : baseline?.Maintenance.FirstOrDefault(m => m.Requirement == purpose.Subject)?.MissingXp is ForecastMaintenanceGap.Projected(<= 0)
                ? PurposeCondition.Completed : PurposeCondition.Open;
    }

    /// <summary>A batch whose purposes have all ended is completed when every one completed, otherwise cancelled; open while
    /// any stands and when no order carries a purpose. A meal, sleep or recreation order is cancelled unless it carries the
    /// NeedShortfall of the need it serves for its own pawn (the one exception <see cref="InterventionRules.Readmitted"/> admits):
    /// any other such shape, a routine self-service order persisted before those were deleted, is released, never continued.</summary>
    public static PurposeCondition Ended(PawnActivities batch, Func<InterventionPurpose, PurposeCondition> condition)
    {
        if (batch.Orders.Any(o => RoutineSelfService(batch, o))) return PurposeCondition.Cancelled;
        var purposes = Purposes(batch);
        if (purposes.Length == 0) return PurposeCondition.Open;
        var conditions = purposes.Select(condition).ToArray();
        return conditions.Any(c => c == PurposeCondition.Open) ? PurposeCondition.Open
            : conditions.All(c => c == PurposeCondition.Completed) ? PurposeCondition.Completed : PurposeCondition.Cancelled;
    }

    private static bool RoutineSelfService(PawnActivities batch, PawnActivityOrder order)
    {
        if (order.Release) return false;
        // The order's own steps, and for a preparation holding ahead of its task, that task's.
        var kinds = order.Steps.Select(s => s.Kind)
            .Concat(batch.Tasks.Where(t => order.Preparing && t.Id == order.Task).Select(t => t.Step.Kind));
        return kinds.Where(NativeNeedServices.Routine).Select(k => (PawnActivityKind?)k).FirstOrDefault() is { } kind
            && !(order.Purpose is { Kind: InterventionPurposeKind.NeedShortfall } purpose
                && purpose.Subject == NativeNeedServices.NeedOf(kind) && purpose.SubjectPawn == order.Pawn);
    }

    private static PurposeCondition NeedShortfall(InterventionPurpose purpose, PawnActivities batch, NeedsForecastRead needs)
    {
        var pawn = needs.Pawns.SingleOrDefault(p => p.Pawn == purpose.SubjectPawn);
        if (pawn?.Needs.FirstOrDefault(n => n.Kind == purpose.Subject) is not { } need) return PurposeCondition.Open;
        if (need.Level > need.Threshold) return PurposeCondition.Completed;
        var ordered = batch.Orders.Any(o => !o.Release && o.Pawn == purpose.SubjectPawn);
        return !ordered && NativeNeedServices.Observe(needs, purpose.SubjectPawn, purpose.Subject) is NativeNeedService.Available or NativeNeedService.InProgress
            ? PurposeCondition.Cancelled : PurposeCondition.Open;
    }

    /// <summary>
    /// The first stale prerequisite of an exact intervention against the frozen admission evidence, or null: an ordered pawn
    /// the evidence reads unavailable (drafted, downed, in a mental state or in native care, <see cref="WorkPawnAvailability.Available"/>)
    /// or an observed input a task consumes whose resource the evidence no longer stocks in that quantity. Evidence that
    /// reads no stock leaves the input check to the claims.
    /// </summary>
    public static string? StalePrerequisite(WorkAdmissionEvidence evidence, PawnActivities batch)
    {
        foreach (var order in batch.Orders.Where(o => !o.Release && o.Purpose is not null).OrderBy(o => o.Pawn, StringComparer.Ordinal))
            if (evidence.Pawns.FirstOrDefault(p => p.Pawn == order.Pawn) is { Available: false })
                return StalePrerequisitePrefix + "pawn_unavailable:" + order.Pawn;
        if (evidence.Stock.Count == 0) return null;
        var stock = evidence.Stock.GroupBy(a => a.Resource.Value, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Sum(a => a.Count), StringComparer.Ordinal);
        var wanted = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var task in batch.Tasks.OrderBy(t => t.Id, StringComparer.Ordinal))
            foreach (var input in task.Inputs.Where(i => i.Observed && i.Producers.Count == 0))
            {
                wanted[input.Resource] = wanted.GetValueOrDefault(input.Resource) + input.Count;
                if (wanted[input.Resource] > stock.GetValueOrDefault(input.Resource))
                    return StalePrerequisitePrefix + "input_consumed:" + task.Id + ":" + input.Resource;
            }
        return null;
    }
}
