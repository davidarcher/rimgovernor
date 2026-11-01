using RimGovernor.Brain.Domain;
using RimGovernor.Brain.GameFacts.Observations;
using RimGovernor.Brain.Policy;
using Xunit;
using ClearanceTarget = RimGovernor.Brain.Policy.ClearanceTarget;
using Rectangle = RimGovernor.Brain.Policy.Rectangle;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// Task 8.1: a standing building's transition is one comparison of Retain, Repair, Replace, Relocate, Repurpose, Store and Remove,
/// priced from the native read (uninstall and deconstruct work, Minifiable, accepting storage, the worker's legs, the recovered
/// CostListAdjusted x resourcesFractionWhenDeconstructed) and the owning model's service term. Damage alone never selects Repair,
/// idleness alone never selects Remove, and nothing packs a building the native read says cannot be minified.
/// </summary>
public sealed class TransitionChoiceTests
{
    private const string Component = "ComponentIndustrial";

    private static readonly Cell At = new(5, 5);

    private static ServicingAlternative Alternative(TransitionDecision d, ServicingChoiceKind kind) => Assert.Single(d.Alternatives, a => a.Kind == kind);

    private static TransitionPrice Price(TransitionDecision d, ServicingChoiceKind kind) => Assert.IsType<ServicingStanding.Priced>(Alternative(d, kind).Standing).Price;

    private static TransitionFacts Serviced(TransitionMechanics m, params ServicingOption[] options) => TransitionFixtures.Facts(m) with
    {
        Servicing = options,
        Values = new Dictionary<Resource, ConstructionMaterialValue>(TransitionFixtures.Values) { [new Resource(Component)] = new(MaterialValueBasis.Market, 1) },
    };

    private static ServicingOption Repair => new ServicingOption.Assessed(new RepairWork("bench", "builder", true, true, 50, 100, 1000) { ServiceLoss = new ServiceLoss.Condition(50, 100) });

    private static ServicingOption Fix => new ServicingOption.Assessed(new FixBreakdownWork("bench", "builder", true, true, new Resource(Component), 100, 100, 1000, 1) { ServiceLoss = new ServiceLoss.Condition(0, 100) });

    [Fact]
    public void ADamagedIdleBenchIsRetainedAndOnlyABreakdownUnderABillQueueBuysTheFix()
    {
        // Half its hit points gone, a bill queue worth half a capability-day a day: hit points carry no function, so Repair only
        // adds labor to what Retain keeps, with the queue's service term or without it.
        var queue = new TransitionService.Assessed(0.5, 0);
        var damaged = ServicingChoiceRules.Transition(new("bench", "TableButcher", TransitionCause.StaleInterior) { Service = queue },
            Serviced(TransitionFixtures.Mechanics("bench", "TableButcher", At), Repair));
        Assert.Equal(ServicingChoiceKind.Retain, damaged.Chosen!.Kind);
        Assert.True(Price(damaged, ServicingChoiceKind.Repair).Total > Price(damaged, ServicingChoiceKind.Retain).Total);
        Assert.Equal(ServicingChoiceKind.Retain, ServicingChoiceRules.Transition(new("bench", "TableButcher", TransitionCause.StaleInterior),
            Serviced(TransitionFixtures.Mechanics("bench", "TableButcher", At), Repair)).Chosen!.Kind);

        // Broken down, Retain loses the queue for the whole horizon; the fix loses it only for its work and wins.
        var broken = ServicingChoiceRules.Transition(new("bench", "TableButcher", TransitionCause.StaleInterior) { Service = queue },
            Serviced(TransitionFixtures.Mechanics("bench", "TableButcher", At, brokenDown: true), Repair, Fix));
        Assert.Equal(ServicingChoiceKind.FixBreakdown, broken.Chosen!.Kind);
        Assert.Equal(0.5 * ColonyModelPolicy.DefaultHorizonDays, Price(broken, ServicingChoiceKind.Retain).Service, 6);
        Assert.False(broken.Admitted);
    }

