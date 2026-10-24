using RimGovernor.Brain.Policy;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>#3500: meme and precept goals steer the fluid ideoligion's reform one meme change at a time, then precept swaps.</summary>
public sealed class IdeoligionGoalReformTests
{
    private const string Structure = "Structure_Ideal";
    private const string Rancher = "Rancher";
    private const string Nudism = "Nudism";
    private const string Covered = "Nudity_Covered";
    private const string Naked = "Nudity_Naked";
    private const string Despised = "Cannibalism_Despised";
    private const string Approved = "Cannibalism_Approved";

    private static DesignOptions Options(bool nudismAllowed = true) => new()
    {
        Memes =
        [
            new DesignMeme { Name = Structure, Structure = true, Allowed = true, Supported = true },
            new DesignMeme { Name = Rancher, Allowed = true, Supported = true },
            new DesignMeme { Name = Nudism, Allowed = nudismAllowed, Supported = true },
        ],
        Precepts =
        [
            new DesignPrecept { Name = Covered, Issue = "Nudity", Default = true, Allowed = true, Supported = true },
            new DesignPrecept { Name = Naked, Issue = "Nudity", Allowed = true, Supported = true, RequiredMemes = [Nudism] },
            new DesignPrecept { Name = Despised, Issue = "Cannibalism", Default = true, Allowed = true, Supported = true },
            new DesignPrecept { Name = Approved, Issue = "Cannibalism", Allowed = true, Supported = true },
        ],
        VeneratedAnimals = ["Monkey", "Muffalo", "Thrumbo"],
        MaxVeneratedAnimals = 2,
    };

    private static IdeoligionDesign Design(string[] memes, params string[] precepts) => new(EquatableArray.From(memes), EquatableArray.From(precepts), true);

    private static readonly IdeoligionDesign Lacking = Design([Rancher, Structure], Covered, Despised);

    private static readonly IdeoligionDesign Nudist = Design([Nudism, Rancher, Structure], Covered, Despised);

    private static readonly IdeoGoalTarget[] NakedCannibals =
    [
        new(1, IdeoGoalKind.Precept, Naked),
        new(2, IdeoGoalKind.Precept, Approved),
    ];

    private static DesignNeeds NoBelievers() => new() { Thoughts = new BelieverThoughts.Unjudged() };

    private static int? GoalOf(DesignChoice choice) => choice.Serves is ReformServes.Goal goal ? goal.Id : null;

    private static IdeoDevelopment Points(int have = 40, int cost = 30, bool fluid = true) =>
        new IdeoDevelopment(fluid, fluid && have >= cost, 2, have, cost);

    private static DesignChoice Choose(IdeoligionDesign current, IReadOnlyList<IdeoGoalTarget> targets, IdeoDevelopment? development = null, DesignOptions? options = null) =>
        IdeoligionDesignRules.ChooseIdeoligionReform(options ?? Options(), current, development ?? Points(), true, NoBelievers, targets);

    [Fact]
    public void ThePreceptNeedingAMemeTheIdeoligionLacksAddsThatMemeFirst()
    {
        var choice = Choose(Lacking, NakedCannibals);
        Assert.Equal(DesignDecision.Improves, choice.Comparison!.Decision);
        Assert.Equal(1, GoalOf(choice));
        Assert.Equal([Nudism, Rancher, Structure], choice.Design.Memes);
        Assert.Equal(IdeoligionDesign.CanonicalIdeoligionDesign(Lacking).Precepts, choice.Design.Precepts);
    }

    [Fact]
    public void WithTheMemeHeldTheNextReformSwapsTheNudityPreceptThenTheCannibalismOne()
    {
        var swap = Choose(Nudist, NakedCannibals);
        Assert.Equal((1, DesignDecision.Improves), (GoalOf(swap), swap.Comparison!.Decision));
        Assert.Equal(Nudist.Memes, swap.Design.Memes);
        Assert.Equal([Despised, Naked], swap.Design.Precepts);

        var both = Design([Nudism, Rancher, Structure], Naked, Despised);
        var cannibal = Choose(both, NakedCannibals);
        Assert.Equal(2, GoalOf(cannibal));
        Assert.Equal([Approved, Naked], cannibal.Design.Precepts);
    }

