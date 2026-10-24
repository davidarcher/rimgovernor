using RimGovernor.Brain.Policy;
namespace RimGovernor.Brain.BuildingRuntime;

partial class AnomalyRounds
{
    /// <summary>The able colonists wearing a lance item, with the item they would use.</summary>
    internal static IReadOnlyList<LanceUser> LanceUsers(IReadOnlyList<O.PawnState> rows, Things things)
    {
        var output = new List<LanceUser>();
        foreach (var row in rows)
        {
            var facts = Armament.SquadDefenderFacts(row, new HashSet<Domain.PawnId>(), Unknown<bool>());
            var dk = facts.Dead.TryGetValue(out var dead);
            var wk = facts.Downed.TryGetValue(out var downed);
            var vk = facts.ViolenceCapable.TryGetValue(out var violent);
            if (!dk || !wk || !vk || dead || downed || facts.MentalState || !violent)
            {
                continue;
            }

            foreach (var item in row.Equipment.Apparel)
            {
                if (PopulationRules.LanceDefs.ContainsKey(new Resource(RoundsGear.GearDef(things, item.Thing))) && (item.Thing?.Id ?? "") != "")
                {
                    output.Add(new LanceUser(new PawnId(row.Pawn.Id ?? ""), item.Thing?.Id ?? ""));
                    break;
                }
            }
        }

        return output;
    }
}

/// <summary>
/// The runtime half of the anomaly family (#3302): its full read (the owned rounds census, whose unread containment,
/// bioferrite and monolith facts it reports) and the lance check of a ready <c>anomaly/lance</c>, which reads who can
/// use a lance now. Door closes and extract flags are the family's pure proposals.
/// </summary>
public sealed partial class AnomalyRounds
{
    private const string Component = "routine-anomaly";

    private readonly Rounder _reviewer;
    private readonly FacadeNativeClient _native;

    public AnomalyRounds(Rounder reviewer, FacadeNativeClient native)
    {
        _reviewer = reviewer ?? throw new ControlException("AnomalyRounds: reviewer is missing");
        _native = native ?? throw new ControlException("AnomalyRounds: native is missing");
    }

    /// <summary>The family's scheduler entry: it wakes on its reads and on door control and pawn settings outcomes.</summary>
    public FamilyRunner Runner() => new FamilyRunner<AnomalyFacts>(new AnomalyFamily(), ReadAsync, Schedule, WorkAsync);

    /// <summary>A critical entry (a held entity's breach is urgent) woken by the outcomes of the door closes and extract flags it commits.</summary>
    public static FamilySchedule Schedule { get; } = new()
    {
        Class = PlannerClass.Critical,
        Kinds = [ActionKind.DoorControl, ActionKind.PawnSettings],
    };

    /// <summary>Reads the census in <paramref name="scope"/>'s world and reports its unread containment facts; throws when the native boundary is another world or older than the review.</summary>
    internal async Task<AnomalyFacts> ReadAsync(FamilyReadScope scope, CancellationToken ct)
    {
        if (scope.Immediate)
        {
            // No SeriousHarm Needs: the immediate pass reads nothing.
            return new AnomalyFacts();
        }
        var review = await _reviewer.Player.Journal.LoadRoundsAsync(ct).ConfigureAwait(false);
        var expected = await StepScope.StepScopeAsync(scope, _reviewer.Native, ct).ConfigureAwait(false);
        if (!RoundsBuildingPlanner.RoundsBuildingBoundary(expected, scope.World, review.Tick))
        {
            throw new ControlException("anomaly read: the scope is another world or older than the review");
        }
        var reading = await _reviewer.ObserveOwnedAsync(_reviewer.Native, expected, Unknown<EquatableArray<ConstructionClaim>>(), ct).ConfigureAwait(false);
        var f = reading.Projection.Facts;
        Report(f, ct);
        return AnomalyFacts.From(f);
    }

