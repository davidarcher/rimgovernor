using RimGovernor.Brain.GameAccess;
using RimGovernor.Brain.GameFacts.Observations;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Runtime;
using RimGovernor.Brain.Store;
using Xunit;
using Journal = RimGovernor.Brain.Store.Store;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// Task 8.3: a transition removal is admitted only while what the building provides is observed served by a replacement on fresh
/// facts (TransitionBridgingRules): an occupied bed's sleepers, a live net's consumers, a pen's herd, the defense's standing
/// tiers. Otherwise the chosen removal is gated, <c>replacement not serving:&lt;dependant&gt;</c>, and binds nothing; a replacement
/// lost before the old service goes reopens it (the binding drops and the unissued removal is cancelled), and settlement is read
/// from the census alone, so a removal settled in an earlier load is cancelled on the first review after a reload.
/// </summary>
public sealed class TransitionBridgingTests
{
    private static readonly GenerationSnapshot World = new(new("colony"), new(1), new("load"), new("review"), new(1), new(1));

    private static readonly PawnId A = new("a"), B = new("b");

    // A building whose footprint the plan builds on: it cannot stay, and not being minifiable, the comparison removes it.
    private static TransitionDecision Decide(string target, string def, Func<string, string, string?>? unserved, params PlannedTransition[] planned) =>
        ServicingChoiceRules.Choose(new(target, def, TransitionCause.PlannedGround) { RetainExcluded = ReconcileRules.FootprintPlanned, Service = new TransitionService.Assessed(0, 0) },
            TransitionFixtures.Facts(TransitionFixtures.Mechanics(target, def, new Cell(1, 1), minifiable: false)) with { Unserved = unserved, Planned = planned });

    private static SleepingBed Bed(string id, EquatableArray<PawnId> owners = default, EquatableArray<PawnId> reach = default, int slots = 1) => new()
    {
        Native = SleepingNativeFixtures.Bed(id, owners: owners, accessibleTo: reach, slots: slots), Building = SleepingNativeFixtures.Building(id),
        Humanlike = true, RestEffectiveness = 1,
    };

    private static (Journal Journal, NeedOwner Owner) Committed(MethodId method, CancellationToken ct, params string[] targets)
    {
        var journal = Journal.Open(Path.Combine(Path.GetTempPath(), "rg-transition-bridge-" + Guid.NewGuid().ToString("N") + ".db"), ct);
        journal.Review(new ReviewRequest { Current = World, Tick = new(100), Enabled = true, Policy = Rules.DefaultPlannerSettings(), Facts = WorkResourceTestFacts.Create("a") }, ct);
        var need = HomeUpkeepFamily.Clearance;
        journal.RecordPlannerReasons(new Dictionary<FilingKey, PlannerNote>(), [new FamilyProgress(need.Family, true, [new NeedProgress(need, false, default, method)])], ct);
        var goal = journal.LoadNeed(need, World, ct);
        var id = PlanIds.MintPlanId();
        var actions = targets.Select((t, i) => DomainAction.NewDeconstructionAction(new ActionId(id.Value + "-" + i), new Deconstruction(t, "Bed", new Cell(1 + i, 1)))).ToArray();
        var commit = journal.CommitOwnerWork(goal, method, "", new PlanSpec(id, new(1), actions), ct);
        Assert.True(commit.Outcome == ClaimOutcome.Admitted, $"{commit.Outcome} {commit.Refusal} {commit.Conflict}");
        return (journal, journal.LoadNeed(need, World, ct));
    }

    private static Stage[] Stages(Journal journal, NeedOwner owner, MethodId method, CancellationToken ct) =>
        [.. journal.LoadPlan(owner.Methods.Single(m => m.Method == method).Plan, ct).Progress.Select(p => p.View.Stage)];

