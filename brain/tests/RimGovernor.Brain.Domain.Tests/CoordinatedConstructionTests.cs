using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimGovernor.Contracts.Wire.Observations;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class CoordinatedConstructionTests
{
    private static ConstructionActivityOption Option(string pawn, double approach, double delivery, double finish) =>
        new("blueprint", "Wall", new(30, 30), pawn, "WoodLog", "wood", new(10, 10), 20, 10, "FinishFrames",
            Known(approach), Known(delivery), Known(finish)) { SiteApproachTicks = Known(approach), AvailableInput = Known(20d) };
    private static WorkResourceView Resources()
    {
        var facts = WorkResourceTestFacts.Create("producer", "helper") with
        { Resources = Known<EquatableArray<RimGovernor.Brain.Policy.Amount>>([new(new("WoodLog"), 20)]) };
        return WorkRules.WorkResourcesOf(ProductionRules.WorkAdmissionEvidenceOf(facts, Known(default(EquatableArray<ProductionBatch>))),
            Known(default(EquatableArray<ProductionBatch>)), []);
    }
    private static PawnActivities? Start(out ForecastActivityOutcomeComparison? comparison, params ConstructionActivityOption[] options) =>
        Start(out comparison, [], options);
    private static PawnActivities? Start(out ForecastActivityOutcomeComparison? comparison, CoupledRoute[] routes, params ConstructionActivityOption[] options)
    {
        var facts = ForecastCoupledSelectionTests.Review(level: 1, fall: 0);
        var template = facts.ForecastObserved.Needs!.Pawns[0];
        facts = facts with { WorkView = Resources(), ForecastObserved = facts.ForecastObserved with
        {
            Needs = facts.ForecastObserved.Needs with { Pawns = [template with { Pawn = "producer" }, template with { Pawn = "helper" }] },
            Coupled = new() { Research = ActivityContinuationFixtures.NoResearch, Tick = 100, Construction = new() { Known = true, Context = new() { Identity = new() { ColonyId = "colony", LoadToken = "load", MapId = 0 }, Tick = 100 }, Options = EquatableArray.From(options), Routes = EquatableArray.From(routes) } }
        } };
        var generated = ForecastCoupledCandidates.GenerateCoupledActivities(facts, 1900)!;
        var request = ForecastCoupled.CoupledForecastRequestOf(new("world/map/load", 100), 1900d / GameTime.TicksPerDay,
            facts,  new() { Work = ForecastWorkInputs.ForecastWorkInputsOf(facts) }, [], new());
        var selected = ForecastCoupledSelection.CompareActivityOutcomes(request, generated);
        // This fixture tests assignment and execution of an already requested physical job.
        // A bare wall has no automatic colony value after service-based admission (#3166).
        Assert.Empty(selected.Selection.Selected);
        var physical = ForecastActivities.CompareActivityPlans(generated.Context.Activities);
        comparison = new(physical.Selected is null ? [] : generated.Alternatives.Select(c => c.Id).ToArray(),
            physical.Selected, default, physical.Findings, physical.Expansions, physical.SearchTruncated);
        selected = selected with { Selection = comparison, Deferred = [] };
        return ForecastCoupledSelection.StartCoupledActivity(selected, generated, 1, "construction", 100);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void OnlyFloorSitesCarryFlooringReliefSoEachLaidFloorIsCreditedOnce(bool floor)
    {
        var facts = ForecastCoupledSelectionTests.Review(level: 1, fall: 0);
        var template = facts.ForecastObserved.Needs!.Pawns[0];
        var option = Option("producer", 0, 20, 20) with { Definition = floor ? FlooringRules.DefaultFlooringPolicy().Floors[0] : "Wall" };
        facts = facts with { WorkView = Resources(), ForecastObserved = facts.ForecastObserved with
        {
            Needs = facts.ForecastObserved.Needs with { Pawns = [template with { Pawn = "producer" }, template with { Pawn = "helper" }] },
            Coupled = new() { Research = ActivityContinuationFixtures.NoResearch, Tick = 100, Construction = new() { Known = true, Context = new() { Identity = new() { ColonyId = "colony", LoadToken = "load", MapId = 0 }, Tick = 100 }, Options = EquatableArray.From([option]) } }
        } };
        var generated = ForecastCoupledCandidates.GenerateCoupledActivities(facts, 1900)!;
        var outcome = Assert.Single(generated.Alternatives, c => c.ExclusiveOutcome.StartsWith("construction/", StringComparison.Ordinal)).Outcome!;
        if (floor) Assert.Equal("flooring/blueprint", Assert.Single(outcome.Requirements!.Relief).Need);
        else Assert.Null(outcome.Requirements);
    }

    private static RoundsFacts RemovalFacts(params PlannedSiteClearing[] removals)
    {
        var facts = ForecastCoupledSelectionTests.Review(level: 1, fall: 0);
        var template = facts.ForecastObserved.Needs!.Pawns[0];
        return facts with { WorkView = Resources(), ForecastObserved = facts.ForecastObserved with
        {
            Needs = facts.ForecastObserved.Needs with { Pawns = [template with { Pawn = "producer" }, template with { Pawn = "helper" }] },
            Coupled = new() { Research = ActivityContinuationFixtures.NoResearch, Tick = 100, Construction = new() { Known = true, Context = new() { Identity = new() { ColonyId = "colony", LoadToken = "load", MapId = 0 }, Tick = 100 },
                Removals = EquatableArray.From(removals) } }
        } };
    }

    /// <summary>
    /// #3557, #3612: a removal designation the room reconcile stood (a Deconstruct over an interior ruin, an Uninstall over a
    /// piece it packs) gets no worker under voluntary control. The construction read reports it as a clearing op of its kind;
    /// the family binds it as the pawn's Work task of that kind on the building, one unit of development when it comes down,
    /// and the selection orders it. A row whose finish is unknown binds nothing.
    /// </summary>
    [Theory]
    [InlineData(PlannedClearingKind.Deconstruct, "Deconstruct", "Door")]
    [InlineData(PlannedClearingKind.Uninstall, "Uninstall", "Bed")]
    public void AStandingRemovalDesignationIsRemovalWorkTheSelectionOrders(PlannedClearingKind kind, string job, string definition)
    {
        var building = "Thing_" + definition + "1";
        var row = new ConstructionActivityOption(building, definition, new(30, 30), "producer", definition, building, new(30, 30), 0, 0, job,
            default, default, Known(50d)) { SiteApproachTicks = Known(10d) };
        PlannedSiteClearing Removal(params ConstructionActivityOption[] rows) => new(building, kind) { Options = EquatableArray.From(rows) };
        var facts = RemovalFacts(Removal(row, row with { Pawn = "helper", FinishTicks = Known(80d) }));
        var generated = ForecastCoupledCandidates.GenerateCoupledActivities(facts, 1900)!;
        var binding = Assert.Single(generated.Context.Bindings.Values, b => b.Job == job && b.Pawn == "producer");
        Assert.Equal((PawnActivityKind.Work, building, job, definition), (binding.Kind, binding.Target, binding.WorkGiver, binding.SiteDefinition));
        var root = Assert.Single(generated.Alternatives, c => c.ExclusiveOutcome == "construction/" + building);
        Assert.Equal("removal/" + building, Assert.Single(root.Outcome!.Requirements!.Relief).Need);

        var request = ForecastCoupled.CoupledForecastRequestOf(new("world/map/load", 100), 1900d / GameTime.TicksPerDay,
            facts, new() { Work = ForecastWorkInputs.ForecastWorkInputsOf(facts) }, [], new());
        var selected = ForecastCoupledSelection.CompareActivityOutcomes(request, generated);
        Assert.Contains(root.Id, selected.Selection.Selected);
        Assert.True(selected.SelectedVector.Development > selected.BaselineVector.Development, string.Join(";", selected.Selection.Findings));
        var batch = ForecastCoupledSelection.StartCoupledActivity(selected, generated, 1, "construction", 100)!;
        var task = Assert.Single(batch.Tasks);
        Assert.Equal((binding.Id, "producer", PawnActivityKind.Work, building, job), (task.Id, task.Pawn, task.Step.Kind, task.Step.Target, task.Step.Job));
        Assert.Contains(batch.Orders, o => o.Pawn == "producer" && !o.Release && o.Task == task.Id);

        Assert.DoesNotContain(ForecastCoupledCandidates.GenerateCoupledActivities(RemovalFacts(Removal(row with { FinishTicks = default })), 1900)!.Context.Bindings.Values,
            b => b.Job == job);
    }

    /// <summary>
    /// #3617: a roof-holder's Deconstruct op carries the roofed cells it leaves unsupported in Cells. The family binds it as
    /// one task whose order runs Work/RemoveRoof as a cell set step over those cells (Count their number) before the
    /// Work/Deconstruct, the same op and relief as a plain removal; the started order carries both steps.
    /// </summary>
    [Fact]
    public void ARoofHolderRemovalBindsRoofOffThenDeconstructInOneOrder()
    {
        const string wall = "Thing_Wall1";
        Cell[] cells = [new(31, 30), new(31, 31)];
        var row = new ConstructionActivityOption(wall, "Wall", new(31, 30), "producer", "Wall", wall, new(31, 30), 0, 0, "Deconstruct",
            default, default, Known(180d)) { SiteApproachTicks = Known(10d) };
        var facts = RemovalFacts(new PlannedSiteClearing(wall, PlannedClearingKind.Deconstruct) { Options = [row], Cells = EquatableArray.From(cells) });
        var generated = ForecastCoupledCandidates.GenerateCoupledActivities(facts, 1900)!;
        var binding = Assert.Single(generated.Context.Bindings.Values, b => b.Job == "Deconstruct");
        Assert.Equal((PawnActivityKind.Work, wall, "Deconstruct", "Wall", 0), (binding.Kind, binding.Target, binding.WorkGiver, binding.SiteDefinition, binding.Count));
        Assert.Equal(cells, binding.RoofCells);
        var root = Assert.Single(generated.Alternatives, c => c.ExclusiveOutcome == "construction/" + wall);
        Assert.Equal("removal/" + wall, Assert.Single(root.Outcome!.Requirements!.Relief).Need);

        var request = ForecastCoupled.CoupledForecastRequestOf(new("world/map/load", 100), 1900d / GameTime.TicksPerDay,
            facts, new() { Work = ForecastWorkInputs.ForecastWorkInputsOf(facts) }, [], new());
        var selected = ForecastCoupledSelection.CompareActivityOutcomes(request, generated);
        Assert.Contains(root.Id, selected.Selection.Selected);
        var batch = ForecastCoupledSelection.StartCoupledActivity(selected, generated, 1, "construction", 100)!;
        var task = Assert.Single(batch.Tasks);
        var prior = Assert.Single(task.PriorSteps);
        Assert.Equal((PawnActivityKind.Work, wall, "RemoveRoofs", "RemoveRoof", 2, task.Step.Deadline), (prior.Kind, prior.Target, prior.WorkGiver, prior.Job, prior.Count, prior.Deadline));
        Assert.Equal(cells, prior.Cells);
        Assert.Equal((PawnActivityKind.Work, wall, "Deconstruct"), (task.Step.Kind, task.Step.Target, task.Step.Job));
        var order = Assert.Single(batch.Orders, o => o.Pawn == "producer" && !o.Release && o.Task == task.Id);
        Assert.Equal([prior, task.Step], order.Steps);
    }

    /// <summary>
    /// #3613: a standing Cut wave (the room reconcile's CutPlant designations over one group of plants) is one removal op
    /// whose Targets are the plants. The family binds it as one Work/CutPlant set step over them (Count the set's size),
    /// one task per op, and the selection orders it. A Cut op naming no plants binds nothing.
    /// </summary>
    [Fact]
    public void AStandingCutWaveIsOneSetOrderOverItsPlants()
    {
        string[] trees = ["Thing_Plant_TreeOak1", "Thing_Plant_TreeOak2", "Thing_Plant_TreeOak3"];
        var row = new ConstructionActivityOption(trees[0], "Plant_TreeOak", new(30, 30), "producer", "", "", new(30, 30), 0, 0, "PlantsCut",
            default, default, Known(150d)) { SiteApproachTicks = Known(10d) };
        PlannedSiteClearing Wave(params string[] targets) => new(trees[0], PlannedClearingKind.Cut)
            { Options = [row, row with { Pawn = "helper", FinishTicks = Known(200d) }], Targets = EquatableArray.From(targets) };
        var facts = RemovalFacts(Wave(trees));
        var generated = ForecastCoupledCandidates.GenerateCoupledActivities(facts, 1900)!;
        var binding = Assert.Single(generated.Context.Bindings.Values, b => b.Job == "CutPlant" && b.Pawn == "producer");
        Assert.Equal((PawnActivityKind.Work, trees[0], "PlantsCut", 3, "Plant_TreeOak"), (binding.Kind, binding.Target, binding.WorkGiver, binding.Count, binding.SiteDefinition));
        Assert.Equal(trees, binding.Targets);
        Assert.Equal(2, generated.Context.Bindings.Values.Count(b => b.Job == "CutPlant"));
        var root = Assert.Single(generated.Alternatives, c => c.ExclusiveOutcome == "construction/" + trees[0]);
        Assert.Equal("removal/" + trees[0], Assert.Single(root.Outcome!.Requirements!.Relief).Need);

        var request = ForecastCoupled.CoupledForecastRequestOf(new("world/map/load", 100), 1900d / GameTime.TicksPerDay,
            facts, new() { Work = ForecastWorkInputs.ForecastWorkInputsOf(facts) }, [], new());
        var selected = ForecastCoupledSelection.CompareActivityOutcomes(request, generated);
        Assert.Contains(root.Id, selected.Selection.Selected);
        var batch = ForecastCoupledSelection.StartCoupledActivity(selected, generated, 1, "construction", 100)!;
        var task = Assert.Single(batch.Tasks);
        Assert.Equal((binding.Id, "producer", PawnActivityKind.Work, "CutPlant", 3), (task.Id, task.Pawn, task.Step.Kind, task.Step.Job, task.Step.Count));
        Assert.Equal(trees, task.Step.Targets);
        var order = Assert.Single(batch.Orders, o => o.Pawn == "producer" && !o.Release && o.Task == task.Id);
        Assert.Equal(trees, order.Steps[0].Targets);

        Assert.DoesNotContain(ForecastCoupledCandidates.GenerateCoupledActivities(RemovalFacts(Wave()), 1900)!.Context.Bindings.Values, b => b.Job == "CutPlant");
    }

    /// <summary>
    /// #3615: a standing FloorOut wave (the room reconcile's RemoveFloor designations over one group of cells) is one removal
    /// op whose Cells are the cells. The family binds it as one Work/RemoveFloor set step over them (Cells carried, Count the
    /// cells), with the removal relief, and the selection orders it. A RemoveFloor op naming no cells binds nothing.
    /// </summary>
    [Fact]
    public void AStandingFloorOutWaveIsOneCellSetOrderOverItsCells()
    {
        Cell[] cells = [new(30, 30), new(31, 30), new(30, 31), new(31, 31)];
        var id = "removefloor/30/30/x4";
        var row = new ConstructionActivityOption(id, "WoodPlankFloor", cells[0], "producer", "", "", cells[0], 0, 0, "ConstructRemoveFloors",
            default, default, Known(120d)) { SiteApproachTicks = Known(10d) };
        PlannedSiteClearing Wave(params Cell[] members) => new(id, PlannedClearingKind.RemoveFloor)
            { Options = [row, row with { Pawn = "helper", FinishTicks = Known(200d) }], Cells = EquatableArray.From(members) };
        var facts = RemovalFacts(Wave(cells));
        var generated = ForecastCoupledCandidates.GenerateCoupledActivities(facts, 1900)!;
        var binding = Assert.Single(generated.Context.Bindings.Values, b => b.Job == "RemoveFloor" && b.Pawn == "producer");
        Assert.Equal((PawnActivityKind.Work, id, "ConstructRemoveFloors", 4, "WoodPlankFloor"), (binding.Kind, binding.Target, binding.WorkGiver, binding.Count, binding.SiteDefinition));
        Assert.Equal(cells, binding.Cells);
        Assert.Empty(binding.Targets);
        var root = Assert.Single(generated.Alternatives, c => c.ExclusiveOutcome == "construction/" + id);
        Assert.Equal("removal/" + id, Assert.Single(root.Outcome!.Requirements!.Relief).Need);

        var request = ForecastCoupled.CoupledForecastRequestOf(new("world/map/load", 100), 1900d / GameTime.TicksPerDay,
            facts, new() { Work = ForecastWorkInputs.ForecastWorkInputsOf(facts) }, [], new());
        var selected = ForecastCoupledSelection.CompareActivityOutcomes(request, generated);
        Assert.Contains(root.Id, selected.Selection.Selected);
        var batch = ForecastCoupledSelection.StartCoupledActivity(selected, generated, 1, "construction", 100)!;
        var task = Assert.Single(batch.Tasks);
        Assert.Equal((binding.Id, "producer", PawnActivityKind.Work, "RemoveFloor", 4), (task.Id, task.Pawn, task.Step.Kind, task.Step.Job, task.Step.Count));
        Assert.Equal(cells, task.Step.Cells);
        var order = Assert.Single(batch.Orders, o => o.Pawn == "producer" && !o.Release && o.Task == task.Id);
        Assert.Equal(cells, order.Steps[0].Cells);
        batch.Validate();
        // The cell set rides the wire (#3615): the native side deserialises orders' steps with the wire context.
        var info = RimGovernor.Contracts.Wire.WireJsonContext.Default.GetTypeInfo(typeof(PawnActivities))!;
        var copy = (PawnActivities)System.Text.Json.JsonSerializer.Deserialize(System.Text.Json.JsonSerializer.Serialize(batch, info), info)!;
        Assert.Equal(cells, copy.Orders.Single(o => o.Task == task.Id).Steps[0].Cells);

        Assert.DoesNotContain(ForecastCoupledCandidates.GenerateCoupledActivities(RemovalFacts(Wave()), 1900)!.Context.Bindings.Values, b => b.Job == "RemoveFloor");
    }

    /// <summary>
    /// #3613: a set step completes partially the way a delivery does. A failed order is retried on the members native still
    /// finds legal (the binding's Targets, its RemainingCount); the retried order carries only those, and its completion
    /// (or a native satisfied task, every member gone) finishes the task and releases the pawn.
    /// </summary>
    [Fact]
    public void ASetStepRetriesOnTheMembersStillLegalAndCompletesWithThem()
    {
        string[] trees = ["Thing_Plant_TreeOak1", "Thing_Plant_TreeOak2", "Thing_Plant_TreeOak3"];
        var row = new ConstructionActivityOption(trees[0], "Plant_TreeOak", new(30, 30), "producer", "", "", new(30, 30), 0, 0, "PlantsCut",
            default, default, Known(150d)) { SiteApproachTicks = Known(10d) };
        var facts = RemovalFacts(new PlannedSiteClearing(trees[0], PlannedClearingKind.Cut) { Options = [row], Targets = EquatableArray.From(trees) });
        var generated = ForecastCoupledCandidates.GenerateCoupledActivities(facts, 1900)!;
        var request = ForecastCoupled.CoupledForecastRequestOf(new("world/map/load", 100), 1900d / GameTime.TicksPerDay,
            facts, new() { Work = ForecastWorkInputs.ForecastWorkInputsOf(facts) }, [], new());
        var batch = ForecastCoupledSelection.StartCoupledActivity(ForecastCoupledSelection.CompareActivityOutcomes(request, generated), generated, 1, "construction", 100)!;
        var task = Assert.Single(batch.Tasks);
        ActivityTaskRead Read(PawnActivities current, string state, params ActivityTaskBinding[] bindings) => new()
        {
            Known = true, Context = new() { Identity = new() { ColonyId = "colony", LoadToken = "load", MapId = 0 }, Tick = 300 }, Bindings = EquatableArray.From(bindings),
            Pawns = EquatableArray.From(current.Orders.Select(o => new ActivityTaskObservation(o.Pawn, new(o.Assignment, o.Method, o.Revision, 0, state, "", null, 300))))
        };
        var remaining = task.Step with { Targets = EquatableArray.From(trees.Skip(1)), Count = 2 };
        var retry = CoordinatedActivitiesRules.ContinueCoordinatedActivity(batch, Known(true), Read(batch, "failed",
            new ActivityTaskBinding(task.Id, remaining) { RemainingCount = Known(2) }))!;
        var retried = Assert.Single(retry.Orders);
        Assert.False(retried.Release);
        Assert.Equal((2, 2), (retried.Steps[0].Count, retried.Steps[0].Targets.Count));
        Assert.Equal(trees.Skip(1), retried.Steps[0].Targets);
        Assert.Equal(2, retry.Tasks.Single().Step.Count);
        Assert.Equal(2, retry.Tasks.Single().AttemptsLeft);
        // A binding naming a member outside the set is not this task's.
        Assert.True(CoordinatedActivitiesRules.ContinueCoordinatedActivity(batch, Known(true), Read(batch, "failed",
            new ActivityTaskBinding(task.Id, remaining with { Targets = ["Thing_Plant_TreeOak9", trees[2]] }) { RemainingCount = Known(2) }))!.Orders.Single().Release);
        var done = CoordinatedActivitiesRules.ContinueCoordinatedActivity(retry, Known(true), Read(retry, "completed"))!;
        Assert.Empty(done.Tasks);
        Assert.True(done.Orders.Single().Release);
        var cleared = CoordinatedActivitiesRules.ContinueCoordinatedActivity(batch, Known(true), Read(batch, "completed") with { SatisfiedTasks = [task.Id] })!;
        Assert.Empty(cleared.Tasks);
        Assert.True(cleared.Orders.Single().Release);
    }

    /// <summary>
    /// #3614: a Haul designation the room reconcile stood over a stack in the room (HaulOut) is a Haul removal op whose rows
    /// are the planned-clearing haul rows. The family binds it as the pawn's Deliver of the stack (Count the stack) to the
    /// row's destination with the removal's relief, and the selection orders it. A row whose delivery is unknown binds nothing.
    /// </summary>
    [Fact]
    public void AStandingHaulDesignationIsADeliverOfTheStackTheSelectionOrders()
    {
        const string item = "Thing_Steel1";
        var store = new Cell(40, 30);
        var origin = new Cell(30, 30);
        var row = new ConstructionActivityOption(item, "Steel", store, "producer", "Steel", item, origin, 75, 75, "HaulGeneral",
            Known(10d), Known(20d), Known(0d)) { SiteApproachTicks = Known(10d), AvailableInput = Known(75d) };
        PlannedSiteClearing Removal(params ConstructionActivityOption[] rows) => new(item, PlannedClearingKind.Haul) { Options = EquatableArray.From(rows) };
        var facts = RemovalFacts(Removal(row, row with { Pawn = "helper", DeliveryTicks = Known(30d) }));
        var generated = ForecastCoupledCandidates.GenerateCoupledActivities(facts, 1900)!;
        var binding = Assert.Single(generated.Context.Bindings.Values, b => b.Kind == PawnActivityKind.Deliver && b.Pawn == "producer");
        Assert.Equal((item, 75, "Steel", 75, ""), (binding.Target, binding.Count, binding.Resource, binding.Capacity, binding.SupportDemand));
        Assert.Equal(store, binding.Destination);
        Assert.Equal(origin, binding.Source);
        var root = Assert.Single(generated.Alternatives, c => c.ExclusiveOutcome == "construction/" + item);
        Assert.Equal("removal/" + item, Assert.Single(root.Outcome!.Requirements!.Relief).Need);

        var request = ForecastCoupled.CoupledForecastRequestOf(new("world/map/load", 100), 1900d / GameTime.TicksPerDay,
            facts, new() { Work = ForecastWorkInputs.ForecastWorkInputsOf(facts) }, [], new());
        var selected = ForecastCoupledSelection.CompareActivityOutcomes(request, generated);
        Assert.Contains(root.Id, selected.Selection.Selected);
        var batch = ForecastCoupledSelection.StartCoupledActivity(selected, generated, 1, "construction", 100)!;
        var task = Assert.Single(batch.Tasks);
        Assert.Equal((binding.Id, "producer", PawnActivityKind.Deliver, item, 75), (task.Id, task.Pawn, task.Step.Kind, task.Step.Target, task.Step.Count));
        Assert.Equal(store, task.Step.Destination);
        Assert.Contains(batch.Orders, o => o.Pawn == "producer" && !o.Release && o.Task == task.Id);

        Assert.DoesNotContain(ForecastCoupledCandidates.GenerateCoupledActivities(RemovalFacts(Removal(row with { DeliveryTicks = default })), 1900)!.Context.Bindings.Values,
            b => b.Kind == PawnActivityKind.Deliver);
    }

    /// <summary>
    /// #3625: an ordinary stack the HaulOut moves stands no designation, only a haul-out record the read lists as the same
    /// Haul removal op, and a stack larger than one carry binds too: one Deliver task of the whole stack whose first order
    /// carries one Capacity, the continuation a second Deliver of the remainder to the same storage cell, then done.
    /// </summary>
    [Fact]
    public void AStackLargerThanOneCarryIsOneDeliverTaskWorkedInTwoCarries()
    {
        const string item = "Thing_Silver1";
        var store = new Cell(40, 30);
        var origin = new Cell(30, 30);
        var row = new ConstructionActivityOption(item, "Silver", store, "producer", "Silver", item, origin, 100, 75, "HaulGeneral",
            Known(10d), Known(50d), Known(0d)) { SiteApproachTicks = Known(10d), AvailableInput = Known(100d) };
        var facts = RemovalFacts(new PlannedSiteClearing(item, PlannedClearingKind.Haul) { Options = [row] });
        var generated = ForecastCoupledCandidates.GenerateCoupledActivities(facts, 1900)!;
        var binding = Assert.Single(generated.Context.Bindings.Values, b => b.Kind == PawnActivityKind.Deliver);
        Assert.Equal((item, 100, 75), (binding.Target, binding.Count, binding.Capacity));
        var request = ForecastCoupled.CoupledForecastRequestOf(new("world/map/load", 100), 1900d / GameTime.TicksPerDay,
            facts, new() { Work = ForecastWorkInputs.ForecastWorkInputsOf(facts) }, [], new());
        var batch = ForecastCoupledSelection.StartCoupledActivity(ForecastCoupledSelection.CompareActivityOutcomes(request, generated), generated, 1, "construction", 100)!;
        var task = Assert.Single(batch.Tasks);
        Assert.Equal(100, task.Step.Count);
        var first = Assert.Single(Assert.Single(batch.Orders, o => !o.Release).Steps);
        Assert.Equal((PawnActivityKind.Deliver, item, 75, (Cell?)store), (first.Kind, first.Target, first.Count, first.Destination));

        ActivityTaskRead Read(PawnActivities current, string state, params ActivityTaskBinding[] bindings) => new()
        {
            Known = true, Context = new() { Identity = new() { ColonyId = "colony", LoadToken = "load", MapId = 0 }, Tick = 300 }, Bindings = EquatableArray.From(bindings),
            Pawns = EquatableArray.From(current.Orders.Select(o => new ActivityTaskObservation(o.Pawn, new(o.Assignment, o.Method, o.Revision, 0, state, "", null, 300))))
        };
        var next = CoordinatedActivitiesRules.ContinueCoordinatedActivity(batch, Known(true), Read(batch, "completed",
            new ActivityTaskBinding(task.Id, task.Step with { Count = 25 })))!;
        Assert.Equal(25, Assert.Single(next.Tasks).Step.Count);
        var second = Assert.Single(Assert.Single(next.Orders, o => !o.Release).Steps);
        Assert.Equal((PawnActivityKind.Deliver, item, 25, (Cell?)store), (second.Kind, second.Target, second.Count, second.Destination));
        var done = CoordinatedActivitiesRules.ContinueCoordinatedActivity(next, Known(true), Read(next, "completed"))!;
        Assert.Empty(done.Tasks);
        Assert.True(done.Orders.Single().Release);
    }

    /// <summary>#3612: the 16-op cap is shared across removal kinds, and a kind no order works yet (RemoveFloor) binds nothing.</summary>
    [Fact]
    public void RemovalOpsShareOneCapAcrossKindsAndAnUnworkedKindBindsNothing()
    {
        static PlannedSiteClearing Removal(int n, PlannedClearingKind kind) => new("Thing_" + n, kind)
        {
            Options = [new("Thing_" + n, "Bed", new(30, 30), "producer", "Bed", "Thing_" + n, new(30, 30), 0, 0, "Giver", default, default, Known(50d)) { SiteApproachTicks = Known(10d) }],
        };
        var ops = Enumerable.Range(10, 17).Select(n => Removal(n, n % 2 == 0 ? PlannedClearingKind.Deconstruct : PlannedClearingKind.Uninstall)).ToArray();
        var generated = ForecastCoupledCandidates.GenerateCoupledActivities(RemovalFacts(ops), 1900)!;
        Assert.Equal(16, generated.Context.Bindings.Values.Count(b => b.Job is "Deconstruct" or "Uninstall"));
        Assert.True(generated.Truncated);
        var floor = ForecastCoupledCandidates.GenerateCoupledActivities(RemovalFacts(Removal(1, PlannedClearingKind.RemoveFloor)), 1900);
        Assert.True(floor is null || !floor.Context.Bindings.Values.Any(b => b.Target == "Thing_1"));
    }

    /// <summary>#3047, #3649: in game a plant cut was gated behind another colonist's meal a day ahead and held both colonists.</summary>
    [Theory]
    [InlineData("rest")]
    [InlineData("food")]
    public void WorkDelayedBehindARecoveryBelongsToTheRecoveringPawn(string kind)
    {
        var facts = ForecastCoupledSelectionTests.Review();
        var roster = WorkResourceTestFacts.Create("pawn", "helper");
        var pawn = facts.ForecastObserved.Needs!.Pawns[0] with
        {
            Location = new(1, 1),
            SleepThreshold = Known(.9), WakeThreshold = Known(1d), SleepBlockedUntilTicks = Known(0d), SleepWhileStarving = Known(false),
            Needs = [new(kind, Known(.8), Known(.0001), Known(.3))],
            Targets = [kind == "rest" ? new("target-pawn", "rest", "", 1, Known(10d), default, default, Known(.001)) { Location = new(1, 1) }
                : new("target-pawn", "food", "MealSimple", 1, Known(10d), Known(10d), Known(.9), default) { Location = new(1, 1) }]
        };
        var helper = pawn with { Pawn = "helper", Targets = [pawn.Targets[0] with { Id = "target-helper" }] };
        facts = facts with { WorkResources = roster.WorkResources, Resources = Known<EquatableArray<RimGovernor.Brain.Policy.Amount>>([new(new("WoodLog"), 20)]),
            ForecastObserved = facts.ForecastObserved with
            {
                WorkPawns = roster.ForecastObserved.WorkPawns,
                Needs = facts.ForecastObserved.Needs with { Pawns = [pawn, helper] },
                Coupled = facts.ForecastObserved.Coupled! with
                {
                    Food = facts.ForecastObserved.Coupled.Food with { Options = [] }, FoodUses = [],
                    Construction = new() { Known = true, Context = new() { Identity = new() { ColonyId = "colony", LoadToken = "load", MapId = 0 }, Tick = 100 },
                        Options = EquatableArray.From([Option("pawn", 0, 20, 20), Option("helper", 0, 20, 20)]) }
                }
            } };
        facts = WorkRules.ReviewWorkResources(facts, Known<EquatableArray<ProductionBatch>>([]), new([]));
        var activities = ForecastCoupledCandidates.GenerateCoupledActivities(facts, 1900)!.Context.Activities;
        var delayed = activities.Activities.Where(a => a.Id.Contains("/after/need/", StringComparison.Ordinal)).ToArray();
        Assert.NotEmpty(delayed);
        foreach (var task in delayed)
        {
            var recovering = task.Id[(task.Id.IndexOf("/after/need/", StringComparison.Ordinal) + "/after/need/".Length)..].Split('/')[0];
            Assert.All(activities.Options.Where(o => o.Activity == task.Id), o => Assert.Equal(recovering, o.Pawn));
        }
    }

    [Fact]
    public void SharedSearchChoosesDeliverySpecialistAndBuilderThenDistanceFlipsChoice()
    {
        var builder = Option("producer", 0, 400, 20);
        var hauler = Option("helper", 5, 20, 500);
        var batch = Start(out _, builder, hauler)!;
        Assert.Equal("helper", batch.Tasks.Single(t => t.Step.Kind == PawnActivityKind.Supply).Pawn);
        Assert.Equal("producer", batch.Tasks.Single(t => t.Step.Job == "FinishFrame").Pawn);
        Assert.Equal(10, batch.Orders.Single(o => o.Pawn == "helper").Steps[0].Count);
        Assert.Equal(20, batch.Tasks.Single(t => t.Step.Kind == PawnActivityKind.Supply).Step.Count);
        Assert.Single(Start(out _, builder, hauler with { ApproachTicks = Known(1500.0), SiteApproachTicks = Known(1500.0) })!.Orders);
        Assert.Null(Start(out var unknown, builder with { DeliveryTicks = default }));
        Assert.Contains(unknown!.Findings, f => f.Contains("unknown", StringComparison.Ordinal));
    }

    /// <summary>#3673: no one stack holds the site's 20 logs, so both are supplied, each its share, before the finish.</summary>
    [Fact]
    public void ASiteNoOneStackCoversIsSuppliedFromEveryStackOfItsSet()
    {
        var near = Option("producer", 0, 20, 20) with { AvailableInput = Known(12d) };
        var far = Option("producer", 5, 30, 20) with { Input = "wood-b", Origin = new(12, 10), AvailableInput = Known(8d) };
        // Native reads the leg back from the site to each stack of the set.
        CoupledRoute[] back = [new("producer", near.Location, near.Origin, Known(20d)), new("producer", far.Location, far.Origin, Known(22d))];
        var batch = Start(out _, back, near, far)!;
        var supplies = batch.Tasks.Where(t => t.Step.Kind == PawnActivityKind.Supply).ToDictionary(t => t.Step.Target, t => t.Step.Count);
        Assert.Equal(new Dictionary<string, int> { ["wood"] = 12, ["wood-b"] = 8 }, supplies);
        Assert.Equal(2, batch.Tasks.Single(t => t.Step.Job == "FinishFrame").Prerequisites.Count);
        // A set whose stacks do not cover the demand is no candidate.
        Assert.Empty(RimGovernor.Brain.Policy.Methods.Generation.ConstructionMethodCandidates.StackShares([near, far with { AvailableInput = Known(7d) }], _ => 0)!);
        // Without the legs back from the site one pawn cannot fetch the second stack.
        Assert.Null(Start(out _, near, far));
    }

    [Fact]
    public void FreshNativeContainerBindingClampsDemandAndFinishRequiresObservedBuilding()
    {
        var batch = Start(out _, Option("producer", 0, 20, 10))!;
        var supply = batch.Tasks.Single(t => t.Step.Kind == PawnActivityKind.Supply);
        ActivityTaskRead Read(PawnActivities current, string state, params ActivityTaskBinding[] bindings) => new()
        {
            Known = true, Context = new() { Identity = new() { ColonyId = "colony", LoadToken = "load", MapId = 0 }, Tick = 300 }, Bindings = EquatableArray.From(bindings),
            Pawns = EquatableArray.From(current.Orders.Select(o => new ActivityTaskObservation(o.Pawn,
                new(o.Assignment, o.Method, o.Revision, 0, state, "", null, 300))))
        };
        var retry = CoordinatedActivitiesRules.ContinueCoordinatedActivity(batch, Known(true), Read(batch, "failed",
            new ActivityTaskBinding(supply.Id, supply.Step with { Target = "carried-wood", Container = "frame", Count = 7 }) { RemainingCount = Known(7) }))!;
        Assert.Equal(7, retry.Tasks.Single(t => t.Id == supply.Id).Step.Count);
        var finish = retry.Tasks.Single(t => t.Step.Job == "FinishFrame");
        var next = CoordinatedActivitiesRules.ContinueCoordinatedActivity(retry, Known(true), Read(retry, "completed",
            new ActivityTaskBinding(finish.Id, finish.Step with { Target = "frame" })) with { SatisfiedTasks = [supply.Id] })!;
        Assert.Equal("frame", next.Orders.Single().Steps[0].Target);
        Assert.True(CoordinatedActivitiesRules.ContinueCoordinatedActivity(next, Known(true), Read(next, "completed"))!.Orders.Single().Release);
        var done = CoordinatedActivitiesRules.ContinueCoordinatedActivity(next, Known(true), Read(next, "completed") with { SatisfiedTasks = [finish.Id] })!;
        Assert.Empty(done.Tasks);
        Assert.True(done.Orders.Single().Release);
    }

    /// <summary>
    /// #3046: wall/remote_pickup's geometry. A frame owes 5 logs; three stored stacks stand 40 cells east (15, then 20 at 8
    /// cells and 20 at 14 cells from the first). Native lists all three; delivering from the first is remote (40 cells from
    /// the frame, 0 from storage), so it is a full load that collects the stack 8 cells away (8 &lt; 20 * 40 / 75) and not the
    /// one 14 cells away. With no storage accepting the stack, or storage farther than the frame, it takes the need alone.
    /// </summary>
    [Fact]
    public void ARemoteDeliveryTakesAFullLoadAndCollectsTheStackWorthItsDetour()
    {
        ConstructionActivityOption Stack(string id, Cell origin, double held, double delivery) => Option("producer", 0, delivery, 20) with
        { Location = new(50, 50), Input = id, Origin = origin, Count = 5, Capacity = 75, AvailableInput = Known(held), StorageDistance = Known(0.0) };
        ConstructionActivityOption[] rows = [Stack("wood", new(90, 50), 15, 20), Stack("wood-near", new(90, 58), 20, 30), Stack("wood-far", new(90, 36), 20, 30)];
        var (fullLoad, collect) = RimGovernor.Brain.Policy.Methods.Generation.ConstructionMethodCandidates.RemoteLoad(rows[0], rows);
        Assert.True(fullLoad);
        Assert.Equal(["wood-near"], collect);
        Assert.False(RimGovernor.Brain.Policy.Methods.Generation.ConstructionMethodCandidates.RemoteLoad(rows[0] with { StorageDistance = default }, rows).FullLoad);
        Assert.False(RimGovernor.Brain.Policy.Methods.Generation.ConstructionMethodCandidates.RemoteLoad(rows[0] with { StorageDistance = Known(41.0) }, rows).FullLoad);
        // The selected step carries the full load to native, collecting a stack under the need a cell away (1 < 4 * 40 / 75);
        // the site still takes its need.
        var supply = Start(out _, rows[0], Stack("wood-small", new(90, 51), 4, 30))!.Tasks.Single(t => t.Step.Kind == PawnActivityKind.Supply).Step;
        Assert.Equal("wood", supply.Target);
        Assert.True(supply.FullLoad);
        Assert.Equal(5, supply.Count);
        Assert.Equal(["wood-small"], supply.Collect);
    }

    /// <summary>#3046: native lists a stack under the site's need beside one that covers it; only the whole stack is a delivery.</summary>
    [Fact]
    public void AStackUnderTheNeedIsNoAlternativeBesideAWholeOne()
    {
        var whole = Option("producer", 0, 20, 20);
        var small = Option("producer", 0, 5, 20) with { Input = "wood-small", AvailableInput = Known(3d) };
        Assert.Null(RimGovernor.Brain.Policy.Methods.Generation.ConstructionMethodCandidates.StackShares([whole, small], _ => 0));
        var supply = Start(out _, whole, small)!.Tasks.Single(t => t.Step.Kind == PawnActivityKind.Supply);
        Assert.Equal("wood", supply.Step.Target);
        Assert.False(supply.Step.FullLoad);
    }
    [Fact]
    public void GeneratedConstructionChoiceCarriesLearningThroughTheSelectedPhysicalPlan()
    {
        var work = ProductiveLearningTests.Work() with
        { Recipe = new("Construction", Known(.25), true), LearnBeforeWork = true };
        var batch = Start(out var comparison, Option("producer", 0, 20, 100) with { SkillWork = work });
        Assert.NotNull(batch);
        var learned = Assert.Single(comparison!.Plan!.Allocations, a => a.Learning != null).Learning!;
        Assert.Equal("Construction", learned.Skill);
        Assert.True(learned.After.StoredLevel.Value > learned.Before.StoredLevel.Value);
        Assert.True(learned.WorkTicks < learned.ObservedWorkTicks);
        Assert.True(learned.After.AppliedGain.Value > 0);
    }
}