    /// <summary>
    /// The lance check of a ready <c>anomaly/lance</c>: held once an able colonist wears a lance for the target,
    /// otherwise no method. The other Needs have no work.
    /// </summary>
    internal async Task<Verdict> WorkAsync(NeedWork work, AnomalyFacts facts, CancellationToken call)
    {
        var idle = Verdict.AwaitingPlan(work.Need.Id.Value, "no_method");
        if (work.Need.Id != AnomalyFamily.Lance || work.Immediate || PopulationRules.LanceCandidate(facts.Rounds) is not { } target || target.Value == "")
        {
            return idle;
        }
        var review = await _reviewer.Player.Journal.LoadRoundsAsync(call).ConfigureAwait(false);
        return await LanceUsableAsync(work.State, review.Tick, target, call).ConfigureAwait(false)
            ? Verdict.AwaitingPlan(AnomalyFamily.LanceHeld, "Population acquisition through lance use requires shared participant and outcome comparison.")
            : idle;
    }

    /// <summary>Whether an able colonist other than <paramref name="target"/> wears a lance now, by the emergency and combat reads.</summary>
    private async Task<bool> LanceUsableAsync(ControlState state, Tick reviewTick, PawnId target, CancellationToken call)
    {
        var identity = Boundary.Identity(state.Snapshot);
        var emergency = await _native.ReadEmergencyAsync(identity, call).ConfigureAwait(false);
        try
        {
            Boundary.Context(emergency.Context, state.Snapshot);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            throw new ControlException("anomaly lance: emergency context", e);
        }

        if (emergency.Context is not { } emergencyContext || emergencyContext.Tick < reviewTick.Value)
        {
            throw new ControlException("anomaly lance: emergency context");
        }

        if (!emergency.Facts.ColonistsComplete.TryGetValue(out var complete) || !complete || emergency.Facts.Colonists.Count == 0)
        {
            return false;
        }

        var ids = emergency.Facts.Colonists.Where(pawn => pawn.Id != target).Select(pawn => pawn.Id.Value).ToList();
        if (ids.Count == 0)
        {
            return false;
        }

        var reply = await _native.ReadPawnsAsync(identity, ids, call).ConfigureAwait(false);
        var observed = reply;
        try
        {
            Boundary.Context(observed.Context, state.Snapshot);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            throw new ControlException("anomaly lance: combat context", e);
        }

        foreach (var row in observed.Pawns)
        {
            if (row is null)
            {
                throw new ControlException("anomaly lance: row or its pawn is missing");
            }
        }

        var things = await RoundsGear.FrameThingsOfAsync(_native, identity, call).ConfigureAwait(false);
        return PopulationRules.LanceUsable(target, AnomalyRounds.LanceUsers(observed.Pawns, things));
    }

    /// <summary>Reports each containment, bioferrite and monolith fact the rules could not decide: a loud issue, never a pass.</summary>
    private static void Report(RoundsFacts f, CancellationToken ct)
    {
        foreach (var issue in EntityUpkeepRules.ContainmentDoorUpkeep(f.Containment).Issues)
        {
            DefensePipeline.DefenseAction(Component, TelemetryLevel.Warn, "refused", "containment_upkeep_issue", $"{issue.Cell.X},{issue.Cell.Z}",
                new Dictionary<string, object?> { ["x"] = issue.Cell.X, ["z"] = issue.Cell.Z, ["detail"] = issue.Reason }, ct);
        }
        foreach (var reason in EntityBioferriteRules.BioferriteHarvestOwed(f.Containment, f.Research).Issues)
        {
            DefensePipeline.DefenseAction(Component, TelemetryLevel.Warn, "refused", "bioferrite_harvest_unread", "bioferrite", new Dictionary<string, object?> { ["detail"] = reason }, ct);
        }
        if (MonolithRules.MonolithInPlay(f.Monolith))
        {
            foreach (var issue in MonolithRules.MonolithAdvanceOwed(f.Monolith, MonolithRules.MonolithGate(f)).Issues)
            {
                DefensePipeline.DefenseAction(Component, TelemetryLevel.Warn, "refused", "monolith_advance_unread", "monolith", new Dictionary<string, object?> { ["detail"] = issue }, ct);
            }
        }
    }
}