    [Fact]
    public void AGoalSwapNeverUndoesAnotherGoalsPrecept()
    {
        var swapped = Design([Nudism, Rancher, Structure], Naked, Despised);
        var choice = Choose(swapped, [new(1, IdeoGoalKind.Precept, Naked), new(2, IdeoGoalKind.Precept, Covered)]);
        Assert.Null(GoalOf(choice));
        Assert.Equal("precept_conflicts_goal:" + Naked, choice.Blocked[2]);
    }

    [Fact]
    public void ANonFluidIdeoligionIsNotReformedAndAnUnsafeColonyWaits()
    {
        var rigid = Choose(Lacking, NakedCannibals, Points(fluid: false));
        Assert.Equal((DesignDecision.Ineligible, null), (rigid.Comparison!.Decision, GoalOf(rigid)));
        var unsafeColony = IdeoligionDesignRules.ChooseIdeoligionReform(Options(), Lacking, Points(), false, NoBelievers, NakedCannibals);
        Assert.Equal(DesignDecision.Unsafe, unsafeColony.Comparison!.Decision);
    }

    [Fact]
    public void ADesignTheFactionDisallowsIsNotChosenAndTheGoalReportsTheFirstMissingPrerequisite()
    {
        var choice = Choose(Lacking, [new(1, IdeoGoalKind.Precept, Naked)], options: Options(nudismAllowed: false));
        Assert.Null(GoalOf(choice));
        Assert.Equal("precept_requires_meme:" + Nudism, choice.Blocked[1]);

        var missing = Choose(Lacking, [new(1, IdeoGoalKind.Meme, "Unlisted")]);
        Assert.Equal("meme_unavailable:Unlisted", missing.Blocked[1]);
        var absent = Choose(Lacking, [new(1, IdeoGoalKind.Precept, "Unlisted")]);
        Assert.Equal("precept_unavailable:Unlisted", absent.Blocked[1]);
    }

    [Fact]
    public void ABlockedGoalDoesNotHoldBackAnotherGoalsStep()
    {
        var choice = Choose(Lacking, [new(1, IdeoGoalKind.Precept, "Unlisted"), new(2, IdeoGoalKind.Precept, Approved)]);
        Assert.Equal(2, GoalOf(choice));
        Assert.Equal("precept_unavailable:Unlisted", choice.Blocked[1]);
    }

    [Fact]
    public void AMemeGoalAddsThatMemeAndAHeldMemeIsLeftAlone()
    {
        var choice = Choose(Lacking, [new(3, IdeoGoalKind.Meme, Nudism)]);
        Assert.Equal(3, GoalOf(choice));
        Assert.Contains(Nudism, choice.Design.Memes);
        Assert.Equal(DesignDecision.Unchanged, Choose(Nudist, [new(3, IdeoGoalKind.Meme, Nudism)]).Comparison!.Decision);
    }

    [Fact]
    public void ANeededMemeThatConflictsWithAHeldOneRemovesIt()
    {
        var options = Options() with
        {
            Precepts =
            [
                .. Options().Precepts,
                new DesignPrecept { Name = "Meat_Raw", Issue = "Meat", Allowed = true, Supported = true, ConflictingMemes = [Rancher] },
            ],
        };
        var choice = Choose(Nudist, [new(1, IdeoGoalKind.Precept, "Meat_Raw")], options: options);
        Assert.Equal(1, GoalOf(choice));
        Assert.Equal([Nudism, Structure], choice.Design.Memes);
        Assert.DoesNotContain("Meat_Raw", choice.Design.Precepts);
    }

