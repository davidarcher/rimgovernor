using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Store;
using WorkResourcePawn = RimGovernor.Contracts.Wire.Observations.WorkResourcePawn;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;
using Journal = RimGovernor.Brain.Store.Store;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// The generic owner of store-level owner tests (#3265): the admin/work-assignments Need, admitted by claims in the
/// review's world. Its Method is needed while a progress row files it unmet (<see cref="File"/>).
/// </summary>
internal static class TestNeed
{
    internal static NeedId Id => AdminFamily.WorkAssignments;

    /// <summary>Commits <paramref name="plan"/> as <paramref name="method"/> under the Need and requires admission.</summary>
    internal static NeedOwner Commit(Journal journal, GenerationSnapshot world, MethodId method, PlanSpec plan, CancellationToken ct)
    {
        var commit = journal.CommitClaimedMethod(Id, world, method, "", plan, ct);
        Assert.Equal(ClaimOutcome.Admitted, commit.Outcome);
        return commit.Owner;
    }

    /// <summary>Files the Need's progress: unmet working <paramref name="method"/>, or met (no row).</summary>
    internal static void File(Journal journal, bool met, MethodId method, CancellationToken ct) =>
        journal.RecordPlannerReasons(new Dictionary<ConcernId, PlannerNote>(),
            [new FamilyProgress(AdminFamily.FamilyId, true, met ? [] : [new NeedProgress(Id, false, default, method)])], ct);
}

internal static class WorkResourceTestFacts
{
    /// <summary>
    /// The generated alternatives with their builders read (#3835): <see cref="Create"/>'s pawns carry no work rows, so the
    /// planned builders are unread and a room left owing would throw; the planned-room fixtures count none read instead.
    /// </summary>
    internal static ForecastCoupledCandidates? BuildersRead(this ForecastCoupledCandidates? candidates) =>
        candidates is null ? null : candidates with { Context = candidates.Context with { PlannedBuildersUnread = false } };

    internal static RoundsFacts Create(params string[] pawns) => new()
    {
        Tick = new(100),
        WorkProfiles = Known<EquatableArray<PawnProfile>>([new() { Id = new("artist"),
            WorkSkill = new Dictionary<WorkType, string> { [WorkType.Art] = "Art" },
            Skills = new Dictionary<string, ProfileSkill> { ["Art"] = new() { Name = "Art", Level = 8 } } }]),
        Resources = Known<EquatableArray<Amount>>([new(new("Rice"), 10)]),
        CurrentConstruction = CurrentConstruction.Empty,
        BillReservations = [],
        WorkResources = Known(EquatableArray.From(pawns.Select(p => new WorkResourcePawn(p, "", 0, "", "", "", 0) { LocalHour = 12 }))),
        ForecastObserved = new() { WorkPawns = Known(EquatableArray.From(pawns.Select(p => PawnFixtures.Worker(p) with
        { Schedule = EquatableArray.From(Enumerable.Repeat(PawnRules.ScheduleAnything, 24)) }))) },
        WorkAdmission = new()
        {
            Buildings = Known<EquatableArray<WorkBuildingCost>>([new("TestWall", "Rice", Known<EquatableArray<Amount>>([new(new("Rice"), 6)]))]),
            Recipes = Known<EquatableArray<ProductionRecipeInput>>([new("TestRecipe", [[new(new("Rice"), 6)]], false) { Products = [], WorkAmount = 0 }]),
            Bills = Known(default(EquatableArray<ProductionBatchState>)),
        },
    };
}

