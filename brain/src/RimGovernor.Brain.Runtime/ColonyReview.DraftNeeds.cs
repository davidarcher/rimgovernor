
namespace RimGovernor.Brain.Runtime;

// Drafts are plan-owned: a plan drafts the pawns it needs, and the undraft sweep undrafts every drafted colonist
// no live plan needs. There is no native claim; the journal's plans and fights are the record.
internal static class DraftNeeds
{
    /// <summary>
    /// The undraft candidates of a quiet frame: none while the census is incomplete or the shared emergency
    /// policy holds for a threat, and none from a pawn read of another generation.
    /// </summary>
    public static List<PawnId> IdleDrafts(ControlState state, EmergencyFacts emergency, RimGovernor.Brain.GameFacts.Observations.PawnSnapshot? observed, HashSet<PawnId> needed)
    {
        if (observed is null || DraftReleaseRules.ReleaseHeld(emergency))
        {
            return [];
        }
        try
        {
            Boundary.Context(observed.Context, state.Snapshot);
        }
        catch (DomainException)
        {
            throw new ControlException("idleDrafts: pawn read context mismatch");
        }
        return DraftReleaseRules.UndraftCandidates(observed.Pawns, needed,
            nameof(RimWorld.JobDefOf.Arrest), nameof(RimWorld.JobDefOf.Capture));
    }

    /// <summary>
    /// The stray drafts of a frame for <c>military/restore-workers</c> (#3311): the undraft candidates' count, none
    /// while a threat holds the drafts, unknown while the colonist census is incomplete or the pawns are unread.
    /// </summary>
    public static int StrayDrafts(ControlState state, EmergencyFacts emergency, RimGovernor.Brain.GameFacts.Observations.PawnSnapshot? observed, HashSet<PawnId> needed) =>
        !emergency.AllSeen || observed is null
            ? throw new RequiredFactException("complete drafted pawn census")
            : IdleDrafts(state, emergency, observed, needed).Count;

}

public sealed partial class ColonyReview
{
    /// <summary>
    /// The <c>military/restore-workers</c> work step (#3311): undrafts every drafted colonist no live plan needs.
    /// Reads a fresh frame, holds the pawns against the step's other planners and admits the release Method.
    /// Hands dispatches its actions through the same authority and receipt checks as other work.
    /// </summary>
    internal async Task<Verdict> SweepDraftsAsync(StepArbiter arbiter, CancellationToken ct, CancellationToken epoch)
    {
        var p = Player;
        var state = p.State();
        if (!state.Enabled)
        {
            return Verdict.BuildingReasonDisabled;
        }
        if (!state.ObservationKnown)
        {
            throw new ControlException("sweepDrafts: observation scope unknown");
        }
        try
        {
            state.Snapshot.Validate();
        }
        catch (DomainException)
        {
            throw new ControlException("sweepDrafts: invalid snapshot");
        }
        var started = Clock.Now();
        var frame = await Native.ReadReviewFrameAsync(Boundary.Identity(state.Snapshot), ct).ConfigureAwait(false);
        try
        {
            Boundary.Context(frame.Context, state.Snapshot);
        }
        catch (DomainException)
        {
            throw new ControlException("sweepDrafts: context mismatch");
        }
        var needed = await p.Journal.PlannedDraftsAsync(ct).ConfigureAwait(false);
        var pawns = DraftNeeds.IdleDrafts(state, frame.Emergency.Facts, frame.Pawns, needed);
        if (pawns.Count == 0)
        {
            return Verdict.BuildingReasonNoDeficit;
        }
        if (!arbiter.TryClaim(pawns))
        {
            return Verdict.WaitFor(Cause.Claim, "undraft_pawns");
        }
        p.Current(ct, epoch);
        var elapsed = Clock.Now() - started;
        if (p.State() != state || elapsed < TimeSpan.Zero || elapsed > MaxAge)
        {
            throw new ControlException("sweepDrafts: session changed or read too old");
        }
        var id = PlanIds.MintPlanId();
        var actions = pawns.Select((pawn, index) => DomainAction.NewUndraftAction(new ActionId($"{id}-{index}"), new Undraft(pawn))).ToArray();
        var plan = new PlanSpec(id, new PlanRevision(1), actions);
        var commit = p.Journal.CommitClaimedMethod(MilitaryFamily.RestoreWorkers, state.Snapshot,
            new MethodId("restore-workers"), "no live plan needs these drafts", plan, ct);
        return Verdict.Committed(commit);
    }
}
