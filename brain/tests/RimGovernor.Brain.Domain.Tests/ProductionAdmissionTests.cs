using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Store;
using RimGovernor.Brain.Store.Codec;
using System.Buffers;
using System.Text.Json;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;
using Journal = RimGovernor.Brain.Store.Store;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class ProductionAdmissionTests
{
    private static readonly Resource Wood = new("TestMaterial");
    private static readonly Resource Other = new("TestOther");
    private static readonly GenerationSnapshot World = new(new ColonyId("colony"), new MapId(1), new LoadId("load"), new PlanId("review"), new PlanRevision(1), default);
    private static ProductionBatch Batch(string id) => new(new ProductionBill("bench-" + id, "TestRecipe", BillMode.GearBatch, 5, Wood.Value),
        new ProgressView { Action = new ActionId(id), Stage = Stage.Pending });
    private static RoundsFacts Facts => new()
    {
        Tick = new Tick(100),
        WorkResources = Known(default(EquatableArray<RimGovernor.Contracts.Wire.Observations.WorkResourcePawn>)),
        ForecastObserved = new() { WorkPawns = Known(default(EquatableArray<WorkPawn>)) },
        WorkProfiles = Known<EquatableArray<PawnProfile>>([new() { Id = new("artist"),
            WorkSkill = new Dictionary<WorkType, string> { [WorkType.Art] = "TestArt" },
            Skills = new Dictionary<string, ProfileSkill> { ["TestArt"] = new() { Name = "TestArt", Level = 8 } } }]),
        Resources = Known<EquatableArray<Amount>>([new(Wood, 60), new(Other, 100)]),
        CurrentConstruction = CurrentConstruction.Empty with { Deficit = new Dictionary<Resource, long> { [Wood] = 40 } },
        BillReservations = [],
        WorkAdmission = new()
        {
            Recipes = Known<EquatableArray<ProductionRecipeInput>>([new("TestRecipe", [[new(Wood, 10)]], false) { Products = [], WorkAmount = 0 }]),
            Bills = Known(default(EquatableArray<ProductionBatchState>)),
        },
    };
    private static WorkAdmissionEvidence Evidence(RoundsFacts? facts = null, params ProductionBatch[] reviewed) =>
        ProductionRules.WorkAdmissionEvidenceOf(facts ?? Facts, Known(EquatableArray.From(reviewed)));
    private static bool Fits(WorkAdmissionEvidence evidence, ProductionBatch[] committed, params ProductionBatch[] proposed) =>
        ProductionRules.ProductionAdmissionFits(evidence, Known(EquatableArray.From(committed)), EquatableArray.From(proposed));
    private static PlanSpec Plan(string id) => new(new PlanId(id), new PlanRevision(1),
        [DomainAction.NewProductionBillAction(new ActionId(id), Batch(id).Order)]);

    private static WorkAdmissionEvidence RoundTrip(WorkAdmissionEvidence evidence)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            Codecs.Default.Write(writer, evidence);
        }
        using var doc = JsonDocument.Parse(buffer.WrittenMemory);
        return Codecs.Default.Read<WorkAdmissionEvidence>(doc.RootElement);
    }

    [Fact]
    public void TheFrozenCensusSurvivesTheJournalCodecReadOrUnread()
    {
        var standing = RoundTrip(new WorkAdmissionEvidence { Standing = new([], new Dictionary<Resource, long> { [Wood] = 3 }) }).Standing;
        Assert.NotNull(standing);
        Assert.Empty(standing.Buildings);
        Assert.Equal(3, standing.Owed[Wood]);
        Assert.NotNull(Evidence().Standing);
        Assert.Null(Evidence(Facts with { CurrentConstruction = null }).Standing);
        Assert.Null(RoundTrip(new WorkAdmissionEvidence()).Standing);
    }

    // #3895: a target's quantity is a stack's units or none, and its claimant capacity a plain count.
    [Fact]
    public void ANeedTargetCapacityKeepsItsStackOrItsAbsenceThroughTheJournalCodec()
    {
        WorkTargetCapacity[] targets = [new("stack", 10, new TargetQuantity.Units(7)), new("bed", 1, new TargetQuantity.Unbounded())];
        var back = RoundTrip(new WorkAdmissionEvidence { NeedTargets = EquatableArray.From(targets) }).NeedTargets;
        Assert.Equal(targets, back.ToArray());
    }

    [Fact]
    public void ANeedTargetsQuantityIsItsStackedCountOrUnbounded()
    {
        Assert.Equal(new TargetQuantity.Units(4), TargetQuantity.Of(new RimGovernor.Contracts.Wire.Observations.NeedStock.Stacked(4, new RimGovernor.Contracts.Wire.Observations.FoodFreshness.Never())));
        Assert.Equal(new TargetQuantity.Unbounded(), TargetQuantity.Of(new RimGovernor.Contracts.Wire.Observations.NeedStock.NotStacked()));
    }

    [Fact]
    public void TwoAffordableBatchesCannotSpendTheSameRemainderOrHideInOneBundle()
    {
        var e = Evidence();
        Assert.True(Fits(e, [], Batch("first")));
        Assert.False(Fits(e, [Batch("first")], Batch("second")));
        Assert.False(Fits(e, [], Batch("first"), Batch("second")));
        Assert.True(Fits(Evidence(Facts with { Resources = Known<EquatableArray<Amount>>([new(Wood, 100), new(Other, 100)]) }), [], Batch("first"), Batch("second")));
    }

    [Fact]
    public void CancellationReleasesOnlyNeverDispatchedClaimsIncludingReviewedOnes()
    {
        var first = Batch("first");
        var cancelled = first with { Progress = first.Progress with { Stage = Stage.Cancelled } };
        Assert.True(Fits(Evidence(), [cancelled], Batch("second")));
        Assert.True(Fits(Evidence(null, first), [cancelled], Batch("second")));
        var dispatched = cancelled with { Progress = cancelled.Progress with { Attempt = new AttemptId(1), Bill = Known("bill") } };
        Assert.False(Fits(Evidence(), [dispatched], Batch("second")));
        Assert.False(Fits(Evidence(null, first), [dispatched], Batch("second")));
    }

    [Fact]
    public void DispatchCompletionDoesNotSpendOldStockTwiceAndFreshReadCanReleaseIt()
    {
        var first = Batch("first");
        var done = first with { Progress = first.Progress with { Stage = Stage.Completed, Attempt = new AttemptId(1), Bill = Known("bill") } };
        Assert.False(Fits(Evidence(), [done], Batch("second")));
        Assert.False(Fits(Evidence(null, first), [done], Batch("second")));
        // Complete census says the bill disappeared; a fresh stock observation now owns what remains.
        Assert.True(Fits(Evidence(Facts, done), [done], Batch("second")));
    }

    [Fact]
    public void UnknownEvidenceRefusesAndNativeHoldsOverlapOnlyTheirExactBill()
    {
        Assert.False(Fits(Evidence(Facts with { Resources = default }), [], Batch("new")));
        Assert.False(Fits(Evidence(Facts with { WorkAdmission = new() }), [], Batch("new")));
        var first = Batch("first") with { Progress = Batch("first").Progress with { Stage = Stage.Completed, Attempt = new AttemptId(1), Bill = Known("bill") } };
        var f = Facts with
        {
            WorkAdmission = Facts.WorkAdmission with { Bills = Known<EquatableArray<ProductionBatchState>>([new("bill", true, BillRepeat.Count, 1, false, false, new ProductionUnfinishedWork("", default))]) },
            BillReservations = [new() { Bill = "bill", Pawn = new("pawn"), Items = [new(Wood, 10)] }],
        };
        Assert.True(Fits(Evidence(f, first), [first], Batch("new")));
        Assert.False(Fits(Evidence(f with { BillReservations = [new() { Bill = "other", Pawn = new("pawn"), Items = [new(Wood, 10)] }] }, first), [first], Batch("new")));
    }

    [Fact]
    public void ImmediateReviewRetainsEvidenceButWorldChangeAndUnreadFullReviewCannotApprove()
    {
        var ct = TestContext.Current.CancellationToken;
        using var journal = Journal.Open(Path.Combine(Path.GetTempPath(), "rg-admission-world-" + Guid.NewGuid().ToString("N") + ".db"), ct);
        var request = new RoundsRequest { Current = World, Tick = Facts.Tick, Enabled = true, Policy = RoundsRules.DefaultRoundsPolicy(), Facts = Facts };
        var full = journal.ReviewRounds(request, ct);
        var immediate = journal.ReviewRounds(request with { Revision = full.Review.Revision, Immediate = true, Tick = new Tick(101), Facts = new() }, ct);
        Assert.Equal(60, journal.LoadRounds(ct).WorkEvidence!.Stock.Value[0].Count);
        var unread = journal.ReviewRounds(request with { Revision = immediate.Review.Revision, Tick = new Tick(102), Facts = Facts with { Resources = default } }, ct);
        var refused = journal.CommitClaimedMethod(AdminFamily.WorkLedger, World, new MethodId("unread"), "", Plan("unread"), ct);
        Assert.Equal(RefusalKind.ProductionMaterial, refused.Refusal!.Kind);
        var changed = journal.ReviewRounds(request with { Revision = unread.Review.Revision, Immediate = true, Current = World with { Load = new LoadId("new-load") }, Tick = new Tick(103), Facts = new() }, ct);
        Assert.Null(changed.Review.WorkEvidence);
    }

    [Fact]
    public async Task SeparateStoreConnectionsCompeteAtomicallyAndCancellationReleasesTheWinner()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = Path.Combine(Path.GetTempPath(), "rg-admission-" + Guid.NewGuid().ToString("N") + ".db");
        using var first = Journal.Open(path, ct);
        first.ReviewRounds(new() { Current = World, Tick = Facts.Tick, Enabled = true, Policy = RoundsRules.DefaultRoundsPolicy(), Facts = Facts }, ct);
        // Two admin/work-ledger Methods, one per connection, compete for the same material.
        bool Commit(Journal store, int i) =>
            store.CommitClaimedMethod(AdminFamily.WorkLedger, World, new MethodId("batch-" + i), "", Plan("candidate-" + i), ct).Outcome == ClaimOutcome.Admitted;
        using var second = Journal.Open(path, ct);
        using var start = new ManualResetEventSlim();
        var stores = new[] { first, second };
        var tasks = Enumerable.Range(0, 2).Select(i => Task.Run(() =>
        {
            start.Wait(ct);
            return Commit(stores[i], i);
        }, ct)).ToArray();
        start.Set();
        var accepted = await Task.WhenAll(tasks);
        Assert.Single(accepted, v => v);
        Assert.Single(first.ProductionBatches(World, Facts.Tick, ct).Value);
        var winner = Array.FindIndex(accepted, v => v);
        var loser = 1 - winner;
        first.Cancel(new PlanId("candidate-" + winner), new ActionId("candidate-" + winner), ct);
        // The failed transaction left neither a method binding nor a plan behind; its exact retry can commit.
        Commit(second, loser);
        Assert.Equal(2, first.ProductionBatches(World, Facts.Tick, ct).Value.Count);
    }
}
