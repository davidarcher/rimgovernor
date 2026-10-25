using RimGovernor.Brain.Policy;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// Skill and health goals (#3508): a skill goal's status is the pawn's skill read and the work planner hands the pawn
/// the skill's work (and the reading policy textbooks); a health goal's status is the pawn's hediff read and the surgery
/// planner queues the recipe, or wants its part while none is stocked.
/// </summary>
public sealed class SkillHealthGoalTests
{
    private static GoalRecord Goal(GoalKind kind, string pawn, string qualifier, int count = 0) =>
        new() { Id = 1, Kind = kind, Target = pawn, Qualifier = qualifier, Count = count };

    private static WorkPawn Pawn(string id, int cooking, int building, bool cookingDisabled = false) => PawnFixtures.Worker(id) with
    {
        Schedule = EquatableArray.From(Enumerable.Repeat(PawnRules.ScheduleAnything, 24)),
        Skills = [
            new() { Name = "Cooking", Level = cooking, Disabled = cookingDisabled }, new() { Name = "Construction", Level = building }],
        Work = new PawnWorkTable.Works(true, [
            new() { Work = WorkType.Cooking, Skill = "Cooking", Order = 10, Priority = 3 },
            new() { Work = WorkType.Construction, Skill = "Construction", Order = 5, Priority = 3 }]),
    };

    private static GoalsFacts SkillFacts(GoalRecord goal, params WorkPawn[] pawns) =>
        new() { Goals = [goal], Pawns = new WorkPawnsRead(EquatableArray.From(pawns)) };

    private static int Priority(WorkDecision decision, string pawn, WorkType work) =>
        decision.Assignments.Single(a => a.Pawn.Value == pawn).Priorities.Single(p => p.Work == work).Priority;

    [Fact]
    public void ASkillGoalsStatusIsThePawnsSkillRead()
    {
        var goal = Goal(GoalKind.Skill, "trainee", "Cooking", 10);
        var open = GoalRules.Evaluate(goal, SkillFacts(goal, Pawn("trainee", 6, 18)));
        Assert.Equal((GoalState.Pursuing, AdminFamily.WorkAssignments, 4.0), (open.State, open.Owner, ((GoalDeficit.Measured)open.Deficit).Amount));
        Assert.Equal(GoalState.Met, GoalRules.Evaluate(goal, SkillFacts(goal, Pawn("trainee", 12, 18))).State);

        Assert.Equal((GoalState.NoMethod, "not_a_colonist"), Reason(GoalRules.Evaluate(goal, new GoalsFacts { Goals = [goal] })));
        Assert.Equal((GoalState.NoMethod, "not_a_colonist"), Reason(GoalRules.Evaluate(goal, SkillFacts(goal, Pawn("other", 6, 18)))));
        Assert.Equal((GoalState.NoMethod, "unknown_skill"), Reason(GoalRules.Evaluate(goal with { Qualifier = "Taming" }, SkillFacts(goal, Pawn("trainee", 6, 18)))));
        Assert.Equal((GoalState.NoMethod, "incapable"), Reason(GoalRules.Evaluate(goal, SkillFacts(goal, Pawn("trainee", 6, 18, cookingDisabled: true)))));
    }

    [Fact]
    public void TheWorkPlannerHandsTheTraineeTheSkillsWorkBesideTheFitterOwner()
    {
        var chef = Pawn("chef", 15, 4);
        var trainee = Pawn("trainee", 6, 18);
        var plain = WorkRules.PlanWork([chef, trainee], [], new());
        Assert.Equal(1, Priority(plain, "chef", WorkType.Cooking));
        Assert.NotEqual(1, Priority(plain, "trainee", WorkType.Cooking));

        var goal = Goal(GoalKind.Skill, "trainee", "Cooking", 10);
        var facts = GoalRules.Stamp(new RoundsFacts { Wealth = new(0, 0) }, GoalRules.DemandOf(SkillFacts(goal, chef, trainee)));
        Assert.Equal([new SkillGoal(new PawnId("trainee"), "Cooking")], facts.GoalTraining.Items);
        var trained = WorkRules.PlanWork([chef, trainee], [], new() { Training = facts.GoalTraining.Items });
        Assert.Equal(1, Priority(trained, "chef", WorkType.Cooking));
        Assert.Equal(1, Priority(trained, "trainee", WorkType.Cooking));
        Assert.Equal(facts.GoalTraining.Items, WorkRules.RoundsWorkDemand(facts, false).Training);

        // A met goal places no demand.
        Assert.Empty(GoalRules.DemandOf(SkillFacts(goal, chef, Pawn("trainee", 10, 18))).Training);
    }

