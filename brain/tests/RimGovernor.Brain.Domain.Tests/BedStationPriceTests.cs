using RimGovernor.Brain.Policy;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// A bed group hosting a kitchen, dining, butchery or freezer station is priced, not refused by a table (#3750): the
/// sleepers' disturbed sleep (each visit in their rest hours, at the SleepDisturbed def's mood) and the impressiveness
/// stage the butchery's filth costs them are lost effects of the group, and the regrouping merges the beds with the
/// station only when the walk saved beats them.
/// </summary>
public sealed class BedStationPriceTests
{
    private const string Pool = "Bed/sleep";
    private const string Stove = "Stove/work";
    internal const string Butcher = "TableButcher/work";
    private const string Lab = "ResearchBench/work";

    private static Station Bench(string def, RoomRole role) => new()
    {
        Shape = new InteriorPieceDef(def, new Cell(3, 1), RoomRole.None, null),
        Contributions = [new RoleScore { Role = role, Weight = 1 }],
        Capacity = new StationCapacity { Kind = CapacityKind.Workers, Slots = 1 },
    };

    /// <summary>The game's SleepDisturbed: -1 mood for a day, stacking three times at full weight.</summary>
    private static readonly MemoryThought Disturbed = new(-1, 1, 3, 1);

    /// <summary>A break curve in the game's shape: the minor threshold 0.35, each tier's mean break days.</summary>
    internal static readonly MoodModel Mood = new()
    {
        Break = new MoodBreakModel { Threshold = 0.35, Tiers = [new(1, 2), new(1, 3), new(1, 5)] },
        SleepDisturbed = Disturbed,
    };

    /// <summary>Filth_Blood's cleanliness a unit, the FoodPoisoning hediff's work and the 600 ticks filth stands before it may be cleaned.</summary>
    internal static readonly FilthModel Filth = new()
    {
        Def = "Filth_Blood",
        Cleanliness = -10,
        FoodPoison = [new(-5, 0.02), new(0, 0.01)],
        Poisoning = new PoisoningModel { Initial = 1, PerDay = 1, Stages = [new(0, 0.6, 0.4), new(0.2, 0.5, 0.2), new(0.8, 0.6, 0.3)] },
        CleaningWork = 70,
        CleaningSpeed = 1,
        StandTicks = 600,
    };

    private static StationCatalog Catalog(MoodModel mood) => BedMergePriceTests.Catalog with
    {
        Stations = new Dictionary<string, Station>(BedMergePriceTests.Catalog.Stations)
        {
            ["Stove"] = Bench("Stove", RoomRole.Kitchen),
            ["TableButcher"] = Bench("TableButcher", RoomRole.Kitchen),
            ["ResearchBench"] = Bench("ResearchBench", RoomRole.Laboratory),
        },
        Mood = mood,
        Filth = Filth,
    };

    // Hours 22 to 5 are the night.
    private static bool Night(int h) => h >= 22 || h < 6;

    /// <summary>A worker's timetable, hour 0 first.</summary>
    private static IReadOnlyList<string> Timetable(Func<int, string> slot) => [.. Enumerable.Range(0, 24).Select(slot)];

    /// <summary>Three sleepers who sleep the night and take their leisure by day, and a cook working at night or by day.</summary>
    internal static RestSchedule? Colony(bool nightCook) => RestSchedule.Of(
    [
        Timetable(h => Night(h) ? PawnRules.ScheduleSleep : PawnRules.ScheduleJoy),
        Timetable(h => Night(h) ? PawnRules.ScheduleSleep : PawnRules.ScheduleJoy),
        Timetable(h => Night(h) ? PawnRules.ScheduleSleep : PawnRules.ScheduleJoy),
        Timetable(h => Night(h) == nightCook ? PawnRules.ScheduleWork : PawnRules.ScheduleSleep),
    ]);

    private static BedMergePrice Price(BedMergeFacts facts, double[] curve) => new()
    {
        Facts = facts,
        OutdoorCurve = Known(EquatableArray.From(curve)),
        WorkDay = BedMergePriceTests.Day(8000.0),
        Builders = Known(1),
    };

