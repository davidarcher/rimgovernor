using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimGovernor.Contracts.Wire.Observations;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;
using static RimGovernor.Brain.Policy.PolicyFunctions;

namespace RimGovernor.Brain.Domain.Tests;

// #3211: Hands admits claims against an observed target's capacity (MaxPawns, stack quantity), as the evaluator does.
public sealed class WorkTargetCapacityClaimsTests
{
    private static NeedTarget Stack(double quantity, Fact<int> maxPawns) =>
        new("stack", "food", "Rice", 10, Known(0d), Known(10d), Known(0d), Known(.01)) { StockCount = Known(quantity), MaxPawns = maxPawns };
    private static NeedTarget Service(string id, string kind, Fact<int> maxPawns) =>
        new(id, kind, "", 1, Known(0d), Known(10d), Known(0d), Known(.01)) { MaxPawns = maxPawns };

    private static RoundsFacts Facts(params NeedTarget[] targets)
    {
        var facts = WorkResourceTestFacts.Create("a", "b", "c", "d");
        return facts with { ForecastObserved = facts.ForecastObserved with
            { Needs = new() { Tick = 100, Pawns = [new() { Pawn = "a", Targets = EquatableArray.From(targets) }] } } };
    }

    private static PawnActivityOrder Order(PawnActivityKind kind, string pawn, string target, int count = 0) =>
        new(pawn, pawn + "/assignment", "method", 0, 1, false, [new(kind, target, "", "", count, 2000)]) { Task = pawn + "/task" };
    private static DomainAction Action(string id, params PawnActivityOrder[] orders) =>
        DomainAction.NewPawnActivitiesAction(new(id), new(1, orders));
    private static PlanSpec Plan(string id, params DomainAction[] actions) => new(new(id), new(1), EquatableArray.From(actions));
    private static WorkResourceView Committed(RoundsFacts facts, params DomainAction[] held) =>
        ReviewWorkResources(facts, Known<EquatableArray<ProductionBatch>>([]), new(EquatableArray.From(held
            .Select(a => new WorkAction(new(a.Id.Value + "-plan"), a, new() { Action = a.Id, Stage = Stage.Pending })))))
        .WorkView!;

    [Fact]
    public void TwoProposalsEatFromOneObservedStackWithinItsQuantityButNotBeyond()
    {
        var eatThree = Action("first", Order(PawnActivityKind.Eat, "a", "stack", 3));
        var within = Committed(Facts(Stack(7, Known(10))), eatThree);
        Assert.Null(WorkResourcesRefusal(within, Plan("second", Action("second", Order(PawnActivityKind.Eat, "b", "stack", 4)))));
        Assert.Equal("target_claimed:stack", WorkResourcesRefusal(within, Plan("third", Action("third", Order(PawnActivityKind.Eat, "b", "stack", 5)))));
        Assert.Equal("target_claimed:stack", WorkResourcesRefusal(Committed(Facts(Stack(7, Known(10))), eatThree,
            Action("second", Order(PawnActivityKind.Eat, "b", "stack", 4))), Plan("third", Action("third", Order(PawnActivityKind.Eat, "c", "stack", 1)))));
    }

    [Fact]
    public void OneProposalSharesAStackWithinItsQuantityAndCapacity()
    {
        var facts = Facts(Stack(7, Known(10)));
        var view = Committed(facts);
        Assert.Null(WorkResourcesRefusal(view, Plan("two", Action("two", Order(PawnActivityKind.Eat, "a", "stack", 3), Order(PawnActivityKind.Eat, "b", "stack", 4)))));
        Assert.Equal("shared_target:stack", WorkResourcesRefusal(view, Plan("over", Action("over", Order(PawnActivityKind.Eat, "a", "stack", 4), Order(PawnActivityKind.Eat, "b", "stack", 4)))));
        // A stack of capacity 1 admits one owner however small the claims.
        Assert.Equal("shared_target:stack", WorkResourcesRefusal(Committed(Facts(Stack(7, Known(1)))),
            Plan("crowd", Action("crowd", Order(PawnActivityKind.Eat, "a", "stack", 1), Order(PawnActivityKind.Eat, "b", "stack", 1)))));
    }

