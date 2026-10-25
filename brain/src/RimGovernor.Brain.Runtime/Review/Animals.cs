using RimGovernor.Brain.Policy;
namespace RimGovernor.Brain.Runtime;

/// <summary>
/// The runtime half of the animals family (#3260): one rooms read gives the herd census, the herd plan's inputs, the
/// uncontained animals and the next pen and ward steps. The family proposes the herd's husbandry write and the
/// sterilize step purely; the work step builds the pens and the hospital's animal beds through the shared building
/// planner under the containment and ward Needs, and flags a ward bed medical.
/// </summary>
public sealed class AnimalsReader
{
    private readonly ColonyReview _reviewer;
    private readonly GameClient _native;
    private readonly BuildingPlanner _building;

    public AnimalsReader(ColonyReview reviewer, GameClient native)
    {
        _reviewer = reviewer ?? throw new ControlException("AnimalsReader: reviewer is missing");
        _native = native ?? throw new ControlException("AnimalsReader: native is missing");
        _building = new BuildingPlanner { Reviewer = reviewer, Native = native };
    }

    /// <summary>The family's scheduler entry: the husbandry toggle derives the herd and sterilize Needs, the containment toggle the containment and ward Needs.</summary>
    public static FamilyRunner<AnimalsFacts> Runner(ColonyReview reviewer, GameClient native, bool herd, bool containment)
    {
        var reader = new AnimalsReader(reviewer, native);
        return new FamilyRunner<AnimalsFacts>(new AnimalsFamily(herd, containment), reader.ReadAsync, Schedule, reader.WorkAsync);
    }

    /// <summary>An optional entry woken by husbandry, bed-use and every building and removal kind a room reconcile commits.</summary>
    public static FamilySchedule Schedule { get; } = new()
    {
        Class = PlannerClass.Optional,
        Kinds =
        [
            ActionKind.Husbandry, ActionKind.BedUse, ActionKind.Building, ActionKind.BuildRoof,
            ActionKind.Deconstruction, ActionKind.UninstallBuilding, ActionKind.MoveBuilding, ActionKind.RemoveRoof, ActionKind.FloorRemoval,
        ],
    };

    /// <summary>
    /// Reads the family's facts in <paramref name="scope"/>'s world. Throws when the read is from another world,
    /// older than the review, or slower than the review's age bound. The immediate pass reads nothing: the family has
    /// no SeriousHarm Need.
    /// </summary>
    internal async Task<AnimalsFacts> ReadAsync(FamilyReadScope scope, CancellationToken call)
    {
        if (scope.Immediate)
        {
            return new AnimalsFacts();
        }
        var started = _reviewer.Clock.Now();
        var (_, reading) = await ReadingAsync(scope, call).ConfigureAwait(false);
        var projection = reading.Reading;
        var f = projection.Facts;
        var handlers = projection.Facts.WorkProfiles;
        var uncontained = AnimalUpkeepRules.ReviewAnimalUpkeep(f.AnimalUpkeep).Containment;
        var pen = HerdRooms.PenStepOf(projection);
        var ward = HerdRooms.WardStepOf(projection);
        var elapsed = _reviewer.Clock.Now() - started;
        if (elapsed < TimeSpan.Zero || elapsed > _reviewer.MaxAge)
        {
            throw new ControlException("animals read: the read took too long");
        }
        return new AnimalsFacts
        {
            Animals = f.AnimalUpkeep.Animals,
            Wild = f.AnimalUpkeep.WildAnimals,
            Herd = f.HerdPolicy(),
            FeedShort = f.AnimalFeedRunway().Projection.IsShort(),
            FoodPlan = f.FoodPlan,
            Handlers = handlers,
            Uncontained = uncontained,
            Pen = pen,
            Ward = ward,
            WardRoom = f.AnimalWard with { Ready = HerdRooms.WardReadyOf(projection) },
        };
    }

    /// <summary>The review and the rooms reading (the census cache serves a repeat) for the world, under the current construction claims.</summary>
    private async Task<(ReviewState Review, ReviewReading Reading)> ReadingAsync(FamilyReadScope scope, CancellationToken call)
    {
        var world = scope.World;
        var review = await _reviewer.Player.Journal.LoadReviewAsync(call).ConfigureAwait(false);
        var expected = await StepScope.StepScopeAsync(scope, _reviewer.Native, call).ConfigureAwait(false);
        if (!BuildingPlanner.BuildingBoundary(expected, world, review.Tick))
        {
            throw new ControlException("animals read: the scope is another world or older than the review");
        }
        var claims = _reviewer.Player.Journal.ConstructionClaims(world, expected.Tick, call);
        var reading = await _reviewer.ObserveOwnedAsync(_reviewer.Native, expected, claims, HerdRooms.HerdDefinitions, call).ConfigureAwait(false);
        return (review, reading);
    }

