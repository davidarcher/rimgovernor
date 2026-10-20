using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimGovernor.Contracts.Wire.Observations;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>The brain's explicit idle (#3322): every controlled colonist the review selected nothing for holds standby.</summary>
public sealed class StandbyTests
{
    private static readonly Tick Now = new(1000);

    private static WorkPawnAvailability Pawn(string id, bool available = true, bool occupied = false) =>
        new(id, Known(available), Known(true), Known(occupied), default);

    private static WorkResourcePawn Native(string id, string assignment = "", long revision = 0) =>
        new(id, assignment, revision, assignment.Length == 0 ? "" : "started", "", "", 0);

    private static RoundsFacts Facts(WorkPawnAvailability[] pawns, WorkResourcePawn[] native, WorkAction[] actions)
    {
        var evidence = new WorkAdmissionEvidence { Tick = Now, Pawns = Known(EquatableArray.From(pawns)), WorkResources = Known(EquatableArray.From(native)) };
        return new RoundsFacts { Tick = Now, WorkView = WorkRules.WorkResourcesOf(evidence, default, actions) };
    }

    private static WorkAction Action(string plan, PawnActivities batch, Stage stage, ulong attempt = 0) =>
        new(new(plan), DomainAction.NewPawnActivitiesAction(new(plan + "-0"), batch),
            new ProgressView { Action = new(plan + "-0"), Stage = stage, Attempt = new AttemptId(attempt), Tick = Now });

    private static PawnActivities Work(string pawn, string method) =>
        new(7, [new(pawn, method + "/" + pawn, method, 0, 1, false, [new(PawnActivityKind.Work, "wall", "Repair", "Repair", 0, 5000)])]);

    [Fact]
    public void AnIdleUnassignedColonistGetsStandbyAndNoOtherDoes()
    {
        var facts = Facts(
            [Pawn("idle"), Pawn("drafted", available: false), Pawn("assigned"), Pawn("busy", occupied: true)],
            [Native("idle"), Native("drafted"), Native("assigned", "work-1/assigned", 1), Native("busy")], []);
        var census = new WorkActionCensus([]);

        Assert.Equal(["idle"], StandbyAssignmentsRules.StandbyPawnsOf(facts, census, new HashSet<string>()));

        var standby = StandbyAssignmentsRules.StandbyActivity("idle", 7, "standby-1");
        standby.Validate();
        var order = Assert.Single(standby.Orders);
        Assert.True(PawnActivities.IsStandby(order));
        Assert.Equal(0, order.ExpectedRevision); // Acquires an unassigned pawn, and replaces nothing that is not standby.
        Assert.Equal(PawnActivityKind.Standby, Assert.Single(order.Steps).Kind);
    }

    [Fact]
    public void SelectedOrInFlightWorkTakesThePawnInsteadOfStandby()
    {
        var pending = Action("work-2", Work("queued", "work-2"), Stage.Pending);
        var facts = Facts([Pawn("selected"), Pawn("queued"), Pawn("free")],
            [Native("selected"), Native("queued"), Native("free")], [pending]);
        var census = new WorkActionCensus([pending]);

        Assert.Equal(["free"], StandbyAssignmentsRules.StandbyPawnsOf(facts, census, new HashSet<string>(["selected"])));
    }

    [Fact]
    public void StandbyClaimsNothingSoSelectedWorkIsAdmittedOverIt()
    {
        var standby = Action("standby-1", StandbyAssignmentsRules.StandbyActivity("pawn", 7, "standby-1"), Stage.Completed, attempt: 1);
        var evidence = new WorkAdmissionEvidence { Tick = Now, WorkResources = Known(EquatableArray.From([Native("pawn", "standby-1/pawn", 1)])) };

        Assert.Empty(WorkRules.WorkClaimsOf(evidence, [standby]).Pawns);
        var held = MethodClaimsRules.MethodClaimsOf(evidence, [standby]);
        var candidate = MethodClaimsRules.MethodClaimsOf(evidence, [Action("work-3", Work("pawn", "work-3"), Stage.Pending)]);
        Assert.Null(MethodClaimsRules.MethodClaimOverlap(candidate, held));
    }

    [Fact]
    public void StandbyHoldsUntilWorkReplacesIt()
    {
        var batch = StandbyAssignmentsRules.StandbyActivity("pawn", 7, "standby-1");

        // No Concern, no deadline: a satisfied or unknown Need never releases standby into an unassigned hold.
        var held = ActivityContinuationRules.ReviewActivityOwnership(batch, Known(false), 900_000, [new("pawn", "standby-1/pawn", "standby-1", 1, "started")]);
        Assert.False(held.OwnershipEnded);
        Assert.Null(held.Release);

        // Selected work acquired the pawn from revision zero: the standby Method has ended.
        var replaced = ActivityContinuationRules.ReviewActivityOwnership(batch, Known(true), 1000, [new("pawn", "work-3/pawn", "work-3", 1, "offered")]);
        Assert.True(replaced.OwnershipEnded);
    }

    [Fact]
    public void StandbyIsTheOrdersOnlyTargetlessStep()
    {
        var step = StandbyAssignmentsRules.StandbyStep();
        Assert.Throws<DomainException>(() => new PawnActivities(7, [new("pawn", "a/pawn", "a", 0, 1, false, [step, step])]).Validate());
        Assert.Throws<DomainException>(() => new PawnActivities(7, [new("pawn", "a/pawn", "a", 0, 1, false, [step with { Target = "bed" }])]).Validate());
        Assert.False(PawnActivities.IsStandby(new("pawn", "a/pawn", "a", 1, 2, true, [])));
    }
}
