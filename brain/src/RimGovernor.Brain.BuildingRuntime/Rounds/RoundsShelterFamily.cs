namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>
/// The runtime half of the shelter family (#3257). The read gives the housing facts (the
/// retained bed-use review with the bedroom steps the room census owes), the sleeping rooms'
/// temperatures with the temperature work owed, and the safe area's pending edits; in the immediate pass only the
/// safe area, which the immediate review measured. The work step runs one planner per ready Need: the sleeping
/// ladder (the stand-in sleeping spots, bed ownership, bedrooms and their upgrades), the
/// temperature planner and the safe-area edit. Every Method is committed under its Need and admitted by its claims.
/// </summary>
public sealed class ShelterRounds
{
    private readonly Rounder _reviewer;
    private readonly bool _safeArea;
    private readonly RoundsSleepingUpkeepPlanner? _sleeping;
    private readonly RoundsBuildingPlanner? _temperature;

    /// <summary>Each toggle composes its Need and, for a building Need, its planner over <paramref name="native"/>.</summary>
    public ShelterRounds(Rounder reviewer, IRoundsBuildingSource? native, bool sleeping, bool temperature, bool safeArea)
    {
        if (reviewer is null || reviewer.Native is null)
        {
            throw new ControlException("ShelterRounds: reviewer == nil || reviewer.native == nil");
        }
        if ((sleeping || temperature) && native is null)
        {
            throw new ControlException("ShelterRounds: a building Need is composed without a building source");
        }
        if (!sleeping && !temperature && !safeArea)
        {
            throw new ControlException("ShelterRounds: no Need is composed");
        }
        _reviewer = reviewer;
        _safeArea = safeArea;
        if (sleeping)
        {
            _sleeping = RoundsSleepingUpkeepPlanner.Create(reviewer, native);
        }
        if (temperature)
        {
            _temperature = RoundsBuildingPlanner.NewRoundsTemperaturePlanner(reviewer, native);
        }
    }

    /// <summary>
    /// The family's scheduler entry. It is critical while the sleeping ladder is composed (the old sleeping upkeep
    /// planner's class) and wakes on its building, bed, assignment, door, draft and area outcomes.
    /// </summary>
    public FamilyRunner Runner() =>
        new FamilyRunner<ShelterFacts>(new ShelterFamily(_sleeping is not null, _temperature is not null, _safeArea), ReadAsync,
            _sleeping is not null ? Schedule with { Class = PlannerClass.Critical } : Schedule, WorkAsync);

    /// <summary>An optional entry woken by the outcomes of every kind its planners commit.</summary>
    public static FamilySchedule Schedule { get; } = new()
    {
        Class = PlannerClass.Optional,
        Kinds =
        [
            ActionKind.Building, ActionKind.Deconstruction, ActionKind.Excavation, ActionKind.Assign, ActionKind.MoveBuilding,
            ActionKind.UninstallBuilding, ActionKind.RecoveryService, ActionKind.BedUse, ActionKind.DoorControl, ActionKind.OwnedDraft,
            ActionKind.Movement, ActionKind.Area,
        ],
    };

    /// <summary>
    /// Reads the family's facts in <paramref name="scope"/>'s world. The immediate pass reads only the safe area's
    /// pending edits, which the immediate review measured; the full read is the room census with the sleeping review
    /// the store keeps, the bedroom and temperature work it owes, and the safe area's edits. Throws when the scope is
    /// another world or older than the review.
    /// </summary>
    internal async Task<ShelterFacts> ReadAsync(FamilyReadScope scope, CancellationToken call)
    {
        var world = RoundsStockpilePlanner.StockpileWorld(scope.World);
        if (scope.Immediate)
        {
            return new ShelterFacts { SafeAreaOwed = _safeArea ? _reviewer.SafeArea.Owed(world) : Unknown<bool>() };
        }
        var review = await _reviewer.Player.Journal.LoadRoundsAsync(call).ConfigureAwait(false);
        var expected = await StepScope.StepScopeAsync(scope, _reviewer.Native, call).ConfigureAwait(false);
        if (!RoundsBuildingPlanner.RoundsBuildingBoundary(expected, scope.World, review.Tick))
        {
            throw new ControlException("shelter read: the scope is another world or older than the review");
        }
        var reading = await _reviewer.ObserveRoomsAsync(_reviewer.Native, expected, Unknown<EquatableArray<Pol.ConstructionClaim>>(), call).ConfigureAwait(false);
        var projection = reading.Projection;
        var f = projection.Facts;
        // The bed-use review the store retains, with a due bedroom step holding sleeping unmet (as the review does).
        var recovered = SleepingRules.ReviewSleeping(f.Sleeping, review.Sleeping, expected.Tick).Recovered();
        if (RoundsBuildingPlanner.PositiveFact(RoundsSleepingUpkeepPlanner.BedroomsOwedOf(projection)))
        {
            recovered = Known(false);
        }
        f = f with
        {
            SleepingRecovered = recovered,
            TemperatureOwed = RoundsBuildingPlanner.TemperatureOwed(projection),
            Upkeep = f.Upkeep with { Rooms = projection.Rooms },
            SafeAreaOwed = _safeArea ? _reviewer.SafeArea.Owed(world) : Unknown<bool>(),
        };
        return ShelterFacts.From(f, _reviewer.Policy);
    }