    [Fact]
    public void WithoutEnoughPointsTheReformWaitsAndNamesTheCounts()
    {
        var choice = Choose(Lacking, NakedCannibals, Points(have: 12, cost: 30));
        Assert.Equal(DesignDecision.Awaiting, choice.Comparison!.Decision);

        var facts = new IdeologyFacts { Reform = choice, Development = Points(have: 12, cost: 30) };
        var need = IdeologyFamilyRules.ReformNeedOf(facts);
        Assert.Equal(Satisfaction.Unmet, need.Satisfaction);
        Assert.Equal("development_points:12/30", Assert.Single(new IdeologyFamily().Propose([need], facts)).Hold!.Subject);
    }

    private static GoalsFacts GoalFacts(GoalRecord goal, IdeoDevelopment development, DesignChoice? reform, params string[] memes) => new()
    {
        Goals = [goal],
        IdeologyInstalled = Known(true),
        Ideology = Known(new Ideoligion(
            new IdeologyDefs
            {
                Precepts = new Dictionary<string, PreceptDef> { [Naked] = new([]) },
                Memes = new Dictionary<string, MemeDef> { [Nudism] = new([], []) },
            },
            new IdeoligionFacts { Memes = memes, Development = development })),
        Reform = reform,
        ReformGoal = ReformFocus.Of(1),
    };

    [Fact]
    public void TheGoalStatusFollowsTheReformPlan()
    {
        var goal = new GoalRecord { Id = 1, Kind = GoalKind.Precept, Target = Naked };
        var plan = Choose(Lacking, [new(1, IdeoGoalKind.Precept, Naked)]);

        var pursuing = GoalRules.Evaluate(goal, GoalFacts(goal, Points(), plan));
        Assert.Equal((GoalState.Pursuing, "", "ideology/reform"), (pursuing.State, pursuing.Reason, pursuing.Owner.Value));
        Assert.Equal("queued", GoalRules.Evaluate(goal, GoalFacts(goal, Points(), plan) with { ReformGoal = ReformFocus.Of(9) }).Reason);

        var points = GoalRules.Evaluate(goal, GoalFacts(goal, Points(have: 12, cost: 30), Choose(Lacking, [new(1, IdeoGoalKind.Precept, Naked)], Points(have: 12, cost: 30))));
        Assert.Equal((GoalState.Blocked, "development points 12/30"), (points.State, points.Reason));

        var broken = Choose(Lacking, [new(1, IdeoGoalKind.Precept, Naked)], options: Options(nudismAllowed: false));
        var blocked = GoalRules.Evaluate(goal, GoalFacts(goal, Points(), broken));
        Assert.Equal((GoalState.Blocked, "precept_requires_meme:" + Nudism), (blocked.State, blocked.Reason));

        var rigid = GoalRules.Evaluate(goal, GoalFacts(goal, Points(fluid: false), default));
        Assert.Equal((GoalState.NoMethod, "not fluid"), (rigid.State, rigid.Reason));
        Assert.Equal("ideoligion reform design", Assert.Throws<RequiredFactException>(() => GoalRules.Evaluate(goal, GoalFacts(goal, Points(), default))).Fact);
    }

    [Fact]
    public void AMissingDefIsUnavailableAndAHeldMemeOrVeneratedAnimalIsMet()
    {
        var unlisted = new GoalRecord { Id = 1, Kind = GoalKind.Precept, Target = "Unlisted" };
        Assert.Equal((GoalState.NoMethod, "unavailable"), (GoalRules.Evaluate(unlisted, GoalFacts(unlisted, Points(), default)).State, GoalRules.Evaluate(unlisted, GoalFacts(unlisted, Points(), default)).Reason));

        var meme = new GoalRecord { Id = 1, Kind = GoalKind.Meme, Target = Nudism };
        Assert.Equal(GoalState.Met, GoalRules.Evaluate(meme, GoalFacts(meme, Points(), default, Nudism)).State);

        var animal = new GoalRecord { Id = 1, Kind = GoalKind.VeneratedAnimal, Target = "Muffalo" };
        var facts = GoalFacts(animal, Points(), default);
        var held = facts with { Ideology = Known(facts.Ideology.Value with { Facts = facts.Ideology.Value.Facts with { VeneratedAnimals = ["Muffalo"] } }) };
        Assert.Equal(GoalState.Met, GoalRules.Evaluate(animal, held).State);
    }