    [Fact]
    public void AnOccupiedBedIsKeptWhileItsReplacementIsUnassignableAndRemovedOnceTheSleeperIsAssigned()
    {
        // The new bed is out of the sleeper's reach, then full with another owner: either way the old bed's sleeper has no rest.
        foreach (var replacement in new[] { Bed("new"), Bed("new", owners: [B], reach: [A, B]) })
        {
            var sleeping = new SleepingObservation { Beds = [Bed("old", owners: [A], reach: [A]), replacement] };
            Assert.Equal("rest:a", TransitionBridgingRules.RestUnserved(sleeping, "old"));
            var held = Decide("old", "Bed", (t, _) => TransitionBridgingRules.RestUnserved(sleeping, t));
            Assert.Equal(ServicingChoiceKind.Remove, held.Chosen?.Kind);
            Assert.Equal("replacement not serving:rest:a", held.Gate);
            Assert.Equal(held.Gate, held.Hold);
            Assert.False(held.Admitted);
            Assert.Null(held.Planned);
        }

        // Assignable (in reach, a free slot) the replacement serves; assigned, the old bed has no sleeper left to strand.
        Assert.Null(TransitionBridgingRules.RestUnserved(new SleepingObservation { Beds = [Bed("old", owners: [A], reach: [A]), Bed("new", reach: [A])] }, "old"));
        var moved = new SleepingObservation { Beds = [Bed("old", reach: [A]), Bed("new", owners: [A], reach: [A])] };
        var removed = Decide("old", "Bed", (t, _) => TransitionBridgingRules.RestUnserved(moved, t));
        Assert.True(removed.Admitted);
        Assert.Equal(ServicingChoiceRules.ReplacementServing, removed.DependencyState);
        Assert.Equal(new PlannedTransition("old", "Bed", TransitionCause.PlannedGround, ServicingChoiceKind.Remove), removed.Planned);
    }

    [Fact]
    public void AGeneratorIsHeldUntilItsReplacementPowersTheNetAndAReplacementBreakdownReopensAndCancels()
    {
        var ct = TestContext.Current.CancellationToken;
        static PowerTopology Net(bool replacementRunning) => new()
        {
            Buildings =
            [
                PowerBuildingFixtures.Trader("old", "WoodFiredGenerator", new Cell(1, 1), 1000, 1000, true),
                PowerBuildingFixtures.Trader("new", "SolarGenerator", new Cell(4, 1), 1700, replacementRunning ? 1000 : 0, replacementRunning),
                PowerBuildingFixtures.Trader("lamp", "StandingLamp", new Cell(2, 2), -500, -500, true),
            ],
            Networks = [new PowerNetwork { Id = "net", GenerationW = replacementRunning ? 2000 : 1000, ConsumptionW = 500, StoredWD = 0 }],
        };
        // The new generator stands but does not run: removing the old one would black out the lamp.
        Assert.Equal("power:lamp", TransitionBridgingRules.PowerUnserved(Net(false), "old"));
        Assert.Equal("replacement not serving:power:lamp", Decide("old", "WoodFiredGenerator", (t, _) => TransitionBridgingRules.PowerUnserved(Net(false), t)).Gate);
        // A consumer's own removal blacks out no one.
        Assert.Null(TransitionBridgingRules.PowerUnserved(Net(false), "lamp"));

        // It powers the net: the removal is admitted and bound.
        var bound = Decide("old", "WoodFiredGenerator", (t, _) => TransitionBridgingRules.PowerUnserved(Net(true), t));
        Assert.True(bound.Admitted);
        var plan = new LayoutPlan().WithTransitions([bound]);
        var method = new MethodId("deconstruct-old");
        var (journal, owner) = Committed(method, ct, "old");
        using var _ = journal;
        // While it still serves, the next review reads the binding back and the open removal stands.
        var still = Decide("old", "WoodFiredGenerator", (t, _) => TransitionBridgingRules.PowerUnserved(Net(true), t), [.. plan.Transitions]);
        Assert.IsType<ServicingStanding.Bound>(still.Chosen!.Standing);
        Assert.Same(plan, plan.WithTransitions([still]));
        Assert.Equal(0, ColonyReview.CancelStaleTransitionWork(journal, owner, plan, _ => true, t => t == "old", HomeUpkeepFamily.FamilyId, ct));

        // The replacement breaks down before the old generator is taken down: the dependency reopens, the binding drops and the
        // unissued removal is cancelled; the old generator keeps the lamp lit.
        var reopened = Decide("old", "WoodFiredGenerator", (t, _) => TransitionBridgingRules.PowerUnserved(Net(false), t), [.. plan.Transitions]);
        Assert.Equal("replacement not serving:power:lamp", reopened.Gate);
        var dropped = plan.WithTransitions([reopened]);
        Assert.Empty(dropped.Transitions);
        Assert.Equal(1, ColonyReview.CancelStaleTransitionWork(journal, owner, dropped, _ => true, t => t == reopened.Target, HomeUpkeepFamily.FamilyId, ct));
        Assert.Equal([Stage.Cancelled], Stages(journal, owner, method, ct));
    }