public sealed class WorkResourcesTests
{
    private static readonly GenerationSnapshot World = new(new("colony"), new(1), new("load"), new("review"), new(1), new(1));
    private static DomainAction Delivery(string id, string pawn = "cook-builder", int count = 6) =>
        DomainAction.NewPawnActivitiesAction(new(id), new(1, [new(pawn, id, "method", 0, 1, false,
            [new(PawnActivityKind.Deliver, "stack-" + id, "", "", count, 500) { Destination = new(pawn == "helper" ? 4 : 2, 2) }])
            { OutputDelivery = new("Rice", new(1, 1), new(pawn == "helper" ? 4 : 2, 2), count, 500, 3) }]));
    private static DomainAction Building(string id) => DomainAction.NewBuildingAction(new(id), new("TestWall", new(3, 3), Rotation.North, "Rice"), ConstructionTier.Survive);
    private static DomainAction Craft(string id) => DomainAction.NewProductionBillAction(new(id), new("bench", "TestRecipe", BillMode.GearBatch, 1, "Rice"));
    private static PlanSpec Plan(string id, params DomainAction[] actions) => new(new(id), new(1), EquatableArray.From(actions));
    private static WorkResourceView View(RoundsFacts facts, params WorkAction[] actions) => WorkRules.WorkResourcesOf(
        ProductionRules.WorkAdmissionEvidenceOf(facts, Known(default(EquatableArray<ProductionBatch>))), Known(default(EquatableArray<ProductionBatch>)), actions);
    private static WorkAction Pending(DomainAction action) => new(new("owner"), action, new() { Action = action.Id, Stage = Stage.Pending });

    [Fact]
    public void ProducedFoodRetainsItsPendingDeliveryShareWithoutCreatingStock()
    {
        var step = new PawnActivityStep(PawnActivityKind.Deliver, "output", "", "", 6, 500) { Destination = new(2, 2) };
        var task = new SelectedActivityTask("deliver", "helper", [], step, new(1, 1), 100)
        { Resource = "Rice", Inputs = [new("Rice", "harvest-output", 6, new(1, 1), "harvest") { Producers = ["harvest"] }] };
        var action = DomainAction.NewPawnActivitiesAction(new("pending-output"), new(1,
            [new("helper", "helper-assignment", "harvest-method", 0, 1, false,
                [new(PawnActivityKind.Hold, "", "", "", 0, 500)]) { Task = "deliver", Preparing = true }]) { Tasks = [task] });
        var facts = WorkResourceTestFacts.Create("helper", "eater") with
        { Resources = Known<EquatableArray<Amount>>([new(new("Rice"), 7)]) };
        var held = View(facts, Pending(action));
        Assert.Equal(1, held.Materials.Value[new("Rice")].Value);
        Assert.False(WorkRules.WorkResourcesFit(held, Plan("hungry", Eat(7))));
        Assert.True(WorkRules.WorkResourcesFit(held, Plan("spare", Eat(1))));
        var producer = new SelectedActivityTask("harvest", "helper", [],
            new(PawnActivityKind.Work, "plant", "Harvest", "Harvest", 0, 500), new(1, 1), 100);
        var beforeHarvest = DomainAction.NewPawnActivitiesAction(action.Id, action.PawnActivities! with { Tasks = [producer, task] });
        Assert.Equal(7, View(facts, Pending(beforeHarvest)).Materials.Value[new("Rice")].Value);
        var noStock = facts with { Resources = Known<EquatableArray<Amount>>([new(new("Rice"), 0)]) };
        Assert.True(View(noStock, Pending(action)).Materials.Value[new("Rice")].Value <= 0);

        static DomainAction Eat(int count)
        {
            var eat = new PawnActivityStep(PawnActivityKind.Eat, "rice", "", "", count, 500);
            return DomainAction.NewPawnActivitiesAction(new("eat"), new(1,
                [new("eater", "eat-assignment", "eat-method", 0, 1, false, [eat]) { Task = "eat-task" }])
            { Tasks = [new("eat-task", "eater", [], eat, new(1, 1), 100)
                { Resource = "Rice", Inputs = [new("Rice", "rice", count, new(1, 1), "observed") { Observed = true }] }] });
        }
    }

