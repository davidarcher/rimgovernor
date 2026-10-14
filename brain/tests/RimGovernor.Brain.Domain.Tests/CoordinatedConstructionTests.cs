using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimGovernor.Contracts.Wire.Observations;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;
using static RimGovernor.Brain.Policy.PolicyFunctions;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class CoordinatedConstructionTests
{
    private static ConstructionActivityOption Option(string pawn, double approach, double delivery, double finish) =>
        new("blueprint", "Wall", new(30, 30), pawn, "WoodLog", "wood", new(10, 10), 20, 10, "FinishFrames",
            Known(approach), Known(delivery), Known(finish)) { SiteApproachTicks = Known(approach) };
    private static WorkResourceView Resources()
    {
        var facts = WorkResourceTestFacts.Create("producer", "helper");
        return WorkResourcesOf(WorkAdmissionEvidenceOf(facts, Known(default(EquatableArray<ProductionBatch>))),
            Known(default(EquatableArray<ProductionBatch>)), []);
    }
    private static PawnActivities? Start(out ForecastActivityOutcomeComparison? comparison, params ConstructionActivityOption[] options) =>
        StartConstructionActivity(new() { Known = true, Context = new() { Identity = new() { ColonyId = "colony", LoadToken = "load", MapId = 0 }, Tick = 100 }, Options = EquatableArray.From(options) },
            1, "construction", 2000, Resources(), out comparison);

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
        var retry = ContinueCoordinatedActivity(batch, Known(true), Read(batch, "failed",
            new ActivityTaskBinding(supply.Id, supply.Step with { Target = "carried-wood", Container = "frame", Count = 7 }) { RemainingCount = Known(7) }))!;
        Assert.Equal(7, retry.Tasks.Single(t => t.Id == supply.Id).Step.Count);
        var finish = retry.Tasks.Single(t => t.Step.Job == "FinishFrame");
        var next = ContinueCoordinatedActivity(retry, Known(true), Read(retry, "completed",
            new ActivityTaskBinding(finish.Id, finish.Step with { Target = "frame" })) with { SatisfiedTasks = [supply.Id] })!;
        Assert.Equal("frame", next.Orders.Single().Steps[0].Target);
        Assert.True(ContinueCoordinatedActivity(next, Known(true), Read(next, "completed"))!.Orders.Single().Release);
        var done = ContinueCoordinatedActivity(next, Known(true), Read(next, "completed") with { SatisfiedTasks = [finish.Id] })!;
        Assert.Empty(done.Tasks);
        Assert.True(done.Orders.Single().Release);
    }
}
