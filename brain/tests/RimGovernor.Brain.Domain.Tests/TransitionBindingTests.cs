using RimGovernor.Brain.GameAccess;
using RimGovernor.Brain.GameFacts.Observations;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Store;
using Xunit;
using Journal = RimGovernor.Brain.Store.Store;
using Rectangle = RimGovernor.Brain.Policy.Rectangle;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// Task 8.2: a chosen standing-building transition enters the LayoutPlan as intent only (LayoutPlan.Transitions), survives a save
/// and load, and is read back instead of compared again while its inputs hold. A rewind to before it was planned leaves none; an
/// open removal whose transition is absent is stale evidence; a replacement that stops serving or a filled slot re-selects; an
/// older plan blob decodes with none; and nothing else in the store carries a transition (no restoration ledger).
/// </summary>
public sealed class TransitionBindingTests
{
    internal const string Spot = "CraftingSpot", Smithy = "Smithy", Knife = "MakeKnife";
    private static readonly GenerationSnapshot World = new(new("colony"), new(1), new("load"), new("review"), new(1), new(1));
    internal static readonly OrderSpec KnifeOrder = new() { Recipe = Knife, Target = 1, Mode = BillMode.StockTarget };
    internal static readonly HostBench SpotBench = new("spot", Spot, true, [Knife], []), SmithyBench = new("smithy", Smithy, true, [Knife], []);
    internal static readonly Cell BedAt = new(22, 22), BedSlot = new(21, 21);

    internal static LedgerView View(params HostBench[] benches) => WorkRules.NewLedgerView(WorkRules.LedgerViewReconciled) with { Orders = [KnifeOrder], Benches = benches };

    /// <summary>A workshop hosting the knife at the smithy.</summary>
    internal static LayoutPlan Workshop() => new LayoutPlan
    {
        Rooms = [new PlannedRoom
        {
            Role = PlannedRole.Workshop, Region = RegionRules.RectRegion(new Rectangle(20, 20, 6, 5)), Door = new Cell(22, 25),
            DoorSide = new DoorSide.Doored(Rotation.North), Production = [new PlannedProduction(Smithy, Knife, 1)],
        }],
        Entrances = [new Cell(22, 26)],
    }.WithRoomIDs();

    internal static readonly PlannedTransition Retire = new("spot", Spot, TransitionCause.HostRetirement, ServicingChoiceKind.Remove) { Dependency = new(Knife, Smithy) };
    internal static readonly PlannedTransition Move = new("bed", "Bed", TransitionCause.PlannedGround, ServicingChoiceKind.Relocate) { Slot = new(BedSlot) };

    private static LayoutPlan Bound() => Workshop() with { Transitions = [Move, Retire] };

    private static TransitionCandidate BedCandidate(Cell slot) => new("bed", "Bed", TransitionCause.PlannedGround)
    {
        RetainExcluded = ReconcileRules.FootprintPlanned,
        Destination = new(new TransitionLeg.Walked(300), new TransitionBuild.Priced(2)) { Slot = slot },
    };

    private static Journal Open(CancellationToken ct) => Journal.Open(Path.Combine(Path.GetTempPath(), "rg-transition-binding-" + Guid.NewGuid().ToString("N") + ".db"), ct);

    // (a) Two transitions round-trip through the save blobs byte for byte, and the next review reads the bound retirement back:
    // with no native mechanics the comparison would hold the spare, but the binding retires it without comparing again.
    [Fact]
    public void BoundTransitionsSurviveLoadAndAreNotComparedAgain()
    {
        var ct = TestContext.Current.CancellationToken;
        var plan = Bound();
        Assert.True(plan.Valid());
        using var source = Open(ct);
        source.RecordLayoutPlan(World, new Tick(100), plan, ct);
        var saved = new Dictionary<string, string>(source.GovernorStateBlobs(ct));
        using var loaded = Open(ct);
        loaded.RebuildFamilies(saved, ct);
        var restored = Assert.IsType<LayoutPlan>(loaded.LayoutPlan(World, new Tick(100), ct)?.Plan);
        Assert.Equal(plan.Transitions, restored.Transitions);
        Assert.Equal(saved[Journal.GovernorLayoutPlanKey], loaded.GovernorStateBlobs(ct)[Journal.GovernorLayoutPlanKey]);

        var (retirements, decisions) = PlannedRules.HostTransitionsOf(restored, View(SpotBench, SmithyBench), [Knife], _ => false, new TransitionFacts { Planned = restored.Transitions });
        Assert.Equal([new HostRetirement.Retire("spot", Spot, false)], retirements);
        var d = Assert.Single(decisions);
        Assert.IsType<ServicingStanding.Bound>(d.Chosen!.Standing);
        Assert.Empty(d.Alternatives);
        Assert.Same(restored, restored.WithTransitions(decisions));
        Assert.IsType<HostRetirement.Held>(Assert.Single(PlannedRules.HostRetirementsOf(restored, View(SpotBench, SmithyBench), [Knife], _ => false, new TransitionFacts())));
    }