    [Fact]
    public void FutureConsumingCommitmentReservesQuantityButKeepsItsWholeObservedLotExclusive()
    {
        // Native repeat-needs exposed this conservative boundary (#3194): nineteen
        // actual units remain, fifteen promised to future eating, four numerically
        // spare. Another pawn cannot borrow those four through the same exact target.
        var step = new PawnActivityStep(PawnActivityKind.Eat, "shared-rice", "", "", 15, 2000);
        var task = new SelectedActivityTask("future-eat", "helper", [], step, new(2, 2), 1000)
        { Inputs = [new("Rice", "shared-rice", 15, new(2, 2), "observed need target") { Observed = true }] };
        var heldAction = DomainAction.NewPawnActivitiesAction(new("future-eat-action"), new(1,
            [new("helper", "helper-assignment", "helper-method", 1, 2, false,
                [new(PawnActivityKind.Approach, "", "", "", 0, 2000) { Destination = new(2, 2), NotBefore = 1000 },
                 new(PawnActivityKind.Hold, "", "", "", 0, 2000)]) { Task = task.Id, Preparing = true }]) { Tasks = [task] });
        var facts = WorkResourceTestFacts.Create("helper", "eater") with
        {
            Resources = Known<EquatableArray<Amount>>([new(new("Rice"), 19)]),
            WorkResources = Known<EquatableArray<WorkResourcePawn>>([
                new("helper", "helper-assignment", 2, "holding", "", "", 0) { LocalHour = 12 },
                new("eater", "", 0, "", "", "", 0) { LocalHour = 12 }])
        };
        var row = Pending(heldAction) with { Progress = new() { Action = heldAction.Id, Stage = Stage.Completed,
            Attempt = new(1), Receipt = new ReceiptState.Received(Receipt.Accepted), Tick = new(99) } };
        var held = View(facts, row);
        Assert.Equal(19, Assert.Single(held.Evidence.Stock.Value).Count);
        Assert.Equal(4d, held.Materials.Value[new("Rice")].Value);
        Assert.Contains("shared-rice", held.Claims.Targets);
        var eat = step with { Count = 4 };
        var borrow = DomainAction.NewPawnActivitiesAction(new("borrow"), new(1,
            [new("eater", "eater-assignment", "eater-method", 0, 1, false, [eat]) { Task = "eat-four" }])
            { Tasks = [new("eat-four", "eater", [], eat, new(2, 2), 100)
                { Inputs = [new("Rice", "shared-rice", 4, new(2, 2), "observed need target") { Observed = true }] }] });
        Assert.False(WorkRules.WorkResourcesFit(held, Plan("borrow-plan", borrow)));
    }

    [Fact]
    public void SelectedInputFrontierCountsBranchesButNotDownstreamHandoffs()
    {
        var delivery = new SelectedActivityTask("delivery", "helper", [],
            new(PawnActivityKind.Deliver, "rice", "", "", 6, 500) { Destination = new(2, 2) }, new(1, 1), 100)
        { Inputs = [new("Rice", "rice", 6, new(1, 1), "physical") { Observed = true }] };
        var consume = delivery with { Id = "consume", Inputs = [new("Rice", "delivered", 6, new(1, 1), "transfer") { Producers = ["delivery"] }] };
        var branch = delivery with { Id = "branch", Inputs = [new("Rice", "rice", 1, new(1, 1), "physical") { Observed = true }] };
        var action = DomainAction.NewPawnActivitiesAction(new("frontier"), new(1,
            [new("helper", "assignment", "method", 0, 1, false, [delivery.Step]) { Task = "delivery" }])
        { Tasks = [delivery, consume, branch] });
        var facts = WorkResourceTestFacts.Create("helper");
        Assert.Equal(3, View(facts, Pending(action)).Materials.Value[new("Rice")].Value);
        Assert.Equal(3, View(facts, Pending(action), Pending(DomainAction.NewPawnActivitiesAction(new("revision"), action.PawnActivities!)))
            .Materials.Value[new("Rice")].Value);
        facts = facts with { WorkResources = Known<EquatableArray<WorkResourcePawn>>([
            new("helper", "assignment", 1, "started", "carried", "Rice", 4) { LocalHour = 12 }]) };
        Assert.Equal(7, View(facts, Pending(action)).Materials.Value[new("Rice")].Value);
        facts = facts with { WorkResources = Known<EquatableArray<WorkResourcePawn>>([
            new("helper", "assignment", 1, "completed", "", "", 0) { LocalHour = 12 }]) };
        Assert.Equal(3, View(facts, Pending(action)).Materials.Value[new("Rice")].Value);
    }

