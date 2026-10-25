using O = RimGovernor.Contracts.Wire.Observations;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>Pawns fight home fires natively (#3252): no concern owns fire, and work assignments keep Firefighter on.</summary>
public sealed class HomeFireTests
{
    [Fact]
    public void NoConcernOrImmediateReviewOwnsAHomeFire()
    {
        // The home-upkeep family derives only repairs and clearance; a home fire raises nothing that could hold development.
        var upkeep = new HomeUpkeepFamily().Derive(new HomeUpkeepFacts { Structures = [], Clearance = new ClearanceCensus([]) });
        Assert.Equal([HomeUpkeepFamily.Repairs, HomeUpkeepFamily.Clearance], upkeep.Select(n => n.Id));
    }

    [Fact]
    public void WorkAssignmentsPinFirefighterForEveryCapablePawn()
    {
        WorkPawn Pawn(string id, bool pyromaniac) => PawnFixtures.Worker(id) with
        { Work = PawnFixtures.WorkTable(true, [
                new() { Observed = new O.WorkSetting { DefName = "Firefighter", Priority = 0, Disabled = pyromaniac }, Order = 1 },
                new() { Observed = new O.WorkSetting { DefName = "Construction", Priority = 3, Disabled = false }, Skill = "Construction", Order = 5 }]), Observed = PawnFixtures.Row(id) with { Biography = PawnFixtures.Row(id).Biography with { Skills = [new() { Passion = O.Passion.None, DefName = "Construction", Level = 8 }] }, Settings = PawnFixtures.Row(id).Settings with { Schedule = EquatableArray.From((EquatableArray.From(Enumerable.Repeat(PawnRules.ScheduleAnything, 24))).Select((assignment, hour) => new O.TimetableSlot { Hour = (uint)hour, AssignmentDefName = assignment })) } } };
        var decision = WorkRules.PlanWork([Pawn("a", false), Pawn("b", false), Pawn("pyro", true)], [], new());
        int Firefighter(string pawn) => decision.Assignments.Single(a => a.Pawn.Value == pawn).Priorities.SingleOrDefault(p => p.Work == WorkType.Firefighter)?.Priority ?? 0;
        Assert.Equal(1, Firefighter("a"));
        Assert.Equal(1, Firefighter("b"));
        Assert.Equal(0, Firefighter("pyro"));
    }
}