    // (b) A rewind to before planning: the earlier save restores a plan with no transition, and the journal's plan at an earlier
    // tick carries none, so every open removal for those targets is stale and no order is issued for them.
    [Fact]
    public void ARewindToBeforePlanningLeavesNoTransition()
    {
        var ct = TestContext.Current.CancellationToken;
        using var journal = Open(ct);
        journal.RecordLayoutPlan(World, new Tick(100), Workshop(), ct);
        var before = new Dictionary<string, string>(journal.GovernorStateBlobs(ct));
        journal.RecordLayoutPlan(World, new Tick(200), Bound(), ct);
        Assert.Equal(2, Assert.IsType<LayoutPlan>(journal.LayoutPlan(World, new Tick(200), ct)?.Plan).Transitions.Count);
        var earlier = Assert.IsType<LayoutPlan>(journal.LayoutPlan(World, new Tick(150), ct)?.Plan);
        Assert.Empty(earlier.Transitions);

        using var rewound = Open(ct);
        rewound.RebuildFamilies(before, ct);
        var plan = Assert.IsType<LayoutPlan>(rewound.LayoutPlan(World, new Tick(100), ct)?.Plan);
        Assert.Empty(plan.Transitions);
        var open = new[] { ("host-retire-spot", "spot"), ("room-move-bed", "bed") };
        Assert.Equal(open, TransitionBindingRules.Stale(plan, open, o => o.Item2));
    }

    // (c) A receipt for an absent transition is stale evidence and closes nothing: the open removal is flagged, and the bound
    // transitions stay until the census shows them done (an accepted receipt never settles one).
    [Fact]
    public void AReceiptForAnAbsentTransitionIsRejectedAndClosesNothing()
    {
        var plan = Bound();
        var open = new[] { ("host-retire-spot", "spot"), ("host-retire-table", "table") };
        Assert.Equal([("host-retire-table", "table")], TransitionBindingRules.Stale(plan, open, o => o.Item2));

        var standing = new Dictionary<string, StandingBuilding>(StringComparer.Ordinal)
        {
            ["spot"] = new(Spot, [new Cell(1, 1)]),
            ["bed"] = new("Bed", [BedAt, new Cell(22, 23)]),
        };
        Assert.Empty(TransitionBindingRules.Settled(plan, standing, _ => false));
        // Done is observed: the spot gone, the bed standing on its slot.
        Assert.Equal(["bed", "spot"], TransitionBindingRules.Settled(plan, new Dictionary<string, StandingBuilding> { ["bed"] = new("Bed", [BedSlot]) }, _ => false).Order(StringComparer.Ordinal));
        // Moot: the plan no longer hosts the knife at the smithy, the bed's planned ground is gone.
        Assert.Equal(["bed", "spot"], TransitionBindingRules.Settled(plan with { Rooms = [] }, standing, _ => false).Order(StringComparer.Ordinal));
    }

    // (d) The replacement stops serving: the dependency is no longer named, the spare is compared afresh and kept, its binding
    // drops, and the open retirement is stale so its unissued actions are cancelled; once it serves again the removal is re-proposed.
    [Fact]
    public void AReplacementThatStopsServingReopensTheDependency()
    {
        var plan = Bound();
        var facts = TransitionFixtures.Facts(TransitionFixtures.Mechanics("spot", Spot, new Cell(1, 1), minifiable: false)) with { Planned = plan.Transitions };
        var (retirements, decisions) = PlannedRules.HostTransitionsOf(plan, View(SpotBench, SmithyBench with { Serves = false }), [Knife], _ => false, facts);
        Assert.Empty(retirements);
        var d = Assert.Single(decisions);
        Assert.False(d.Admitted);
        Assert.Null(d.Planned);
        var reopened = plan.WithTransitions(decisions);
        Assert.Equal([Move], reopened.Transitions);
        Assert.Equal([("host-retire-spot", "spot")], TransitionBindingRules.Stale(reopened, [("host-retire-spot", "spot")], o => o.Item2));

        var (again, rebound) = PlannedRules.HostTransitionsOf(reopened, View(SpotBench, SmithyBench), [Knife], _ => false, facts with { Planned = reopened.Transitions });
        Assert.Equal([new HostRetirement.Retire("spot", Spot, false)], again);
        Assert.Equal(Retire, Assert.Single(rebound).Planned);
        Assert.Equal(plan.Transitions, reopened.WithTransitions(rebound).Transitions);
    }

