using RimGovernor.Brain.Policy;
using RimGovernor.Brain.GameFacts.Observations;
using RimGovernor.Brain.GameOrders.Observations;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class HuntAlternativesTests
{
    static readonly string[] Eligible = ["a", "b"];
    static readonly Cell Store = new(50, 50);
    static HuntActorRead Hunter(string id, int shooting = 8, bool available = true, bool armed = true, bool hauls = false) => HuntFixtures.Actor(id, "Colonist", new(0, 0), 30) with
    {
        Weapon = armed ? new("Gun_BoltActionRifle", "", "Normal") : null, Gun = armed ? HuntFixtures.Gun(30) : null,
        Skills = [new("Shooting", shooting)], Available = available, Hauls = hauls,
    };
    static HuntActorRead Prey(string id, int x, string herd = "", bool store = true) => HuntFixtures.Actor(id, "Boar", new(x, 10), 3) with
    {
        Herd = herd, Meat = 100, MeatNutrition = 0.05, CorpseStore = store ? new HuntCorpseStore.Store(Store) : new HuntCorpseStore.NoStore(),
    };
    static HuntActivityRead Read(IEnumerable<HuntActorRead> hunters, IEnumerable<HuntActorRead> prey) =>
        HuntFixtures.NoHunt with { Hunters = EquatableArray.From(hunters), Prey = EquatableArray.From(prey) };
    static HuntRoster Roster(string[] gunners, params string[] prey) => new(EquatableArray.From(gunners), EquatableArray.From(prey));
    /// <summary>An estimate with its harm and death chances in sixteenths.</summary>
    static HuntEstimate Estimate(HuntRoster r, int harm, int death, double nutrition, double ticks = 1000) =>
        new(true, harm / 16.0, death / 16.0, 1, nutrition, ticks);
    /// <summary>Chooses over stated estimates by encounter; a roster without one is refused as <c>estimate_missing</c>.</summary>
    static HuntSelection Choose(HuntActivityRead read, EquatableArray<HuntRoster> rosters, Dictionary<string, HuntEstimate> estimates, IReadOnlyList<CoupledRoute> routes, double needed) =>
        HuntAlternatives.Choose(read, rosters, r => estimates.TryGetValue(r.SceneKey, out var e) ? (e, "") : (null, "estimate_missing"), routes, needed);
    static CoupledRoute Walk(string pawn, int x, double ticks) => new(pawn, new(0, 0), new(x, 10), new RouteTicks.Reachable(ticks)) { FromStart = true };
    /// <summary>The pawn's trip from the prey at x to the corpse store, carrying.</summary>
    static CoupledRoute Haul(string pawn, int x, double ticks) => new(pawn, new(x, 10), Store, new RouteTicks.Reachable(ticks)) { FromStart = false };
    /// <summary>The pawn's empty walk from the prey at x back to where it stood.</summary>
    static CoupledRoute Back(string pawn, int x, double ticks) => new(pawn, new(x, 10), new(0, 0), new RouteTicks.Reachable(ticks)) { FromStart = false };
    /// <summary>Every leg of every pawn at the prey at x, at one tick cost.</summary>
    static IEnumerable<CoupledRoute> Legs(int x, double ticks, params string[] pawns) =>
        pawns.SelectMany(p => new[] { Walk(p, x, ticks), Haul(p, x, ticks), Back(p, x, ticks) });

    [Fact]
    public void ProposalsNeedNoMinimumRosterAndSkipBusyAndUnarmedHunters()
    {
        var read = Read([Hunter("a"), Hunter("b", 3), Hunter("c", available: false), Hunter("d", armed: false), Hunter("e")], [Prey("boar", 10)]);
        var (rosters, findings) = HuntAlternatives.Propose(read, new HashSet<string> { "e" }, []);
        Assert.Contains(rosters, r => r.Gunners.Count == 1);
        Assert.Contains(rosters, r => r.Gunners.Count == 2);
        Assert.DoesNotContain(rosters, r => r.Gunners.Count == 3);
        Assert.All(rosters, r => Assert.All(r.Gunners, g => Assert.Contains(g, Eligible)));
        Assert.Equal(3, rosters.Count);
        Assert.DoesNotContain("no_available_gunner", findings);
        Assert.Contains("no_available_gunner", HuntAlternatives.Propose(Read([Hunter("x", available: false)], [Prey("boar", 10)]), new HashSet<string>(), []).Findings);
    }

    [Fact]
    public void HerdMatesAreOneTargetGroupAndTargetsAreBounded()
    {
        var herd = Read([Hunter("a")], [Prey("p1", 10, "wild-herd"), Prey("p2", 12, "wild-herd"), Prey("far", 80, "wild-herd")]);
        var (rosters, _) = HuntAlternatives.Propose(herd, new HashSet<string>(), []);
        Assert.Contains(rosters, r => r.Prey.SequenceEqual(["p1", "p2"]));
        Assert.Contains(rosters, r => r.Prey.SequenceEqual(["far"]));
        var many = Read([Hunter("a")], Enumerable.Range(0, 9).Select(i => Prey("p" + i, i * 40)));
        var (bounded, findings) = HuntAlternatives.Propose(many, new HashSet<string>(), []);
        Assert.Equal(HuntAlternatives.MaxTargets, bounded.SelectMany(r => r.Prey).Distinct().Count());
        Assert.Contains("hunt_targets_truncated", findings);
    }

    [Fact]
    public void FewerDeathsBeatMoreMeatAndHarmBeatsLaborButTiesFallThrough()
    {
        var read = Read([Hunter("a"), Hunter("b"), Hunter("c")], [Prey("boar", 10)]);
        var solo = Roster(["a"], "boar"); var pair = Roster(["a", "b"], "boar"); var trio = Roster(["a", "b", "c"], "boar");
        var rosters = EquatableArray.From([solo, pair, trio]);
        var routes = Legs(10, 100, "a", "b", "c").ToArray();
        // The trio brings the most meat but a death chance; the pair and solo have none.
        var estimates = new Dictionary<string, HuntEstimate>
        {
            [solo.Key] = Estimate(solo, 4, 0, 4.0), [pair.Key] = Estimate(pair, 4, 0, 4.0, ticks: 300), [trio.Key] = Estimate(trio, 4, 1, 5.0),
        };
        var choice = Choose(read, rosters, estimates, routes, 3.0);
        // The death chance ranks the trio last; harm and nutrition tie between solo and pair; the pair's shorter fight is less labor than solo's.
        Assert.Equal(pair.Key, choice.Selected.Roster!.Key);
        // Both walk and fight; one carries the corpse to the store, the other walks back empty.
        Assert.Equal(100 + 100 + 300 * 2 + 100 + 100, choice.Selected.LaborTicks);
        Assert.Equal((100.0, 100.0), (choice.Selected.HaulTicks, choice.Selected.ReturnTicks));
        Assert.Equal(4, choice.Alternatives.Count);
        Assert.DoesNotContain("return_travel_and_retrieval_unassessed", choice.Findings);
    }

    [Fact]
    public void MeatShortfallRanksAfterHarmAndSurplusIsNotRewarded()
    {
        var read = Read([Hunter("a"), Hunter("b")], [Prey("boar", 10)]);
        var solo = Roster(["a"], "boar"); var pair = Roster(["a", "b"], "boar");
        var routes = Legs(10, 50, "a", "b").ToArray();
        var estimates = new Dictionary<string, HuntEstimate> { [solo.Key] = Estimate(solo, 0, 0, 1.0), [pair.Key] = Estimate(pair, 0, 0, 6.0) };
        Assert.Equal(pair.Key, Choose(read, [solo, pair], estimates, routes, 5.0).Selected.Roster!.Key);
        // Both cover a smaller need, so the cheaper labor wins.
        estimates = new() { [solo.Key] = Estimate(solo, 0, 0, 6.0), [pair.Key] = Estimate(pair, 0, 0, 9.0) };
        Assert.Equal(solo.Key, Choose(read, [solo, pair], estimates, routes, 5.0).Selected.Roster!.Key);
    }

    [Fact]
    public void MissingEstimateOrRouteIsRefusedNeverZeroRisk()
    {
        var read = Read([Hunter("a"), Hunter("b")], [Prey("boar", 10)]);
        var solo = Roster(["a"], "boar"); var other = Roster(["b"], "boar");
        var estimates = new Dictionary<string, HuntEstimate> { [other.Key] = Estimate(other, 0, 0, 5.0) };
        var choice = Choose(read, [solo, other], estimates, [Walk("a", 10, 10), Haul("a", 10, 10)], 3.0);
        Assert.Null(choice.Selected.Roster);
        Assert.Contains("no_usable_hunt_roster", choice.Findings);
        Assert.Contains(choice.Alternatives, a => a.Roster?.Key == solo.Key && a.Refusal == "estimate_missing");
        Assert.Contains(choice.Alternatives, a => a.Roster?.Key == other.Key && a.Refusal == "route_unknown");
    }

    [Fact]
    public void AnUnknownReturnRouteRefusesTheRosterNeverPricesItAtZero()
    {
        var read = Read([Hunter("a"), Hunter("b"), Hunter("h", armed: false, hauls: true)], [Prey("boar", 10)]);
        var solo = Roster(["a"], "boar"); var pair = Roster(["a", "b"], "boar"); var hauled = solo with { Retrievers = ["h"] };
        var estimates = new Dictionary<string, HuntEstimate> { [solo.Key] = Estimate(solo, 0, 0, 5.0), [pair.Key] = Estimate(pair, 0, 0, 5.0) };
        // The lone gunner has no trip to the store; the pair's second gunner has no walk back; the hauler has no haul trip.
        var routes = new[] { Walk("a", 10, 10), Walk("b", 10, 10), Haul("b", 10, 10), Walk("h", 10, 10) };
        var choice = Choose(read, [solo, pair, hauled], estimates, routes, 3.0);
        Assert.Null(choice.Selected.Roster);
        Assert.All(choice.Alternatives.Where(a => a.Roster is not null), a => Assert.Equal("return_route_unknown", a.Refusal));
        // No store accepts the corpse: there is no trip to price, so the roster is refused the same way.
        var storeless = Read([Hunter("a")], [Prey("boar", 10, store: false)]);
        var refused = Choose(storeless, [solo], estimates, Legs(10, 10, "a").ToArray(), 3.0);
        Assert.Equal("return_route_unknown", refused.Alternatives.Single(a => a.Roster is not null).Refusal);
    }

    [Fact]
    public void AFarCorpseStoreMakesAHaulerBeatTheGunnersOwnHaul()
    {
        var read = Read([Hunter("a"), Hunter("h", armed: false, hauls: true)], [Prey("boar", 10)]);
        var near = new[] { Walk("a", 10, 100), Back("a", 10, 50), Haul("a", 10, 100), Walk("h", 10, 50), Haul("h", 10, 100) };
        var (rosters, _) = HuntAlternatives.Propose(read, new HashSet<string>(), near);
        var solo = Assert.Single(rosters, r => r.Retrievers.Count == 0);
        var hauled = Assert.Single(rosters, r => r.Retrievers.Count == 1);
        Assert.Equal(["h"], hauled.Retrievers);
        // The hauler is outside the encounter: its roster is the gunner's scene and estimate.
        Assert.Equal(solo.SceneKey, hauled.SceneKey);
        Assert.NotEqual(solo.Key, hauled.Key);
        var estimates = new Dictionary<string, HuntEstimate> { [solo.SceneKey] = Estimate(solo, 0, 0, 5.0) };
        // A near store: the gunner's own trip (100) is cheaper than walking back (50) while the hauler walks out and hauls (150).
        var own = Choose(read, rosters, estimates, near, 3.0).Selected;
        Assert.Empty(own.Roster!.Retrievers);
        Assert.Equal((100 + 1000 + 100.0, 100.0, 0.0), (own.LaborTicks, own.HaulTicks, own.ReturnTicks));
        // A far store the slow gunner would carry to for 2000 ticks: the hauler's 800 and the gunner's empty return are less.
        var far = near.Select(r => r.FromStart || r.To != Store ? r : r with { Ticks = new RouteTicks.Reachable(r.Pawn == "a" ? 2000.0 : 800.0) }).ToArray();
        var retrieved = Choose(read, rosters, estimates, far, 3.0).Selected;
        Assert.Equal(["h"], retrieved.Roster!.Retrievers);
        Assert.Equal((100 + 1000 + 50 + 50 + 800.0, 850.0, 50.0), (retrieved.LaborTicks, retrieved.HaulTicks, retrieved.ReturnTicks));
    }

    [Fact]
    public void AGunnerRetrieverLosesWhenTheRemainingGunnerTakesHarmAfterTheDeparture()
    {
        var read = Read([Hunter("a"), Hunter("b", shooting: 3)], [Prey("boar", 10)]);
        var solo = Roster(["a"], "boar"); var pair = Roster(["a", "b"], "boar");
        var routes = Legs(10, 100, "a", "b").ToArray();
        var estimates = new Dictionary<string, HuntEstimate> { [solo.Key] = Estimate(solo, 3, 0, 5.0), [pair.Key] = Estimate(pair, 0, 0, 5.0, ticks: 400) };
        var first = Choose(read, [solo, pair], estimates, routes, 3.0);
        // The second stage restates the pair with its weaker shooter leaving at the estimated encounter ticks; a solo is not restated.
        var second = HuntAlternatives.ProposeRetrieverStage(read, first);
        var variant = Assert.Single(second);
        Assert.Equal(["b"], variant.Retrievers);
        Assert.Equal((400, true), (variant.DepartureTick, variant.GunnerRetriever));
        Assert.Equal(pair.SceneKey + "<b@400", variant.SceneKey);
        // With b gone, a is likelier harmed: the plain pair stays the choice although the variant is less labor.
        estimates[variant.SceneKey] = Estimate(variant, 2, 0, 5.0, ticks: 300);
        var harmed = Choose(read, [solo, pair, variant], estimates, routes, 3.0);
        Assert.Equal(pair.Key, harmed.Selected.Roster!.Key);
        // Without that harm the earlier departure is less labor: a fights 300, b 300 then hauls, a walks back.
        estimates[variant.SceneKey] = Estimate(variant, 0, 0, 5.0, ticks: 300);
        var chosen = Choose(read, [solo, pair, variant], estimates, routes, 3.0).Selected;
        Assert.Equal(variant.Key, chosen.Roster!.Key);
        Assert.Equal(100 + 100 + 300 + 300 + 100 + 100, chosen.LaborTicks);
    }

    [Fact]
    public void NoRetrieverIsGeneratedWithoutAnAvailableHaulingColonistAndHaulersAreTheFastestFew()
    {
        var prey = Prey("boar", 10);
        var idle = Read([Hunter("a"), Hunter("c", available: false, hauls: true), Hunter("d", armed: false), Hunter("e", hauls: true)], [prey]);
        var routes = Legs(10, 100, "a", "c", "d", "e", "f", "g", "h").ToArray();
        // c is unavailable, d does not haul, e is in the gunner pool: nobody retrieves.
        Assert.All(HuntAlternatives.Propose(idle, new HashSet<string>(), routes).Rosters, r => Assert.Empty(r.Retrievers));
        // Three haulers outside the pool: the two with the shortest walk and haul are offered with every team, in one scene each.
        var crowd = Read([Hunter("a"), Hunter("f", armed: false, hauls: true), Hunter("g", armed: false, hauls: true), Hunter("h", armed: false, hauls: true)], [prey]);
        var ranked = routes.Select(r => r.Pawn == "g" && r.FromStart ? r with { Ticks = new RouteTicks.Reachable(500.0) } : r).ToArray();
        var (rosters, _) = HuntAlternatives.Propose(crowd, new HashSet<string>(), ranked);
        Assert.Equal(["f", "h"], rosters.Where(r => r.Retrievers.Count == 1).Select(r => r.Retrievers[0]).Order());
        Assert.Single(rosters.Select(r => r.SceneKey).Distinct());
        // A hauler without a known trip is not offered.
        Assert.All(HuntAlternatives.Propose(crowd, new HashSet<string>(), []).Rosters, r => Assert.Empty(r.Retrievers));
    }

    [Fact]
    public void RequestsStayWithinMaxRostersPlusTheSecondStageBound()
    {
        var hunters = Enumerable.Range(0, 6).Select(i => Hunter("g" + i, shooting: 10 - i)).ToArray();
        var prey = Enumerable.Range(0, 5).Select(i => Prey("p" + i, i * 40)).ToArray();
        var read = Read(hunters, prey);
        var routes = prey.SelectMany(p => Legs(p.Location.X, 100, hunters.Select(h => h.Pawn).ToArray())).ToArray();
        var (rosters, findings) = HuntAlternatives.Propose(read, new HashSet<string>(), routes);
        var scenes = rosters.Select(r => r.SceneKey).Distinct().Count();
        Assert.True(scenes <= HuntAlternatives.MaxRosters);
        Assert.Contains("hunt_rosters_truncated", findings);
        var estimates = rosters.ToDictionary(r => r.SceneKey, r => Estimate(r, 0, 0, 5.0));
        var second = HuntAlternatives.ProposeRetrieverStage(read, Choose(read, rosters, estimates, routes, 3.0));
        Assert.Equal(HuntAlternatives.MaxTargets * HuntAlternatives.MaxRetrieverStage, second.Count);
        Assert.All(second, r => Assert.True(r.GunnerRetriever && r.Gunners.Count >= 2 && r.DepartureTick == 1000));
        Assert.Equal(second.Count, second.Select(r => r.SceneKey).Distinct().Count());
    }

    [Fact]
    public void NoNutritionNeededMeansNoHunt()
    {
        var read = Read([Hunter("a")], [Prey("boar", 10)]);
        var solo = Roster(["a"], "boar");
        var choice = Choose(read, [solo], new Dictionary<string, HuntEstimate> { [solo.Key] = Estimate(solo, 0, 0, 5.0) }, Legs(10, 10, "a").ToArray(), 0);
        Assert.Null(choice.Selected.Roster);
        Assert.Contains(choice.Alternatives, a => a.Roster is not null && a.Usable);
    }
}
