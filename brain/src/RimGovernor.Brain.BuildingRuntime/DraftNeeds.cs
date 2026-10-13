
namespace RimGovernor.Brain.BuildingRuntime;

// Drafts are plan-owned: a plan drafts the pawns it needs, and the undraft sweep undrafts every drafted colonist
// no live plan needs. There is no native claim; the journal's plans and fights are the record.
internal static class DraftNeeds
{
    // The census jobs whose pawn stays drafted though no plan names it: an arrest or capture native is carrying out.
    private static readonly HashSet<string> CustodyJobs = new(StringComparer.Ordinal) { "Arrest", "Capture" };

    /// <summary>
    /// Every pawn a live plan needs drafted: an owned draft not yet settled, or one its plan still holds; the
    /// capturer of an open capture or arrest plan; the roster of an open combat fight.
    /// </summary>
    public static async Task<HashSet<PawnId>> PlannedDraftsAsync(JournalStore journal, CancellationToken ct)
    {
        var plans = await journal.LoadPlansAsync(ct).ConfigureAwait(false);
        var needed = new HashSet<PawnId>();
        foreach (var plan in plans)
        {
            var open = StorePackage.PlanOpen(plan);
            foreach (var progress in plan.Progress)
            {
                var v = progress.View;
                if (progress.Action.OwnedDraft is { } draft)
                {
                    if (v.Unresolved || v.Stage is Stage.Pending or Stage.Prepared or Stage.Dispatched or Stage.AwaitingObservation
                        || Worker.WorkerPlanHoldsDraft(plan, v))
                    {
                        needed.Add((PawnId)draft.Pawn);
                    }
                }
                if (progress.Action.Capture is { } capture && open)
                {
                    needed.Add((PawnId)capture.Capturer);
                }
            }
        }
        foreach (var fight in journal.OpenCombatFights(ct).Values)
        {
            foreach (var pawn in fight.Roster)
            {
                needed.Add((PawnId)pawn);
            }
        }
        return needed;
    }

    /// <summary>The drafted, live, sane colonists of <paramref name="rows"/> that no plan needs and that run no arrest or capture job, sorted.</summary>
    public static List<PawnId> UndraftCandidates(IEnumerable<PbObservations.PawnState?> rows, HashSet<PawnId> needed)
    {
        var output = new List<PawnId>();
        foreach (var row in rows)
        {
            if (row?.Pawn is null)
            {
                continue;
            }
            var id = new PawnId(row.Pawn.Id);
            if (needed.Contains(id) || id.Value == "" || output.Contains(id))
            {
                continue;
            }
            if (Boundary.FactBool(row.HasColonist, row.Colonist) != Known(true) || Boundary.FactBool(row.HasDrafted, row.Drafted) != Known(true)
                || Boundary.FactBool(row.HasDead, row.Dead) != Known(false) || Boundary.FactBool(row.HasDowned, row.Downed) != Known(false)
                || Boundary.FactPresence(row.MentalState is not null, row.Issues, "mental_state") != Known(false))
            {
                continue;
            }
            if (row.Job is null || CustodyJobs.Contains(row.Job.DefName))
            {
                continue;
            }
            output.Add(id);
        }
        output.Sort((a, b) => string.CompareOrdinal(a.Value, b.Value));
        return output;
    }

    /// <summary>
    /// The undraft candidates of a quiet frame: none while the census is incomplete or the shared emergency
    /// policy holds for a threat, and none from a pawn read of another generation.
    /// </summary>
    public static List<PawnId> IdleDrafts(ControlState state, EmergencyFacts emergency, PbObservations.PawnSnapshot? observed, HashSet<PawnId> needed)
    {
        if (emergency.ColonistsComplete != Known(true) || emergency.Colonists.Count == 0 || observed is null)
        {
            return [];
        }
        foreach (var threat in emergency.Threats)
        {
            if (threat.Holds())
            {
                return [];
            }
        }
        try
        {
            Boundary.Context(observed.Context, state.Snapshot);
        }
        catch (DomainException)
        {
            throw new ControlException("idleDrafts: pawn read context mismatch");
        }
        return UndraftCandidates(observed.Pawns, needed);
    }

    /// <summary>
    /// Sends one Draft intent per pawn, drafted false, keyed by tick. A refusal (the pawn died or left) is logged
    /// and retried by the next review's sweep if it still stands.
    /// </summary>
    public static async Task UndraftAsync(IActionsWriter writer, PbCommon.Identity identity, Tick tick, IReadOnlyList<PawnId> pawns, CancellationToken ct)
    {
        if (pawns.Count == 0)
        {
            return;
        }
        var actions = pawns.Select(pawn => BridgeApi.UndraftAction($"undraft-{pawn.Value}-{tick.Value}", new Domain.PawnId(pawn.Value))).ToList();
        var reply = await writer.ApplyAsync(identity, actions, ct).ConfigureAwait(false);
        for (var i = 0; i < reply.Results.Count; i++)
        {
            var result = reply.Results[i];
            var verdict = "applied";
            var level = TelemetryLevel.Info;
            var detail = "";
            if (result.Refused is { } refused)
            {
                verdict = "refused";
                detail = refused.Reason;
            }
            else if (result.Failed is { } failed)
            {
                verdict = "failed";
                level = TelemetryLevel.Warn;
                detail = failed.Detail;
            }
            BuildingRuntimePackage.DefenseAction("undraft-sweep", level, verdict, "undraft", pawns[i].Value, new Dictionary<string, object?> { ["detail"] = detail }, ct);
        }
    }
}

public sealed partial class Rounder
{
    /// <summary>
    /// Undrafts every drafted colonist no live plan needs. Reads a fresh frame, holds the pawns against the
    /// step's other planners and sends the Draft intents directly: an undraft owns no plan. A reviewer without
    /// an actions writer never undrafts.
    /// </summary>
    internal async Task SweepDraftsAsync(StepArbiter arbiter, CancellationToken ct, CancellationToken epoch)
    {
        var p = Player;
        var state = p.State();
        if (Undraft is null || !state.Enabled)
        {
            return;
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
        var frame = await Native.ReadRoundsFrameAsync(Boundary.Identity(state.Snapshot), ct).ConfigureAwait(false);
        try
        {
            Boundary.Context(frame.Context, state.Snapshot);
        }
        catch (DomainException)
        {
            throw new ControlException("sweepDrafts: context mismatch");
        }
        var needed = await DraftNeeds.PlannedDraftsAsync(p.Journal, ct).ConfigureAwait(false);
        var pawns = DraftNeeds.IdleDrafts(state, frame.Emergency.Facts, frame.Pawns, needed);
        if (pawns.Count == 0 || !arbiter.TryClaim(pawns))
        {
            return;
        }
        p.Current(ct, epoch);
        var elapsed = Clock.Now() - started;
        if (p.State() != state || elapsed < TimeSpan.Zero || elapsed > MaxAge)
        {
            throw new ControlException("sweepDrafts: session changed or read too old");
        }
        await DraftNeeds.UndraftAsync(Undraft, Boundary.Identity(state.Snapshot), new Tick((long)(frame.Context?.Tick ?? 0)), pawns, ct).ConfigureAwait(false);
    }
}