    /// <summary>Three calm sleepers whom the market price of wood merges into one barracks (BedMergePriceTests), and a station the beds' sleepers walk to <paramref name="walks"/> times a day.</summary>
    internal static ClusterInput Cluster(StationNode station, double walks, RestSchedule? rest, MoodModel mood, BedMergeFacts? facts = null, double[]? curve = null) => new()
    {
        Graph = new StationGraph
        {
            Nodes = [new StationNode { Id = Pool, Def = "Bed", Purpose = StationPurpose.Sleep, Count = 3 }, station, new StationNode { Id = Lab, Def = "ResearchBench", Purpose = StationPurpose.Work, Count = 1 }],
            Edges = [new StationEdge { A = Pool, B = station.Id, Weight = walks }],
        },
        Catalog = Catalog(mood),
        RoofDistance = 6,
        BedMerge = Price(facts ?? BedMergePriceTests.MarketWood(BedMergePriceTests.Calm), curve ?? BedMergePriceTests.Temperate),
        Rest = rest,
    };

    private static StationNode Cook => new() { Id = Stove, Def = "Stove", Purpose = StationPurpose.Work, Count = 1, DailyWork = 20000 };

    internal static StationNode Butchery(double runs) => new() { Id = Butcher, Def = "TableButcher", Purpose = StationPurpose.Work, Count = 1, Filth = true, FilthRuns = runs, FilthWork = 1500 };

    /// <summary>
    /// A barren map whose only ground is three 8 x 8 pockets far apart, zoned as a first plan zones it: the barracks
    /// fills one pocket, so a station in its own room stands in another, about 30 cells' walk away.
    /// </summary>
    private static EmbedInput Embed
    {
        get
        {
            static bool In(Cell c, int x, int z) => c.X >= x && c.X < x + 8 && c.Z >= z && c.Z < z + 8;
            var s = FirstLayoutPlanTests.Survey("mild");
            s = s with { Cells = [.. s.Cells.Select(c => c with { Fertility = 0, Walkable = In(c.Cell, 20, 36) || In(c.Cell, 52, 36) || In(c.Cell, 36, 10) })] };
            return new EmbedInput { Survey = s, Zones = LayoutZoningRules.Zone(s), RoofDistance = 6 };
        }
    }

    /// <summary>The regrouping over an open map, its colonists at 0.37 mood (just over the minor threshold).</summary>
    private static Regrouping Regroup(ClusterInput cluster) => LayoutRegroupRules.Regroup(new RegroupInput
    {
        Cluster = cluster,
        Embed = Embed,
        Price = new RegroupPrice { Work = new WorkPrice(8000, LayoutRegroupRules.CellWalkDays(60)), Mood = cluster.Catalog.Mood, Expected = Known(0.37) },
    });

    private static bool Shared(Clustering c, string a, string b) => c.Groups.Any(g => LayoutRegroupRules.HoldsNode(g, a) && LayoutRegroupRules.HoldsNode(g, b));

    [Fact]
    public void ANightCookDisturbsThreeSleepersSoTheKitchenStaysOutOfTheirBarracks()
    {
        var cluster = Cluster(Cook, 30, Colony(nightCook: true), Mood);

        // The seed keeps the pair apart for the sleepers' cost: three disturbances held (the stack limit), -3 mood each.
        var seed = LayoutClusterRules.ClusterStations(cluster);
        Assert.False(Shared(seed, Pool, Stove));
        var keep = Assert.Single(seed.Decisions, d => d.A == Pool && d.B == Stove || d.A == Stove && d.B == Pool);
        Assert.Equal((LayoutClusterRules.ClusterKeep, LayoutClusterRules.ReasonClusterLostEffect), (keep.Verdict, keep.Reason));
        var lost = Assert.Single(keep.LostEffects);
        Assert.Equal((RoleEffect.SleepDisturbed, 3, 3.0), (lost.Effect, lost.Pawns, lost.Points));

        // The regrouping prices the merge: the walk it saves is less than the sleepers' break risk.
        var r = Regroup(cluster);
        Assert.False(Shared(r.Clustering, Pool, Stove));
        var d = Assert.Single(r.Decisions, d => d.Reason == LayoutRegroupRules.ReasonRegroupBedStationKept);
        Assert.Equal((LayoutRegroupRules.RegroupKept, LayoutRegroupRules.RegroupMerge), (d.Verdict, d.Kind));
        Assert.Contains("sleep_disturbed", d.Effects);
        Assert.True(d.Walk < 0 && d.After > d.Before, $"walk {d.Walk}, before {d.Before}, after {d.After}");
    }