    private static readonly IdeoGoalTarget[] Worship = [new(1, IdeoGoalKind.Animal, "Monkey")];

    [Fact]
    public void AVeneratedAnimalGoalAddsThatAnimalLeavingTheRestOfTheDesignAndIsNotWeighedAgainstTheScore()
    {
        var current = Lacking with { VeneratedAnimals = ["Muffalo"], StyleCategories = ["Rustic"] };
        var choice = Choose(current, Worship);
        Assert.Equal((1, DesignDecision.Improves), (GoalOf(choice), choice.Comparison!.Decision));
        Assert.Equal(["Monkey", "Muffalo"], choice.Design.VeneratedAnimals);
        Assert.Equal(["Rustic"], choice.Design.StyleCategories);
        Assert.Equal(IdeoligionDesign.CanonicalIdeoligionDesign(current).Precepts, choice.Design.Precepts);
        Assert.Equal(current.Memes, choice.Design.Memes);
        Assert.Equal(DesignDecision.Unchanged, Choose(current with { VeneratedAnimals = ["Monkey"] }, Worship).Comparison!.Decision);
    }

    [Fact]
    public void AnAnimalTheGameDoesNotOfferOrBeyondTheLimitIsBlockedWithItsReason()
    {
        var insect = Choose(Lacking, [new(1, IdeoGoalKind.Animal, "Megaspider")]);
        Assert.Null(GoalOf(insect));
        Assert.Equal("venerated_animal_unavailable:Megaspider", insect.Blocked[1]);

        var full = Choose(Lacking with { VeneratedAnimals = ["Muffalo", "Thrumbo"] }, Worship);
        Assert.Null(GoalOf(full));
        Assert.Equal("venerated_animal_unavailable:Monkey", full.Blocked[1]);
    }

    [Fact]
    public void AnAnimalGoalWaitsForPointsLikeAnyOtherAndItsStatusFollowsThePlan()
    {
        Assert.Equal(DesignDecision.Awaiting, Choose(Lacking, Worship, Points(have: 12, cost: 30)).Comparison!.Decision);

        var goal = new GoalRecord { Id = 1, Kind = GoalKind.VeneratedAnimal, Target = "Monkey" };
        var pursuing = GoalRules.Evaluate(goal, GoalFacts(goal, Points(), Choose(Lacking, Worship)));
        Assert.Equal((GoalState.Pursuing, ""), (pursuing.State, pursuing.Reason));

        var points = GoalRules.Evaluate(goal, GoalFacts(goal, Points(have: 12, cost: 30), Choose(Lacking, Worship, Points(have: 12, cost: 30))));
        Assert.Equal((GoalState.Blocked, "development points 12/30"), (points.State, points.Reason));

        var illegal = new GoalRecord { Id = 1, Kind = GoalKind.VeneratedAnimal, Target = "Megaspider" };
        var blocked = GoalRules.Evaluate(illegal, GoalFacts(illegal, Points(), Choose(Lacking, [new(1, IdeoGoalKind.Animal, "Megaspider")])));
        Assert.Equal((GoalState.Blocked, "venerated_animal_unavailable:Megaspider"), (blocked.State, blocked.Reason));
    }

    [Fact]
    public void IdeoTargetsCarryTheAnimalGoalsAndTheAnimalsAreDesignIdentity()
    {
        var targets = GoalRules.IdeoTargets([new GoalRecord { Id = 4, Kind = GoalKind.VeneratedAnimal, Target = "Monkey" }]);
        Assert.Equal([new IdeoGoalTarget(4, IdeoGoalKind.Animal, "Monkey")], targets);
        Assert.NotEqual(Lacking, Lacking with { VeneratedAnimals = ["Monkey"] });
        Assert.NotEqual(Lacking, Lacking with { StyleCategories = ["Rustic"] });
        Assert.Equal(["A", "B"], IdeoligionDesign.CanonicalIdeoligionDesign(Lacking with { VeneratedAnimals = ["B", "A"] }).VeneratedAnimals);
    }
}
