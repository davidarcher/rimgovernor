using RimGovernor.Brain.Policy;
using RimGovernor.Contracts.Wire.Observations;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class HuntPlanningTests
{
    static readonly Cell Store = new(50, 50);
    static HuntActorRead Hunter(string id) => HuntFixtures.Actor(id, "Colonist", new(0, 0), 30) with
    {
        Weapon = new("Gun_BoltActionRifle", "", "Normal"), Gun = HuntFixtures.Gun(30), Skills = [new("Shooting", 8)], Available = true,
    };
    static HuntActorRead Prey(string id, int x) => HuntFixtures.Actor(id, "Boar", new(x, 10), 3) with
    {
        Meat = 100, MeatNutrition = 0.05, CorpseStore = new HuntCorpseStore.Store(Store),
    };
    static AcquisitionSource Source(string id, bool designated = false, bool downed = false) =>
        new() { Id = id, Food = true, Hunt = true, Designated = designated, Downed = downed };
    static HuntActivityRead Read() => HuntFixtures.NoHunt with
    {
        Hunters = [Hunter("a"), Hunter("b")], Prey = [Prey("boar", 10), Prey("far", 60)],
    };
    static IEnumerable<CoupledRoute> Legs(string pawn, int x) =>
    [
        new(pawn, new(0, 0), new(x, 10), new RouteTicks.Reachable(100.0)) { FromStart = true },
        new(pawn, new(x, 10), Store, new RouteTicks.Reachable(100.0)) { FromStart = false },
        new(pawn, new(x, 10), new(0, 0), new RouteTicks.Reachable(100.0)) { FromStart = false },
    ];
    static HuntEstimate Estimate(HuntRoster r, double harm = 0, double death = 0, double ticks = 500) => new(true, harm, death, 1.0, 6.0, ticks);
    static readonly CoupledRoute[] Routes = [.. Legs("a", 10), .. Legs("b", 10), .. Legs("a", 60), .. Legs("b", 60)];

    /// <summary>Plans with a stated estimate per roster in place of the analytic one.</summary>
    static HuntPlan Plan(HuntActivityRead read, AcquisitionSource[] sources, Func<HuntRoster, HuntEstimate> estimate, double needed = 5) =>
        HuntPlanningRules.PlanHunt(read, sources, Routes, needed, (_, r) => (estimate(r), ""));

    /// <summary>Opens the pair formation at the boar (the solos are harmful) and admits it; the fresh plan then prefers a solo.</summary>
    static (HuntAdmission Admission, SupplyCandidate Opened, HuntPlan Fresh) Opened(AcquisitionSource[] sources, HuntActivityRead? read = null)
    {
        read ??= Read();
        var plan = Plan(read, sources, r => Estimate(r, harm: r.Gunners.Count == 1 ? 0.4 : 0));
        var admission = new HuntAdmission();
        var (opened, released) = admission.Formation(plan.Channel(sources), plan, sources, read);
        Assert.Equal("", released);
        Assert.Equal(["a", "b"], opened!.Hunters);
        admission.Admit(opened.Prey.Select(id => new Domain.PawnId(id)));
        return (admission, opened, Plan(Read(), sources, r => Estimate(r)));
    }

    static HuntActivityRead Hunters(Func<HuntActorRead, HuntActorRead> a) => Read() with { Hunters = [a(Hunter("a")), Hunter("b")] };

    [Fact]
    public void AnOpenedFormationIsHeldWhileItsGunnersWalkAndTheFreshComparisonPrefersAnother()
    {
        var sources = new[] { Source("boar") };
        var (admission, opened, fresh) = Opened(sources);
        Assert.Single(fresh.Channel(sources)!.Hunters);

        // The gunners walked to the prey and the hunt drafted them, the animals moved: the admitted formation stands over the fresh choice.
        var walked = Read() with
        {
            Hunters = [Hunter("a") with { Location = new(9, 9), Available = false }, Hunter("b") with { Location = new(30, 2), Available = false }],
            Prey = [Prey("boar", 40), Prey("far", 70)],
        };
        Assert.Equal("", admission.Release(walked, sources));
        var (held, released) = admission.Formation(fresh.Channel(sources), fresh, sources, walked);
        Assert.Equal("", released);
        Assert.Equal((opened.Hunters, opened.Prey), (held!.Hunters, held.Prey));
        // An unknown hunt read decides nothing about the gunners.
        Assert.Equal("", admission.Release(null, sources));
    }

    [Fact]
    public void AGoneOrDownedPreyReleasesTheFormation()
    {
        var sources = new[] { Source("boar") };
        var (admission, _, fresh) = Opened(sources);
        Assert.Equal("prey_gone:boar", admission.Release(Read(), []));
        Assert.Equal("prey_downed:boar", admission.Release(Read(), [Source("boar", downed: true)]));
        var (channel, released) = admission.Formation(null, fresh, [], Read());
        Assert.Null(channel);
        Assert.Equal("prey_gone:boar", released);
        // Released, nothing is held or admitted any more.
        Assert.Equal("", admission.Release(Read(), []));
        var (again, none) = admission.Formation(null, fresh, sources, Read());
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
        Assert.Equal("gunner_changed:a:health(+Cut/arm)", admission.Release(Hunters(a => a with { Health = [new("Cut", "arm")] }), sources));
        Assert.Equal("gunner_changed:a:weapon", admission.Release(Hunters(a => a with { Weapon = new("Gun_Autopistol", "", "Normal") }), sources));
        Assert.Equal("gunner_changed:a:weather", admission.Release(Read() with { WeatherAccuracy = 0.8 }, sources));
        // Walking and ageing a little during the hunt are not changes to the gunner.
        Assert.Equal("", admission.Release(Hunters(a => a with { Location = new(50, 30), Age = a.Age + 0.001 }), sources));
    }

    [Fact]
    public void ASkillLevelOrHediffSeverityMovingNeverReleasesTheHeldHuntButGearOrAHealedHediffDoes()
    {
        var sources = new[] { Source("boar") };
        var cut = Hunters(a => a with { Health = [new("Cut", "arm")] });
        var (admission, _, _) = Opened(sources, cut);
        // By now a's shooting went up a level, his shot reads better and the cut heals (its severity reaches only his body read): the same gunner.
        var healing = Hunters(a => a with { Skills = [new("Shooting", 9)], Health = [new("Cut", "arm")], Gun = Gun(0.99), Body = Body(0.1) });
        Assert.Equal("", admission.Release(healing, sources));
        Assert.Equal("", admission.Release(Hunters(a => a with { Skills = [new("Shooting", 9)], Health = [new("Cut", "arm")], Location = new(40, 40), Available = false }), sources));
        // A rifle swapped for a pistol, or the cut healed away, is another gunner.
        var pistol = Hunters(a => a with { Weapon = new("Gun_Autopistol", "", "Normal"), Health = [new("Cut", "arm")] });
        Assert.Equal("gunner_changed:a:weapon", admission.Release(pistol, sources));
        Assert.Equal("gunner_changed:a:health(-Cut/arm)", admission.Release(Read(), sources));
    }

    static HuntGunRead Gun(double accuracy) => new(30, accuracy, 1, 1, 1, 1, 1, 1, 1, 1, true, false, 60, 60, 1, 0, 18, 0.27, true, 1);
    static HuntBodyRead Body(double pain) => new(1, 1, 0.8, false, pain, 0, 0, 0, 13, 0);

    [Fact]
    public void AfterAReleaseTheFreshComparisonMaySendASmallerRosterAndTheChannelNamesOnlyIt()
    {
        var sources = new[] { Source("boar") };
        var (admission, _, _) = Opened(sources);
        // Gunner b is downed: the comparison proposes a alone, and its estimate is safe.
        var downed = Read() with { Hunters = [Hunter("a"), Hunter("b") with { Downed = true, Available = false }] };
        var fresh = Plan(downed, sources, r => Estimate(r));
        Assert.All(fresh.Selection!.Alternatives.Where(x => x.Roster is not null), x => Assert.Equal(["a"], x.Roster!.Gunners));
        var (channel, released) = admission.Formation(fresh.Channel(sources), fresh, sources, downed);
        Assert.Equal("gunner_downed:b", released);
        Assert.Equal(["a"], channel!.Hunters);
        Assert.Equal(["boar"], channel.Prey);
        // The plan's lone channels skip the formation's prey, and the admitted set was cleared with the release: nothing is Designated until the plan opens the new roster.
        var candidates = admission.Apply(HuntSquadRules.HuntCandidates(sources, 1, channel));
        Assert.Equal(CandidateState.Closed, Assert.Single(candidates).State);
        admission.Admit(channel.Prey.Select(id => new Domain.PawnId(id)));
        Assert.Equal(CandidateState.Designated, Assert.Single(admission.Apply(HuntSquadRules.HuntCandidates(sources, 1, channel))).State);
    }

    [Fact]
    public void AHungryGunnerReleasesTheFormationAndIsNoGunnerOfTheFreshComparison()
    {
        var sources = new[] { Source("boar") };
        var (admission, _, _) = Opened(sources);
        // A drafted gunner does not eat: his hunger breaks the hunt, whatever the hunt read says of him.
        Assert.Equal("gunner_hungry:b", admission.Release(Read(), sources, new HashSet<string> { "b" }));
        Assert.Equal("gunner_hungry:b", admission.Release(null, sources, new HashSet<string> { "b" }));
        Assert.Equal("", admission.Release(Read(), sources, new HashSet<string> { "c" }));

        var asked = new List<HuntRoster>();
        HuntPlanningRules.PlanHunt(Read(), sources, Routes, 5, (_, r) => { asked.Add(r); return (Estimate(r), ""); }, new HashSet<string> { "b" });
        Assert.NotEmpty(asked);
        Assert.All(asked, r => Assert.DoesNotContain("b", r.Gunners));
    }

    [Fact]
    public void AColonistIsTooHungryToHuntFromTheClocksHungerHold()
    {
        static WorkPawn Pawn(string id, FoodNeed food) => PawnFixtures.Worker(id) with { Food = food };
        static FoodNeed Has(RimGovernor.Contracts.Wire.Defs.HungerCategory hunger) => new FoodNeed.Has(0.2, hunger);
        var hungry = HuntPlanningRules.Hungry(
        [
            Pawn("fed", Has(RimGovernor.Contracts.Wire.Defs.HungerCategory.Fed)),
            Pawn("peckish", Has(RimGovernor.Contracts.Wire.Defs.HungerCategory.Hungry)),
            Pawn("urgent", Has(HazardThresholdsRules.HungerHoldCategory)),
            Pawn("starving", Has(RimGovernor.Contracts.Wire.Defs.HungerCategory.Starving)),
            Pawn("no-food-need", new FoodNeed.Lacks()),
        ]);
        Assert.Equal(["starving", "urgent"], hungry.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void PreyTheFoodPlanMayNotSendAHuntAtIsNeverProposed()
    {
        var asked = new List<HuntRoster>();
        var plan = HuntPlanningRules.PlanHunt(Read(), [Source("boar", designated: true), Source("far", downed: true)], Routes, 5, (_, r) => { asked.Add(r); return (Estimate(r), ""); });
        Assert.Empty(asked);
        Assert.Null(plan.Selection);
    }

    [Fact]
    public void NoNutritionNeededOffersNoHunt()
    {
        var plan = Plan(Read(), [Source("boar")], r => Estimate(r), needed: 0);
        Assert.Null(plan.Selection);
        Assert.Contains("no_nutrition_needed", plan.Findings);
    }

    [Fact]
    public void TheEstimatedRostersYieldAChannelThatNamesTheSelectedHuntersAndCarriesTheirHarm()
    {
        var sources = new[] { Source("boar") };
        var plan = Plan(Read(), sources, r => Estimate(r, harm: r.Gunners.Count == 1 ? 0.2 : 0.4));
        var channel = plan.Channel(sources)!;
        // A lower harm chance ranks first, so a solo hunter is sent, who hauls its own kill and walks back nowhere else.
        Assert.Single(channel.Hunters);
        Assert.Empty(channel.Retrievers);
        Assert.Equal(["boar"], channel.Prey);
        Assert.Equal(HuntMode.Formation, channel.Mode());
        Assert.Equal(CandidateKind.Hunt, channel.Kind);
        Assert.Contains(channel.Terms, t => t.Name == "hunt_harm_chance" && t.Value == 0.2);
        Assert.Contains(channel.Terms, t => t.Name == "hunt_risk_estimated" && t.Value == 1);
        Assert.Contains(channel.Terms, t => t.Name == "hunt_haul_ticks" && t.Value == 100);
        Assert.Contains(channel.Terms, t => t.Name == "hunt_return_ticks" && t.Value == 0);
        Assert.Contains(channel.Terms, t => t.Name == "retrievers" && t.Value == 0);
        Assert.Equal(0.2, channel.Risk[0].Weight, 6);
    }

    [Fact]
    public void TheSecondStageRestatesTheBestPairsWithTheEstimatedDepartureAndTheChannelNamesTheRetriever()
    {
        var sources = new[] { Source("boar"), Source("far") };
        var asked = new List<HuntRoster>();
        var plan = HuntPlanningRules.PlanHunt(Read(), sources, Routes, 5, (_, r) =>
        {
            asked.Add(r);
            return (Estimate(r, harm: r.Gunners.Count == 1 ? 0.1 : 0, ticks: r.GunnerRetriever ? 200 : 500), "");
        });
        // The one pair per prey group is restated with a gunner leaving at the 500 ticks the first stage estimated.
        var departed = asked.Where(r => r.GunnerRetriever).DistinctBy(r => r.SceneKey).ToArray();
        Assert.Equal(2, departed.Length);
        Assert.All(departed, r =>
        {
            Assert.Equal(500, r.DepartureTick);
            Assert.Equal(["a", "b"], r.Gunners);
        });
        // The departed roster finishes sooner at no harm: it is the choice, and the channel names who leaves to haul.
        var channel = plan.Channel(sources)!;
        Assert.Equal(["a", "b"], channel.Hunters);
        Assert.Equal(["a"], channel.Retrievers);
        Assert.Contains(channel.Terms, t => t.Name == "retrievers" && t.Value == 1);
        Assert.Contains(channel.Terms, t => t.Name == "hunt_haul_ticks" && t.Value == 100);
        Assert.Contains(channel.Terms, t => t.Name == "hunt_return_ticks" && t.Value == 100);
        var food = new FoodPlan { Portfolio = [new() { Channel = channel, Decision = FoodPlanDecision.Open }] };
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
        var food = new FoodPlan { Portfolio = [new() { Channel = channel, Decision = FoodPlanDecision.Open }] };
        Assert.Equal(["a"], HuntPlanningRules.HuntRequestHunters(food).Select(p => p.Value));
        var facts = WorkResourceTestFacts.Create("a", "h") with { FoodPlan = food };
        var reviewed = WorkRules.ReviewWorkResources(facts, new ProductionBatchCensus.Read([]), new([]));
        Assert.Contains("h", reviewed.WorkClaims!.Pawns);
        Assert.DoesNotContain("a", reviewed.WorkClaims.Pawns);
    }

    [Fact]
    public void ARosterTheEstimatorRefusesIsNeverChosen()
    {
        var sources = new[] { Source("boar") };
        var plan = HuntPlanningRules.PlanHunt(Read(), sources, Routes, 5, (_, _) => (null, "revenge_unobserved:boar"));
        Assert.Null(plan.Channel(sources));
        Assert.Contains("no_usable_hunt_roster", plan.Findings);
        Assert.All(plan.Selection!.Alternatives.Where(x => x.Roster is not null), x => Assert.Equal("revenge_unobserved:boar", x.Refusal));
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
