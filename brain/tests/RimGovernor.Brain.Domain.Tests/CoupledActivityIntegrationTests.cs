using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Store;
using RimGovernor.Brain.Bridge.Contracts;
using RimGovernor.Contracts.Wire.Observations;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;
using Journal = RimGovernor.Brain.Store.Store;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class CoupledActivityIntegrationTests
{
    private static readonly GenerationSnapshot Root = new(new("colony"), new(1), new("load"), new("root"), new(1), new(1));

    private static (RoundsFacts Facts, PawnActivities Batch) SelectObserved()
    {
        var facts = ForecastCoupledSelectionTests.Review(level: .1, fall: .00001, expiry: 100000);
        var observed = facts.ForecastObserved.Coupled!;
        facts = facts with { ForecastObserved = facts.ForecastObserved with { Coupled = observed with
            { Food = observed.Food with { Options = [observed.Food.Options[0] with { Destination = new(2, 1) }] },
                FoodUses = EquatableArray.From(observed.FoodUses.Select(u => u with { Location = new(2, 1) })) } } };
        facts = facts with { CoupledAlternatives = ForecastCoupledCandidates.GenerateCoupledActivities(facts, GameTime.TicksPerDay)?.Priced() };
        var findings = RoundsRules.InspectRounds(facts, new(), RoundsRules.DefaultRoundsPolicy());
        var selection = Assert.IsType<ForecastCoupledSelection>(findings.CoupledSelection);
        Assert.NotEmpty(selection.Selection.Selected);
        Assert.True(selection.SelectedVector.Urgent[0].Value < selection.BaselineVector.Urgent[0].Value);
        var batch = ForecastCoupledSelection.StartCoupledActivity(selection, facts.CoupledAlternatives!, 1, "coupled-observed", 100);
        Assert.NotNull(batch);
        Assert.Equal(2, Assert.Single(batch.Tasks, t => t.Step.Kind == PawnActivityKind.Deliver).Step.Count);
        Assert.Equal(PawnActivityKind.Work, Assert.Single(batch.Orders).Steps[0].Kind);
        return (facts, batch);
    }

    private static ActivityTaskRead Observe(PawnActivities batch, string state, long tick, params ActivityTaskBinding[] bindings) => ConstructionFixtures.NoTasks() with
    {
        Context = new() { Identity = new() { ColonyId = "colony", LoadToken = "load", MapId = 0 }, Tick = tick },
        Bindings = EquatableArray.From(bindings),
        Pawns = EquatableArray.From(batch.Orders.Select(o => new ActivityTaskObservation(o.Pawn,
            new(o.Assignment, o.Method, o.Revision, o.Preparing ? 1 : 0, state, "", tick)))),
    };

    // Fake the native endpoint only. Plans, admission journal, receipts and typed Hands intent use production code.
    private static PawnActivities Send(Journal journal, PlanSpec plan, long tick, CancellationToken ct)
    {
        var scope = Root with { Plan = plan.Id };
        var action = Assert.Single(journal.LoadPlan(plan.Id, ct).Spec.Actions);
        journal.Prepare(plan.Id, action.Id, scope, new(tick), ct);
        var dispatched = journal.Dispatch(plan.Id, action.Id, scope, new(tick), ct);
        var sent = IntentActionRules.IntentAction(action.Id.Value, action).Intent!.PawnActivities!.Batch!;
        journal.RecordReceipt(plan.Id, action.Id, dispatched.View.Attempt, Receipt.Accepted, ct);
        return sent;
    }

    private static PlanSpec Continue(Journal journal, PlanSpec old, PawnActivities batch, PawnActivities next, long tick, string id, CancellationToken ct, string state = "completed")
    {
        var plan = new PlanSpec(new(id), new(1), [DomainAction.NewPawnActivitiesAction(new(id + "-action"), next)]);
        Assert.Equal(ActivityContinuationAdmission.Admitted, journal.ContinueActivityMethod(old.Id, Root, new(tick), EquatableArray.From(batch.Orders.Select(o =>
            new ActivityOwnership(o.Pawn, o.Assignment, o.Method, o.Revision, state))), plan, ct));
        journal.AuthorizeRoundsPlan(Root, Root with { Plan = plan.Id }, ct);
        return plan;
    }

    [Fact]
    public void InstalledGraphRetainsClaimsWhileIndependentSelectedMethodUsesAnotherPawn()
    {
        var ct = TestContext.Current.CancellationToken;
        var (facts, first) = SelectObserved();
        var roster = WorkResourceTestFacts.Create("pawn", "helper");
        facts = facts with { WorkResources = roster.WorkResources,
            ForecastObserved = facts.ForecastObserved with { WorkPawns = roster.ForecastObserved.WorkPawns } };
        IntentActionRules.RegisterIntentKinds();
        using var journal = Journal.Open(Path.Combine(Path.GetTempPath(), "rg-independent-" + Guid.NewGuid().ToString("N") + ".db"), ct);
        journal.ReviewRounds(new RoundsRequest { Current = Root, Tick = new(100), Enabled = true,
            Policy = RoundsRules.DefaultRoundsPolicy(), Facts = facts }, ct);
        var firstPlan = new PlanSpec(new("first"), new(1), [DomainAction.NewPawnActivitiesAction(new("first-action"), first)]);
        TestNeed.Commit(journal, Root, new("coupled-observed"), firstPlan, ct);
        Send(journal, firstPlan, 100, ct);
        var observed = facts.ForecastObserved.Coupled!;
        var other = observed.Food.Options[0] with { Pawn = "helper", Target = "independent-crop", Origin = new(3, 1), Destination = new(4, 1) };
        facts = facts with { CoupledWorkActive = true, ForecastObserved = facts.ForecastObserved with
        {
            Needs = facts.ForecastObserved.Needs! with { Pawns = [facts.ForecastObserved.Needs.Pawns[0],
                facts.ForecastObserved.Needs.Pawns[0] with { Pawn = "helper" }] },
            Coupled = observed with { Food = observed.Food with { Options = [observed.Food.Options[0], other] },
                FoodUses = [observed.FoodUses[0], observed.FoodUses[0] with { Pawn = "helper", Location = new(4, 1) }] },
        } };
        facts = WorkRules.ReviewWorkResources(facts, Known<EquatableArray<ProductionBatch>>([]), journal.WorkActions(Root, ct));
        Assert.Contains("pawn", facts.WorkView!.Claims.Pawns);
        Assert.Contains("plant-a", facts.WorkView.Claims.Targets);
        facts = facts with { CoupledAlternatives = ForecastCoupledCandidates.GenerateCoupledActivities(facts, GameTime.TicksPerDay)?.Priced() };
        var inspected = RoundsRules.InspectRounds(facts, new(), RoundsRules.DefaultRoundsPolicy());
        var choice = Assert.IsType<ForecastCoupledSelection>(inspected.CoupledSelection);
        var next = ForecastCoupledSelection.StartCoupledActivity(choice, facts.CoupledAlternatives!, 1, "coupled-independent", 100);
        Assert.NotNull(next);
        Assert.All(next.Orders, order => Assert.Equal("helper", order.Pawn));
        Assert.DoesNotContain(next.Tasks, task => task.Step.Target == "plant-a" || task.Step.Destination == new Cell(2, 1));
        var secondPlan = new PlanSpec(new("second"), new(1), [DomainAction.NewPawnActivitiesAction(new("second-action"), next)]);
        TestNeed.Commit(journal, Root, new("coupled-independent"), secondPlan, ct);
        Send(journal, secondPlan, 100, ct);
        Assert.False(journal.LoadPlan(firstPlan.Id, ct).Retired);
        Assert.Equal(2, journal.ActivityMethods(Root, ct).Count);
        Assert.Collection(journal.WorkResources(Root, ct).Claims.Pawns.Order(StringComparer.Ordinal),
            pawn => Assert.Equal("helper", pawn), pawn => Assert.Equal("pawn", pawn));
    }
    [Theory]
    [InlineData(PawnActivityKind.Sleep)]
    [InlineData(PawnActivityKind.Recreation)]
    [InlineData(PawnActivityKind.Recipe)]
    [InlineData(PawnActivityKind.Eat)]
    public void MalformedProviderCannotReplaceSelectedServiceOrChangeMealQuantity(PawnActivityKind kind)
    {
        var step = new PawnActivityStep(kind, kind == PawnActivityKind.Eat ? "observed-output" : "selected-target", "", "", kind == PawnActivityKind.Eat ? 2 : 0, 1000)
        { Recipe = kind == PawnActivityKind.Recipe ? new("bill", "recipe", [new("ingredient", 2)]) { Resource = "Rice" } : null };
        var task = new SelectedActivityTask("service", "pawn", [], step, new(1, 1), 100);
        var order = new PawnActivityOrder("pawn", "coupled/pawn", "coupled", 0, 1, false,
            [new(PawnActivityKind.Approach, "", "", "", 0, 1000) { Destination = new(1, 1) },
                new(PawnActivityKind.Hold, "", "", "", 0, 1000)]) { Task = task.Id, Preparing = true };
        var batch = new PawnActivities(1, [order]) { Tasks = [task] };
        var malformed = kind == PawnActivityKind.Eat ? step with { Target = "actual-meal", Count = 1 }
            : step with { Target = "different-service-target" };
        var refused = CoordinatedActivitiesRules.ContinueCoordinatedActivity(batch, ActivityNeed.Owed, Observe(batch, "holding", 120,
            new ActivityTaskBinding.Bound(task.Id, malformed)))!;
        Assert.True(Assert.Single(refused.Orders).Release);
        Assert.Empty(refused.Orders[0].Steps);
        // A future food output can rebind to its observed stack, but must retain the selected quantity.
        var valid = kind == PawnActivityKind.Eat ? step with { Target = "actual-meal" } : step;
        var accepted = CoordinatedActivitiesRules.ContinueCoordinatedActivity(batch, ActivityNeed.Owed, Observe(batch, "holding", 120,
            new ActivityTaskBinding.Bound(task.Id, valid)))!;
        Assert.False(Assert.Single(accepted.Orders).Release);
        Assert.Equal(valid, Assert.Single(accepted.Orders[0].Steps));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OrdinarySelectedGraphSurvivesJournalAndRebindsChangedOutputBeforeDeliveryOrRelease(bool withdrawn)
    {
        var ct = TestContext.Current.CancellationToken;
        var (facts, batch) = SelectObserved();
        Assert.Contains(batch.Tasks.SelectMany(t => t.Inputs), input => input.Producers.Count > 0);
        IntentActionRules.RegisterIntentKinds();
        using var journal = Journal.Open(Path.Combine(Path.GetTempPath(), "rg-coupled-integration-" + Guid.NewGuid().ToString("N") + ".db"), ct);
        var reviewed = journal.ReviewRounds(new RoundsRequest { Current = Root, Tick = new(100), Enabled = true,
            Policy = RoundsRules.DefaultRoundsPolicy(), Facts = facts }, ct);
        var plan = new PlanSpec(new("selected"), new(1), [DomainAction.NewPawnActivitiesAction(new("selected-action"), batch)]);
        TestNeed.Commit(journal, Root, new("coupled-observed"), plan, ct);
        TestNeed.File(journal, met: false, new("coupled-observed"), ct);
        Assert.Equal(batch.Tasks.ToArray(), Send(journal, plan, 100, ct).Tasks.ToArray());
        // Acceptance is not completion. Without observed progress the selected graph is unchanged.
        Assert.Null(CoordinatedActivitiesRules.ContinueCoordinatedActivity(batch, ActivityNeed.Owed, Observe(batch, "started", 101)));
        var delivery = Assert.Single(batch.Tasks, t => t.Step.Kind == PawnActivityKind.Deliver);
        var completedAt = Math.Max(120, delivery.DepartTick);
        var next = CoordinatedActivitiesRules.ContinueCoordinatedActivity(batch, ActivityNeed.Owed, Observe(batch, "completed", completedAt,
            new ActivityTaskBinding.Bound(delivery.Id, delivery.Step with { Target = "merged-real-stack", Count = 2 })))!;
        Assert.NotNull(next);
        var unavailable = new PlanSpec(new("delivery"), new(1),
            [DomainAction.NewPawnActivitiesAction(new("delivery-action"), next)]);
        Assert.Equal(ActivityContinuationAdmission.ResourcesUnavailable,
            journal.ContinueActivityMethod(plan.Id, Root, new(completedAt), EquatableArray.From(batch.Orders.Select(o =>
                new ActivityOwnership(o.Pawn, o.Assignment, o.Method, o.Revision, "completed"))), unavailable, ct));
        // The ordinary observed review, not predicted harvest output, makes the two real units spendable.
        var active = Assert.Single(batch.Orders);
        facts = facts with { Tick = new(completedAt), Resources = Known<EquatableArray<RimGovernor.Brain.Policy.Amount>>([new(new("Rice"), 2)]),
            CoupledAlternatives = null, CoupledWorkActive = true,
            WorkResources = Known<EquatableArray<WorkResourcePawn>>([new(active.Pawn, active.Assignment, active.Revision,
                "completed", "", "", 0) { LocalHour = 6 }]) };
        var refreshed = journal.ReviewRounds(new RoundsRequest { Revision = reviewed.Review.Revision, Current = Root, Tick = new(completedAt), Enabled = true,
            Policy = RoundsRules.DefaultRoundsPolicy(), Facts = facts }, ct);
        Assert.False(journal.LoadPlan(plan.Id, ct).Retired); // Accepted assignment still owns unfinished graph work.
        var continuation = Continue(journal, plan, batch, next, completedAt, "delivery", ct);
        var sent = Send(journal, continuation, completedAt, ct);
        var order = Assert.Single(sent.Orders);
        Assert.Equal(PawnActivityKind.Deliver, order.Steps[0].Kind);
        Assert.Equal("merged-real-stack", order.Steps[0].Target);
        Assert.Equal(2, order.Steps[0].Count);
        Assert.True(journal.LoadPlan(plan.Id, ct).Retired);
        Assert.Equal("coupled-observed", order.Method);
        // A changed physical stack needs a fresh observed binding; no harvested ID is treated as inventory.
        var retry = CoordinatedActivitiesRules.ContinueCoordinatedActivity(sent, withdrawn ? ActivityNeed.Ended : ActivityNeed.Owed, Observe(sent, "failed", completedAt + 10,
            new ActivityTaskBinding.Bound(delivery.Id, delivery.Step with { Target = "split-real-stack", Count = 1 })))!;
        Assert.NotNull(retry);
        var recovered = Continue(journal, continuation, sent, retry, completedAt + 10, "recovery", ct, "failed");
        Assert.True(PawnActivities.SameOrder(Assert.Single(retry.Orders),
            Assert.Single(Send(journal, recovered, completedAt + 10, ct).Orders)));
        if (withdrawn)
        {
            var released = Assert.Single(retry.Orders);
            Assert.True(released.Release);
            journal.ReviewRounds(new RoundsRequest { Revision = refreshed.Review.Revision, Current = Root,
                Tick = new(completedAt + 20), Enabled = true, Policy = RoundsRules.DefaultRoundsPolicy(),
                Facts = facts with { Tick = new(completedAt + 20), CoupledWorkActive = false,
                    WorkResources = Known<EquatableArray<WorkResourcePawn>>([new(released.Pawn, "", 0, "", "", "", 0) { LocalHour = 6 }]) } }, ct);
            Assert.True(journal.LoadPlan(recovered.Id, ct).Retired); // Accepted release must not become a permanent hold.
        }
        else
        {
            Assert.Equal("split-real-stack", Assert.Single(retry.Orders).Steps[0].Target);
            Assert.Equal(1, retry.Orders[0].Steps[0].Count);
            Assert.Equal(2, Assert.Single(retry.Tasks, t => t.Step.Kind == PawnActivityKind.Deliver).Step.Count);
            Assert.Equal(delivery.Step.Deadline, retry.Orders[0].Steps[0].Deadline);
        }
    }
}
