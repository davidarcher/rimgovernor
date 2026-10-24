using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>Pawns fight home fires natively (#3252): no concern owns fire, and work assignments keep Firefighter on.</summary>
public sealed class HomeFireTests
{
    [Fact]
    public void NoConcernOrImmediateReviewOwnsAHomeFire()
    {
        // The home-upkeep family derives only repairs and clearance; a home fire raises nothing that could hold development.
        var upkeep = new HomeUpkeepFamily().Derive(new HomeUpkeepFacts { Structures = Known<EquatableArray<UpkeepStructure>>([]), Clearance = Known<EquatableArray<ClearanceTarget>>([]) });
        Assert.Equal([HomeUpkeepFamily.Repairs, HomeUpkeepFamily.Clearance], upkeep.Select(n => n.Id));
    }

    [Fact]
    public void WorkAssignmentsPinFirefighterForEveryCapablePawn()
    {
        WorkPawn Pawn(string id, bool pyromaniac) => new()
        {
            Id = new PawnId(id), Available = Known(true), Applies = Known(true),
            Manual = Known(true), Ranged = Known(false), Job = Known(new PawnJob()),
            Schedule = Known(EquatableArray.From(Enumerable.Repeat(PawnRules.ScheduleAnything, 24))),
            Skills = Known<EquatableArray<WorkSkill>>([new() { Name = "Construction", Level = 8 }]),
            Work = Known<EquatableArray<WorkPriority>>([
                new() { Work = WorkType.Firefighter, Order = 1, Priority = 0, Disabled = pyromaniac },
                new() { Work = WorkType.Construction, Skill = "Construction", Order = 5, Priority = 3 }]),
        };
        var decision = WorkRules.PlanWork([Pawn("a", false), Pawn("b", false), Pawn("pyro", true)], [], new());
        int Firefighter(string pawn) => decision.Assignments.Single(a => a.Pawn.Value == pawn).Priorities.SingleOrDefault(p => p.Work == WorkType.Firefighter)?.Priority ?? 0;
        Assert.Equal(1, Firefighter("a"));
        Assert.Equal(1, Firefighter("b"));
        Assert.Equal(0, Firefighter("pyro"));
    }
}