    /// <summary>The work step of a ready Need: its planner, or the safe-area edit.</summary>
    internal async Task<Verdict> WorkAsync(NeedWork work, ShelterFacts facts, CancellationToken call)
    {
        var id = work.Need.Id;
        if (id == ShelterFamily.SafeArea && _safeArea)
        {
            return SafeAreaWork(work, call);
        }
        if (id == ShelterFamily.Sleeping && _sleeping is not null)
        {
            return Building(await _sleeping.StepAsync(work.Arbiter!, call, work.Epoch).ConfigureAwait(false));
        }
        if (id == ShelterFamily.Temperature && _temperature is not null)
        {
            return Building(await _temperature.Copy().StepAsync(work.Arbiter!, call, work.Epoch).ConfigureAwait(false));
        }
        return Verdict.AwaitingPlan(id.Value, "no_method");
    }

    /// <summary>A planner step's verdict: admitted when its decision admitted, else the reason it gave.</summary>
    internal static Verdict Building(RoundsBuildingResult result) =>
        result.Decision?.Admitted == true ? BuildingReasonAdmitted : result.Verdict;

    /// <summary>
    /// Commits the safe area's pending edits as one Area plan under the Need's <c>safe-area</c> Method. An open edit
    /// plan continues (its edits are not taken again); a held area claim is a claim conflict. The memory takes the
    /// edits only once they are admitted.
    /// </summary>
    internal Verdict SafeAreaWork(NeedWork work, CancellationToken call)
    {
        var p = _reviewer.Player;
        var world = RoundsStockpilePlanner.StockpileWorld(work.State.Snapshot);
        var edits = _reviewer.SafeArea.Take(world);
        if (edits.Count == 0)
        {
            return Verdict.WaitFor(Cause.MethodUsed, "safe_area_edits");
        }
        var id = PlanIds.MintPlanId();
        var actions = new List<DomainAction>(edits.Count);
        for (var i = 0; i < edits.Count; i++)
        {
            actions.Add(DomainAction.NewAreaAction(new ActionId($"{id}-{i}"), edits[i]));
        }
        var plan = new PlanSpec(id, new PlanRevision(1), actions);
        p.Current(call, work.Epoch);
        if (p.Session.State() != work.State)
        {
            throw new ControlException("safe area step: session state changed");
        }
        var commit = p.Journal.CommitOwnerWork(work.Owner, SafeAreaMethod, $"bot areas {edits.Count} edits", plan, call);
        if (commit.Outcome == ClaimOutcome.Admitted)
        {
            _reviewer.SafeArea.Commit(world);
        }
        return RoundsBuildingPlanner.OwnerCommitVerdict(commit);
    }

    /// <summary>The safe area's one Method: the bot-owned areas' edits, re-issued once its last plan closed.</summary>
    internal static readonly MethodId SafeAreaMethod = new("safe-area");
}

/// <summary>
/// The runtime half of the power family (#3257): one Need, power/basic, whose work step is the power planner
/// (generators, batteries, conduits, the power shelter). A solar flare holds the Need without running the planner.
/// </summary>
public sealed class PowerRounds
{
    private readonly Rounder _reviewer;
    private readonly RoundsBuildingPlanner _power;

    public PowerRounds(Rounder reviewer, IRoundsBuildingSource native)
    {
        _reviewer = reviewer ?? throw new ControlException("PowerRounds: reviewer is missing");
        _power = RoundsBuildingPlanner.NewRoundsPowerPlanner(reviewer, native);
    }

    /// <summary>The family's scheduler entry, woken by its building outcomes.</summary>
    public FamilyRunner Runner() => new FamilyRunner<PowerFacts>(new PowerFamily(), ReadAsync, Schedule, WorkAsync);

    public static FamilySchedule Schedule { get; } = new() { Class = PlannerClass.Optional, Kinds = [ActionKind.Building] };

    /// <summary>Reads the owned census in <paramref name="scope"/>'s world; the immediate pass reads nothing (no SeriousHarm Need).</summary>
    internal async Task<PowerFacts> ReadAsync(FamilyReadScope scope, CancellationToken call)
    {
        if (scope.Immediate)
        {
            return new PowerFacts();
        }
        var review = await _reviewer.Player.Journal.LoadRoundsAsync(call).ConfigureAwait(false);
        var expected = await StepScope.StepScopeAsync(scope, _reviewer.Native, call).ConfigureAwait(false);
        if (!RoundsBuildingPlanner.RoundsBuildingBoundary(expected, scope.World, review.Tick))
        {
            throw new ControlException("power read: the scope is another world or older than the review");
        }
        var reading = await _reviewer.ObserveOwnedAsync(_reviewer.Native, expected, Unknown<EquatableArray<Pol.ConstructionClaim>>(), call).ConfigureAwait(false);
        return PowerFacts.From(reading.Projection.Facts);
    }

    /// <summary>The power planner for the ready Need, unless a flare holds it (the family's hold says why).</summary>
    internal async Task<Verdict> WorkAsync(NeedWork work, PowerFacts facts, CancellationToken call)
    {
        if (work.Need.Id != PowerFamily.Basic)
        {
            return Verdict.AwaitingPlan(work.Need.Id.Value, "no_method");
        }
        if (facts.OutageHeld)
        {
            return Verdict.AwaitingPlan(PowerFamily.OutageHold, "a solar flare switches the grid off; the planner waits for it to end");
        }
        return ShelterRounds.Building(await _power.Copy().StepAsync(work.Arbiter!, call, work.Epoch).ConfigureAwait(false));
    }
}