    [Fact]
    public void FutureEatingCommitmentSharesItsStackByTaskAndOrderOnce()
    {
        // The task row and its order both name the claim; the count must not be taken twice.
        var step = new PawnActivityStep(PawnActivityKind.Eat, "stack", "", "", 4, 2000);
        var task = new SelectedActivityTask("a/task", "a", [], step, new(2, 2), 500)
            { Inputs = [new("Rice", "stack", 4, new(2, 2), "observed need target") { Observed = true }] };
        var held = DomainAction.NewPawnActivitiesAction(new("held"), new(1, [new("a", "a/assignment", "method", 0, 1, false, [step]) { Task = task.Id }])
            { Tasks = [task] });
        var view = Committed(Facts(Stack(7, Known(10))), held);
        Assert.Equal(4, view.Claims.Shared.Sum(s => s.Count));
        Assert.Contains("stack", view.Claims.Targets);
        Assert.Null(WorkResourcesRefusal(view, Plan("rest", Action("rest", Order(PawnActivityKind.Eat, "b", "stack", 3)))));
        Assert.Equal("target_claimed:stack", WorkResourcesRefusal(view, Plan("more", Action("more", Order(PawnActivityKind.Eat, "b", "stack", 4)))));
    }

    [Fact]
    public void OneSlotBedRefusesASecondClaimantAndAMultiSlotBedAdmitsItsCapacity()
    {
        var sleep = Action("sleeper", Order(PawnActivityKind.Sleep, "a", "bed"));
        var other = Plan("other", Action("other", Order(PawnActivityKind.Sleep, "b", "bed")));
        Assert.Equal("target_claimed:bed", WorkResourcesRefusal(Committed(Facts(Service("bed", "rest", Known(1))), sleep), other));
        Assert.Null(WorkResourcesRefusal(Committed(Facts(Service("bed", "rest", Known(2))), sleep), other));
        Assert.Equal("target_claimed:bed", WorkResourcesRefusal(Committed(Facts(Service("bed", "rest", Known(2))), sleep,
            Action("second", Order(PawnActivityKind.Sleep, "b", "bed"))), Plan("third", Action("third", Order(PawnActivityKind.Sleep, "c", "bed")))));
    }

    [Fact]
    public void CapacityIsTheObservedMaxPawnsAndUnknownBehavesAsOne()
    {
        var play = Action("player", Order(PawnActivityKind.Recreation, "a", "pin"));
        PlanSpec Next(string pawn) => Plan("next-" + pawn, Action("next-" + pawn, Order(PawnActivityKind.Recreation, pawn, "pin")));
        var pin = Facts(Service("pin", "joy", Known(3)));
        Assert.Null(WorkResourcesRefusal(Committed(pin, play, Action("b", Order(PawnActivityKind.Recreation, "b", "pin"))), Next("c")));
        Assert.Equal("target_claimed:pin", WorkResourcesRefusal(Committed(pin, play, Action("b", Order(PawnActivityKind.Recreation, "b", "pin")),
            Action("c", Order(PawnActivityKind.Recreation, "c", "pin"))), Next("d")));
        Assert.Equal("target_claimed:pin", WorkResourcesRefusal(Committed(Facts(Service("pin", "joy", default)), play), Next("b")));
        Assert.Equal("target_claimed:pin", WorkResourcesRefusal(Committed(Facts(), play), Next("b")));
    }

    [Fact]
    public void ATargetTakenWholeStaysExclusiveAgainstASharedClaim()
    {
        var facts = Facts(Stack(7, Known(10)));
        var deliver = Action("haul", new PawnActivityOrder("a", "a/haul", "method", 0, 1, false,
            [new(PawnActivityKind.Deliver, "stack", "", "", 2, 2000) { Destination = new(2, 2) }]));
        Assert.Equal("target_claimed:stack", WorkResourcesRefusal(Committed(facts, deliver),
            Plan("eat", Action("eat", Order(PawnActivityKind.Eat, "b", "stack", 1)))));
        Assert.Equal("shared_target:stack", WorkResourcesRefusal(Committed(facts),
            Plan("mixed", deliver, Action("eat", Order(PawnActivityKind.Eat, "b", "stack", 1)))));
    }
}
