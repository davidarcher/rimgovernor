namespace RimGovernor.Brain.BuildingRuntime;

public interface IRoundsFireSafetySource : IColonySource
{
    BridgeEmergencyObservation ReadEmergency(C.Identity identity, CancellationToken ct);

    O.ListPawnsReply ReadTendPawns(C.Identity identity, IReadOnlyList<string> ids, CancellationToken ct);
}

public sealed class RoundsFireSafetyResult
{
    public Verdict Verdict { get; init; }

    public FireSafetyOutcome Outcome { get; init; }

    public uint NativeWorkTicks { get; init; }
}

/// <summary>Keeps the colonists able to fight fires.</summary>
public sealed class RoundsFireSafetyPlanner
{
    internal const uint FireSafetyNativeWorkTicks = 600;

    private readonly Rounder _reviewer;
    private readonly IRoundsFireSafetySource _native;

    public RoundsFireSafetyPlanner(Rounder reviewer, IRoundsFireSafetySource native)
    {
        if (reviewer is null || native is null)
        {
            throw new ControlException("NewRoundsFireSafetyPlanner: reviewer == nil || native == nil");
        }
        _reviewer = reviewer;
        _native = native;
    }

    internal RoundsFireSafetyResult Step(CancellationToken call, CancellationToken epoch)
    {
        var p = _reviewer.Player;
        var state = p.Session.State();
        if (!state.Enabled)
        {
            return new RoundsFireSafetyResult { Verdict = BuildingReasonDisabled };
        }
        if (!state.ObservationKnown || !state.Snapshot.IsValid || state.Snapshot.Native == 0)
        {
            throw new ControlException("step: !state.ObservationKnown || state.Snapshot.Validate() != nil || state.Snapshot.Native == 0");
        }
        var review = p.Journal.LoadRounds(call);
        if (!review.Enabled || !review.Snapshot.Matches(state.Snapshot))
        {
            return new RoundsFireSafetyResult { Verdict = BuildingReasonNoReview };
        }
        var (_, workable) = p.Journal.Workable(review, PolicyApi.MaintainFireSafety, call);
        if (!workable)
        {
            return new RoundsFireSafetyResult { Verdict = BuildingReasonNoDeficit, Outcome = FireSafetyOutcome.Recovered };
        }
        var expected = StepScope(call, _native);
        if (!RoundsBuildingBoundary(expected, state.Snapshot, review.Tick))
        {
            throw new ControlException("step: !roundsBuildingBoundary(expected, state.Snapshot, review.Tick)");
        }
        var reading = _reviewer.ObserveColony(call, _native, expected, null);
        var upkeepReview = PolicyApi.ReviewUpkeep(reading.Projection.Facts.Upkeep, new UpkeepHistory(), null);
        var active = false;
        var known = false;
        foreach (var need in upkeepReview.Needs)
        {
            if (need.Concern == PolicyApi.MaintainFireSafety)
            {
                active = need.Active;
                known = need.Targets.IsKnown;
            }
        }
        if (!active)
        {
            return new RoundsFireSafetyResult { Verdict = BuildingReasonNoDeficit, Outcome = FireSafetyOutcome.Recovered };
        }
        var identityRef = Boundary.Identity(state.Snapshot);
        var emergency = _native.ReadEmergency(identityRef, call);
        try
        {
            Boundary.Context(emergency.Context, state.Snapshot);
        }
        catch (ControlException)
        {
            throw new ControlException("step: err != nil || emergency.Context.GetTick() < int64(review.Tick)");
        }
        if (emergency.Context.Tick < (long)review.Tick)
        {
            throw new ControlException("step: err != nil || emergency.Context.GetTick() < int64(review.Tick)");
        }
        if (!(emergency.Facts.ColonistsComplete.TryGetValue(out var complete) && complete) || emergency.Facts.Colonists.Count == 0)
        {
            return new RoundsFireSafetyResult { Verdict = FieldUnavailable("colonists_complete"), Outcome = FireSafetyOutcome.Unknown };
        }
        var ids = emergency.Facts.Colonists.Select(pawn => pawn.Id.Value).ToList();
        var reply = _native.ReadTendPawns(identityRef, ids, call);
        if (!reply.HasObserved)
        {
            throw new ControlException("step: observed == nil");
        }
        var observed = reply.Observed;
        try
        {
            Boundary.Context(observed.Context, state.Snapshot);
        }
        catch (ControlException)
        {
            throw new ControlException("step: err != nil");
        }
        if (observed.Pawns.Count != ids.Count)
        {
            throw new ControlException("step: len(observed.Pawns) != len(ids)");
        }
        var pawns = new List<FireSafetyPawnFacts>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in observed.Pawns)
        {
            if (row is null || row.Pawn is null || !seen.Add(row.Pawn.Id))
            {
                throw new ControlException("step: row == nil || row.Pawn == nil || seen[row.Pawn.GetId()]");
            }
            pawns.Add(FireSafetyPawnFactsOf(new PawnId(row.Pawn.Id), row));
        }
        p.Current(call, epoch);
        if (p.Session.State() != state)
        {
            throw new ControlException("step: p.session.State() != state");
        }
        var outcome = PolicyApi.EvaluateFireSafety(active, known, pawns);
        switch (outcome)
        {
            case FireSafetyOutcome.WaitingForNative:
                return new RoundsFireSafetyResult { Verdict = BuildingReasonExistingWork, Outcome = outcome, NativeWorkTicks = FireSafetyNativeWorkTicks };
            case FireSafetyOutcome.Unknown:
                return new RoundsFireSafetyResult { Verdict = FieldUnavailable("fire_safety"), Outcome = outcome };
            case FireSafetyOutcome.Blocked:
                return new RoundsFireSafetyResult { Verdict = NoWorker("firefighter"), Outcome = outcome };
            case FireSafetyOutcome.Recovered:
                return new RoundsFireSafetyResult { Verdict = BuildingReasonNoDeficit, Outcome = outcome };
            default:
                throw new InvalidOperationException($"fire safety outcome {outcome} is not one of the closed set");
        }
    }
}

