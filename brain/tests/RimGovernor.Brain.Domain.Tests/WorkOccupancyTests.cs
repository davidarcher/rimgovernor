using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class WorkOccupancyTests
{
    private static WorkPawn Pawn(string id, int cooking = 10, int building = 10) => PawnFixtures.Worker(id) with
    { Work = PawnFixtures.WorkTable(true, [
            new() { Observed = new RimGovernor.Brain.GameFacts.Observations.WorkSetting { DefName = "Cooking", Priority = 3, Disabled = false }, Skill = "Cooking", Order = 10 },
            new() { Observed = new RimGovernor.Brain.GameFacts.Observations.WorkSetting { DefName = "Construction", Priority = 3, Disabled = false }, Skill = "Construction", Order = 5 }]), Observed = PawnFixtures.Row(id) with { Biography = PawnFixtures.Row(id).Biography with { Skills = [new() { Passion = RimGovernor.Brain.GameFacts.Observations.Passion.None, DefName = "Cooking", Level = cooking }, new() { Passion = RimGovernor.Brain.GameFacts.Observations.Passion.None, DefName = "Construction", Level = building }] }, Settings = PawnFixtures.Row(id).Settings with { Schedule = EquatableArray.From((EquatableArray.From(Enumerable.Repeat(PawnRules.ScheduleAnything, 24))).Select((assignment, hour) => new RimGovernor.Brain.GameFacts.Observations.TimetableSlot { Hour = (uint)hour, AssignmentDefName = assignment })) } } };
    private static WorkPawn WithRows(WorkPawn pawn, IEnumerable<WorkPriority> rows) => pawn with { Work = PawnFixtures.WorkTable(true, EquatableArray.From(rows)) };
    private static WorkPawn Cooking(WorkPawn pawn) => pawn with { Observed = (pawn).Observed with { Job = new RimGovernor.Brain.GameFacts.Observations.JobEvidence { DefName = "TestCook", WorkTypeDefName = "Cooking", TargetA = new() { Target = new RimGovernor.Brain.GameFacts.Common.Unavailable { Reason = RimGovernor.Brain.GameFacts.Common.UnavailableReason.NotApplicable } } } } };
    /// <summary>The capacity forecast over the pawn's own roster row and current job.</summary>
    private static ForecastCapacityInput Input(WorkPawn pawn, ForecastWorkRemaining? remaining = null) => new()
    {
        Pawn = new() { Id = pawn.Id, State = pawn.Available && pawn.Applies ? ForecastPawnCapacityState.Available : ForecastPawnCapacityState.Unavailable, Schedule = pawn.Schedule },
        CurrentWork = ForecastCurrentWork.ForecastCurrentWorkOf(pawn, remaining),
        Location = "home", HorizonTicks = 100, LocalHour = 12.0, OpportunityCostPerTick = 1,
        Occupancy = new ForecastProtection.Read([]),
    };
    private static int Priority(WorkDecision decision, string pawn, WorkType work) =>
        decision.Assignments.Single(a => a.Pawn.Value == pawn).Priorities.Single(p => p.Work == work).Priority;
    private static WorkDecision Plan(params WorkPawn[] pawns) => WorkRules.PlanWork(pawns, [], new());
    private static WorkPawn Apply(WorkPawn pawn, WorkDecision decision) => pawn with
    {
        Work = PawnFixtures.WorkTable(true, EquatableArray.From(pawn.Work.Priorities().Select(w => w with { Observed = w.Observed with { Priority = Priority(decision, pawn.Id.Value, w.Work) } }))),
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
        var repeated = Plan(Apply(cook, decision), Apply(free, decision));
        Assert.True(repeated.Matches);
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
        Assert.Equal(2, Priority(busy, "both", WorkType.Construction));
        // Completion and cancellation both arrive as a fresh job read. No saved hold needs clearing.
        var idle = Plan(Apply(pawn, busy) with { Observed = pawn.Observed with { Job = new RimGovernor.Brain.GameFacts.Observations.JobEvidence() } });
        Assert.Equal(1, Priority(idle, "both", WorkType.Construction));
    }

    [Fact]
    public void ChangedRequirementsOrDisabledReadbackRemoveOngoingRoleProtection()
    {
        var pawn = Cooking(Pawn("both", cooking: 6));
        var raised = WorkRules.PlanWork([pawn], [new() { Work = WorkType.Cooking, Skill = "Cooking", Minimum = 8 }], new());
        Assert.Equal(1, Priority(raised, "both", WorkType.Construction));
        Assert.Equal(0, Priority(raised, "both", WorkType.Cooking));
        var disabled = WithRows(pawn, pawn.Work.Priorities().Select(w =>
            w.Work == WorkType.Cooking ? w with { Observed = w.Observed with { Priority = 0 } } : w));
        Assert.Equal(1, Priority(Plan(disabled), "both", WorkType.Construction));
        var incapable = WithRows(pawn, pawn.Work.Priorities().Select(w =>
            w.Work == WorkType.Cooking ? w with { Observed = w.Observed with { Disabled = true } } : w));
        Assert.Equal(1, Priority(Plan(incapable), "both", WorkType.Construction));
    }

    [Fact]
    public void AnUnavailablePawnHasNoTimeAndDoesNotBlockAnIndependentWorker()
    {
        var away = Pawn("away") with { Observed = (Pawn("away")).Observed with { Downed = true } };
        var free = Pawn("free");
        var decision = Plan(away, free);
        Assert.Contains(decision.Assignments, a => a.Pawn == free.Id);
        Assert.Equal(100, ForecastCapacity.ForecastPawnCapacityOf(Input(free)).Capacity.Windows.ReadWindows().Sum(w => w.End - w.Start));
        Assert.Equal(0, ForecastCapacity.ForecastPawnCapacityOf(Input(away)).Capacity.Windows.ReadWindows().Sum(w => w.End - w.Start));
    }

    [Fact]
    public void RosterAndForecastAgreeOnIdleAndOngoingWorkWithoutInventingDuration()
    {
        var pawn = Pawn("pawn") with { Observed = (Pawn("pawn")).Observed with { Job = new RimGovernor.Brain.GameFacts.Observations.JobEvidence { DefName = "Wait_Wander", TargetA = new() { Target = new RimGovernor.Brain.GameFacts.Common.Unavailable { Reason = RimGovernor.Brain.GameFacts.Common.UnavailableReason.NotApplicable } } } } };
        Assert.Equal(100, ForecastCapacity.ForecastPawnCapacityOf(Input(pawn)).Capacity.Windows.ReadWindows().Sum(w => w.End - w.Start));
        Assert.Contains("unknown_current_job_remaining", ForecastCapacity.ForecastPawnCapacityOf(Input(Cooking(pawn))).Findings);
        Assert.Equal(60, ForecastCapacity.ForecastPawnCapacityOf(Input(Cooking(pawn), new ForecastWorkRemaining.Timed(40.0))).Capacity.Windows.ReadWindows().Sum(w => w.End - w.Start));
        Assert.Throws<DomainException>(() => ForecastCurrentWork.ForecastCurrentWorkOf(pawn, new ForecastWorkRemaining.Timed(-1.0)));
    }

    // Interruptibility does not erase useful native work or provide a remaining-duration estimate.
    [Fact]
    public void UsefulNativeWorkIsOccupiedAndKeepsItsRoleWithoutInventingDuration()
    {
        var cook = Pawn("cook", building: 20) with { Observed = (Pawn("cook", building: 20)).Observed with { Job = new RimGovernor.Brain.GameFacts.Observations.JobEvidence { DefName = "DoBill", WorkTypeDefName = "Cooking", TargetA = new() { Target = new RimGovernor.Brain.GameFacts.Common.Unavailable { Reason = RimGovernor.Brain.GameFacts.Common.UnavailableReason.NotApplicable } } } } };
        var current = ForecastCurrentWork.ForecastCurrentWorkOf(cook);
        Assert.True(current.Occupied);
        Assert.Equal(WorkType.Cooking, current.Work);
        Assert.IsType<ForecastWorkRemaining.Untimed>(current.Remaining);
        Assert.False(ConstructionHelpersRules.IdleJob(cook.Job));
        Assert.IsType<ForecastCapacityWindows.Unread>(ForecastCapacity.ForecastPawnCapacityOf(Input(cook)).Capacity.Windows);
        var decision = Plan(cook, Pawn("free", cooking: 5));
        Assert.Equal(1, Priority(decision, "cook", WorkType.Cooking));
        Assert.NotEqual(1, Priority(decision, "cook", WorkType.Construction));
    }

    [Fact]
    public void ConstructionSkillOrderingStillWinsAmongEligibleBuilders()
    {
        var working = Pawn("working", building: 6) with { Observed = (Pawn("working", building: 6)).Observed with { Job = new RimGovernor.Brain.GameFacts.Observations.JobEvidence { DefName = "TestBuild", WorkTypeDefName = "Construction", TargetA = new() { Target = new RimGovernor.Brain.GameFacts.Common.Unavailable { Reason = RimGovernor.Brain.GameFacts.Common.UnavailableReason.NotApplicable } } } } };
        var skilled = Pawn("skilled", building: 15);
        var decision = Plan(working, skilled);
        Assert.Equal(1, Priority(decision, "skilled", WorkType.Construction));
        Assert.NotEqual(1, Priority(decision, "working", WorkType.Construction));
    }

    [Fact]
    public void HigherNativePriorityAndMedicalRestCanOverrideOngoingWork()
    {
        var pawn = Cooking(Pawn("doctor"));
        pawn = pawn with
        { Work = PawnFixtures.WorkTable(true, [.. pawn.Work.Priorities(),
                new() { Observed = new RimGovernor.Brain.GameFacts.Observations.WorkSetting { DefName = "Doctor", Priority = 3, Disabled = false }, Skill = "Medicine", Order = 20 }]), Observed = pawn.Observed with { Biography = pawn.Observed.Biography with { Skills = [.. pawn.Skills, new() { Passion = RimGovernor.Brain.GameFacts.Observations.Passion.None, DefName = "Medicine", Level = 10 }] } } };
        Assert.Equal(1, Priority(Plan(pawn), "doctor", WorkType.Doctor));
        var rest = WorkRules.PlanWork([pawn], [], new() { Resting = [new(pawn.Id, ["TestDisease"])] });
        Assert.Equal(0, Priority(rest, "doctor", WorkType.Cooking));
        Assert.Equal(0, Priority(rest, "doctor", WorkType.Construction));
    }

    /// <summary>Task 5.5: ordinary care and hauling run through native work settings, not exact orders. The colony always owns
    /// Doctor (the capable medic at priority 1, so native WorkGiver_Tend serves an ordinary tend), and every capable pawn keeps
    /// Hauling enabled below skilled work at 4, rising to 3 under an observed haul backlog, so native storage hauling and bill
    /// ingredient gathering are reachable without a stall read.</summary>
    [Fact]
    public void OrdinaryCareAndHaulingAreReachableThroughNativeWorkSettings()
    {
        var pawn = Pawn("medic");
        pawn = pawn with
        {
            Work = PawnFixtures.WorkTable(true, [.. pawn.Work.Priorities(),
                new() { Observed = new RimGovernor.Brain.GameFacts.Observations.WorkSetting { DefName = "Doctor", Priority = 0, Disabled = false }, Skill = "Medicine", Order = 20 },
                new() { Observed = new RimGovernor.Brain.GameFacts.Observations.WorkSetting { DefName = "Hauling", Priority = 0, Disabled = false }, Skill = "", Order = 30 }]),
            Observed = pawn.Observed with { Biography = pawn.Observed.Biography with { Skills = [.. pawn.Skills,
                new() { Passion = RimGovernor.Brain.GameFacts.Observations.Passion.None, DefName = "Medicine", Level = 10 }] } },
        };
        var ordinary = Plan(pawn);
        Assert.Equal(1, Priority(ordinary, "medic", WorkType.Doctor));
        Assert.Equal(4, Priority(ordinary, "medic", WorkType.Hauling));
        Assert.Equal(3, Priority(WorkRules.PlanWork([pawn], [], new() { HaulBacklog = true }), "medic", WorkType.Hauling));
    }
}
