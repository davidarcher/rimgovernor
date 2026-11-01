using RimGovernor.Brain.GameAccess;
using RimGovernor.Brain.GameFacts.Observations;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Runtime;
using RimGovernor.Brain.Store;
using Xunit;
using ClearanceTarget = RimGovernor.Brain.Policy.ClearanceTarget;
using Journal = RimGovernor.Brain.Store.Store;
using Rectangle = RimGovernor.Brain.Policy.Rectangle;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// Task 8.4: each admitted transition kind is continued through Hands as one native order. Remove is a Deconstruction, Store an
/// UninstallBuilding (the game's Uninstall), Relocate one MoveBuilding of the standing building onto its slot (the game's
/// Reinstall blueprint, which uninstalls, carries and installs it). Per kind: the order is issued once, a Refused receipt ends
/// the attempt, MethodOnce holds it in the same load, the receipt never settles the transition, and the census showing the
/// target gone (or, for a Relocate, standing on its slot) does. A Relocate whose slot is taken or whose binding dropped mid-chain
/// has its unissued reinstall cancelled.
/// </summary>
public sealed class TransitionHandsTests
{
    private static readonly GenerationSnapshot World = new(new("colony"), new(1), new("load"), new("review"), new(1), new(1));

    private static readonly WantedPiece Slot = new() { DefName = "Bed", Minimum = new Cell(11, 11), Maximum = new Cell(11, 11), Size = new Cell(1, 1), Rot = Rotation.North };

    private static readonly ClearanceTarget Old = new() { EntityId = "old", DefName = "Bed", Minimum = new Cell(13, 13), Maximum = new Cell(13, 13), Player = true, Deconstructible = true, InUse = true };

    private static PlannedRoom Room(PlannedRole role, int id = 1, int x = 10) => new()
    {
        Id = id, Role = role, Region = RegionRules.RectRegion(new Rectangle { X = x, Z = 10, Width = 5, Height = 5 }),
        Door = new Cell(x + 2, 9), DoorSide = new DoorSide.Doored(Rotation.South),
    };

    private static LayoutPlan Plan(params PlannedTransition[] transitions) => new() { Rooms = [Room(PlannedRole.Bedroom)], Transitions = transitions };

    private static DomainAction Order(ServicingChoiceKind kind, ActionId id) => kind switch
    {
        ServicingChoiceKind.Remove => DomainAction.NewDeconstructionAction(id, new Deconstruction(Old.EntityId, Old.DefName, Old.Minimum)),
        ServicingChoiceKind.Store => DomainAction.NewUninstallBuildingAction(id, new MoveBuilding(Old.EntityId, Old.DefName, Old.Minimum, Rotation.South)),
        _ => DomainAction.NewMoveBuildingAction(id, BuildingPlanner.ReinstallMethod("bedroom-1", Old, Slot).Action.MoveBuilding!),
    };

    private static (Journal Journal, NeedOwner Owner, PlanId Plan, ActionId Action) Committed(MethodId method, ServicingChoiceKind kind, CancellationToken ct)
    {
        IntentActionRules.RegisterIntentKinds();
        var journal = Journal.Open(Path.Combine(Path.GetTempPath(), "rg-transition-hands-" + Guid.NewGuid().ToString("N") + ".db"), ct);
        journal.Review(new ReviewRequest { Current = World, Tick = new(100), Enabled = true, Policy = Rules.DefaultPlannerSettings(), Facts = WorkResourceTestFacts.Create("a") }, ct);
        journal.RecordPlannerReasons(new Dictionary<FilingKey, PlannerNote>(), [new FamilyProgress(LayoutFamily.FamilyId, true, [new NeedProgress(LayoutFamily.Defense, false, default, method)])], ct);
        var goal = journal.LoadNeed(LayoutFamily.Defense, World, ct);
        var id = PlanIds.MintPlanId();
        var action = Order(kind, new ActionId(id.Value + "-0"));
        var commit = journal.CommitOwnerWork(goal, method, "", new PlanSpec(id, new(1), [action]), ct);
        Assert.True(commit.Outcome == ClaimOutcome.Admitted, $"{commit.Outcome} {commit.Refusal} {commit.Conflict}");
        return (journal, journal.LoadNeed(LayoutFamily.Defense, World, ct), id, action.Id);
    }

    [Theory]
    [InlineData(ServicingChoiceKind.Remove)]
    [InlineData(ServicingChoiceKind.Store)]
    [InlineData(ServicingChoiceKind.Relocate)]
    public void ARefusedOrderIsConsumedNotReissuedAndNeverSettlesTheTransition(ServicingChoiceKind kind)
    {
        var ct = TestContext.Current.CancellationToken;
        var method = new MethodId("bedroom-1-" + Wire.Name(kind) + "-old");
        var (journal, _, plan, action) = Committed(method, kind, ct);
        using var _ = journal;
        var scope = World with { Plan = plan };
        journal.Prepare(plan, action, scope, new(110), ct);
        var sent = journal.Dispatch(plan, action, scope, new(110), ct);
        journal.RecordReceipts([new BatchReceipt(plan, action, sent.View.Attempt, Receipt.Refused) { Refusal = new NativeRefusal("refused", "designated", RefusalClass.Permanent) }], ct);

        // The refused receipt ends the attempt (intent-mode) and the Method's attempt holds it: not issued again in this load.
        Assert.Equal(Stage.Unsuccessful, journal.LoadPlan(plan, ct).Progress[0].View.Stage);
        Assert.False(BuildingPlanner.MethodOnce(journal, journal.LoadNeed(LayoutFamily.Defense, World, ct), method, ct));

        // A receipt never settles: the bed still stands where it stood, so its transition stays bound.
        var bound = new PlannedTransition("old", "Bed", TransitionCause.PlannedGround, kind) { Slot = kind == ServicingChoiceKind.Relocate ? new RelocationSlot(Slot.Minimum) : null };
        var standing = new Dictionary<string, StandingBuilding> { ["old"] = new("Bed", [Old.Minimum]) };
        Assert.Empty(TransitionBindingRules.Settled(Plan(bound), standing, _ => false));
        // The census settles it: the target gone, or a relocated piece standing on its slot.
        Assert.Equal(["old"], TransitionBindingRules.Settled(Plan(bound), new Dictionary<string, StandingBuilding>(), _ => false));
        if (kind == ServicingChoiceKind.Relocate)
        {
            Assert.Equal(["old"], TransitionBindingRules.Settled(Plan(bound), new Dictionary<string, StandingBuilding> { ["old"] = new("Bed", [Slot.Minimum]) }, _ => false));
        }
    }