internal static partial class BuildingRuntimePackage
{
    internal static FireSafetyPawnFacts FireSafetyPawnFactsOf(PawnId pawn, O.PawnState row)
    {
        var facts = new FireSafetyPawnFacts
        {
            Pawn = pawn,
            Dead = Boundary.FactBool(row.Dead),
            Downed = Boundary.FactBool(row.Downed),
            Drafted = Boundary.FactBool(row.Drafted),
            MentalState = Boundary.FactPresence(row.MentalState, row.Issues, "mental_state"),
        };
        if (row.Job is not null && !Boundary.IssueField(row.Job.Issues, "player_forced"))
        {
            facts.PlayerForced = Boundary.FactBool(row.Job.PlayerForced);
        }
        var health = row.Health;
        if (health is not null && !Boundary.IssueField(health.Issues, "health"))
        {
            facts.NeedsTend = Boundary.FactBool(health.NeedsTend);
            facts.Bleeding = Boundary.FactBool(health.Bleeding);
        }
        var settings = row.Settings;
        if (settings is not null && !Boundary.IssueField(settings.Issues, "work"))
        {
            facts.FirefightingEnabled = WorkTypeEnabled(settings.Work, "Firefighter");
        }
        return facts;
    }

    internal static Fact<bool> WorkTypeEnabled(IEnumerable<O.WorkSetting?> work, string defName)
    {
        foreach (var w in work)
        {
            if (w is null || w.DefName != defName)
            {
                continue;
            }
            if (!w.HasDisabled || !w.HasPriority)
            {
                return Unknown<bool>();
            }
            return Known(!w.Disabled && w.Priority > 0);
        }
        return Unknown<bool>();
    }
}