    [Fact]
    public void ActualUnfinishedRecipeOwnsConsumedInputsOutsideLooseStock()
    {
        var action = DomainAction.NewPawnActivitiesAction(new("tailor"), new(1, [new("tailor", "garment", "method", 0, 1, false,
            [new(PawnActivityKind.Recipe, "bench", "Tailor", "", 0, 500)
            { Recipe = new("", "shirt", [new("cloth", 6)]) { Resource = "Rice", Stuff = "Rice" } }])]));
        var facts = WorkResourceTestFacts.Create("tailor");
        Assert.Equal(4, View(facts, Pending(action)).Materials.Value[new("Rice")].Value);
        facts = facts with { WorkResources = Known<EquatableArray<WorkResourcePawn>>([
            new("tailor", "garment", 1, "started", "", "", 0) { LocalHour = 12, Unfinished = "actual-uft" }]) };
        Assert.Equal(10, View(facts, Pending(action)).Materials.Value[new("Rice")].Value);
        Assert.Contains("tailor", View(facts, Pending(action)).Claims.Pawns);
    }

    [Fact]
    public void BlueprintShortOfStockIsPlacedAndHoldsItsCostAgainstOtherWork()
    {
        // A naked start owns no wood: the shelter and the storage room blueprints were refused materials_short, so
        // nothing was ever placed for the colony to gather toward. A blueprint spends nothing until delivery.
        var facts = WorkResourceTestFacts.Create("cook-builder") with { Resources = Known<EquatableArray<Amount>>([new(new("Rice"), 0)]) };
        Assert.Null(WorkRules.WorkResourcesRefusal(View(facts), Plan("build", Building("wall"))));
        var held = View(facts, Pending(Building("wall")));
        var other = DomainAction.NewBuildingAction(new("other"), new("TestWall", new(7, 7), Rotation.North, "Rice"), ConstructionTier.Survive);
        Assert.Null(WorkRules.WorkResourcesRefusal(held, Plan("other", other)));
        Assert.StartsWith("materials_short:Rice", WorkRules.WorkResourcesRefusal(
            View(facts with { Resources = Known<EquatableArray<Amount>>([new(new("Rice"), 6)]) }, Pending(Building("wall"))), Plan("craft", Craft("bill"))));
    }

    [Fact]
    public void MixedFamiliesShareOneBudgetIncludingWithinOneAtomicBundle()
    {
        var facts = WorkResourceTestFacts.Create("cook-builder", "helper");
        var view = View(facts);
        Assert.True(WorkRules.WorkResourcesFit(view, Plan("build", Building("wall"))));
        Assert.True(WorkRules.WorkResourcesFit(view, Plan("craft", Craft("bill"))));
        Assert.True(WorkRules.WorkResourcesFit(view, Plan("food", Delivery("food"))));
        Assert.False(WorkRules.WorkResourcesFit(view, Plan("mixed", Building("wall"), Craft("bill"))));
        var held = View(facts, Pending(Delivery("food")));
        Assert.True(WorkRules.WorkResourcesFit(held, Plan("build", Building("wall"))));
        Assert.False(WorkRules.WorkResourcesFit(held, Plan("craft", Craft("bill"))));
        Assert.Equal(4, held.Materials.Value[new("Rice")].Value);
        Assert.Equal(10, held.Evidence.Stock.Value.Single().Count);
    }

