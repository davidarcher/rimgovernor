using RimGovernor.Brain.Policy;
using RimGovernor.Contracts.Wire.Observations;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class HuntPlanningTests
{
    static HuntActorRead Hunter(string id) => new(id, "Colonist", new(0, 0), 30)
    {
        Weapon = new("Gun_BoltActionRifle", "", "Normal", 100), WeaponRange = Fact.Known(30.0), Skills = [new("Shooting", 8)], Available = true,
    };
    static HuntActorRead Prey(string id, int x) => new(id, "Boar", new(x, 10), 3)
    {
        MeatDefinition = "Meat_Pig", Meat = Fact.Known(100.0), MeatNutrition = Fact.Known(0.05),
    };
    static AcquisitionSource Source(string id, bool designated = false, bool downed = false) =>
        new() { Id = id, Food = true, Hunt = true, Designated = designated, Downed = downed };
    static HuntActivityRead Read() => new() { Known = true, Hunters = [Hunter("a"), Hunter("b")], Prey = [Prey("boar", 10), Prey("far", 60)] };
    static CoupledRoute Walk(string pawn, int x, double ticks) => new(pawn, new(0, 0), new(x, 10), Fact.Known(ticks)) { FromStart = true };
    static HuntEstimate Estimate(HuntRoster r, int harm = 0, int death = 0) => new(r.Key, "fp", 16, harm, death, 16, 6.0, 500);
    static readonly CoupledRoute[] Routes = [Walk("a", 10, 100), Walk("b", 10, 100), Walk("a", 60, 100), Walk("b", 60, 100)];

    [Fact]
    public void AnOpenedFormationIsHeldWhileItsPreyStandEvenWhenItsEstimatesGoStale()
    {
        var sources = new[] { Source("boar") };
        var asked = PolicyFunctions.PlanHunt(Read(), sources, Routes, HuntEstimateView.None, 5).Unestimated;
        var view = new HuntEstimateView(asked.ToDictionary(r => r.Key, r => Estimate(r)), new Dictionary<string, string>(), new HashSet<string>());
        var answered = PolicyFunctions.PlanHunt(Read(), sources, Routes, view, 5);
        var admission = new HuntAdmission();
        var opened = admission.Formation(answered.Channel(sources), answered, sources)!;
        admission.Admit(opened.Prey.Select(id => new Domain.PawnId(id)));

        // The animals moved, the estimates are gone and the pool is smaller: the plan offers nothing, the admitted formation stands.
        var stale = PolicyFunctions.PlanHunt(Read(), sources, Routes, HuntEstimateView.None, 5);
        Assert.Null(stale.Channel(sources));
        var held = admission.Formation(stale.Channel(sources), stale, sources)!;
        Assert.Equal((opened.Hunters, opened.Prey), (held.Hunters, held.Prey));

        // Once no admitted animal is offered any more the hunt is over and the plan decides again.
        Assert.Null(admission.Formation(null, stale, []));
        Assert.Null(admission.Formation(null, stale, sources));
    }

    [Fact]
    public void UnansweredRostersAreAskedForAndNoHuntIsOfferedUntilAllAreAnswered()
    {
        var plan = PolicyFunctions.PlanHunt(Read(), [Source("boar")], Routes, HuntEstimateView.None, 5);
        Assert.Null(plan.Selection);
        Assert.NotEmpty(plan.Unestimated);
        Assert.All(plan.Unestimated, r => Assert.Equal(["boar"], r.Prey));
        Assert.Contains("hunt_estimates_pending", plan.Findings);
        Assert.Null(plan.Channel([Source("boar")]));
    }

    [Fact]
    public void PreyTheFoodPlanMayNotSendAHuntAtIsNeverProposed()
    {
        var plan = PolicyFunctions.PlanHunt(Read(), [Source("boar", designated: true), Source("far", downed: true)], Routes, HuntEstimateView.None, 5);
        Assert.Empty(plan.Unestimated);
        Assert.Null(plan.Selection);
    }

    [Fact]
    public void NoNutritionNeededOffersNoHunt()
    {
        var plan = PolicyFunctions.PlanHunt(Read(), [Source("boar")], Routes, HuntEstimateView.None, 0);
        Assert.Empty(plan.Unestimated);
        Assert.Contains("no_nutrition_needed", plan.Findings);
    }

    [Fact]
    public void AnsweredRostersYieldAChannelThatNamesTheSelectedHuntersAndCarriesTheirHarm()
    {
        var sources = new[] { Source("boar") };
        var asked = PolicyFunctions.PlanHunt(Read(), sources, Routes, HuntEstimateView.None, 5).Unestimated;
        var estimates = asked.ToDictionary(r => r.Key, r => Estimate(r, harm: r.Gunners.Count == 1 ? 2 : 4));
        var view = new HuntEstimateView(estimates, new Dictionary<string, string>(), new HashSet<string>());
        var plan = PolicyFunctions.PlanHunt(Read(), sources, Routes, view, 5);
        Assert.Empty(plan.Unestimated);
        var channel = plan.Channel(sources)!;
        // Fewer harmed samples rank first, so a solo hunter is sent.
        Assert.Single(channel.Hunters);
        Assert.Equal(["boar"], channel.Prey);
        Assert.Equal(HuntMode.Formation, channel.Mode());
        Assert.Equal(CandidateKind.Hunt, channel.Kind);
        Assert.Contains(channel.Terms, t => t.Name == "hunt_harm_samples" && t.Value == 2);
        Assert.Equal(2.0 / 16, channel.Risk[0].Weight, 6);
    }

    [Fact]
    public void ARefusedRosterIsAnsweredButNeverChosen()
    {
        var sources = new[] { Source("boar") };
        var asked = PolicyFunctions.PlanHunt(Read(), sources, Routes, HuntEstimateView.None, 5).Unestimated;
        var refusals = asked.ToDictionary(r => r.Key, _ => "prediction_busy");
        var view = new HuntEstimateView(new Dictionary<string, HuntEstimate>(), refusals, new HashSet<string>());
        var plan = PolicyFunctions.PlanHunt(Read(), sources, Routes, view, 5);
        Assert.Empty(plan.Unestimated);
        Assert.Null(plan.Channel(sources));
        Assert.Contains("no_usable_hunt_roster", plan.Findings);
    }

    [Fact]
    public void LoneChannelsWaitForPreyWhoseRostersAreStillBeingPredicted()
    {
        var sources = new[] { Source("boar"), Source("deer") };
        var plan = PolicyFunctions.PlanHunt(Read(), sources, Routes, HuntEstimateView.None, 5);
        Assert.NotEmpty(plan.Unestimated);
        var candidates = PolicyFunctions.HuntCandidates(sources, 2, plan.Channel(sources), plan.Awaiting);
        Assert.DoesNotContain(candidates, c => c.Id == "boar");
    }

    [Fact]
    public void LoneChannelsSkipThePreyAFormationTakes()
    {
        var sources = new[] { Source("boar"), Source("deer") };
        var roster = new HuntRoster(EquatableArray.From(["a"]), EquatableArray.From(["boar"]));
        var formation = PolicyFunctions.HuntRosterChannel(new(roster, "", Estimate(roster), 100, 700), roster, sources);
        var candidates = PolicyFunctions.HuntCandidates(sources, 2, formation);
        Assert.Contains(candidates, c => c.Mode() == HuntMode.Formation && c.Hunters.SequenceEqual(["a"]));
        Assert.Contains(candidates, c => c.Id == "deer" && c.Mode() == HuntMode.Lone);
        Assert.DoesNotContain(candidates, c => c.Id == "boar");
    }
}
