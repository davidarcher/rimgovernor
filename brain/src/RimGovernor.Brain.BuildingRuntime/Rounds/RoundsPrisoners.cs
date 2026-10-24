using RimGovernor.Brain.Policy;
namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>
/// The runtime half of the prisoners family (#3262): its full read (the owned rounds census) and, while
/// <c>prisoners/recruit</c> is unmet, the jail staging step (the ring and next bed through the shared building
/// planner, then the prisoner flag on every markable jail bed) under the Need's owner.
/// </summary>
public sealed class PrisonersRounds
{
    private readonly Rounder _reviewer;
    private readonly RoundsBuildingPlanner? _building;
    private readonly bool _recruit;
    private readonly bool _custody;

    public PrisonersRounds(Rounder reviewer, bool recruit, bool custody)
    {
        _reviewer = reviewer ?? throw new ControlException("PrisonersRounds: reviewer is missing");
        _recruit = recruit;
        _custody = custody;
        if (recruit && reviewer.Native is IRoundsBuildingSource source)
        {
            _building = new RoundsBuildingPlanner { Reviewer = reviewer, Native = source, Definition = "Wall" };
        }
    }

    /// <summary>The family's scheduler entry: it wakes on its reads and on prisoner interaction, jail building and bed-use outcomes.</summary>
    public FamilyRunner Runner() =>
        new FamilyRunner<PrisonersFacts>(new PrisonersFamily(_reviewer.Policy.Prisoners(), _recruit, _custody), ReadAsync, Schedule, WorkAsync);

    /// <summary>An optional entry woken by the outcomes of every kind its Methods carry.</summary>
    public static FamilySchedule Schedule { get; } = new()
    {
        Class = PlannerClass.Optional,
        Kinds = [ActionKind.PrisonerInteraction, ActionKind.Building, ActionKind.BedUse],
    };

    /// <summary>Reads the census in <paramref name="scope"/>'s world; throws when the native boundary is another world or older than the review.</summary>
    internal async Task<PrisonersFacts> ReadAsync(FamilyReadScope scope, CancellationToken ct)
    {
        if (scope.Immediate)
        {
            // No SeriousHarm Needs: the immediate pass reads nothing.
            return new PrisonersFacts();
        }
        var review = await _reviewer.Player.Journal.LoadRoundsAsync(ct).ConfigureAwait(false);
        var expected = await StepScope.StepScopeAsync(scope, _reviewer.Native, ct).ConfigureAwait(false);
        if (!RoundsBuildingPlanner.RoundsBuildingBoundary(expected, scope.World, review.Tick))
        {
            throw new ControlException("prisoners read: the scope is another world or older than the review");
        }
        var reading = await _reviewer.ObserveOwnedAsync(_reviewer.Native, expected, Unknown<EquatableArray<ConstructionClaim>>(), ct).ConfigureAwait(false);
        return PrisonersFacts.From(reading.Projection.Facts);
    }