    [Fact]
    public void ABuildingThatCannotBeMinifiedIsNeverPackedAndAMinifiableBedRelocates()
    {
        var spot = ServicingChoiceRules.Transition(new("spot", "CraftingSpot", TransitionCause.PlannedGround) { RetainExcluded = ReconcileRules.FootprintPlanned },
            TransitionFixtures.Facts(TransitionFixtures.Mechanics("spot", "CraftingSpot", At, minifiable: false)));
        Assert.Equal(new ServicingStanding.Excluded(ServicingChoiceRules.NotMinifiable), Alternative(spot, ServicingChoiceKind.Relocate).Standing);
        Assert.Equal(new ServicingStanding.Excluded(ServicingChoiceRules.NotMinifiable), Alternative(spot, ServicingChoiceKind.Store).Standing);
        Assert.Equal(ServicingChoiceKind.Remove, spot.Chosen!.Kind);
        Assert.False(spot.Uninstalls);

        // A bed the native read can minify, with a planned cell 300 ticks away that would otherwise be built new.
        var bed = ServicingChoiceRules.Transition(new("bed", "Bed", TransitionCause.PlannedGround)
        {
            RetainExcluded = ReconcileRules.FootprintPlanned,
            Destination = new(new TransitionLeg.Walked(300), new TransitionBuild.Priced(2)),
        }, TransitionFixtures.Facts(TransitionFixtures.Mechanics("bed", "Bed", At)));
        Assert.Equal(ServicingChoiceKind.Relocate, bed.Chosen!.Kind);
        Assert.True(bed.Uninstalls);
        Assert.Equal(-2, Price(bed, ServicingChoiceKind.Relocate).Material);
        // Without a read destination leg the relocation is unassessed, never guessed; no typed op repurposes anything.
        var unread = ServicingChoiceRules.Transition(new("bed", "Bed", TransitionCause.PlannedGround) { Destination = new(new TransitionLeg.Unread("no_path"), new TransitionBuild.Priced(2)) },
            TransitionFixtures.Facts(TransitionFixtures.Mechanics("bed", "Bed", At)));
        Assert.Equal(new ServicingStanding.Unassessed("relocation leg:bed:no_path"), Alternative(unread, ServicingChoiceKind.Relocate).Standing);
        Assert.Equal(new ServicingStanding.Excluded(ServicingChoiceRules.NoTypedOp), Alternative(unread, ServicingChoiceKind.Repurpose).Standing);
    }

    [Fact]
    public void StoreIsExcludedWithNoStockpileThatAcceptsTheBuilding()
    {
        var d = ServicingChoiceRules.Transition(new("dresser", "Dresser", TransitionCause.PlannedGround) { RetainExcluded = ReconcileRules.FootprintPlanned },
            TransitionFixtures.Facts(TransitionFixtures.Mechanics("dresser", "Dresser", At, storage: false)));
        Assert.Equal(new ServicingStanding.Excluded(ServicingChoiceRules.NoAcceptingStorage), Alternative(d, ServicingChoiceKind.Store).Standing);
        Assert.Equal(ServicingChoiceKind.Remove, d.Chosen!.Kind);
        // With accepting storage the dresser is kept whole: storing it costs only the walk, the uninstall and the carry.
        var stored = ServicingChoiceRules.Transition(new("dresser", "Dresser", TransitionCause.PlannedGround) { RetainExcluded = ReconcileRules.FootprintPlanned },
            TransitionFixtures.Facts(TransitionFixtures.Mechanics("dresser", "Dresser", At)));
        Assert.Equal(ServicingChoiceKind.Store, stored.Chosen!.Kind);
        // The uninstall driver works at ConstructionSpeed x 1.7.
        Assert.Equal((100 + 200 / 1.7 + 200) / GameTime.TicksPerDay, Price(stored, ServicingChoiceKind.Store).Labor, 9);
    }

