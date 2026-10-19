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
    private static PawnActivities? Start(out ForecastActivityOutcomeComparison? comparison, params ConstructionActivityOption[] options)
    {
        var facts = ForecastCoupledSelectionTests.Review(level: 1, fall: 0);
        var template = facts.ForecastObserved.Needs!.Pawns[0];
        facts = facts with { WorkView = Resources(), ForecastObserved = facts.ForecastObserved with
        {
            Needs = facts.ForecastObserved.Needs with { Pawns = [template with { Pawn = "producer" }, template with { Pawn = "helper" }] },
            Coupled = new() { Tick = 100, Construction = new() { Known = true, Context = new() { Identity = new() { ColonyId = "colony", LoadToken = "load", MapId = 0 }, Tick = 100 }, Options = EquatableArray.From(options) } }
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
            Coupled = new() { Tick = 100, Construction = new() { Known = true, Context = new() { Identity = new() { ColonyId = "colony", LoadToken = "load", MapId = 0 }, Tick = 100 }, Options = EquatableArray.From([option]) } }
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
            Coupled = new() { Tick = 100, Construction = new() { Known = true, Context = new() { Identity = new() { ColonyId = "colony", LoadToken = "load", MapId = 0 }, Tick = 100 },
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

    [Fact]
    public void WorkDelayedBehindARecoveryBelongsToTheRecoveringPawn()
    {
        var facts = ForecastCoupledSelectionTests.Review();
        var roster = WorkResourceTestFacts.Create("pawn", "helper");
        var pawn = facts.ForecastObserved.Needs!.Pawns[0] with
        {
            Location = new(1, 1),
            SleepThreshold = Known(.9), WakeThreshold = Known(1d), SleepBlockedUntilTicks = Known(0d), SleepWhileStarving = Known(false),
            Needs = [new("rest", Known(.8), Known(.0001), Known(.3))],
            Targets = [new("bed-pawn", "rest", "", 1, Known(10d), default, default, Known(.001)) { Location = new(1, 1) }]
        };
        var helper = pawn with { Pawn = "helper", Targets = [pawn.Targets[0] with { Id = "bed-helper" }] };
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
}
