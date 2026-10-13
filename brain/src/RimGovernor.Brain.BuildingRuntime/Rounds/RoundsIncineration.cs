namespace RimGovernor.Brain.BuildingRuntime;

public interface IRoundsIncinerationSource : IColonySource
{
    (EmergencyObservation Emergency, BridgeResult Result) ReadEmergency(CancellationToken ct, C.Identity identity);

    (O.ListPawnsReply Reply, BridgeResult Result) ReadTendPawns(CancellationToken ct, C.Identity identity, IReadOnlyList<string> pawns);
}

/// <summary>Composes MaintainIncineration: the fireproof incinerator shell, then the burn and the ash cleanup.</summary>
public sealed partial class RoundsIncinerationPlanner
{
    internal Rounder Reviewer { get; init; } = null!;

    internal IRoundsIncinerationSource Native { get; init; } = null!;

    internal RoundsBuildingPlanner? Building { get; init; }

    public static RoundsIncinerationPlanner NewRoundsIncinerationPlanner(Rounder reviewer, IRoundsIncinerationSource native)
    {
        if (reviewer is null || native is null)
        {
            throw new ControlException("NewRoundsIncinerationPlanner: reviewer or native is missing");
        }

        RoundsBuildingPlanner? building = null;
        if (native is IRoundsBuildingSource source)
        {
            building = new RoundsBuildingPlanner { Reviewer = reviewer, Native = source, Concern = ConcernId.MaintainIncineration };
        }

        return new RoundsIncinerationPlanner { Reviewer = reviewer, Native = native, Building = building };
    }
}

public readonly record struct RoundsIncinerationResult
{
    public Verdict Verdict { get; init; }

    public PlanId Plan { get; init; }
}

internal static partial class BuildingRuntimePackage
{
    internal static readonly string[] IncinerationDefinitions = ["Wall", "Door"];

    /// <summary>The colonists' pawn rows when the emergency read lists them completely; none when the roster is unknown or incomplete.</summary>
    internal static (IReadOnlyList<O.PawnState> Rows, bool Complete) ColonistRows(CancellationToken call, IRoundsIncinerationSource native, ControlState state, Rounds review)
    {
        var identityRef = Boundary.Identity(state.Snapshot);
        var (emergency, _) = native.ReadEmergency(call, identityRef);
        try
        {
            Boundary.Context(emergency.Context, state.Snapshot);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            throw new ControlException("colonistRows: emergency context", e);
        }

        if (emergency.Context.Tick < review.Tick.Value)
        {
            throw new ControlException("colonistRows: emergency read older than the review");
        }

        if (!emergency.Facts.ColonistsComplete.TryGetValue(out var complete) || !complete || emergency.Facts.Colonists.Count == 0)
        {
            return ([], false);
        }

        var ids = emergency.Facts.Colonists.Select(pawn => pawn.Id.Value).ToList();
        var (reply, _) = native.ReadTendPawns(call, identityRef, ids);
        var observed = reply.Observed;
        if (observed is null)
        {
            throw new ControlException("colonistRows: no observed pawns");
        }

        try
        {
            Boundary.Context(observed.Context, state.Snapshot);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            throw new ControlException("colonistRows: observed context", e);
        }

        if (observed.Pawns.Count != ids.Count)
        {
            throw new ControlException("colonistRows: pawn count differs from the roster");
        }

        return (observed.Pawns, true);
    }
}

public sealed partial class RoundsIncinerationPlanner
{
    internal RoundsIncinerationResult Step(CancellationToken call, CancellationToken epoch, StepArbiter arbiter)
    {
        var p = Reviewer.Player;
        var state = p.Session.State();
        if (!state.Enabled)
        {
            return new RoundsIncinerationResult { Verdict = BuildingReasonDisabled };
        }

        if (!state.ObservationKnown || !state.Snapshot.IsValid || state.Snapshot.Native.Value == 0)
        {
            throw new ControlException("step: observation unknown, snapshot invalid or native generation unset");
        }

        var review = p.Journal.LoadRounds(call);
        if (!review.Enabled || !review.Snapshot.Matches(state.Snapshot))
        {
            return new RoundsIncinerationResult { Verdict = BuildingReasonNoReview };
        }

        var (goal, workable) = p.Journal.Workable(call, review, ConcernId.MaintainIncineration);
        if (!workable)
        {
            return new RoundsIncinerationResult { Verdict = BuildingReasonNoDeficit };
        }

        foreach (var m in goal.Methods)
        {
            var open = p.Journal.LoadPlan(call, m.Plan);
            if (StorePackage.PlanOpen(open))
            {
                return new RoundsIncinerationResult { Verdict = BuildingReasonExistingWork };
            }
        }

        if (Native is not IRoundsSource source || Building is null)
        {
            return new RoundsIncinerationResult { Verdict = BuildingReasonNoDeficit };
        }

        var (identity, _) = Native.Identity(call);
        var expected = ObservationPackage.DecodeIdentity(identity);
        if (!RoundsBuildingBoundary(expected, state.Snapshot, review.Tick))
        {
            throw new ControlException("step: boundary moved");
        }

        var reading = Reviewer.ObserveRooms(call, source, expected, Unknown<IReadOnlyList<ConstructionClaim>>(), IncinerationDefinitions);
        var (result, handled) = StageDisposal(call, epoch, state, review, goal, arbiter, reading);
        if (handled)
        {
            return result;
        }

        return new RoundsIncinerationResult { Verdict = WaitFor(Cause.MethodUsed, "incineration") };
    }
}