    [Fact]
    public void AnOldFenceIsHeldWhileTheNewPenHasAGapAndRemovedOnceItIsClosedAndFed()
    {
        // The old fence still bounds a pen holding the muffalo, so it is one of the herd's pieces.
        var gap = new HerdArrangement { Herd = "muffalo", PenUnserved = true, Pieces = ["fence"] };
        var unfed = new HerdArrangement { Herd = "muffalo", FeedingDeficit = 2, Pieces = ["fence"] };
        var served = new HerdArrangement { Herd = "muffalo", Pieces = ["fence"] };
        Assert.Equal("pen:muffalo", TransitionBridgingRules.PenUnserved([gap], "fence"));
        Assert.Equal("pen:muffalo", TransitionBridgingRules.PenUnserved([unfed], "fence"));
        Assert.Equal("pen:muffalo", TransitionBridgingRules.PenUnserved([served with { Uncontained = 1 }], "fence"));
        Assert.Null(TransitionBridgingRules.PenUnserved([gap], "other fence"));
        Assert.Equal("replacement not serving:pen:muffalo", Decide("fence", "Fence", (t, _) => TransitionBridgingRules.PenUnserved([gap], t)).Gate);

        // Closed and fed: the old fence goes.
        var removed = Decide("fence", "Fence", (t, _) => TransitionBridgingRules.PenUnserved([served], t));
        Assert.True(removed.Admitted);

        // The new fence breaks before the old one is down: the bound removal is re-proposed and held.
        var reopened = Decide("fence", "Fence", (t, _) => TransitionBridgingRules.PenUnserved([gap], t), removed.Planned!);
        Assert.Equal("replacement not serving:pen:muffalo", reopened.Gate);
        Assert.Empty(new LayoutPlan().WithTransitions([removed]).WithTransitions([reopened]).Transitions);
    }

