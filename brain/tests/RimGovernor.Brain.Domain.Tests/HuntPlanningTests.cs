using RimGovernor.Brain.Policy;
using RimGovernor.Contracts.Wire.Observations;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class HuntPlanningTests
{
    static readonly Cell Store = new(50, 50);
    static HuntActorRead Hunter(string id) => new(id, "Colonist", new(0, 0), 30)
    {
        Weapon = new("Gun_BoltActionRifle", "", "Normal", 100), WeaponRange = Fact.Known(30.0), Skills = [new("Shooting", 8)], Available = true,
    };
    static HuntActorRead Prey(string id, int x) => new(id, "Boar", new(x, 10), 3)
    {
        Meat = Fact.Known(100.0), MeatNutrition = Fact.Known(0.05), CorpseStore = Store,
    };
    static AcquisitionSource Source(string id, bool designated = false, bool downed = false) =>
        new() { Id = id, Food = true, Hunt = true, Designated = designated, Downed = downed };
    static HuntActivityRead Read() => new()
    {
        Known = true, Hunters = [Hunter("a"), Hunter("b")], Prey = [Prey("boar", 10), Prey("far", 60)],
        Scene = new("w", "build", "mods", "Clear", "Soil", 0, 0, 80, 40, 20, 1.0),
    };
    static IEnumerable<CoupledRoute> Legs(string pawn, int x) =>
    [
        new(pawn, new(0, 0), new(x, 10), Fact.Known(100.0)) { FromStart = true },
        new(pawn, new(x, 10), Store, Fact.Known(100.0)),
        new(pawn, new(x, 10), new(0, 0), Fact.Known(100.0)),
    ];
    static HuntEstimate Estimate(HuntRoster r, int harm = 0, int death = 0, double ticks = 500) => new(16, harm, death, 16, 6.0, ticks);
    static readonly CoupledRoute[] Routes = [.. Legs("a", 10), .. Legs("b", 10), .. Legs("a", 60), .. Legs("b", 60)];
    static HuntEstimateView View(Dictionary<string, HuntEstimate> estimates) => new(estimates, new Dictionary<string, string>(), new HashSet<string>());

    /// <summary>Answers every roster the plan asks for, stage by stage, until it chooses.</summary>
    static HuntPlan Settle(HuntActivityRead read, AcquisitionSource[] sources, Func<HuntRoster, HuntEstimate> estimate)
    {
        var estimates = new Dictionary<string, HuntEstimate>();
        var plan = HuntPlanningRules.PlanHunt(read, sources, Routes, View(estimates), 5);
        for (var stage = 0; plan.Unestimated.Count > 0 && stage < 3; stage++)
        {
            foreach (var r in plan.Unestimated) estimates[r.SceneKey] = estimate(r);
            plan = HuntPlanningRules.PlanHunt(read, sources, Routes, View(estimates), 5);
        }
        return plan;
    }

    static HuntEstimateView Answered(IEnumerable<HuntRoster> rosters, Func<HuntRoster, HuntEstimate> estimate) =>
        new(rosters.ToDictionary(r => r.SceneKey, estimate), new Dictionary<string, string>(), new HashSet<string>());

    /// <summary>Opens the pair formation at the boar (the solos are harmful) and admits it; the plan then holds nothing fresh.</summary>
    static (HuntAdmission Admission, SupplyCandidate Opened, HuntPlan Stale) Opened(AcquisitionSource[] sources)
    {
        var answered = Settle(Read(), sources, r => Estimate(r, harm: r.Gunners.Count == 1 ? 4 : 0));
        var admission = new HuntAdmission();
        var (opened, released) = admission.Formation(answered.Channel(sources), answered, sources, Read());
        Assert.Equal("", released);
        Assert.Equal(["a", "b"], opened!.Hunters);
        admission.Admit(opened.Prey.Select(id => new Domain.PawnId(id)));
        return (admission, opened, HuntPlanningRules.PlanHunt(Read(), sources, Routes, HuntEstimateView.None, 5));
    }

    static HuntActivityRead Hunters(Func<HuntActorRead, HuntActorRead> a) => Read() with { Hunters = [a(Hunter("a")), Hunter("b")] };

    [Fact]
    public void AnOpenedFormationIsHeldWhileItsGunnersWalkAndItsEstimatesGoStale()
    {
        var sources = new[] { Source("boar") };
        var (admission, opened, stale) = Opened(sources);
        Assert.Null(stale.Channel(sources));

        // The gunners walked to the prey and the hunt drafted them, the animals moved and the estimates are gone: the plan offers nothing, the admitted formation stands.
        var walked = Read() with
        {
            Hunters = [Hunter("a") with { Location = new(9, 9), Available = false }, Hunter("b") with { Location = new(30, 2), Available = false }],
            Prey = [Prey("boar", 40), Prey("far", 70)],
        };
        Assert.Equal("", admission.Release(walked, sources));
        var (held, released) = admission.Formation(stale.Channel(sources), stale, sources, walked);
        Assert.Equal("", released);
        Assert.Equal((opened.Hunters, opened.Prey), (held!.Hunters, held.Prey));
        // An unknown hunt read decides nothing about the gunners.
        Assert.Equal("", admission.Release(null, sources));
        Assert.Equal("", admission.Release(new HuntActivityRead(), sources));
    }

    [Fact]
    public void AGoneOrDownedPreyReleasesTheFormation()
    {
        var sources = new[] { Source("boar") };
        var (admission, _, stale) = Opened(sources);
        Assert.Equal("prey_gone:boar", admission.Release(Read(), []));
        Assert.Equal("prey_downed:boar", admission.Release(Read(), [Source("boar", downed: true)]));
        var (channel, released) = admission.Formation(null, stale, [], Read());
        Assert.Null(channel);
        Assert.Equal("prey_gone:boar", released);
        // Released, nothing is held or admitted any more.
        Assert.Equal("", admission.Release(Read(), []));
        var (again, none) = admission.Formation(null, stale, sources, Read());
        Assert.Null(again);
        Assert.Equal("", none);
    }

    [Fact]
    public void APreyOutOfTheObservedWindowReleasesTheFormation()
    {
        var sources = new[] { Source("boar") };
        var (admission, _, _) = Opened(sources);
        Assert.Equal("prey_unobserved:boar", admission.Release(Read() with { Prey = [Prey("far", 60)] }, sources));
    }

    [Fact]
    public void ANewAnimalJoiningThePreyGroupReleasesTheFormation()
    {
        var sources = new[] { Source("boar") };
        var (admission, _, _) = Opened(sources);
        var joined = Read() with { Prey = [Prey("boar", 10) with { Herd = "wild-herd" }, Prey("boar2", 14) with { Herd = "wild-herd" }, Prey("far", 60)] };
        Assert.Equal("prey_joined:boar2", admission.Release(joined, sources));
        // A herd-mate beyond the group radius, or one downed, has not joined.
        var apart = Read() with { Prey = [Prey("boar", 10) with { Herd = "wild-herd" }, Prey("boar2", 30) with { Herd = "wild-herd" }, Prey("far", 60)] };
        Assert.Equal("", admission.Release(apart, sources));
        var downed = Read() with { Prey = [Prey("boar", 10) with { Herd = "wild-herd" }, Prey("boar2", 14) with { Herd = "wild-herd", Downed = true }, Prey("far", 60)] };
        Assert.Equal("", admission.Release(downed, sources));
    }

    [Fact]
    public void ADownedBreakingOrAbsentGunnerReleasesTheFormationButADraftedOneDoesNot()
    {
        var sources = new[] { Source("boar") };
        var (admission, _, _) = Opened(sources);
        Assert.Equal("gunner_downed:a", admission.Release(Hunters(a => a with { Downed = true, Available = false }), sources));
        Assert.Equal("gunner_mental:a", admission.Release(Hunters(a => a with { Mental = true, Available = false }), sources));
        Assert.Equal("gunner_unobserved:a", admission.Release(Read() with { Hunters = [Hunter("b")] }, sources));
        // The hunt's own draft and the player's read the same: a drafted gunner stays rostered.
        Assert.Equal("", admission.Release(Hunters(a => a with { Available = false }), sources));
    }

    [Fact]
    public void AGunnerTheEstimateNoLongerDescribesReleasesTheFormation()
    {
        var sources = new[] { Source("boar") };
        var (admission, _, _) = Opened(sources);
        Assert.Equal("gunner_changed:a", admission.Release(Hunters(a => a with { Health = [new("Cut", "arm", 0.5)] }), sources));
        Assert.Equal("gunner_changed:a", admission.Release(Hunters(a => a with { Weapon = new("Gun_Autopistol", "", "Normal", 100) }), sources));
        Assert.Equal("gunner_changed:a", admission.Release(Read() with { Scene = Read().Scene! with { Weather = "Rain" } }, sources));
        // Walking and ageing a little during the hunt are not changes to the gunner.
        Assert.Equal("", admission.Release(Hunters(a => a with { Location = new(50, 30), Age = a.Age + 0.001 }), sources));
    }

    [Fact]
    public void AfterAReleaseTheFreshComparisonMaySendASmallerRosterAndTheChannelNamesOnlyIt()
    {
        var sources = new[] { Source("boar") };
        var (admission, _, _) = Opened(sources);
        // Gunner b is downed: the comparison proposes a alone, and its fresh estimate is safe.
        var downed = Read() with { Hunters = [Hunter("a"), Hunter("b") with { Downed = true, Available = false }] };
        var asked = HuntPlanningRules.PlanHunt(downed, sources, Routes, HuntEstimateView.None, 5).Unestimated;
        Assert.All(asked, r => Assert.Equal(["a"], r.Gunners));
        var fresh = HuntPlanningRules.PlanHunt(downed, sources, Routes, Answered(asked, r => Estimate(r)), 5);
        var (channel, released) = admission.Formation(fresh.Channel(sources), fresh, sources, downed);
        Assert.Equal("gunner_downed:b", released);
        Assert.Equal(["a"], channel!.Hunters);
        Assert.Equal(["boar"], channel.Prey);
        // The plan's lone channels skip the formation's prey, and the admitted set was cleared with the release: nothing is Designated until the plan opens the new roster.
        var candidates = admission.Apply(HuntSquadRules.HuntCandidates(sources, 1, channel));
        Assert.Equal(Fact.Known(CandidateState.Closed), Assert.Single(candidates).State);
        admission.Admit(channel.Prey.Select(id => new Domain.PawnId(id)));
        Assert.Equal(Fact.Known(CandidateState.Designated), Assert.Single(admission.Apply(HuntSquadRules.HuntCandidates(sources, 1, channel))).State);
    }

    [Fact]
    public void UnansweredRostersAreAskedForAndNoHuntIsOfferedUntilAllAreAnswered()
    {
        var plan = HuntPlanningRules.PlanHunt(Read(), [Source("boar")], Routes, HuntEstimateView.None, 5);
        Assert.Null(plan.Selection);
        Assert.NotEmpty(plan.Unestimated);
        Assert.All(plan.Unestimated, r => Assert.Equal(["boar"], r.Prey));
        Assert.Contains("hunt_estimates_pending", plan.Findings);
        Assert.Null(plan.Channel([Source("boar")]));
    }

    [Fact]
    public void PreyTheFoodPlanMayNotSendAHuntAtIsNeverProposed()
    {
        var plan = HuntPlanningRules.PlanHunt(Read(), [Source("boar", designated: true), Source("far", downed: true)], Routes, HuntEstimateView.None, 5);
        Assert.Empty(plan.Unestimated);
        Assert.Null(plan.Selection);
    }

    [Fact]
    public void NoNutritionNeededOffersNoHunt()
    {
        var plan = HuntPlanningRules.PlanHunt(Read(), [Source("boar")], Routes, HuntEstimateView.None, 0);
        Assert.Empty(plan.Unestimated);
        Assert.Contains("no_nutrition_needed", plan.Findings);
    }

    [Fact]
    public void AnsweredRostersYieldAChannelThatNamesTheSelectedHuntersAndCarriesTheirHarm()
    {
        var sources = new[] { Source("boar") };
        var plan = Settle(Read(), sources, r => Estimate(r, harm: r.Gunners.Count == 1 ? 2 : 4));
        Assert.Empty(plan.Unestimated);
        var channel = plan.Channel(sources)!;
        // Fewer harmed samples rank first, so a solo hunter is sent, who hauls its own kill and walks back nowhere else.
        Assert.Single(channel.Hunters);
        Assert.Empty(channel.Retrievers);
        Assert.Equal(["boar"], channel.Prey);
        Assert.Equal(HuntMode.Formation, channel.Mode());
        Assert.Equal(CandidateKind.Hunt, channel.Kind);
        Assert.Contains(channel.Terms, t => t.Name == "hunt_harm_samples" && t.Value == 2);
        Assert.Contains(channel.Terms, t => t.Name == "hunt_haul_ticks" && t.Value == 100);
        Assert.Contains(channel.Terms, t => t.Name == "hunt_return_ticks" && t.Value == 0);
        Assert.Contains(channel.Terms, t => t.Name == "retrievers" && t.Value == 0);
        Assert.Equal(2.0 / 16, channel.Risk[0].Weight, 6);
    }

    [Fact]
    public void TheSecondStageRestatesTheBestPairsWithTheMeasuredDepartureAndTheChannelNamesTheRetriever()
    {
        var sources = new[] { Source("boar"), Source("far") };
        var first = HuntPlanningRules.PlanHunt(Read(), sources, Routes, HuntEstimateView.None, 5).Unestimated;
        Assert.All(first, r => Assert.Empty(r.Retrievers));
        var estimates = first.ToDictionary(r => r.SceneKey, r => Estimate(r, harm: r.Gunners.Count == 1 ? 1 : 0));
        var second = HuntPlanningRules.PlanHunt(Read(), sources, Routes, View(estimates), 5);
        Assert.Null(second.Selection);
        Assert.Contains("hunt_estimates_pending", second.Findings);
        // The one pair per prey group is restated with a gunner leaving at the 500 ticks the first stage measured; lone hunts keep waiting for that prey.
        Assert.Equal(2, second.Unestimated.Count);
        Assert.All(second.Unestimated, r =>
        {
            Assert.True(r.GunnerRetriever);
            Assert.Equal(500, r.DepartureTick);
            Assert.Equal(["a", "b"], r.Gunners);
        });
        Assert.Equal(["boar", "far"], second.Awaiting.Order());
        // The departed roster finishes sooner at no harm: it is the choice, and the channel names who leaves to haul.
        foreach (var r in second.Unestimated) estimates[r.SceneKey] = Estimate(r, ticks: 200);
        var plan = HuntPlanningRules.PlanHunt(Read(), sources, Routes, View(estimates), 5);
        Assert.Empty(plan.Unestimated);
        var channel = plan.Channel(sources)!;
        Assert.Equal(["a", "b"], channel.Hunters);
        Assert.Equal(["a"], channel.Retrievers);
        Assert.Contains(channel.Terms, t => t.Name == "retrievers" && t.Value == 1);
        Assert.Contains(channel.Terms, t => t.Name == "hunt_haul_ticks" && t.Value == 100);
        Assert.Contains(channel.Terms, t => t.Name == "hunt_return_ticks" && t.Value == 100);
        var food = Known(new FoodPlan { Portfolio = [new() { Channel = channel, Decision = FoodPlanDecision.Open }] });
        Assert.Equal(["a", "b"], HuntPlanningRules.HuntRequestHunters(food).Select(p => p.Value));
        Assert.Equal(["a"], HuntPlanningRules.HuntRequestRetrievers(food).Select(p => p.Value));
    }

    [Fact]
    public void AHauledRostersChannelNamesTheHaulerWhoIsClaimedForTheTripButNotSent()
    {
        var sources = new[] { Source("boar") };
        var roster = new HuntRoster(["a"], ["boar"]) with { Retrievers = ["h"] };
        var channel = HuntPlanningRules.HuntRosterChannel(new(roster, "", Estimate(roster), 900, 50, 250), roster, sources);
        Assert.Equal(["a"], channel.Hunters);
        Assert.Equal(["h"], channel.Retrievers);
        Assert.Contains(channel.Terms, t => t.Name == "hunt_haul_ticks" && t.Value == 250);
        var food = Known(new FoodPlan { Portfolio = [new() { Channel = channel, Decision = FoodPlanDecision.Open }] });
        Assert.Equal(["a"], HuntPlanningRules.HuntRequestHunters(food).Select(p => p.Value));
        var facts = WorkResourceTestFacts.Create("a", "h") with { FoodPlan = food };
        var reviewed = WorkRules.ReviewWorkResources(facts, Known<EquatableArray<ProductionBatch>>([]), new([]));
        Assert.Contains("h", reviewed.WorkClaims!.Pawns);
        Assert.DoesNotContain("a", reviewed.WorkClaims.Pawns);
    }

    [Fact]
    public void ARefusedRosterIsAnsweredButNeverChosen()
    {
        var sources = new[] { Source("boar") };
        var asked = HuntPlanningRules.PlanHunt(Read(), sources, Routes, HuntEstimateView.None, 5).Unestimated;
        var refusals = asked.ToDictionary(r => r.SceneKey, _ => "prediction_busy");
        var view = new HuntEstimateView(new Dictionary<string, HuntEstimate>(), refusals, new HashSet<string>());
        var plan = HuntPlanningRules.PlanHunt(Read(), sources, Routes, view, 5);
        Assert.Empty(plan.Unestimated);
        Assert.Null(plan.Channel(sources));
        Assert.Contains("no_usable_hunt_roster", plan.Findings);
    }

    [Fact]
    public void LoneChannelsWaitForPreyWhoseRostersAreStillBeingPredicted()
    {
        var sources = new[] { Source("boar"), Source("deer") };
        var plan = HuntPlanningRules.PlanHunt(Read(), sources, Routes, HuntEstimateView.None, 5);
        Assert.NotEmpty(plan.Unestimated);
        var candidates = HuntSquadRules.HuntCandidates(sources, 2, plan.Channel(sources), plan.Awaiting);
        Assert.DoesNotContain(candidates, c => c.Id == "boar");
    }

    [Fact]
    public void LoneChannelsSkipThePreyAFormationTakes()
    {
        var sources = new[] { Source("boar"), Source("deer") };
        var roster = new HuntRoster(EquatableArray.From(["a"]), EquatableArray.From(["boar"]));
        var formation = HuntPlanningRules.HuntRosterChannel(new(roster, "", Estimate(roster), 700, 0, 0), roster, sources);
        var candidates = HuntSquadRules.HuntCandidates(sources, 2, formation);
        Assert.Contains(candidates, c => c.Mode() == HuntMode.Formation && c.Hunters.SequenceEqual(["a"]));
        Assert.Contains(candidates, c => c.Id == "deer" && c.Mode() == HuntMode.Lone);
        Assert.DoesNotContain(candidates, c => c.Id == "boar");
    }
}
