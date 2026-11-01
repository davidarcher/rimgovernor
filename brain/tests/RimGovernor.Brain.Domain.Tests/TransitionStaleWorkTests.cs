using RimGovernor.Brain.GameAccess;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Runtime;
using RimGovernor.Brain.Store;
using Xunit;
using Journal = RimGovernor.Brain.Store.Store;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// Task 8.2: stale execution evidence is rejected on every path that issues transition work. An open removal whose building the
/// plan carries no transition for (re-selected to stay, or settled) has its unissued actions cancelled, a removal the plan still
/// binds or the transition choice does not govern is left alone, and the cancelled removal is not issued again in the same load:
/// the host-retire and bed removals by their latest attempt, the room reconcile by MethodOnce, clearance by its withdrawn targets.
/// </summary>
public sealed class TransitionStaleWorkTests
{
    private static readonly GenerationSnapshot World = new(new("colony"), new(1), new("load"), new("review"), new(1), new(1));

    private static readonly PlannedTransition Bed = new("bed", "Bed", TransitionCause.SpareBed, ServicingChoiceKind.Remove);

    private static LayoutPlan Plan(params PlannedTransition[] transitions) => new()
    {
        Rooms = [new PlannedRoom { Id = 1, Role = PlannedRole.Workshop, Region = RegionRules.RectRegion(new RimGovernor.Brain.Policy.Rectangle(20, 20, 6, 5)), Door = new Cell(22, 25), DoorSide = new DoorSide.Doored(Rotation.North) }],
        Transitions = transitions,
    };

    /// <summary>A journal under review whose <paramref name="need"/> committed <paramref name="method"/>: one deconstruction per target, all unissued.</summary>
    private static (Journal Journal, NeedOwner Owner) Committed(NeedId need, MethodId method, CancellationToken ct, params string[] targets)
    {
        var journal = Journal.Open(Path.Combine(Path.GetTempPath(), "rg-transition-stale-" + Guid.NewGuid().ToString("N") + ".db"), ct);
        journal.Review(new ReviewRequest { Current = World, Tick = new(100), Enabled = true, Policy = Rules.DefaultPlannerSettings(), Facts = WorkResourceTestFacts.Create("a") }, ct);
        journal.RecordPlannerReasons(new Dictionary<FilingKey, PlannerNote>(), [new FamilyProgress(need.Family, true, [new NeedProgress(need, false, default, method)])], ct);
        var goal = journal.LoadNeed(need, World, ct);
        var id = PlanIds.MintPlanId();
        var actions = targets.Select((t, i) => DomainAction.NewDeconstructionAction(new ActionId(id.Value + "-" + i), new Deconstruction(t, "Stool", new Cell(1 + i, 1)))).ToArray();
        var commit = journal.CommitOwnerWork(goal, method, "", new PlanSpec(id, new(1), actions), ct);
        Assert.True(commit.Outcome == ClaimOutcome.Admitted, $"{commit.Outcome} {commit.Refusal} {commit.Conflict}");
        return (journal, journal.LoadNeed(need, World, ct));
    }

    private static Stage[] Stages(Journal journal, NeedOwner owner, MethodId method, CancellationToken ct) =>
        [.. journal.LoadPlan(owner.Methods.Single(m => m.Method == method).Plan, ct).Progress.Select(p => p.View.Stage)];

    [Fact]
    public void AStaleHostRetirementIsCancelledAndNotIssuedAgain()
    {
        var ct = TestContext.Current.CancellationToken;
        var method = BuildingPlanner.RetireMethodId(BuildingPlanner.HostRetirePrefix, "spot");
        var (journal, owner) = Committed(LayoutFamily.Defense, method, ct, "spot");
        using var _ = journal;
        bool Retire(MethodId m) => m.Value.StartsWith(BuildingPlanner.HostRetirePrefix + "-", StringComparison.Ordinal);
        var bound = Plan(new PlannedTransition("spot", "CraftingSpot", TransitionCause.HostRetirement, ServicingChoiceKind.Remove) { Dependency = new("MakeKnife", "Smithy") });
        Assert.Equal(0, ColonyReview.CancelStaleTransitionWork(journal, owner, bound, Retire, _ => true, "building", ct));

        // The replacement stopped serving: the binding dropped, so the open retirement is stale.
        Assert.Equal(1, ColonyReview.CancelStaleTransitionWork(journal, owner, Plan(), Retire, _ => true, "building", ct));
        Assert.Equal([Stage.Cancelled], Stages(journal, owner, method, ct));
        var after = journal.LoadNeed(LayoutFamily.Defense, World, ct);
        Assert.Equal(0, ColonyReview.CancelStaleTransitionWork(journal, after, Plan(), Retire, _ => true, "building", ct));
        // RetireBuildingAsync's gate: the Method's latest attempt holds it in this load.
        Assert.NotNull(journal.OwnerMethodPlan(after, method, ct));
    }

