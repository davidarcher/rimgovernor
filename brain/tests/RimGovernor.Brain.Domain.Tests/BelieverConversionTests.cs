using RimGovernor.Brain.Policy;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// Believer goals (#3501): an unmet Believers goal makes the ideology family convert the lowest-certainty colonist of
/// another ideoligion through the moral guide's Convert ability, and biases a recruitable foreign prisoner to Convert.
/// </summary>
public sealed class BelieverConversionTests
{
    private const string Ours = "Ideo_ours";

    private static readonly GoalRecord Everyone = new() { Id = 4, Kind = GoalKind.Believers };

    private static WorkPawn Colonist(string id, string ideo, double certainty, RimGovernor.Contracts.Wire.Defs.GuestStatus? guest = null, bool available = true, double age = 30) => PawnFixtures.Worker(id) with
    { Observed = PawnFixtures.Row(id) with { Biography = PawnFixtures.Row(id).Biography with { BiologicalAgeYears = age }, Settings = PawnFixtures.Row(id).Settings with { PolicyInputs = PawnFixtures.Inputs(ideo, certainty, guest: guest) }, Downed = !available } };

    private static WorkPawnsRead Pawns(params WorkPawn[] rows) => new WorkPawnsRead(EquatableArray.From(rows));

    private static IdeologyMode.Installed Ideo(params RimGovernor.Contracts.Wire.Observations.IdeoRole[] roles) => new IdeologyMode.Installed(new Ideoligion(new IdeologyDefs(), new IdeoligionFacts { Observed = IdeologyReadsTests.NoPrecepts with { IdeoId = Ours, Roles = [.. roles] } }));

    private static RimGovernor.Contracts.Wire.Observations.IdeoRole Guide(string pawn, bool ready = true, bool active = true) =>
        IdeologyTestRows.Role("role-1", "IdeoRole_Moralist", active, pawn.Length == 0 ? [] : [new PawnId(pawn)], converts: true, ready: ready);

    private static IdeologyFacts Facts(IdeologyMode ideo, WorkPawnsRead pawns, params GoalRecord[] goals) =>
        new() { Installed = true, Roles = [], Conversion = ConversionRules.Owed(goals, ideo, pawns) };

    private static readonly WorkPawn Believer = Colonist("guide", Ours, 0.9);

    [Fact]
    public void TheLowestCertaintyNonBelieverIsConvertedNextByTheGuide()
    {
        var facts = Facts(Ideo(Guide("guide")), Pawns(Believer, Colonist("b", "Ideo_other", 0.8), Colonist("a", "Ideo_other", 0.3)), Everyone);
        var family = new IdeologyFamily(rituals: false, reform: false);

        var need = Assert.Single(family.Derive(facts), n => n.Id.Kind == IdeologyFamily.ConvertKind);
        Assert.Equal("ideology/convert/a", need.Id.Value);
        Assert.Equal((NeedTier.Development, Satisfaction.Unmet), (need.Tier, need.Satisfaction));
        Assert.Equal([IdeologyFamily.Roles], need.Preconditions);

        var method = Assert.Single(family.Propose([need], facts));
        Assert.Null(method.Hold);
        var plan = method.Plan(new PlanId("p"));
        var action = Assert.Single(plan.Actions);
        Assert.Equal(ActionKind.Conversion, action.Kind);
        Assert.Equal(new Conversion(new PawnId("guide"), new PawnId("a")), action.Conversion);
    }

    [Fact]
    public void NoNeedIsOwedOnceEveryColonistBelievesOrWithoutAGoal()
    {
        var family = new IdeologyFamily(rituals: false, reform: false);
        var everyone = Facts(Ideo(Guide("guide")), Pawns(Believer, Colonist("a", Ours, 0.5)), Everyone);
        Assert.DoesNotContain(family.Derive(everyone), n => n.Id.Kind == IdeologyFamily.ConvertKind);

        var noGoal = Facts(Ideo(Guide("guide")), Pawns(Believer, Colonist("a", "Ideo_other", 0.5)));
        Assert.DoesNotContain(family.Derive(noGoal), n => n.Id.Kind == IdeologyFamily.ConvertKind);
        Assert.False(new IdeologyFacts().Conversion.IsOwed);
        Assert.DoesNotContain(family.Derive(new IdeologyFacts()), n => n.Id.Kind == IdeologyFamily.ConvertKind);
    }