    [Fact]
    public void RemoveCreditsTheRecoveredFractionChargesTheServiceAndLosesToRetainWhileTheServiceIsUnassessed()
    {
        var facts = TransitionFixtures.Facts(TransitionFixtures.Mechanics("dresser", "Dresser", At));
        // 10 steel at one capability-day each, half recovered by deconstruction: 5 destroyed.
        var priced = ServicingChoiceRules.Transition(new("dresser", "Dresser", TransitionCause.StaleInterior) { Service = new TransitionService.Assessed(0.1, 0) }, facts);
        var remove = Price(priced, ServicingChoiceKind.Remove);
        Assert.Equal(5, remove.Material, 9);
        Assert.Equal((100 + 200 / 1.7) / GameTime.TicksPerDay, remove.Labor, 9);
        Assert.Equal(0.1 * ColonyModelPolicy.DefaultHorizonDays, remove.Service, 6);
        Assert.Equal(ServicingChoiceKind.Retain, priced.Chosen!.Kind);

        // Idle and unassessed: the removal loses more service than Retain and cannot be priced against it.
        var idle = ServicingChoiceRules.Transition(new("dresser", "Dresser", TransitionCause.StaleInterior), facts);
        Assert.Equal(new ServicingStanding.Unassessed("service:Dresser"), Alternative(idle, ServicingChoiceKind.Remove).Standing);
        Assert.Equal(new ServicingStanding.Unassessed("service:Dresser"), Alternative(idle, ServicingChoiceKind.Store).Standing);
        Assert.Equal(ServicingChoiceKind.Retain, idle.Chosen!.Kind);
        Assert.False(idle.Admitted);
    }

    [Fact]
    public void ASpareBedLosesItsRemovalWhileADatedArrivalNeedsIt()
    {
        const long now = 600000;
        var arrival = new ExpectedChange("quest:7", ExpectedOwner.Sleeper, 1, new ExpectedTiming.Dated(now + 2 * GameTime.TicksPerDay), new ExpectedStay.Permanent(), new ExpectedLoss.PerDay(0.3));
        var service = ServicingChoiceRules.SpareBedService([arrival], now);
        Assert.Equal(new TransitionService.Assessed(0.3, 2), service);
        var facts = TransitionFixtures.Facts(TransitionFixtures.Mechanics("spare", "Bed", At));
        var kept = ServicingChoiceRules.Transition(new("spare", "Bed", TransitionCause.SpareBed) { Service = service }, facts);
        Assert.Equal(ServicingChoiceKind.Retain, kept.Chosen!.Kind);
        Assert.Equal(0.3 * (ColonyModelPolicy.DefaultHorizonDays - 2), Price(kept, ServicingChoiceKind.Remove).Service, 6);
        Assert.Equal(0.3 * (ColonyModelPolicy.DefaultHorizonDays - 2), Price(kept, ServicingChoiceKind.Store).Service, 6);
        // An undated arrival leaves the bed's service unassessed; with none expected the spare is unneeded and stored.
        Assert.Equal(new TransitionService.Unassessed("arrival tick:7"), ServicingChoiceRules.SpareBedService([arrival with { At = new ExpectedTiming.Unread("arrival tick:7") }], now));
        var spare = ServicingChoiceRules.Transition(new("spare", "Bed", TransitionCause.SpareBed)
        {
            Service = ServicingChoiceRules.SpareBedService([], now), RetainExcluded = RoomQualityRules.UnneededSpare,
        }, facts);
        Assert.Equal(ServicingChoiceKind.Store, spare.Chosen!.Kind);
    }