    /// <summary>
    /// The building work of a ready Need: the containment Need reconciles the next pen, then waits on a handler or
    /// native delivery; the ward Need builds or flags the next ward bed. The herd and sterilize Needs work through
    /// their pure proposals.
    /// </summary>
    internal async Task<Verdict> WorkAsync(NeedWork work, AnimalsFacts facts, CancellationToken call)
    {
        if (work.Need.Id == AnimalsFamily.Containment)
        {
            return await ContainAsync(work, facts, call).ConfigureAwait(false);
        }
        if (work.Need.Id == AnimalsFamily.Ward)
        {
            return await WardAsync(work, facts, call).ConfigureAwait(false);
        }
        return Verdict.AwaitingPlan(work.Need.Id.Value, "no_method");
    }

    private async Task<Verdict> ContainAsync(NeedWork work, AnimalsFacts facts, CancellationToken call)
    {
        var pen = facts.Pen;
        var (review, reading) = await ReadingAsync(new FamilyReadScope(work.State.Snapshot, false), call).ConfigureAwait(false);
        if (pen.Owed())
        {
            return await ReconcileAsync(work, review, reading, pen, call).ConfigureAwait(false);
        }
        var projection = reading.Reading;
        var workPawns = projection.Facts.ForecastObserved.WorkPawns.Pawns;
        var handler = AnimalContainment.AnimalHandlerAvailable(workPawns);
        var choice = AnimalUpkeepRules.SelectAnimalContainmentMethod(projection.Facts.AnimalUpkeep.Animals, handler, AnimalContainment.PensStand(projection));
        return choice.Reason == AnimalContainmentReason.NoDeficit ? Verdict.AwaitingPlan(work.Need.Id.Value, "no_method") : AnimalContainment.ContainmentWait(choice.Reason);
    }

    private async Task<Verdict> WardAsync(NeedWork work, AnimalsFacts facts, CancellationToken call)
    {
        var step = facts.Ward;
        if (step.Kind == HerdStepKind.BedUnavailable)
        {
            return Verdict.AwaitingPlan(work.Need.Id.Value, "animal_bed_unavailable");
        }
        if (!step.Owed())
        {
            return Verdict.AwaitingPlan(work.Need.Id.Value, "no_method");
        }
        var (review, reading) = await ReadingAsync(new FamilyReadScope(work.State.Snapshot, false), call).ConfigureAwait(false);
        return step.Kind == HerdStepKind.Medical
            ? await MarkBedMedicalAsync(work, reading.Reading, step.Bed, call).ConfigureAwait(false)
            : await ReconcileAsync(work, review, reading, step, call).ConfigureAwait(false);
    }

    /// <summary>Reconciles a pen or the ward's room under the Need owner; the MethodIds name the room's inner corner.</summary>
    private async Task<Verdict> ReconcileAsync(NeedWork work, ReviewState review, ReviewReading reading, HerdStep step, CancellationToken call)
    {
        var inner = step.Room.Region.Bounds();
        var stock = new PackedStock(_reviewer.Native, Boundary.Identity(work.State.Snapshot));
        var result = await _building.ReconcileRoomAsync(work.State, review, work.Owner, reading, stock, new RoomReconcile
        {
            Room = step.Room,
            Template = step.Template,
            Name = $"herd-{inner.X}-{inner.Z}",
            Reason = Wire.Name(step.Role),
        }, call, work.Epoch).ConfigureAwait(false);
        return result.Verdict;
    }

    /// <summary>Flags one ward bed medical, once per bed: a fresh bed-use read first, so a bed already flagged commits nothing.</summary>
    private async Task<Verdict> MarkBedMedicalAsync(NeedWork work, ColonyReading facts, string bed, CancellationToken call)
    {
        var method = new MethodId("herd-medical-" + bed);
        if (!_building.MethodOnce(work.Owner, method, call))
        {
            return WaitFor(Cause.MethodUsed, "herd_bed_medical_method");
        }
        var target = await _native.ReadBedUseTargetAsync(Boundary.Identity(work.State.Snapshot), bed, call).ConfigureAwait(false);
        try
        {
            Boundary.Context(target.Context, work.State.Snapshot);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            throw new ControlException("herd bed medical: stale bed read", e);
        }
        if ((target.Context?.Tick ?? 0) < facts.Identity.Tick.Value)
        {
            throw new ControlException("herd bed medical: stale bed read");
        }
        if (target.Medical)
        {
            return WaitFor(Cause.MethodUsed, "herd_bed_medical");
        }
        var id = PlanIds.MintPlanId();
        var action = DomainAction.NewBedUseAction(new ActionId($"{id}-0"), BedUse.NewBedMedical(bed, true));
        return _building.CommitOwnerActions(work.State, work.Owner, method, id, [action], call, work.Epoch).Verdict;
    }
}
