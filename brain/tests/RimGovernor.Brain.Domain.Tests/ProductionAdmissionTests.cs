using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Store;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;
using static RimGovernor.Brain.Policy.PolicyFunctions;
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
        ConstructionDeficit = Known<IReadOnlyDictionary<Resource, long>>(new Dictionary<Resource, long> { [Wood] = 40 }),
        BillReservations = Known(default(EquatableArray<IngredientReservation>)),
        WorkAdmission = new()
        {
            Recipes = Known<EquatableArray<ProductionRecipeInput>>([new("TestRecipe", Known<EquatableArray<EquatableArray<Amount>>>([[new(Wood, 10)]]), Known(false))]),
            Bills = Known(default(EquatableArray<ProductionBatchState>)),
        },
    };
    private static WorkAdmissionEvidence Evidence(RoundsFacts? facts = null, params ProductionBatch[] reviewed) =>
        WorkAdmissionEvidenceOf(facts ?? Facts, Known(EquatableArray.From(reviewed)));
    private static bool Fits(WorkAdmissionEvidence evidence, ProductionBatch[] committed, params ProductionBatch[] proposed) =>
        ProductionAdmissionFits(evidence, Known(EquatableArray.From(committed)), EquatableArray.From(proposed));
    private static PlanSpec Plan(string id) => new(new PlanId(id), new PlanRevision(1),
        [DomainAction.NewProductionBillAction(new ActionId(id), Batch(id).Order)]);

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
        Assert.False(Fits(Evidence(Facts with { BillReservations = default }), [], Batch("new")));
        Assert.False(Fits(Evidence(Facts with { WorkAdmission = new() }), [], Batch("new")));
        var first = Batch("first") with { Progress = Batch("first").Progress with { Stage = Stage.Completed, Attempt = new AttemptId(1), Bill = Known("bill") } };
        var f = Facts with
        {
            WorkAdmission = Facts.WorkAdmission with { Bills = Known<EquatableArray<ProductionBatchState>>([new("bill", Known(true), Known(1), Known(false), Known(false))]) },
            BillReservations = Known<EquatableArray<IngredientReservation>>([new() { Bill = Known("bill"), Pawn = new("pawn"), Items = [new(Wood, 10)] }]),
        };
        Assert.True(Fits(Evidence(f, first), [first], Batch("new")));
        Assert.False(Fits(Evidence(f with { BillReservations = Known<EquatableArray<IngredientReservation>>([new() { Bill = Known("other"), Pawn = new("pawn"), Items = [new(Wood, 10)] }]) }, first), [first], Batch("new")));
    }

    [Fact]
    public void ImmediateReviewRetainsEvidenceButWorldChangeAndUnreadFullReviewCannotApprove()
    {
        var ct = TestContext.Current.CancellationToken;
        using var journal = Journal.Open(Path.Combine(Path.GetTempPath(), "rg-admission-world-" + Guid.NewGuid().ToString("N") + ".db"), ct);
        var request = new RoundsRequest { Current = World, Tick = Facts.Tick, Enabled = true, Policy = DefaultRoundsPolicy(), Facts = Facts };
        var full = journal.ReviewRounds(request, ct);
        var immediate = journal.ReviewRounds(request with { Revision = full.Review.Revision, Immediate = true, Tick = new Tick(101), Facts = new() }, ct);
        Assert.Equal(60, journal.LoadRounds(ct).WorkEvidence!.Stock.Value[0].Count);
        var unread = journal.ReviewRounds(request with { Revision = immediate.Review.Revision, Tick = new Tick(102), Facts = Facts with { Resources = default } }, ct);
        Assert.Throws<ProductionMaterialException>(() => journal.CommitClaimedMethod(AdminFamily.WorkLedger, World, new MethodId("unread"), "", Plan("unread"), ct));
        var changed = journal.ReviewRounds(request with { Revision = unread.Review.Revision, Immediate = true, Current = World with { Load = new LoadId("new-load") }, Tick = new Tick(103), Facts = new() }, ct);
        Assert.Null(changed.Review.WorkEvidence);
    }

    [Fact]
    public async Task SeparateStoreConnectionsCompeteAtomicallyAndCancellationReleasesTheWinner()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = Path.Combine(Path.GetTempPath(), "rg-admission-" + Guid.NewGuid().ToString("N") + ".db");
        using var first = Journal.Open(path, ct);
        first.ReviewRounds(new() { Current = World, Tick = Facts.Tick, Enabled = true, Policy = DefaultRoundsPolicy(), Facts = Facts }, ct);
        // Two admin/work-ledger Methods, one per connection, compete for the same material.
        void Commit(Journal store, int i) =>
            store.CommitClaimedMethod(AdminFamily.WorkLedger, World, new MethodId("batch-" + i), "", Plan("candidate-" + i), ct);
        using var second = Journal.Open(path, ct);
        using var start = new ManualResetEventSlim();
        var stores = new[] { first, second };
        var tasks = Enumerable.Range(0, 2).Select(i => Task.Run(() =>
        {
            start.Wait(ct);
            try { Commit(stores[i], i); return true; }
            catch (ProductionMaterialException) { return false; }
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
