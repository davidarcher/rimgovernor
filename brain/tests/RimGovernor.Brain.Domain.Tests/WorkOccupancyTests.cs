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

    // #3670: a preemptible pawn's vanilla job leaves it free for the forecast, yet still protects its current role.
    [Fact]
    public void PreemptibleVanillaJobIsUnoccupiedButKeepsItsRole()
    {
        var cook = Pawn("cook", building: 20) with { Observed = (Pawn("cook", building: 20)).Observed with { Job = new RimGovernor.Brain.GameFacts.Observations.JobEvidence { DefName = "DoBill", WorkTypeDefName = "Cooking", Preemptible = true, TargetA = new() { Target = new RimGovernor.Brain.GameFacts.Common.Unavailable { Reason = RimGovernor.Brain.GameFacts.Common.UnavailableReason.NotApplicable } } } } };
        var current = ForecastCurrentWork.ForecastCurrentWorkOf(cook);
        Assert.False(current.Occupied);
        Assert.Equal(WorkType.Cooking, current.Work);
        Assert.Equal(0, Assert.IsType<ForecastWorkRemaining.Timed>(current.Remaining).Ticks);
        Assert.True(ConstructionHelpersRules.IdleJob(cook.Job));
        Assert.Equal(100, ForecastCapacity.ForecastPawnCapacityOf(Input(cook)).Capacity.Windows.ReadWindows().Sum(w => w.End - w.Start));
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
        Assert.Equal(1, pawns[0].Work.Priorities().Single(w => w.Work == WorkType.Cooking).Priority);
        var claimed = WorkRules.PlanWork(pawns, [], new() { CommittedPawns = ["cook"] });
        Assert.True(claimed.Matches);
        Assert.Equal(1, claimed.Coverage.Single(c => c.Work == WorkType.Cooking).Owners);
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
}