    [Fact]
    public void ADayCookDisturbsNobodySoTheBarracksHostsTheKitchen()
    {
        // The cook works while the sleepers are awake and the stove leaves no filth: the merge costs nothing, and the walk it saves wins.
        var r = Regroup(Cluster(Cook, 30, Colony(nightCook: false), Mood));

        var g = Assert.Single(r.Clustering.Groups, g => LayoutRegroupRules.HoldsNode(g, Pool));
        Assert.True(LayoutRegroupRules.HoldsNode(g, Stove));
        Assert.DoesNotContain(g.LostEffects, l => l.Effect is RoleEffect.SleepDisturbed or RoleEffect.Impressiveness);
    }

    [Fact]
    public void ButcheryFilthLowersTheBarracksStageUntilItsLossBeatsTheWalk()
    {
        // Sleepers who gain 0.05 a day in a barracks of dull stage or better: the beds' room is sized dull, and the
        // butchery's filth, standing a longer share of the day as the runs grow, drops it to awful while it stands.
        var dull = BedMergePriceTests.MarketWood(BedMergePriceTests.Staged(Enumerable.Repeat((new double[5], new[] { 0, 0.05, 0.05, 0.05, 0.05 }), 3).ToArray()));
        Regrouping At(double runs) => Regroup(Cluster(Butchery(runs), 4, Colony(nightCook: false), Mood, dull, BedMergePriceTests.Temperate));

        var few = At(0.05);
        Assert.True(Shared(few.Clustering, Pool, Butcher), "a rare butchery shares the barracks");
        Assert.Contains(few.Decisions, d => d.Verdict == LayoutRegroupRules.RegroupApplied && d.Reason == LayoutRegroupRules.ReasonRegroupFilthMerge);

        RegroupDecision? kept = null;
        for (var runs = 1; runs <= 40 && kept is null; runs++)
        {
            kept = At(runs).Decisions.FirstOrDefault(d => d.Reason == LayoutRegroupRules.ReasonRegroupFilthKept);
        }
        Assert.NotNull(kept);
        // No station here cooks a meal, so nothing is poisoned: the stage the sleepers lose outweighs the walk saved.
        Assert.Contains("impressiveness", kept.Effects);
        Assert.Equal(0, kept.Poison);
        Assert.True(kept.Walk < 0 && kept.After > kept.Before, $"walk {kept.Walk}, before {kept.Before}, after {kept.After}");
    }

    [Fact]
    public void AnUnreadRestScheduleOrSleepDisturbedDefFailsTheClusteringNamingIt()
    {
        RequiredFactException Fails(ClusterInput c) => Assert.Throws<RequiredFactException>(() => LayoutClusterRules.ClusterStations(c));

        Assert.Equal("rest_schedule", Fails(Cluster(Cook, 30, null, Mood)).Fact);
        Assert.Equal("sleep_disturbed_thought", Fails(Cluster(Cook, 30, Colony(nightCook: true), Mood with { SleepDisturbed = null })).Fact);
        // A worker's timetable unread (not its 24 hours) or holding an unclassified assignment leaves the schedule unread; night hours are no fallback.
        var unread = RestSchedule.Of([Timetable(_ => PawnRules.ScheduleWork), []]);
        Assert.Null(unread);
        Assert.Null(RestSchedule.Of([Timetable(_ => "ModdedAssignment")]));
        Assert.Equal("rest_schedule", Fails(Cluster(Cook, 30, unread, Mood)).Fact);
    }

    [Fact]
    public void TheScheduleSharesTheVisitsAndTheMemoriesStackToTheirLimit()
    {
        // The night cook makes every visit, all while three of the four workers sleep.
        var night = Assert.IsType<RestSchedule>(Colony(nightCook: true));
        Assert.Equal(0.75, night.DisturbedShare(), 9);
        var day = Assert.IsType<RestSchedule>(Colony(nightCook: false));
        Assert.Equal(0, day.DisturbedShare(), 9);

        // A disturbance every two days keeps half a memory; three a day hold the limit of three.
        Assert.Equal(-0.5, Disturbed.Mood(0.5), 9);
        Assert.Equal(-3, Disturbed.Mood(20), 9);
        Assert.Equal(-1 - 0.5 - 0.25, (Disturbed with { StackedMultiplier = 0.5 }).Mood(3), 9);
    }
}
