using RimGovernor.Brain.Policy;
using RimGovernor.Contracts.Wire.Observations;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class HuntAlternativesTests
{
    static readonly string[] Eligible = ["a", "b"];
    static HuntActorRead Hunter(string id, int shooting = 8, bool available = true, bool armed = true) => new(id, "Colonist", new(0, 0), 30)
    {
        Weapon = armed ? new("Gun_BoltActionRifle", "", "Normal", 100) : null, WeaponRange = armed ? Fact.Known(30.0) : default,
        Skills = [new("Shooting", shooting)], Available = available,
    };
    static HuntActorRead Prey(string id, int x, string herd = "", bool known = true) => new(id, "Boar", new(x, 10), 3)
    {
        Herd = herd, MeatDefinition = "Meat_Pig", Meat = known ? Fact.Known(100.0) : default, MeatNutrition = Fact.Known(0.05),
    };
    static HuntActivityRead Read(IEnumerable<HuntActorRead> hunters, IEnumerable<HuntActorRead> prey) =>
        new() { Known = true, Hunters = EquatableArray.From(hunters), Prey = EquatableArray.From(prey) };
    static HuntRoster Roster(string[] gunners, params string[] prey) => new(EquatableArray.From(gunners), EquatableArray.From(prey));
    static HuntEstimate Estimate(HuntRoster r, int harm, int death, double nutrition, double ticks = 1000, int samples = 16) =>
        new(r.Key, "fp", samples, harm, death, samples, nutrition, ticks);
    static CoupledRoute Walk(string pawn, int x, double ticks) => new(pawn, new(0, 0), new(x, 10), Fact.Known(ticks)) { FromStart = true };

    [Fact]
    public void ProposalsNeedNoMinimumRosterAndSkipBusyAndUnarmedHunters()
    {
        var read = Read([Hunter("a"), Hunter("b", 3), Hunter("c", available: false), Hunter("d", armed: false), Hunter("e")], [Prey("boar", 10)]);
        var (rosters, findings) = HuntAlternatives.Propose(read, new HashSet<string> { "e" });
        Assert.Contains(rosters, r => r.Gunners.Count == 1);
        Assert.Contains(rosters, r => r.Gunners.Count == 2);
        Assert.DoesNotContain(rosters, r => r.Gunners.Count == 3);
        Assert.All(rosters, r => Assert.All(r.Gunners, g => Assert.Contains(g, Eligible)));
        Assert.Equal(3, rosters.Count);
        Assert.DoesNotContain("no_available_gunner", findings);
        Assert.Contains("no_available_gunner", HuntAlternatives.Propose(Read([Hunter("x", available: false)], [Prey("boar", 10)]), new HashSet<string>()).Findings);
    }

    [Fact]
    public void HerdMatesAreOneTargetGroupAndTargetsAreBounded()
    {
        var herd = Read([Hunter("a")], [Prey("p1", 10, "wild-herd"), Prey("p2", 12, "wild-herd"), Prey("far", 80, "wild-herd")]);
        var (rosters, _) = HuntAlternatives.Propose(herd, new HashSet<string>());
        Assert.Contains(rosters, r => r.Prey.SequenceEqual(["p1", "p2"]));
        Assert.Contains(rosters, r => r.Prey.SequenceEqual(["far"]));
        var many = Read([Hunter("a")], Enumerable.Range(0, 9).Select(i => Prey("p" + i, i * 40)));
        var (bounded, findings) = HuntAlternatives.Propose(many, new HashSet<string>());
        Assert.Equal(HuntAlternatives.MaxTargets, bounded.SelectMany(r => r.Prey).Distinct().Count());
        Assert.Contains("hunt_targets_truncated", findings);
    }

    [Fact]
    public void FewerDeathsBeatMoreMeatAndHarmBeatsLaborButTiesFallThrough()
    {
        var read = Read([Hunter("a"), Hunter("b"), Hunter("c")], [Prey("boar", 10)]);
        var solo = Roster(["a"], "boar"); var pair = Roster(["a", "b"], "boar"); var trio = Roster(["a", "b", "c"], "boar");
        var rosters = EquatableArray.From([solo, pair, trio]);
        var routes = new[] { Walk("a", 10, 100), Walk("b", 10, 100), Walk("c", 10, 100) };
        // The trio brings the most meat but a death in the samples; the pair and solo have none.
        var estimates = new Dictionary<string, HuntEstimate>
        {
            [solo.Key] = Estimate(solo, 4, 0, 4.0), [pair.Key] = Estimate(pair, 4, 0, 4.0, ticks: 300), [trio.Key] = Estimate(trio, 4, 1, 5.0),
        };
        var choice = HuntAlternatives.Choose(read, rosters, estimates, routes, 3.0);
        // Death bound removes the trio; harm and nutrition tie between solo and pair; the pair's shorter fight is less labor than solo's.
        Assert.Equal(pair.Key, choice.Selected.Roster!.Key);
        Assert.Equal(100 + 100 + 300 * 2, choice.Selected.LaborTicks);
        Assert.Equal(4, choice.Alternatives.Count);
    }

    [Fact]
    public void MeatShortfallRanksAfterHarmAndSurplusIsNotRewarded()
    {
        var read = Read([Hunter("a"), Hunter("b")], [Prey("boar", 10)]);
        var solo = Roster(["a"], "boar"); var pair = Roster(["a", "b"], "boar");
        var routes = new[] { Walk("a", 10, 50), Walk("b", 10, 50) };
        var estimates = new Dictionary<string, HuntEstimate> { [solo.Key] = Estimate(solo, 0, 0, 1.0), [pair.Key] = Estimate(pair, 0, 0, 6.0) };
        Assert.Equal(pair.Key, HuntAlternatives.Choose(read, [solo, pair], estimates, routes, 5.0).Selected.Roster!.Key);
        // Both cover a smaller need, so the cheaper labor wins.
        estimates = new() { [solo.Key] = Estimate(solo, 0, 0, 6.0), [pair.Key] = Estimate(pair, 0, 0, 9.0) };
        Assert.Equal(solo.Key, HuntAlternatives.Choose(read, [solo, pair], estimates, routes, 5.0).Selected.Roster!.Key);
    }

    [Fact]
    public void MissingEstimateOrRouteIsRefusedNeverZeroRisk()
    {
        var read = Read([Hunter("a"), Hunter("b")], [Prey("boar", 10)]);
        var solo = Roster(["a"], "boar"); var other = Roster(["b"], "boar");
        var estimates = new Dictionary<string, HuntEstimate> { [other.Key] = Estimate(other, 0, 0, 5.0) };
        var choice = HuntAlternatives.Choose(read, [solo, other], estimates, [Walk("a", 10, 10)], 3.0);
        Assert.Null(choice.Selected.Roster);
        Assert.Contains("no_usable_hunt_roster", choice.Findings);
        Assert.Contains(choice.Alternatives, a => a.Roster?.Key == solo.Key && a.Refusal == "estimate_missing");
        Assert.Contains(choice.Alternatives, a => a.Roster?.Key == other.Key && a.Refusal == "route_unknown");
    }

    [Fact]
    public void NoNutritionNeededMeansNoHunt()
    {
        var read = Read([Hunter("a")], [Prey("boar", 10)]);
        var solo = Roster(["a"], "boar");
        var choice = HuntAlternatives.Choose(read, [solo], new Dictionary<string, HuntEstimate> { [solo.Key] = Estimate(solo, 0, 0, 5.0) }, [Walk("a", 10, 10)], 0);
        Assert.Null(choice.Selected.Roster);
        Assert.Contains(choice.Alternatives, a => a.Roster is not null && a.Usable);
    }

    [Fact]
    public void SmallSamplesCannotLookSaferThanLargeOnes()
    {
        Assert.True(HuntAlternatives.WilsonUpper(0, 4) > HuntAlternatives.WilsonUpper(0, 64));
        Assert.True(HuntAlternatives.WilsonUpper(1, 16) > HuntAlternatives.WilsonUpper(0, 16));
        Assert.Equal(1, HuntAlternatives.WilsonUpper(0, 0));
    }

    static EncounterRequest Request(params EncounterActor[] actors) => new()
    {
        Identity = new("w", 1, "b", "m", "t"), Width = 40, Height = 40, Terrain = "Soil", Weather = "Clear", Actors = actors,
        HorizonTicks = 6000, Seeds = [1], SamplingReason = "t",
    };
    static EncounterActor Actor(string id, bool wild) => new() { Id = id, Kind = wild ? "Boar" : "Colonist", Wild = wild, X = 1, Z = 1 };
    static EncounterActorOutcome Outcome(string id, bool down = false, bool dead = false) =>
        new(id, down, dead, 1, 0, false, [], [], [], null, null, null, null, null);

    [Fact]
    public void EstimateCountsHarmDeathAndDownedPreyPerSampleAndRefusesWhatIsNotEvidence()
    {
        var read = Read([Hunter("a")], [Prey("boar", 10)]);
        var roster = Roster(["a"], "boar");
        var request = Request(Actor("a", false), Actor("boar", true));
        EncounterSample Sample(bool hunterDown, bool preyDown, bool dead = false) =>
            new(1, 500, false, [Outcome("a", hunterDown, dead), Outcome("boar", preyDown)]);
        var prediction = new EncounterPrediction
        {
            Status = EncounterStatus.Evidence, RequestFingerprint = "fp", Reason = "",
            Samples = [Sample(false, true), Sample(true, true), Sample(true, false, true), Sample(false, false)],
        };
        var (estimate, refusal) = HuntAlternatives.EstimateOf(read, roster, request, prediction);
        Assert.Equal("", refusal);
        Assert.Equal((4, 2, 1, 2), (estimate!.Samples, estimate.HarmSamples, estimate.DeathSamples, estimate.DownedSamples));
        Assert.Equal(2 * 100 * 0.05 / 4, estimate.ExpectedNutrition, 6);
        Assert.Equal("prediction_busy", HuntAlternatives.EstimateOf(read, roster, request, prediction with { Status = EncounterStatus.Busy }).Refusal);
        Assert.Equal("prediction_without_samples", HuntAlternatives.EstimateOf(read, roster, request, prediction with { Samples = [] }).Refusal);
        Assert.Equal("request_roster_mismatch", HuntAlternatives.EstimateOf(read, Roster(["b"], "boar"), request, prediction).Refusal);
        var unknown = Read([Hunter("a")], [Prey("boar", 10, known: false)]);
        Assert.Equal("prey_yield_unknown", HuntAlternatives.EstimateOf(unknown, roster, request, prediction).Refusal);
    }
}