    [Fact]
    public void AStaleRoomRemovalIsCancelledAndMethodOnceHoldsIt()
    {
        var ct = TestContext.Current.CancellationToken;
        var method = new MethodId("workshop-1-deconstruct-chair");
        var (journal, owner) = Committed(LayoutFamily.Defense, method, ct, "chair", "ruin");
        using var _ = journal;
        // The chair was re-selected to stay this review; the ruin is a foreign building the transition choice does not govern.
        var decided = new HashSet<string>(StringComparer.Ordinal) { "chair" };
        Assert.Equal(1, ColonyReview.CancelStaleTransitionWork(journal, owner, Plan(), m => m.Value.StartsWith("workshop-1-", StringComparison.Ordinal), decided.Contains, "building", ct));
        Assert.Equal([Stage.Cancelled, Stage.Pending], Stages(journal, owner, method, ct));
        Assert.False(BuildingPlanner.MethodOnce(journal, journal.LoadNeed(LayoutFamily.Defense, World, ct), method, ct));
    }

    [Fact]
    public void AStaleBedRemovalIsCancelledAndNotIssuedAgain()
    {
        var ct = TestContext.Current.CancellationToken;
        var method = BuildingPlanner.RetireMethodId(SleepingUpkeepPlanner.BedRemovePrefix, "bed");
        Assert.True(SleepingUpkeepPlanner.BedRemoval(method));
        var (journal, owner) = Committed(LayoutFamily.Defense, method, ct, "bed");
        using var _ = journal;
        var settled = new HashSet<string>(StringComparer.Ordinal) { "bed" };
        // Still bound: the removal is the plan's.
        Assert.Equal(0, ColonyReview.CancelStaleTransitionWork(journal, owner, Plan(Bed), SleepingUpkeepPlanner.BedRemoval, settled.Contains, "building", ct));
        // A pawn owns the spare now: the census settled its transition and the open removal is stale.
        Assert.Equal(1, ColonyReview.CancelStaleTransitionWork(journal, owner, Plan(), SleepingUpkeepPlanner.BedRemoval, settled.Contains, "building", ct));
        Assert.Equal([Stage.Cancelled], Stages(journal, owner, method, ct));
        // RemoveOldBedAsync's gate: the Method's latest attempt holds it in this load.
        Assert.NotNull(journal.OwnerMethodPlan(journal.LoadNeed(LayoutFamily.Defense, World, ct), method, ct));
    }

    [Fact]
    public void AStaleClearanceRemovalIsCancelledAndWithdrawnFromTheNextStep()
    {
        var ct = TestContext.Current.CancellationToken;
        var method = new MethodId("deconstruct-chair");
        var (journal, owner) = Committed(HomeUpkeepFamily.Clearance, method, ct, "chair", "wall");
        using var _ = journal;
        var retained = new TransitionDecision("chair", "Stool", TransitionCause.RetiredGround, [], null);
        var governed = new HashSet<string>(StringComparer.Ordinal) { retained.Target };
        Assert.Equal(1, ColonyReview.CancelStaleTransitionWork(journal, owner, Plan(), _ => true, governed.Contains, HomeUpkeepFamily.FamilyId, ct));
        Assert.Equal([Stage.Cancelled, Stage.Pending], Stages(journal, owner, method, ct));

        var after = journal.LoadNeed(HomeUpkeepFamily.Clearance, World, ct);
        var withdrawn = ColonyReview.WithdrawnTargets(journal, after, _ => true, ct);
        Assert.Equal(["chair"], withdrawn);
        // A later step proposing the chair again in this load (its choice flipped back) leaves it out; a target the choice does not govern stays.
        var step = new GroundStep
        {
            Phase = GroundPhase.Furniture,
            Targets = [new ClearanceTarget { EntityId = "chair", DefName = "Stool" }, new ClearanceTarget { EntityId = "wall", DefName = "Wall" }],
            Transitions = [retained with { Planned = new PlannedTransition("chair", "Stool", TransitionCause.RetiredGround, ServicingChoiceKind.Remove) }],
        };
        Assert.Equal(["wall"], HomeUpkeepReader.UnwithdrawnStep(step, withdrawn).Targets.Select(t => t.EntityId));
    }
}
