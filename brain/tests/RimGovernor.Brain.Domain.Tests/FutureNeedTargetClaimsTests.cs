using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimGovernor.Contracts.Wire.Observations;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;
using static RimGovernor.Brain.Policy.PolicyFunctions;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class FutureNeedTargetClaimsTests
{
    [Theory]
    [InlineData(PawnActivityKind.Sleep)]
    [InlineData(PawnActivityKind.Recreation)]
    [InlineData(PawnActivityKind.Eat)]
    public void PreparingParticipantOwnsItsFutureExactServiceBeforeNativeJobStarts(PawnActivityKind kind)
    {
        var step = new PawnActivityStep(kind, "shared-service", "", "", kind == PawnActivityKind.Eat ? 1 : 0, 2000);
        var task = new SelectedActivityTask("future-service", "owner", [], step, new(2, 2), 500);
        var batch = new PawnActivities(1, [new("owner", "assignment", "method", 0, 1, false,
            [new(PawnActivityKind.Approach, "", "", "", 0, 2000) { Destination = new(2, 2), NotBefore = 500 },
             new(PawnActivityKind.Hold, "", "", "", 0, 2000)]) { Task = task.Id, Preparing = true }])
            { Tasks = [task] };
        var action = DomainAction.NewPawnActivitiesAction(new("future"), batch);
        var facts = WorkResourceTestFacts.Create("owner", "pawn");
        facts = ReviewWorkResources(facts, Known<EquatableArray<ProductionBatch>>([]),
            new([new(new("plan"), action, new() { Action = action.Id, Stage = Stage.Pending })]));
        Assert.Contains("shared-service", facts.WorkView!.Claims.Targets);
        var other = DomainAction.NewPawnActivitiesAction(new("other"), new(1,
            [new("pawn", "other-assignment", "other-method", 0, 1, false, [step])]));
        Assert.False(WorkResourcesFit(facts.WorkView, new(new("other-plan"), new(1), [other])));
        if (kind == PawnActivityKind.Eat)
        {
            var symbolic = batch with { Tasks = [task with { Step = step with { Target = "observed-output" } }] };
            var reviewed = ReviewWorkResources(WorkResourceTestFacts.Create("owner", "pawn"),
                Known<EquatableArray<ProductionBatch>>([]),
                new([new(new("plan"), DomainAction.NewPawnActivitiesAction(action.Id, symbolic),
                    new() { Action = action.Id, Stage = Stage.Pending })]));
            Assert.DoesNotContain("observed-output", reviewed.WorkView!.Claims.Targets);
        }
    }

    [Fact]
    public void CoupledGenerationDoesNotProposeAnotherPawnForFutureClaimedRecreation()
    {
        var facts = ForecastCoupledSelectionTests.Review();
        var roster = WorkResourceTestFacts.Create("owner", "pawn");
        var pawn = facts.ForecastObserved.Needs!.Pawns[0] with
        {
            Location = new(1, 1),
            Needs = [new("joy", Known(.2), Known(.003), Known(.3))],
            Targets = [new("pin", "joy", "", 1, Known(0d), Known(10d), Known(0d), Known(.01))
                { Location = new(2, 2) }]
        };
        facts = facts with { Resources = roster.Resources, WorkResources = roster.WorkResources,
            ForecastObserved = facts.ForecastObserved with { WorkPawns = roster.ForecastObserved.WorkPawns,
                Needs = facts.ForecastObserved.Needs with { Pawns = [pawn] } } };
        var step = new PawnActivityStep(PawnActivityKind.Recreation, "pin", "", "", 0, 2000);
        var action = DomainAction.NewPawnActivitiesAction(new("held"), new(1,
            [new("owner", "held/owner", "held", 0, 1, false,
                [new(PawnActivityKind.Hold, "", "", "", 0, 2000)]) { Preparing = true, Task = "future" }])
            { Tasks = [new("future", "owner", [], step, new(2, 2), 500)] });
        var free = ReviewWorkResources(facts, Known<EquatableArray<ProductionBatch>>([]), new([]));
        Assert.Contains(ForecastCoupledCandidates.GenerateCoupledActivities(free, 300)!.Context.NeedTasks, n => n.Target == "pin");
        var held = ReviewWorkResources(facts, Known<EquatableArray<ProductionBatch>>([]),
            new([new(new("held-plan"), action, new() { Action = action.Id, Stage = Stage.Pending })]));
        Assert.DoesNotContain(ForecastCoupledCandidates.GenerateCoupledActivities(held, 300)!.Context.NeedTasks, n => n.Target == "pin");
    }
}