    [Fact]
    public void ARetiredTurretIsHeldUntilTheDefenseReplacingItStandsAndALostPieceReopens()
    {
        var turret = PowerBuildingFixtures.Trader("turret", "Turret_MiniTurret", new Cell(9, 9), -100, -100, true) with { Turret = new TurretGun(5) };
        var sandbags = PowerBuildingFixtures.Trader("sb", "Sandbags", new Cell(5, 5), 0, 0, false);
        var power = new PowerTopology { Buildings = [turret] };
        var plan = new LayoutPlan
        {
            Sites = [new PlannedSite { Node = "defchoice-firing", Kind = NodeKind.DefChoice, Tier = new TierFacts { Tier = new DefenseTierName("firing_line"), Places = [new TierPlacement("Sandbags", new Cell(5, 5), Rotation.North, "")] } }],
        };
        static IReadOnlyDictionary<string, CurrentBuilding> Standing(params RimGovernor.Brain.GameFacts.Observations.BuildingState[] buildings) =>
            buildings.ToDictionary(b => b.Building.Id, b => new CurrentBuilding { Observed = b }, StringComparer.Ordinal);

        // The tier's sandbags are unbuilt: the turret's cover stays.
        Assert.Equal("defense:firing_line", TransitionBridgingRules.DefenseUnserved(plan, power, Standing(turret), "turret", "Turret_MiniTurret"));
        // No defense planned at all: nothing replaces the turret.
        Assert.Equal("defense:Turret_MiniTurret", TransitionBridgingRules.DefenseUnserved(new LayoutPlan(), power, Standing(turret), "turret", "Turret_MiniTurret"));
        // Unread census with a tier planned: named, not guessed.
        Assert.Equal("defense:construction census", TransitionBridgingRules.DefenseUnserved(plan, power, null, "turret", "Turret_MiniTurret"));

        // The sandbags stand: the turret may go; the sandbags themselves are the chosen defense and never go.
        Assert.Null(TransitionBridgingRules.DefenseUnserved(plan, power, Standing(turret, sandbags), "turret", "Turret_MiniTurret"));
        Assert.Equal("defense:firing_line", TransitionBridgingRules.DefenseUnserved(plan, power, Standing(turret, sandbags), "sb", "Sandbags"));
        var removed = Decide("turret", "Turret_MiniTurret", (t, d) => TransitionBridgingRules.DefenseUnserved(plan, power, Standing(turret, sandbags), t, d));
        Assert.True(removed.Admitted);

        // The sandbags are destroyed before the turret is down: the dependency reopens and the turret stays.
        var reopened = Decide("turret", "Turret_MiniTurret", (t, d) => TransitionBridgingRules.DefenseUnserved(plan, power, Standing(turret), t, d), removed.Planned!);
        Assert.Equal("replacement not serving:defense:firing_line", reopened.Gate);
        Assert.Null(reopened.Planned);
    }

    [Fact]
    public void ASettledTransitionsOpenRemovalIsCancelledOnTheFirstReviewAfterLoad()
    {
        var ct = TestContext.Current.CancellationToken;
        var method = new MethodId("deconstruct-bed");
        var (journal, owner) = Committed(method, ct, "bed");
        using var _ = journal;
        // An earlier load settled the spare bed's transition (a pawn owns it now) and recorded the plan without it, then the game
        // reloaded before the removal was cancelled: nothing settled is remembered, the census alone settles it again.
        var plan = new LayoutPlan();
        var standing = new Dictionary<string, StandingBuilding>(StringComparer.Ordinal) { ["bed"] = new("Bed", [new Cell(1, 1)]) };
        bool Settled(string t) => TransitionBindingRules.SettledOpen(plan, t, [TransitionCause.SpareBed], standing, b => b == "bed");
        Assert.True(Settled("bed"));
        Assert.Equal(1, ColonyReview.CancelStaleTransitionWork(journal, owner, plan, _ => true, Settled, HomeUpkeepFamily.FamilyId, ct));
        Assert.Equal([Stage.Cancelled], Stages(journal, owner, method, ct));

        // An unowned spare, a building gone, or a host spare (its dependency is not recoverable from the removal) is not settled.
        Assert.False(TransitionBindingRules.SettledOpen(plan, "bed", [TransitionCause.SpareBed], standing, _ => false));
        Assert.False(TransitionBindingRules.SettledOpen(plan, "gone", [TransitionCause.SpareBed], standing, _ => true));
        Assert.False(TransitionBindingRules.SettledOpen(plan, "bed", [TransitionCause.HostRetirement], standing, _ => true));
        // Furniture off every retired and planned room's ground has lost its cause.
        Assert.True(TransitionBindingRules.SettledOpen(plan, "bed", [TransitionCause.RetiredGround, TransitionCause.PlannedGround], standing, _ => false));
    }
}