    // #3187: a coupled batch stages several pawns at one location. A shared cell is not a shared thing, so the
    // batch is admitted; two orders on one named stack still are refused, with the reason named.
    [Fact]
    public void OrdersOfOneBatchMayMeetAtACellButNotShareANamedTarget()
    {
        var facts = WorkResourceTestFacts.Create("cook-builder", "helper");
        var view = View(facts);
        PawnActivityOrder Staged(string pawn, string target = "") => new(pawn, "m/" + pawn, "m", 0, 1, false,
            [new(PawnActivityKind.Approach, target, "", "", 0, 500) { Destination = new(5, 5) }, new(PawnActivityKind.Hold, "", "", "", 0, 500)]) { Preparing = true };
        PlanSpec Batch(string id, params PawnActivityOrder[] orders) => Plan(id, DomainAction.NewPawnActivitiesAction(new(id + "-0"), new(1, orders)));
        Assert.Null(WorkRules.WorkResourcesRefusal(view, Batch("meet", Staged("cook-builder"), Staged("helper"))));
        Assert.Equal("shared_target:stack", WorkRules.WorkResourcesRefusal(view, Batch("clash", Staged("cook-builder", "stack"), Staged("helper", "stack"))));
    }

    // #3670: a preemptible pawn's vanilla job is reported. Occupancy reads it as free; the claims still see its target and
    // tell it from a committed holder: a coupled order interrupts the preemptible one (#3671) but never a committed one.
    [Fact]
    public void PreemptibleVanillaJobIsFreeButStillShowsItsTargetToClaims()
    {
        RoundsFacts Holding(bool preemptible)
        {
            var facts = WorkResourceTestFacts.Create("cook-builder", "helper");
            return facts with { ForecastObserved = facts.ForecastObserved with { WorkPawns = Known(EquatableArray.From(facts.ForecastObserved.WorkPawns.Value
                .Select(p => p.Id.Value != "helper" ? p : p with { Job = new PawnJob { Def = "HaulToCell", Work = WorkType.Hauling,
                    Target = new JobTarget.Spawned("stack", new Cell(1, 1)), Preemptible = preemptible } }))) } };
        }
        PlanSpec Approach(string target) => Plan("approach", DomainAction.NewPawnActivitiesAction(new("approach-0"), new(1,
            [new("cook-builder", "m/cook-builder", "m", 0, 1, false,
                [new(PawnActivityKind.Approach, target, "", "", 0, 500) { Destination = new(5, 5) }, new(PawnActivityKind.Hold, "", "", "", 0, 500)]) { Preparing = true }])));
        var vanilla = View(Holding(preemptible: true));
        var row = vanilla.Evidence.Pawns.Value.Single(p => p.Pawn == "helper");
        Assert.True(row.Preemptible);
        Assert.Equal(Known(false), row.Occupied);
        Assert.Equal(Known("stack"), row.Target);
        Assert.True(vanilla.Capacity["helper"].Capacity.Windows.ReadWindows().Sum(w => w.End - w.Start) > 0);
        Assert.Null(WorkRules.WorkResourcesRefusal(vanilla, Approach("stack")));
        var committed = View(Holding(preemptible: false));
        Assert.Equal(Known(true), committed.Evidence.Pawns.Value.Single(p => p.Pawn == "helper").Occupied);
        Assert.Equal("pawn_target_claimed", WorkRules.WorkResourcesRefusal(committed, Approach("stack")));
    }

