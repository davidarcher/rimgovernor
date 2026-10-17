using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class WorkOccupancyTests
{
    private static WorkPawn Pawn(string id, int cooking = 10, int building = 10) => new()
    {
        Id = new RimGovernor.Brain.Domain.PawnId(id), Available = Known(true), Applies = Known(true),
        Manual = Known(true), Ranged = Known(false), Job = Known(new PawnJob()),
        Schedule = Known(EquatableArray.From(Enumerable.Repeat(PawnRules.ScheduleAnything, 24))),
        Skills = Known<EquatableArray<WorkSkill>>([
            new() { Name = "Cooking", Level = cooking }, new() { Name = "Construction", Level = building }]),
        Work = Known<EquatableArray<WorkPriority>>([
            new() { Work = WorkType.Cooking, Skill = "Cooking", Order = 10, Priority = 3 },
            new() { Work = WorkType.Construction, Skill = "Construction", Order = 5, Priority = 3 }]),
    };
    private static WorkPawn Cooking(WorkPawn pawn) => pawn with { Job = Known(new PawnJob { Def = "TestCook", Work = WorkType.Cooking }) };
    private static int Priority(WorkDecision decision, string pawn, WorkType work) =>
        decision.Assignments.Single(a => a.Pawn.Value == pawn).Priorities.Single(p => p.Work == work).Priority;
    private static WorkDecision Plan(params WorkPawn[] pawns) => WorkRules.PlanWork(pawns, [], new());
    private static WorkPawn Apply(WorkPawn pawn, WorkDecision decision) => pawn with
    {
        Work = Known(EquatableArray.From(pawn.Work.Value.Select(w => w with { Priority = Priority(decision, pawn.Id.Value, w.Work) }))),
    };

    [Fact]
    public void BusyCookIsNotAlsoPrimaryBuilderAndFreeWorkerReceivesTheRole()
    {
        var cook = Cooking(Pawn("cook", building: 20));
        var free = Pawn("free", cooking: 5);
        var decision = Plan(cook, free);
        Assert.Equal(1, Priority(decision, "cook", WorkType.Cooking));
        Assert.NotEqual(1, Priority(decision, "cook", WorkType.Construction));
        Assert.Equal(1, Priority(decision, "free", WorkType.Construction));
        Assert.Equal(2, decision.Coverage.Single(c => c.Work == WorkType.Construction).Capable);
        Assert.True(decision.Capacity.Value);
        var repeated = Plan(Apply(cook, decision), Apply(free, decision));
        Assert.True(repeated.Matches.Value);
        Assert.Equal(1, Priority(repeated, "free", WorkType.Construction));
    }

    [Fact]
    public void OneBusyPawnLeavesOtherPrimaryRoleUncoveredButEnabledAndReleasesAfterJobEnds()
    {
        var pawn = Cooking(Pawn("both"));
        var busy = Plan(pawn);
        var building = busy.Coverage.Single(c => c.Work == WorkType.Construction);
        Assert.Equal(1, building.Capable);
        Assert.Equal(0, building.Owners);
        Assert.False(busy.Capacity.Value);
        Assert.Equal(2, Priority(busy, "both", WorkType.Construction));
        // Completion and cancellation both arrive as a fresh job read. No saved hold needs clearing.
        var idle = Plan(Apply(pawn, busy) with { Job = Known(new PawnJob()) });
        Assert.Equal(1, Priority(idle, "both", WorkType.Construction));
    }

    [Fact]
    public void ChangedRequirementsOrDisabledReadbackRemoveOngoingRoleProtection()
    {
        var pawn = Cooking(Pawn("both", cooking: 6));
        var raised = WorkRules.PlanWork([pawn], [new() { Work = WorkType.Cooking, Skill = "Cooking", Minimum = 8 }], new());
        Assert.Equal(1, Priority(raised, "both", WorkType.Construction));
        Assert.Equal(0, Priority(raised, "both", WorkType.Cooking));
        var disabled = pawn with { Work = Known(EquatableArray.From(pawn.Work.Value.Select(w =>
            w.Work == WorkType.Cooking ? w with { Priority = 0 } : w))) };
        Assert.Equal(1, Priority(Plan(disabled), "both", WorkType.Construction));
        var incapable = pawn with { Work = Known(EquatableArray.From(pawn.Work.Value.Select(w =>
            w.Work == WorkType.Cooking ? w with { Disabled = true } : w))) };
        Assert.Equal(1, Priority(Plan(incapable), "both", WorkType.Construction));
    }

    [Fact]
    public void UnknownJobDoesNotInventTimeOrBlockIndependentWorker()
    {
        var unknown = Pawn("unknown") with { Job = Unknown<PawnJob>() };
        var free = Pawn("free");
        var decision = Plan(unknown, free);
        Assert.Contains(decision.Assignments, a => a.Pawn == free.Id);
        var input = new ForecastCapacityInput
        {
            Pawn = unknown, Location = "home", HorizonTicks = 100, LocalHour = Known(12.0),
            Occupancy = Known(default(EquatableArray<ForecastOccupancy>)),
        };
        Assert.False(ForecastCapacity.ForecastPawnCapacityOf(input).FreeTicks.IsKnown);
        Assert.Equal(100, ForecastCapacity.ForecastPawnCapacityOf(input with { Pawn = free }).FreeTicks.Value);
        Assert.Equal(0, ForecastCapacity.ForecastPawnCapacityOf(input with { Pawn = free with { Available = Known(false) } }).FreeTicks.Value);
    }

    [Fact]
    public void RosterAndForecastAgreeOnIdleAndOngoingWorkWithoutInventingDuration()
    {
        var pawn = Pawn("pawn") with { Job = Known(new PawnJob { Def = "Wait_Wander" }) };
        var input = new ForecastCapacityInput
        {
            Pawn = pawn, Location = "home", HorizonTicks = 100, LocalHour = Known(12.0),
            Occupancy = Known(default(EquatableArray<ForecastOccupancy>)),
        };
        Assert.Equal(100, ForecastCapacity.ForecastPawnCapacityOf(input).FreeTicks.Value);
        var busy = input with { Pawn = Cooking(pawn) };
        Assert.Contains("unknown_current_job_remaining", ForecastCapacity.ForecastPawnCapacityOf(busy).Findings);
        Assert.Equal(60, ForecastCapacity.ForecastPawnCapacityOf(busy with { CurrentJobRemainingTicks = Known(40.0) }).FreeTicks.Value);
        Assert.Throws<DomainException>(() => ForecastCurrentWork.ForecastCurrentWorkOf(pawn, Known(-1.0)));
    }

    [Fact]
    public void ConstructionSkillOrderingStillWinsAmongEligibleBuilders()
    {
        var working = Pawn("working", building: 6) with { Job = Known(new PawnJob { Def = "TestBuild", Work = WorkType.Construction }) };
        var skilled = Pawn("skilled", building: 15);
        var decision = Plan(working, skilled);
        Assert.Equal(1, Priority(decision, "skilled", WorkType.Construction));
        Assert.NotEqual(1, Priority(decision, "working", WorkType.Construction));
    }

    [Fact]
    public void CoupledClaimComingAndGoingDoesNotReprioritizeTheOtherPawns()
    {
        // #3348: a coupled activity under admin/work-assignments claims the cook and releases it on alternate reviews.
        // The Need's step admits a work Method whenever the plan has something to write and refuses field_unavailable
        // otherwise. The claim alone must not hand the claimed pawn's roles to the others and back.
        var pawns = new[] { Pawn("cook", cooking: 15, building: 5), Pawn("builder", cooking: 5, building: 15) };
        string Step(string[] claimed)
        {
            var decision = WorkRules.PlanWork(pawns, [], new() { CommittedPawns = EquatableArray.From(claimed) });
            var wrote = false;
            for (var i = 0; i < pawns.Length; i++)
            {
                var assignment = decision.Assignments.SingleOrDefault(a => a.Pawn == pawns[i].Id);
                if (assignment is null) continue;
                var (changed, ok) = WorkRules.WorkChanges(pawns[i], assignment);
                Assert.True(ok);
                if (changed.Count == 0) continue;
                Assert.DoesNotContain(pawns[i].Id.Value, claimed);
                pawns[i] = Apply(pawns[i], decision);
                wrote = true;
            }
            return wrote ? "admitted" : "field_unavailable";
        }
        Assert.Equal("admitted", Step([]));
        Assert.Equal("field_unavailable", Step([]));
        // Two reviews per claim state: before the fix each toggle admitted a write and the repeat refused it.
        var rounds = Enumerable.Range(0, 8).Select(i => Step(i / 2 % 2 == 0 ? ["cook"] : [])).ToList();
        Assert.All(rounds, r => Assert.Equal("field_unavailable", r));
        Assert.Equal(1, pawns[0].Work.Value.Single(w => w.Work == WorkType.Cooking).Priority);
        var claimed = WorkRules.PlanWork(pawns, [], new() { CommittedPawns = ["cook"] });
        Assert.True(claimed.Matches.Value);
        Assert.Equal(1, claimed.Coverage.Single(c => c.Work == WorkType.Cooking).Owners);
    }

    [Fact]
    public void HigherNativePriorityAndMedicalRestCanOverrideOngoingWork()
    {
        var pawn = Cooking(Pawn("doctor"));
        pawn = pawn with
        {
            Work = Known<EquatableArray<WorkPriority>>([.. pawn.Work.Value,
                new() { Work = WorkType.Doctor, Skill = "Medicine", Order = 20, Priority = 3 }]),
            Skills = Known<EquatableArray<WorkSkill>>([.. pawn.Skills.Value, new() { Name = "Medicine", Level = 10 }]),
        };
        Assert.Equal(1, Priority(Plan(pawn), "doctor", WorkType.Doctor));
        var rest = WorkRules.PlanWork([pawn], [], new() { Resting = [new(pawn.Id, ["TestDisease"])] });
        Assert.Equal(0, Priority(rest, "doctor", WorkType.Cooking));
        Assert.Equal(0, Priority(rest, "doctor", WorkType.Construction));
    }
}