    /// <summary>
    /// The jail staging step of an unmet <c>prisoners/recruit</c>: reconcile the planned jail to its next free bed, or
    /// flag its standing beds for prisoners. It runs beside the interaction Method; their claims are disjoint.
    /// </summary>
    internal async Task<Verdict> WorkAsync(NeedWork work, PrisonersFacts facts, CancellationToken call)
    {
        var idle = Verdict.AwaitingPlan(work.Need.Id.Value, "no_method");
        if (work.Need.Id != PrisonersFamily.Recruit || work.Immediate || _building is null)
        {
            return idle;
        }
        var state = work.State;
        var review = await _reviewer.Player.Journal.LoadRoundsAsync(call).ConfigureAwait(false);
        var expected = await StepScope.StepScopeAsync(_reviewer.Native, call).ConfigureAwait(false);
        if (!RoundsBuildingPlanner.RoundsBuildingBoundary(expected, state.Snapshot, review.Tick))
        {
            throw new ControlException("prisoners jail: the native boundary moved");
        }
        var reading = await _reviewer.ObserveRoomsAsync(_reviewer.Native, expected, Unknown<EquatableArray<ConstructionClaim>>(), ["Wall", "Door"], call).ConfigureAwait(false);
        var step = JailStep(reading.Projection);
        switch (step.Kind)
        {
            case JailStepKind.Reconcile:
            {
                var stock = new PackedStock(_reviewer.Native, Boundary.Identity(state.Snapshot));
                var room = step.Room ?? throw new ControlException("prisoners jail: reconcile step without a room");
                var rr = new RoomReconcile { Room = room, Template = step.Template, Name = RoundsSleepingUpkeepPlanner.RoomReconcileName("jail", room), Reason = "jail" };
                return (await _building.ReconcileRoomAsync(state, review, work.Owner, reading, stock, rr, call, work.Epoch).ConfigureAwait(false)).Verdict;
            }
            case JailStepKind.Mark:
            {
                var verdict = Verdict.WaitFor(Cause.MethodUsed, "jail_bed_method");
                foreach (var bed in step.Marks)
                {
                    var one = await MarkJailBedAsync(state, work.Owner, reading.Projection, bed, call, work.Epoch).ConfigureAwait(false);
                    if (one == Verdict.BuildingReasonAdmitted || verdict.Is(Cause.MethodUsed))
                    {
                        verdict = one;
                    }
                }
                return verdict;
            }
            default:
                return idle;
        }
    }

    internal static JailStep JailStep(ColonyProjection facts)
    {
        var (plan, rooms, known) = RoundsBuildingPlanner.PlannedLayout(facts);
        var pk = facts.Facts.Population.TryGetValue(out var census);
        var sk = facts.Facts.Sleeping.TryGetValue(out var sleeping);
        var bk = facts.Facts.CurrentConstruction.TryGetValue(out var built);
        if (!known || !pk || !sk || !bk || !built!.Colony)
        {
            return new JailStep();
        }

        return JailRules.NextJailStep(plan, rooms, HeldPrisoners(census!.Prisoners), sleeping!.Beds, built.Buildings);
    }

    /// <summary>Flags one jail bed for prisoners, once per bed under the Need; nothing when native already has it so.</summary>
    private async Task<Verdict> MarkJailBedAsync(ControlState state, NeedOwner owner, ColonyProjection facts, string bed, CancellationToken call, CancellationToken epoch)
    {
        var p = _reviewer.Player;
        if (_reviewer.Native is not FacadeNativeClient native)
        {
            return Verdict.FieldUnavailable("hospital_source");
        }

        var method = new MethodId("jail-mark-" + bed);
        if (p.Journal.OwnerMethodPlan(owner, method, call) is not null)
        {
            return Verdict.WaitFor(Cause.MethodUsed, "jail_bed_method");
        }

        var target = await native.ReadBedUseTargetAsync(Boundary.Identity(state.Snapshot), bed, call).ConfigureAwait(false);
        try
        {
            Boundary.Context(target.Context, state.Snapshot);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            throw new ControlException("markJailBed: context", e);
        }

        if (target.Context is not { } targetContext || targetContext.Tick < facts.Identity.Tick.Value)
        {
            throw new ControlException("markJailBed: target read older than the facts");
        }

        if (target.Prisoners)
        {
            return Verdict.WaitFor(Cause.MethodUsed, "jail_bed_prisoners");
        }

        var id = PlanIds.MintPlanId();
        var plan = new PlanSpec(id, new PlanRevision(1), [DomainAction.NewBedUseAction(new ActionId($"{id}-0"), BedUse.NewBedPrisoners(bed))]);
        p.Current(call, epoch);
        if (p.Session.State() != state)
        {
            throw new ControlException("markJailBed: session state changed");
        }
        return RoundsBuildingPlanner.OwnerCommitVerdict(p.Journal.CommitOwnerWork(owner, method, "jail", plan, call));
    }

    internal static int HeldPrisoners(IReadOnlyList<PrisonerFacts> prisoners)
    {
        return prisoners.Count(p => !p.Dead);
    }
}
