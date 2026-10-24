using RimGovernor.Brain.Policy;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// A role that disables work is not given to a pawn whose selected work it would disable (#3644): the pawn's
/// priority-1 rows, its live coupled orders and its pending priority writes. Another pawn takes the role.
/// </summary>
public sealed class IdeologyRoleWorkTests
{
    private const string Ours = "Ideo_ours";

    // A ProductionSpecialist-like role: Crafting at 4, disabling ManualDumb's Hauling and Cleaning.
    private static Ideoligion Ideo() => new Ideoligion(
        new IdeologyDefs
        {
            Roles = new Dictionary<string, RoleDef>
            {
                ["Specialist"] = new(1, [new([new("Crafting", 4)])]) { DisabledWork = [WorkType.Hauling, WorkType.Cleaning] },
            },
        },
        new IdeoligionFacts { IdeoId = Ours, Development = IdeoDevelopment.Fixed, Roles = [new("role-1", "Specialist", true, [])] });

    private static WorkPawn Colonist(string id, int crafting, params WorkPriority[] work) => PawnFixtures.Worker(id) with
    {
        Skills = [new() { Name = "Crafting", Level = crafting }],
        Work = new PawnWorkTable.Works(true, EquatableArray.From(work)),
        PolicyInputs = PawnFixtures.Inputs(Ours, 0.5),
    };

    private static EquatableArray<WorkPawn> Pawns(params WorkPawn[] rows) => EquatableArray.From(rows);

    private static readonly WorkCommitments NoneSelected = new WorkCommitments.Listed(new Dictionary<PawnId, PawnCommitment>());

    private static PawnId Assigned(EquatableArray<RoleAssignment> owed) => Assert.Single(owed).Pawn;

    private static WorkActionCensus Delivery(string pawn)
    {
        var order = new PawnActivityOrder(pawn, "haul-1", "coupled-1", 0, 1, false,
            [new PawnActivityStep(PawnActivityKind.Deliver, "Thing_1", "", "", 5, 1000) { Destination = new Cell(3, 3) }]);
        var action = DomainAction.NewPawnActivitiesAction(new ActionId("p-0"), new PawnActivities(1, [order]));
        return new([new WorkAction(new PlanId("p"), action, new ProgressView { Action = action.Id, Stage = Stage.Completed })]);
    }

    private static WorkType? NoGiver(string name) => null;

    [Fact]
    public void PriorityOneWorkTheRoleDisablesSkipsThePawn()
    {
        var hauler = Colonist("a", 10, new WorkPriority { Work = WorkType.Hauling, Priority = 1 });
        var crafter = Colonist("b", 6, new WorkPriority { Work = WorkType.Hauling, Priority = 3 }, new WorkPriority { Work = WorkType.Crafting, Priority = 1 });
        Assert.Equal(new PawnId("b"), Assigned(IdeologyRolesRules.RoleAssignments(Ideo(), Pawns(hauler, crafter), NoneSelected)));
    }

    [Fact]
    public void LiveCoupledHaulSkipsThePawn()
    {
        var selected = IdeologyRolesRules.SelectedWork(Delivery("a"), [], NoGiver);
        Assert.Equal(new PawnId("b"), Assigned(IdeologyRolesRules.RoleAssignments(Ideo(), Pawns(Colonist("a", 10), Colonist("b", 6)), selected)));
        // Without the order the better crafter takes the role.
        Assert.Equal(new PawnId("a"), Assigned(IdeologyRolesRules.RoleAssignments(Ideo(), Pawns(Colonist("a", 10), Colonist("b", 6)), NoneSelected)));
    }

    [Fact]
    public void PendingNonzeroRowOrUnreadGiverSkipsThePawn()
    {
        var pending = WorkAssignment.NewWorkAssignment(new PawnId("a"), [new WorkSetting("Cleaning", 3)]);
        var selected = IdeologyRolesRules.SelectedWork(new([]), [pending], NoGiver);
        Assert.Equal(new PawnId("b"), Assigned(IdeologyRolesRules.RoleAssignments(Ideo(), Pawns(Colonist("a", 10), Colonist("b", 6)), selected)));

        var order = new PawnActivityOrder("a", "cut-1", "coupled-1", 0, 1, false,
            [new PawnActivityStep(PawnActivityKind.Work, "Plant_1", "PlantsCut", "CutPlant", 1, 1000)]);
        var action = DomainAction.NewPawnActivitiesAction(new ActionId("p-0"), new PawnActivities(1, [order]));
        var unread = IdeologyRolesRules.SelectedWork(new([new WorkAction(new PlanId("p"), action, new ProgressView { Action = action.Id, Stage = Stage.Pending })]), [], NoGiver);
        Assert.IsType<PawnCommitment.GiverMissing>(Assert.IsType<WorkCommitments.Listed>(unread).ByPawn[new PawnId("a")]);
        Assert.Equal(new PawnId("b"), Assigned(IdeologyRolesRules.RoleAssignments(Ideo(), Pawns(Colonist("a", 10), Colonist("b", 6)), unread)));
    }

    [Fact]
    public void ATruncatedActionCensusGivesNoWorkDisablingRoleToAnyone()
    {
        var truncated = IdeologyRolesRules.SelectedWork(new([]) { Truncated = true }, [], NoGiver);
        Assert.IsType<WorkCommitments.Truncated>(truncated);
        Assert.Empty(IdeologyRolesRules.RoleAssignments(Ideo(), Pawns(Colonist("a", 10), Colonist("b", 6)), truncated));
    }
}