    [Fact]
    public void ALearnerWithoutAPassionIsAllowedTextbooks()
    {
        var pawn = Pawn("trainee", 6, 18) with { PolicyInputs = PawnFixtures.Inputs(readingPolicyId: "rp1") };
        OwnedName[] names = [new() { PawnId = pawn.Id, ShortName = "Tess", ThingId = 1 }];
        ReadingPolicyEntry[] policies = [new() { Id = "rp1", Label = "Tess", Allowed = ["Novel"] }];
        Book[] books = [new("Novel", BookKind.Novel), new("Textbook", BookKind.Textbook)];
        Assert.Empty(ReadingPolicyRules.ReadingPolicyChanges([pawn], names, policies, books, []));

        var change = Assert.Single(ReadingPolicyRules.ReadingPolicyChanges([pawn], names, policies, books, [new SkillGoal(pawn.Id, "Cooking")]));
        Assert.Equal(["Novel", "Textbook"], change.Write!.Definitions.Items);
        Assert.Null(change.Assign);
    }

    private const string Recipe = "InstallBionicEye";
    private const int EyePart = 12;

    private static CareOperation Install(bool stocked) => CareFixtures.Operation(SurgeryKind.Install, Recipe, EyePart, "Eye", stocked, "BionicEye");

    private static CarePawn Patient(bool stocked, params string[] hediffs) =>
        CareFixtures.Patient("colonist", hediffs.Select(CareFixtures.Condition), [Install(stocked)]);

    private static readonly SleepingObservation Hospital = new() { Beds = [new SleepingBed { Id = "bed", Humanlike = true, Medical = true }] };

    private static GoalsFacts HealthFacts(GoalRecord goal, CarePawn patient, bool bed = true) => new()
    {
        Goals = [goal],
        Rounds = new RoundsFacts
        {
            Recipes = new RecipeFacts { AddedHediffs = new Dictionary<string, string> { [Recipe] = "BionicEye" } },
            MedicalPawns = EquatableArray.From([patient]),
            Sleeping = bed ? Hospital : new SleepingObservation(),
        },
    };

    [Fact]
    public void AHealthGoalsStatusIsThePawnsHediffRead()
    {
        var goal = Goal(GoalKind.Health, "colonist", Recipe);
        var open = GoalRules.Evaluate(goal, HealthFacts(goal, Patient(true)));
        Assert.Equal((GoalState.Pursuing, MedicalRules.SurgeryNeed), (open.State, open.Owner));
        Assert.Equal(GoalState.Met, GoalRules.Evaluate(goal, HealthFacts(goal, Patient(true, "BionicEye"))).State);

        Assert.Equal((GoalState.Blocked, "no_hospital_bed"), Reason(GoalRules.Evaluate(goal, HealthFacts(goal, Patient(true), bed: false))));
        Assert.Equal((GoalState.NoMethod, "unknown_recipe"), Reason(GoalRules.Evaluate(goal with { Qualifier = "Nope" }, HealthFacts(goal, Patient(true)))));
        Assert.Equal((GoalState.NoMethod, "unavailable"), Reason(GoalRules.Evaluate(goal, HealthFacts(goal, Patient(true) with { Operations = [] }))));
        Assert.Equal((GoalState.NoMethod, "not_a_colonist"), Reason(GoalRules.Evaluate(goal with { Target = "other" }, HealthFacts(goal, Patient(true)))));
        var unread = HealthFacts(goal, Patient(true));
    }

    [Fact]
    public void TheSurgeryPlannerQueuesTheGoalsImplantOrWantsItsPart()
    {
        var goal = Goal(GoalKind.Health, "colonist", Recipe);
        var stocked = HealthFacts(goal, Patient(true));
        var facts = GoalRules.Stamp(stocked.Rounds, GoalRules.DemandOf(stocked));
        Assert.Equal([new SurgeryGoal(new PawnId("colonist"), Recipe)], facts.GoalSurgeries.Items);
        var ctx = facts.SurgeryContextOf() with { HospitalBed = true };

        // A tier-0 implant is never an elective of its own; the goal queues it.
        Assert.Empty(SurgeryRules.SelectSurgery(facts.MedicalPawns, null, ctx with { Goals = [] }).Queue);
        var choice = Assert.Single(SurgeryRules.SelectSurgery(facts.MedicalPawns, null, ctx).Queue);
        Assert.Equal((Recipe, EyePart, true), (choice.Recipe, choice.Part, choice.Elective));
        Assert.True(SurgeryRules.ElectiveSurgeryOwed(facts.MedicalPawns, ctx, null));

        // Unstocked, the goal wants its part for the bill and trade paths.
        var missing = EquatableArray.From([Patient(false)]);
        Assert.Empty(SurgeryRules.SelectSurgery(missing, null, ctx).Queue);
        var want = Assert.Single(SurgeryRules.ChosenElectives(missing, ctx));
        Assert.Equal((EyePart, SurgeryWantReason.PartShort), (want.Part, want.Reason));
        Assert.Equal([new Resource("BionicEye")], want.Items);
        Assert.Empty(SurgeryRules.ChosenElectives(missing, ctx with { Goals = [] }));

        // A met goal places no demand.
        Assert.Empty(GoalRules.DemandOf(HealthFacts(goal, Patient(true, "BionicEye"))).Surgeries);
    }

    private static (GoalState, string) Reason(GoalStatus s) => (s.State, s.Reason);
}