    [Fact]
    public void ACountedGoalStopsAtTheCountAndGuestsSlavesBabiesAndTheUnavailableAreNotTargets()
    {
        var pawns = Pawns(
            Believer,
            Colonist("slave", "Ideo_other", 0.1, RimGovernor.Contracts.Wire.Defs.GuestStatus.Slave),
            Colonist("baby", "Ideo_other", 0.1, age: 1),
            Colonist("down", "Ideo_other", 0.1, available: false),
            Colonist("ok", "Ideo_other", 0.9));
        var next = ConversionRules.Owed([Everyone], Ideo(Guide("guide")), pawns);
        Assert.Equal(new PawnId("ok"), next.Target);

        // Two believers of three free colonists wanted: one more is lacking until the count is reached.
        var two = new GoalRecord { Id = 5, Kind = GoalKind.Believers, Count = 2 };
        Assert.Equal(1, ConversionRules.Shortfall([two], Ideo(), Pawns(Believer, Colonist("x", "Ideo_other", 0.5))));
        Assert.Equal(0, ConversionRules.Shortfall([two], Ideo(), Pawns(Believer, Colonist("x", Ours, 0.5), Colonist("y", "Ideo_other", 0.5))));
    }

    [Fact]
    public void WithoutAGuideTheNeedWaitsOnTheRoleNeedAndHoldsItsMethod()
    {
        var pawns = Pawns(Believer, Colonist("a", "Ideo_other", 0.3));
        var family = new IdeologyFamily(rituals: false, reform: false);

        // The ideoligion grants no Convert ability at all: nothing pursues the goal.
        var noRole = Facts(Ideo(), pawns, Everyone);
        var need = Assert.Single(family.Derive(noRole), n => n.Id.Kind == IdeologyFamily.ConvertKind);
        Assert.Equal(IdeologyFamily.NoGuideRole, Assert.Single(family.Propose([need], noRole)).Hold!.Subject);
        Assert.Equal((GoalState.NoMethod, IdeologyFamily.NoGuideRole), Status(noRole, pawns, Ideo()));

        // The role exists but nobody holds it: the goal is blocked on the roles Need.
        var unassigned = Facts(Ideo(Guide("")), pawns, Everyone);
        Assert.Equal((GoalState.Blocked, "no_moral_guide"), Status(unassigned, pawns, Ideo(Guide(""))));
        Assert.NotNull(Assert.Single(family.Propose([Assert.Single(family.Derive(unassigned), n => n.Id.Kind == IdeologyFamily.ConvertKind)], unassigned)).Hold);

        // The ability is charging: the method is held, the goal still pursues.
        var charging = Facts(Ideo(Guide("guide", ready: false)), pawns, Everyone);
        var chargingNeed = Assert.Single(family.Derive(charging), n => n.Id.Kind == IdeologyFamily.ConvertKind);
        Assert.Equal(IdeologyFamily.ConversionCharging, Assert.Single(family.Propose([chargingNeed], charging)).Hold!.Subject);
    }

    [Fact]
    public void TheGoalIsPursuedByTheConvertNeedAndMetOnceNoOneIsLacking()
    {
        var pawns = Pawns(Believer, Colonist("a", "Ideo_other", 0.3));
        var pursued = GoalRules.Evaluate(Everyone, GoalFacts(Ideo(Guide("guide")), pawns));
        Assert.Equal((GoalState.Pursuing, "ideology/convert/a", 1.0), (pursued.State, pursued.Owner.Value, ((GoalDeficit.Measured)pursued.Deficit).Amount));

        var met = GoalRules.Evaluate(Everyone, GoalFacts(Ideo(Guide("guide")), Pawns(Believer, Colonist("a", Ours, 0.3))));
        Assert.Equal(GoalState.Met, met.State);

        // A complete empty census has no colonist left to convert.
        Assert.Equal(GoalState.Met, GoalRules.Evaluate(Everyone, GoalFacts(Ideo(Guide("guide")), new WorkPawnsRead([]))).State);
    }