    [Fact]
    public void TheReinstallIsOneMoveOfTheStandingBuildingOntoItsSlot()
    {
        var (method, _, action) = BuildingPlanner.ReinstallMethod("bedroom-1", Old, Slot);
        Assert.Equal("bedroom-1-reinstall-old-11-11", method.Value);
        var move = Assert.IsType<MoveBuilding>(action.MoveBuilding);
        Assert.Equal(("old", "Bed", Slot.Anchor(), Slot.Rot), (move.Thing, move.Definition, move.Cell, move.Rotation));
        Assert.Null(action.UninstallBuilding);
    }

    [Fact]
    public void AReinstallWhoseSlotIsTakenMidChainIsCancelledAndNotIssuedAgain()
    {
        var ct = TestContext.Current.CancellationToken;
        var (method, _, _) = BuildingPlanner.ReinstallMethod("bedroom-1", Old, Slot);
        var (journal, owner, plan, _) = Committed(method, ServicingChoiceKind.Relocate, ct);
        using var _ = journal;
        static bool Bedroom(MethodId m) => m.Value.StartsWith("bedroom-1-", StringComparison.Ordinal);
        var bound = new PlannedTransition("old", "Bed", TransitionCause.PlannedGround, ServicingChoiceKind.Relocate) { Slot = new RelocationSlot(Slot.Minimum) };
        // While the slot is still the open slot proposed for it, the binding holds and the reinstall is the plan's.
        Assert.Equal(0, ColonyReview.CancelStaleTransitionWork(journal, owner, Plan(bound), Bedroom, _ => true, "building", ct));
        // Another piece took the slot: Holds fails, the binding drops, and the unissued reinstall is stale evidence.
        Assert.Equal(1, ColonyReview.CancelStaleTransitionWork(journal, owner, Plan(), Bedroom, _ => true, "building", ct));
        Assert.Equal([Stage.Cancelled], journal.LoadPlan(plan, ct).Progress.Select(p => p.View.Stage));
        Assert.False(BuildingPlanner.MethodOnce(journal, journal.LoadNeed(LayoutFamily.Defense, World, ct), method, ct));
    }

    [Fact]
    public void AReinstallOntoAnotherRoomsSlotWaitsOnlyOnWorkOverTheSlot()
    {
        // The bed stands in a bedroom whose furnishing omits it; the workshop next door has an open bed slot. With the slot's
        // build priced, the choice relocates it there: one Reinstall pairing the bed with that slot, ready while nothing else
        // owes work over the slot (this room enclosed or not: the slot is not its own).
        var bedroom = Room(PlannedRole.Bedroom);
        var other = Slot with { Minimum = new Cell(21, 11), Maximum = new Cell(21, 11) };
        var rows = new[] { Old };
        var sleeping = new SleepingObservation
        {
            Beds = [new SleepingBed
            {
                Native = SleepingNativeFixtures.Bed(id: "old", room: "", owners: [RoomImprovementTests.A]), Building = SleepingNativeFixtures.Building(id: "old", definition: new Resource("Bed")),
                Humanlike = true, RestEffectiveness = 1,
            }],
        };
        var packing = TransitionFixtures.Packing(rows);
        var facts = packing with
        {
            Standing = ServicingChoiceRules.StandingServiceOf(sleeping, (_, _) => new ExpectedLoss.PerDay(0.3), new Lazy<RoomImprovementFacts>(() => RoomImprovementTests.Facts()), packing),
            BuildPrice = _ => new TransitionBuild.Priced(5),
        };
        var r = ReconcileRules.Reconcile(new ReconcileInput
        {
            Rooms = RoomTestFacts.NoRooms, Plan = new LayoutPlan { Rooms = [bedroom, Room(PlannedRole.Workshop, 2, 20)] }, Room = bedroom, Ground = new GroundCensus(),
            Rows = ReconcileRules.OwnerRows(bedroom, rows, [], null, _ => false), Furniture = [], OpenSlots = [other], Transitions = facts,
        });
        var reinstall = Assert.Single(r.Ready, o => o.Kind == OpKind.Reinstall);
        Assert.Equal("old", Assert.Single(reinstall.Targets).EntityId);
        Assert.Equal(other, Assert.Single(reinstall.Pieces));
        Assert.DoesNotContain(r.Owed, o => o.Kind is OpKind.Pack or OpKind.PackInUse or OpKind.FurnitureOut or OpKind.Install);
    }
}