    [Fact]
    public void AHostRetirementIsAComparedRemoveThatLosesWhileTheReplacementDoesNotServe()
    {
        var facts = TransitionFixtures.Facts(TransitionFixtures.Mechanics("spot", "CraftingSpot", At, minifiable: false));
        var waiting = ServicingChoiceRules.Transition(new("spot", "CraftingSpot", TransitionCause.HostRetirement) { Service = ServicingChoiceRules.HostSpareService("MakeKnife") }, facts);
        Assert.Equal(ServicingChoiceKind.Retain, waiting.Chosen!.Kind);
        Assert.Equal(new ServicingStanding.Unassessed("replacement not serving:MakeKnife"), Alternative(waiting, ServicingChoiceKind.Remove).Standing);
        // Serving, the host comparison has replaced it: Retain is excluded and the priced Remove is chosen.
        var served = ServicingChoiceRules.Transition(new("spot", "CraftingSpot", TransitionCause.HostRetirement)
        {
            Service = ServicingChoiceRules.HostSpareService(null), RetainExcluded = PlannedRules.ReplacedHost,
        }, facts);
        Assert.Equal(ServicingChoiceKind.Remove, served.Chosen!.Kind);
        Assert.True(served.Admitted);
    }

    [Fact]
    public void TheNativeMinifiableReadNotACatalogSetDecidesPacking()
    {
        Assert.Null(typeof(ClearanceTarget).GetProperty("Packable"));
        Assert.Null(typeof(ReviewFacts).GetProperty("Packable"));
        var room = new PlannedRoom
        {
            Id = 1, Role = PlannedRole.Bedroom, Region = RegionRules.RectRegion(new Rectangle { X = 10, Z = 10, Width = 5, Height = 5 }),
            Door = new Cell(12, 9), DoorSide = new DoorSide.Doored(Rotation.South),
        };
        var piece = new WantedPiece { DefName = "Bed", Minimum = new Cell(11, 11), Maximum = new Cell(11, 11), Size = new Cell(1, 1), Rot = Rotation.North };
        var rows = new[] { new ClearanceTarget { EntityId = "old", DefName = "Dresser", Minimum = new Cell(11, 11), Maximum = new Cell(11, 11), Player = true, Deconstructible = true } };
        OpKind Removal(TransitionFacts facts) => Assert.Single(ReconcileRules.Reconcile(new ReconcileInput
        {
            Rooms = RoomTestFacts.NoRooms, Plan = new LayoutPlan { Rooms = [room] }, Room = room, Ground = new GroundCensus(),
            Rows = ReconcileRules.OwnerRows(room, rows, [piece], null, _ => false), Furniture = [piece], Transitions = facts,
        }).Owed, o => o.Kind is OpKind.Pack or OpKind.PackInUse or OpKind.FurnitureOut).Kind;
        Assert.Equal(OpKind.Pack, Removal(TransitionFixtures.Packing(rows)));
        Assert.Equal(OpKind.FurnitureOut, Removal(TransitionFixtures.Removing(rows)));
    }

    [Fact]
    public void RelocatingABrokenDownBuildingMovesItBrokenSoTheFixWinsAndTheMoveLosesTheHorizon()
    {
        var service = new TransitionService.Assessed(1, 0);
        var slot = new TransitionDestination(new TransitionLeg.Walked(300), new TransitionBuild.Priced(2));
        // Whole, the move loses the service only for its own days.
        var whole = ServicingChoiceRules.Transition(new("bench", "TableButcher", TransitionCause.StaleInterior) { Service = service, Destination = slot },
            Serviced(TransitionFixtures.Mechanics("bench", "TableButcher", At), Repair, Fix));
        Assert.Equal((100 + 200 / 1.7 + 300) / GameTime.TicksPerDay, Price(whole, ServicingChoiceKind.Relocate).Service, 9);
        // Broken down, the move restores nothing: it loses the service over the horizon as Retain does, and the fix wins.
        var broken = ServicingChoiceRules.Transition(new("bench", "TableButcher", TransitionCause.StaleInterior) { Service = service, Destination = slot },
            Serviced(TransitionFixtures.Mechanics("bench", "TableButcher", At, brokenDown: true), Repair, Fix));
        Assert.Equal(ColonyModelPolicy.DefaultHorizonDays, Price(broken, ServicingChoiceKind.Relocate).Service, 9);
        Assert.Equal(Price(broken, ServicingChoiceKind.Retain).Service, Price(broken, ServicingChoiceKind.Relocate).Service, 9);
        Assert.Equal(ServicingChoiceKind.FixBreakdown, broken.Chosen!.Kind);
    }
}