    // (e) A Relocate holds while its slot is still the one proposed; a filled slot (another slot proposed, or none) re-selects.
    [Fact]
    public void AFilledDestinationSlotReselectsTheRelocation()
    {
        var facts = TransitionFixtures.Facts(TransitionFixtures.Mechanics("bed", "Bed", BedAt)) with { Planned = [Move] };
        var held = ServicingChoiceRules.Choose(BedCandidate(BedSlot), facts);
        Assert.IsType<ServicingStanding.Bound>(held.Chosen!.Standing);
        Assert.Same(Move, held.Planned);

        var other = new Cell(24, 21);
        var moved = ServicingChoiceRules.Choose(BedCandidate(other), facts);
        Assert.NotEmpty(moved.Alternatives);
        Assert.Equal(ServicingChoiceKind.Relocate, moved.Chosen!.Kind);
        Assert.Equal(new RelocationSlot(other), moved.Planned!.Slot);

        var none = ServicingChoiceRules.Choose(BedCandidate(BedSlot) with { Destination = null }, facts);
        Assert.NotEmpty(none.Alternatives);
        Assert.NotEqual(ServicingChoiceKind.Relocate, none.Chosen!.Kind);
        Assert.False(TransitionBindingRules.Holds(Move, BedCandidate(BedSlot) with { Destination = null }));
    }

    // (f) Task 8.4, no save shim: a plan blob written before schema 77 has no Transitions field. It no longer decodes, so the
    // save restores no plan (StateMirror.SavedLayoutPlan) and the next survey derives a fresh one; the current blob round-trips.
    [Fact]
    public void AnOlderPlanBlobRestoresAsNoPlanAndTheCurrentOneLoads()
    {
        var ct = TestContext.Current.CancellationToken;
        using var source = Open(ct);
        source.RecordLayoutPlan(World, new Tick(100), Workshop(), ct);
        var blobs = new Dictionary<string, string>(source.GovernorStateBlobs(ct));
        var saved = blobs[Journal.GovernorLayoutPlanKey];
        using (var current = Open(ct))
        {
            current.RebuildFamilies(blobs, ct);
            var plan = Assert.IsType<LayoutPlan>(current.LayoutPlan(World, new Tick(100), ct)?.Plan);
            Assert.Equal(Knife, Assert.Single(Assert.Single(plan.Rooms).Production).Recipe);
        }
        var old = saved.Replace(",\"Transitions\":[]", "", StringComparison.Ordinal);
        Assert.NotEqual(saved, old);
        blobs[Journal.GovernorLayoutPlanKey] = old;
        using var loaded = Open(ct);
        loaded.RebuildFamilies(blobs, ct);
        Assert.Null(loaded.LayoutPlan(World, new Tick(100), ct));
    }

    // (g) No second ledger: of every type the store has a codec for, only LayoutPlan holds a PlannedTransition, and a transition
    // carries intent only (no tick, receipt, attempt, price or observed service).
    [Fact]
    public void NoStoredTypeButTheLayoutPlanCarriesATransition()
    {
        static bool Mentions(Type t) => t == typeof(PlannedTransition) || (t.IsArray && Mentions(t.GetElementType()!)) || (t.IsGenericType && t.GetGenericArguments().Any(Mentions));
        var stored = typeof(RimGovernor.Brain.Store.Codec.Codecs).Assembly.GetTypes()
            .Select(t => t.BaseType)
            .Where(b => b is { IsGenericType: true } && b.GetGenericTypeDefinition().Name == "ObjectCodec`1")
            .Select(b => b!.GetGenericArguments()[0])
            .Distinct()
            .ToArray();
        Assert.Contains(typeof(LayoutPlan), stored);
        var carriers = stored.SelectMany(t => t.GetProperties().Where(p => Mentions(p.PropertyType)).Select(p => t.Name + "." + p.Name)).ToArray();
        Assert.Equal(["LayoutPlan.Transitions"], carriers);
        Assert.Equal(["Cause", "Definition", "Dependency", "Kind", "Slot", "Target"], typeof(PlannedTransition).GetProperties().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal(["Host", "Recipe"], typeof(ReplacementDependency).GetProperties().Select(p => p.Name).Order(StringComparer.Ordinal));
    }
}