    [Fact]
    public void OneCookBuilderCannotPromiseTwoDaysAndDisjointPawnRemainsAvailable()
    {
        var facts = WorkResourceTestFacts.Create("cook-builder", "helper");
        var held = View(facts, Pending(Delivery("food")));
        Assert.IsType<ForecastCapacityWindows.Unread>(held.Capacity["cook-builder"].Capacity.Windows);
        Assert.True(held.Capacity["helper"].Capacity.Windows.ReadWindows().Sum(w => w.End - w.Start) > 0);
        Assert.False(WorkRules.WorkResourcesFit(held, Plan("overlap", Delivery("second", count: 1))));
        Assert.True(WorkRules.WorkResourcesFit(held, Plan("parallel", Delivery("second", "helper", 1))));
        var cancelled = Pending(Delivery("food")) with { Progress = new() { Stage = Stage.Cancelled } };
        Assert.True(View(facts, cancelled).Capacity["cook-builder"].Capacity.Windows.ReadWindows().Sum(w => w.End - w.Start) > 0);
        Assert.IsType<ForecastCapacityWindows.Unread>(View(facts, cancelled with { Progress = cancelled.Progress with { Attempt = new(1), Tick = new(100) } }).Capacity["cook-builder"].Capacity.Windows);
    }

    [Fact]
    public void CarriedInputsAreOutsideLooseStockAndNativeHoldSurvivesCancelledPromise()
    {
        var action = Delivery("food");
        var facts = WorkResourceTestFacts.Create("cook-builder") with
        { WorkResources = Known<EquatableArray<WorkResourcePawn>>([new("cook-builder", "food", 1, "running", "carry", "Rice", 4) { LocalHour = 12 }]) };
        Assert.Equal(8, View(facts, Pending(action)).Materials.Value[new("Rice")].Value);
        facts = facts with { BillReservations = 
            [new() { Pawn = new("cook-builder"), Bill = "native-bill", Items = [new(new("Rice"), 3)] }] };
        var cancelled = Pending(action) with { Progress = new() { Stage = Stage.Cancelled } };
        Assert.Equal(7, View(facts, cancelled).Materials.Value[new("Rice")].Value);
    }

    [Fact]
    public void UnknownCurrentWorkDurationIsNotAFreeDayAndZeroIsNotUnknown()
    {
        var facts = WorkResourceTestFacts.Create("cook-builder", "helper");
        facts = facts with { ForecastObserved = facts.ForecastObserved with { WorkPawns = Known(EquatableArray.From(
            facts.ForecastObserved.WorkPawns.Value.Select(p => p.Id.Value == "cook-builder" ? p with { Job = new PawnJob { Def = "DoBill" } } : p))) } };
        var view = View(facts);
        Assert.Contains("unknown_current_job_remaining", view.Capacity["cook-builder"].Findings);
        Assert.IsType<ForecastCapacityWindows.Read>(view.Capacity["helper"].Capacity.Windows);
        facts = facts with { ForecastObserved = facts.ForecastObserved with { WorkPawns = Known(EquatableArray.From(
            facts.ForecastObserved.WorkPawns.Value.Select(p => p with { Available = false }))) } };
        Assert.Equal(0, View(facts).Capacity["helper"].Capacity.Windows.ReadWindows().Sum(w => w.End - w.Start));
    }