    [Fact]
    public void AnEmptyCensusOwesNoConversionForAnyGoal()
    {
        var other = new GoalRecord { Id = 6, Kind = GoalKind.Meme, Target = "Meme_x" };
        Assert.Equal(0, ConversionRules.Shortfall([other], Ideo(), new WorkPawnsRead([])));
        Assert.Equal(ConversionOwed.None, ConversionRules.Owed([other], Ideo(), new WorkPawnsRead([])));
        Assert.Equal(0, ConversionRules.Shortfall([Everyone], new IdeologyMode.NotInstalled(), new WorkPawnsRead([])));
        Assert.Equal(0, ConversionRules.Shortfall([Everyone], Ideo(), new WorkPawnsRead([])));
        Assert.Equal(ConversionOwed.None, ConversionRules.Owed([Everyone], Ideo(), new WorkPawnsRead([])));
    }

    [Fact]
    public void AnUnmetGoalTellsThePrisonerDecisionsToConvertEvenAnUnworthyForeignPrisoner()
    {
        var policy = new PrisonerPolicy { ReleaseAfterDays = 10, FoodTargetDays = 5 };
        var prisoner = PopulationFixtures.Held("p", ideo: "Ideo_other");
        var colony = new PrisonerColony { Colonists = 1, Ideology = new ColonyIdeology.Installed(false, Ours) };

        Assert.NotEqual(PrisonerInteractionMode.Convert, PopulationRules.PrisonerIntent(prisoner, colony, new FoodRunway.Days(20.0), policy));
        Assert.Equal(PrisonerInteractionMode.Convert, PopulationRules.PrisonerIntent(prisoner, colony with { BelieversWanted = true }, new FoodRunway.Days(20.0), policy));

        // A prisoner of our own ideoligion is not converted, and a classic game has no conversion.
        Assert.NotEqual(PrisonerInteractionMode.Convert, PopulationRules.PrisonerIntent(prisoner with { Person = prisoner.Person with { IdeoId = Ours } }, colony with { BelieversWanted = true }, new FoodRunway.Days(20.0), policy));
        Assert.NotEqual(PrisonerInteractionMode.Convert, PopulationRules.PrisonerIntent(prisoner, colony with { BelieversWanted = true, Ideology = new ColonyIdeology.Installed(true, Ours) }, new FoodRunway.Days(20.0), policy));
    }

    [Fact]
    public void TheDemandOfAnUnmetGoalReachesThePrisonerColony()
    {
        var pawns = Pawns(Believer, Colonist("a", "Ideo_other", 0.3));
        Assert.True(GoalRules.DemandOf(GoalFacts(Ideo(Guide("guide")), pawns)).Believers);
        Assert.False(GoalRules.DemandOf(GoalFacts(Ideo(Guide("guide")), Pawns(Believer))).Believers);

        var census = new PrisonerCensus { Prisoners = [], Custody = [], Colony = new PrisonerColony(), Guests = [], Names = [] };
        var facts = GoalRules.Stamp(new RoundsFacts { Population = census }, new GoalDemand { Believers = true });
        Assert.True(facts.Population.Colony.BelieversWanted);
    }

    private static (GoalState State, string Reason) Status(IdeologyFacts _, WorkPawnsRead pawns, IdeologyMode ideo)
    {
        var status = GoalRules.Evaluate(Everyone, GoalFacts(ideo, pawns));
        return (status.State, status.Reason);
    }

    private static GoalsFacts GoalFacts(IdeologyMode ideo, WorkPawnsRead pawns) => new()
    {
        Goals = [Everyone],
        Ideology = ideo,
        Pawns = pawns,
    };
}