    [Fact]
    public void ReviewAdmissionRejectsCrossFamilyOverspendWithoutLeavingAPlan()
    {
        var ct = TestContext.Current.CancellationToken;
        using var journal = Journal.Open(Path.Combine(Path.GetTempPath(), "rg-shared-work-" + Guid.NewGuid().ToString("N") + ".db"), ct);
        var review = journal.ReviewRounds(new() { Current = World, Tick = new(100), Enabled = true,
            Policy = RoundsRules.DefaultRoundsPolicy(), Facts = WorkResourceTestFacts.Create("cook-builder", "helper") }, ct);
        TestNeed.Commit(journal, World, new("method"), Plan("food", Delivery("food")), ct);
        // The craft bill is the admin/work-ledger Need's.
        var refused = journal.CommitClaimedMethod(AdminFamily.WorkLedger, World, new("craft"), "", Plan("craft", Craft("bill")), ct);
        Assert.Equal(RefusalKind.ProductionMaterial, refused.Refusal!.Kind);
        Assert.Empty(journal.LoadNeed(AdminFamily.WorkLedger, World, ct).Methods);
        Assert.Equal(4, journal.ProductionMaterials(World, ct).Availability.Value[new("Rice")].Value);
        journal.Cancel(new("food"), new("food"), ct);
        journal.CommitClaimedMethod(AdminFamily.WorkLedger, World, new("craft"), "", Plan("craft", Craft("bill")), ct);
    }
    [Fact]
    public void ContestedNewActivityIsAnAdmissionRefusalAndPreservesExistingWork()
    {
        var ct = TestContext.Current.CancellationToken;
        using var journal = Journal.Open(Path.Combine(Path.GetTempPath(), "rg-contested-proposal-" + Guid.NewGuid().ToString("N") + ".db"), ct);
        var review = journal.ReviewRounds(new() { Current = World, Tick = new(100), Enabled = true,
            Policy = RoundsRules.DefaultRoundsPolicy(), Facts = WorkResourceTestFacts.Create("cook-builder", "helper") }, ct);
        Assert.Equal(ClaimOutcome.Admitted, journal.CommitClaimedMethod(FoodRules.FoodSupplyNeed, World, new("method"), "", Plan("held", Delivery("held")), ct).Outcome);
        var proposed = Delivery("proposed", "helper").PawnActivities!;
        proposed = proposed with { Orders = proposed.Orders.Select(o => o with { Method = "proposal" }).ToArray() };
        var commit = journal.CommitClaimedMethod(TestNeed.Id, World,
            new("proposal"), "", Plan("proposed", DomainAction.NewPawnActivitiesAction(new("proposed"), proposed)), ct);
        Assert.Equal(ClaimOutcome.Refused, commit.Outcome);
        Assert.Contains("work_resource_conflict_or_unknown", commit.Refusal!.Message);
        Assert.Empty(journal.LoadNeed(TestNeed.Id, World, ct).Methods);
        Assert.False(journal.LoadPlan(new("held"), ct).Retired);
        Assert.Equal(4, journal.WorkResources(World, ct).Materials.Value[new("Rice")].Value);
    }
    [Fact]
    public void UnknownQuantityOnlyBlocksItsResourceAndExhaustedCensusIsExplicit()
    {
        var facts = WorkResourceTestFacts.Create("cook-builder") with
        {
            Resources = Known<EquatableArray<Amount>>([new(new("Rice"), 10), new(new("Steel"), 10)]),
            BillReservations = 
                [new() { Pawn = new("player"), Bill = "player-bill", Items = [new(new("Rice"), 2)] }],
            WorkAdmission = WorkResourceTestFacts.Create().WorkAdmission with
            { Buildings = Known<EquatableArray<WorkBuildingCost>>([new("TestWall", "Rice", Known<EquatableArray<Amount>>([new(new("Steel"), 6)]))]) },
        };
        var evidence = ProductionRules.WorkAdmissionEvidenceOf(facts, Known(default(EquatableArray<ProductionBatch>))) with
        { Promises = Known<EquatableArray<ProductionMaterialPromise>>([new("unknown-quantity", [new(new("Rice"), default, Known(""))])]) };
        var view = WorkRules.WorkResourcesOf(evidence, Known(default(EquatableArray<ProductionBatch>)), []);
        Assert.False(view.Materials.Value[new("Rice")].IsKnown);
        Assert.True(WorkRules.WorkResourcesFit(view, Plan("build", Building("wall"))));
        Assert.False(WorkRules.WorkResourcesFit(view, Plan("food", Delivery("food"))));
        var bounded = WorkRules.WorkResourcesOf(view.Evidence, Known(default(EquatableArray<ProductionBatch>)), [], truncated: true);
        Assert.True(bounded.Claims.Truncated);
        Assert.False(bounded.Materials.IsKnown);
        Assert.IsType<ForecastCapacityWindows.Unread>(bounded.Capacity["cook-builder"].Capacity.Windows);
        Assert.False(WorkRules.WorkResourcesFit(bounded, Plan("build", Building("wall"))));
    }

    [Fact]
    public void SameBenchCannotBeHiddenInsideOnePlanOrUsedThroughPlayerWork()
    {
        var facts = WorkResourceTestFacts.Create("cook-builder") with { Resources = Known<EquatableArray<Amount>>([new(new("Rice"), 100)]) };
        Assert.False(WorkRules.WorkResourcesFit(View(facts), Plan("double-bench", Craft("first"), Craft("second"))));
        var busy = facts.ForecastObserved.WorkPawns.Value[0] with
        { Job = new PawnJob { Def = "DoBill", Target = new JobTarget.Spawned("bench", new Cell(1, 1)) } };
        facts = facts with { ForecastObserved = facts.ForecastObserved with { WorkPawns = Known<EquatableArray<WorkPawn>>([busy]) } };
        Assert.False(WorkRules.WorkResourcesFit(View(facts), Plan("busy-bench", Craft("first"))));
        Assert.True(WorkRules.WorkResourcesFit(View(facts), Plan("other-work", Building("wall"))));
    }

    [Fact]
    public void FixedBillCarriedInputsAndIterationPromiseOverlapOnce()
    {
        var promise = new ForecastCommitment("batch", default)
        { MaterialClaims = [new(new("Rice"), Known(10.0), Known("bill"))] };
        var native = Known<EquatableArray<WorkResourcePawn>>([new("cook", "", 0, "", "carried", "Rice", 4) { LocalHour = 12, Bill = "bill" }]);
        var adjusted = WorkRules.CreditCarriedBillInputs(Known<EquatableArray<ForecastCommitment>>([promise]), native).Value;
        Assert.Equal(6, adjusted[0].MaterialClaims[0].Units.Value);
        var iteration = new ForecastCommitment("iteration", default)
        { MaterialClaims = [new(new("Rice"), Known(2.0), Known("bill"))] };
        var merged = WorkRules.MergeWorkMaterialPromises(adjusted.Concat([iteration]));
        var budget = ForecastClaims.ForecastMaterialBudgetOf(Known<EquatableArray<Amount>>([new(new("Rice"), 10)]),
            [new()
            { Pawn = new("cook"), Bill = "bill", Items = [new(new("Rice"), 2)] }], merged, 0).Value;
        Assert.Equal(4, budget[new("Rice")].Value);
        Assert.False(WorkRules.CreditCarriedBillInputs(Known<EquatableArray<ForecastCommitment>>([promise]), default).Value[0].MaterialClaims[0].Units.IsKnown);
    }
    [Fact]
    public void SameTickReadCannotReleaseDispatchAndReplacementHoldsOneOutstandingPromise()
    {
        var facts = WorkResourceTestFacts.Create("cook-builder");
        var first = Pending(Delivery("food")) with { Progress = new()
        { Action = new("food"), Stage = Stage.Completed, Attempt = new(1), Tick = new(100), Receipt = new ReceiptState.Received(Receipt.Accepted) } };
        Assert.Contains("cook-builder", View(facts, first).Claims.Pawns);
        var later = facts with { Tick = new(101) };
        Assert.DoesNotContain("cook-builder", View(later, first).Claims.Pawns);
        var order = first.Action.PawnActivities!.Orders[0];
        var next = DomainAction.NewPawnActivitiesAction(new("replacement"), new(1,
            [order with { ExpectedRevision = 1, Revision = 2, Steps = [order.Steps[0] with { Count = 3 }] }]));
        var pending = View(facts, first, Pending(next));
        Assert.Equal(4, pending.Materials.Value[new("Rice")].Value);
        Assert.Single(pending.Claims.Materials);
        // A cancelled dispatched placement also waits for a newer complete construction/stock frame.
        var placement = Pending(Building("wall")) with { Progress = first.Progress with { Action = new("wall"), Stage = Stage.Cancelled } };
        Assert.Equal(4, View(facts, placement).Materials.Value[new("Rice")].Value);
        Assert.Equal(10, View(later, placement).Materials.Value[new("Rice")].Value);
    }
}
