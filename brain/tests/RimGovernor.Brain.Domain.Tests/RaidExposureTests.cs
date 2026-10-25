using System.Text;
using RimGovernor.Brain.Acceptance;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Simulation.CombatMetrics;
using Xunit;
using Fx = RimGovernor.Fixtures.Contracts;
using SimMetrics = RimGovernor.Brain.Simulation.CombatMetrics.Metrics;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>The harm model's exposure step (#3541): per-colonist hits from the ledger, the sweep's stamped staging and the table.</summary>
public sealed class RaidExposureTests
{
    private static MetricsHit Hit(int tick, string victim, string victimSide, string instigator, string instigatorSide) =>
        new(tick, victim, victimSide, instigator, instigatorSide, 5, false, false);

    [Fact]
    public void HitsSplitByVictimAndIgnoreFriendlyFireAndHitsOnHostiles()
    {
        var staged = new Dictionary<string, string> { ["a"] = Sides.Colonist, ["b"] = Sides.Colonist, ["h"] = Sides.Hostile };
        var read0 = new MetricsRead(100) { Pawns = [new("a"), new("b"), new("h")] };
        var read1 = new MetricsRead(200)
        {
            Pawns = [new("a"), new("b", Downed: true), new("h")],
            Damage =
            [
                Hit(110, "a", Sides.Colonist, "h", Sides.Hostile),
                Hit(120, "a", Sides.Colonist, "h", Sides.Hostile),
                Hit(130, "a", Sides.Colonist, "h", Sides.Hostile),
                Hit(140, "b", Sides.Colonist, "h", Sides.Hostile),
                Hit(150, "b", Sides.Colonist, "h", Sides.Hostile),
                Hit(160, "b", Sides.Colonist, "a", Sides.Colonist),
                Hit(170, "h", Sides.Hostile, "a", Sides.Colonist),
            ],
        };
        var staging = new MetricsStaging(new Dictionary<string, string> { ["a"] = ExposureRoles.Gunner, ["b"] = ExposureRoles.Melee }, 55, 65);

        var m = SimMetrics.Aggregate("lab-test", staged, [read0, read1], staging);

        Assert.Equal(2, m.Version);
        Assert.Equal(55, m.RaidPoints);
        Assert.Equal(65, m.DefenseCapacity);
        Assert.Equal(1, m.FriendlyFireHits);
        var rows = m.Colonists!;
        Assert.Collection(
            rows,
            a =>
            {
                Assert.Equal(("a", ExposureRoles.Gunner, 3, false, false), (a.Id, a.Role, a.Hits, a.Downed, a.Dead));
            },
            b =>
            {
                Assert.Equal(("b", ExposureRoles.Melee, 2, true, false), (b.Id, b.Role, b.Hits, b.Downed, b.Dead));
            });
    }

    [Fact]
    public void ARunWithoutAStampedStagingHasNoColonistRows()
    {
        var staged = new Dictionary<string, string> { ["a"] = Sides.Colonist, ["h"] = Sides.Hostile };
        var m = SimMetrics.Aggregate("lab-test", staged, [new MetricsRead(1) { Pawns = [new("a"), new("h")] }]);

        Assert.Null(m.Colonists);
        Assert.Equal(0, m.RaidPoints);
    }

    [Fact]
    public async Task RecomputingACommittedBundleYieldsPerColonistHitsWithoutAGame()
    {
        var dir = Path.Combine(Path.GetTempPath(), "exposure-bundle-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var first = new Fx.LabFightRead
            {
                Tick = 10,
                Pawns = [new() { Id = "c1" }, new() { Id = "h1" }],
                Staged = new Dictionary<string, string> { ["c1"] = Sides.Colonist, ["h1"] = Sides.Hostile },
                Roles = new Dictionary<string, string> { ["c1"] = ExposureRoles.Gunner },
                RaidPoints = 40,
                DefenseCapacity = 80,
            };
            var last = new Fx.LabFightRead
            {
                Tick = 400,
                Pawns = [new() { Id = "c1" }, new() { Id = "h1", Dead = true }],
                Damage =
                [
                    new() { Tick = 20, Victim = "c1", VictimSide = Sides.Colonist, Instigator = "h1", InstigatorSide = Sides.Hostile, Dealt = 4 },
                    new() { Tick = 30, Victim = "c1", VictimSide = Sides.Colonist, Instigator = "h1", InstigatorSide = Sides.Hostile, Dealt = 4 },
                ],
            };
            await File.WriteAllTextAsync(
                Path.Combine(dir, CombatLabBundle.ReadsFile),
                Fx.FixtureJson.Serialize(first) + "\n" + Fx.FixtureJson.Serialize(last) + "\n",
                new UTF8Encoding(false),
                TestContext.Current.CancellationToken);

            var m = await CombatLabBundle.AggregateBundleAsync(dir, "lab-test", TestContext.Current.CancellationToken);

            var c1 = Assert.Single(m.Colonists!);
            Assert.Equal((ExposureRoles.Gunner, 2), (c1.Role, c1.Hits));
            Assert.Equal(0.5, m.RaidPoints / m.DefenseCapacity);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void TheStagedFixtureStampsRolesRaidPointsAndTheDefenseCapacityTheColonyReads()
    {
        var fixture = new LabFixture
        {
            Colonists = 3,
            Pawns =
            [
                new() { Side = CombatFixtures.Colonist },
                new() { Side = CombatFixtures.Colonist },
                new() { Side = CombatFixtures.Colonist, Downed = true },
                new() { Side = CombatFixtures.Hostile },
                new() { Side = CombatFixtures.Hostile },
            ],
        };
        var staged = new StagedFixture { Fixture = fixture };
        staged.Pawns.Add(Pawn("g", Fx.StagedPrimary.Ranged, melee: 3, ranged: 10));
        staged.Pawns.Add(Pawn("m", Fx.StagedPrimary.None, melee: 4, ranged: 0));
        staged.Pawns.Add(Pawn("d", Fx.StagedPrimary.None, melee: 4, ranged: 0) with { Downed = true });
        staged.Pawns.Add(Pawn("x", Fx.StagedPrimary.Ranged, combatPower: 30));
        staged.Pawns.Add(Pawn("y", Fx.StagedPrimary.Melee, combatPower: 25));
        staged.Guns.Add(new Fx.StagedGun(4, true));
        staged.Guns.Add(new Fx.StagedGun(9, false));

        var roles = CombatLabBundle.Roles(staged);

        Assert.Equal(ExposureRoles.Gunner, roles["g"]);
        Assert.Equal(ExposureRoles.Unarmed, roles["m"]);
        Assert.Equal(ExposureRoles.Downed, roles["d"]);
        Assert.Equal(55, CombatLabBundle.RaidPoints(staged));

        // (3 + 10) + 4 melee-and-ranged DPS of the two standing colonists, plus the one powered turret's 4, at 5 points a DPS.
        Assert.Equal((13 + 4 + 4) * DefenseCapacityPolicy.DefensePointsPerDps, CombatLabBundle.DefenseCapacity(staged));

        var first = new System.Text.Json.Nodes.JsonObject();
        CombatLabBundle.Stamp(staged, first);
        var read = Fx.FixtureJson.Deserialize<Fx.LabFightRead>(first.ToJsonString());
        Assert.Equal(55, read.RaidPoints);
        Assert.Equal(105, read.DefenseCapacity);
        Assert.Equal(ExposureRoles.Downed, read.Roles!["d"]);
        Assert.Equal(Sides.Hostile, read.Staged!["x"]);
    }

    private static Fx.StagedPawn Pawn(string id, Fx.StagedPrimary primary, double melee = 0, double ranged = 0, double combatPower = 0) =>
        new() { Id = id, Kind = "k", Health = 1, Primary = primary, MeleeDps = melee, RangedDps = ranged, CombatPower = combatPower, Downed = false, ViolenceCapable = true };

    private static ExposureTable OneCell(int trials) => new(
        RaidExposure.Schema,
        [new ExposureCell("1-2", ExposureRoles.Gunner, trials, 1.5, 0.5, 2.5, 0.2, 0.1, 0.3)]);

    [Fact]
    public void ACalibratedCellAnswersAndTheOthersNameThemselvesUncalibrated()
    {
        var table = OneCell(trials: 20);

        var answer = RaidExposure.HitsPerColonist(table, 1.5, ExposureRoles.Gunner);
        Assert.Equal(new HitRange(1.5, 0.5, 2.5), Assert.IsType<ExposureReading.Calibrated>(answer).Range);
        Assert.Equal([RaidExposure.LayoutUnassessed], answer.Findings);

        var otherRole = RaidExposure.HitsPerColonist(table, 1.5, ExposureRoles.Melee);
        Assert.IsType<ExposureReading.Uncalibrated>(otherRole);
        Assert.Equal(["exposure_cell_uncalibrated:1-2/melee", RaidExposure.LayoutUnassessed], otherRole.Findings);

        var otherBin = RaidExposure.HitsPerColonist(table, 5, ExposureRoles.Gunner);
        Assert.IsType<ExposureReading.Uncalibrated>(otherBin);
        Assert.Equal("exposure_cell_uncalibrated:4+/gunner", otherBin.Findings[0]);

        var outside = RaidExposure.HitsPerColonist(table, -1, ExposureRoles.Gunner);
        Assert.IsType<ExposureReading.Uncalibrated>(outside);
        Assert.Equal("exposure_cell_uncalibrated:outside/gunner", outside.Findings[0]);
    }

    [Fact]
    public void ACellUnderTwentyTrialsIsUncalibrated()
    {
        var answer = RaidExposure.HitsPerColonist(OneCell(trials: 19), 1.5, ExposureRoles.Gunner);

        Assert.IsType<ExposureReading.Uncalibrated>(answer);
        Assert.Equal("exposure_cell_uncalibrated:1-2/gunner", answer.Findings[0]);
    }

    [Theory]
    [InlineData(0, "0-0.5")]
    [InlineData(0.4999, "0-0.5")]
    [InlineData(0.5, "0.5-1")]
    [InlineData(0.9999, "0.5-1")]
    [InlineData(1, "1-2")]
    [InlineData(2, "2-4")]
    [InlineData(3.99, "2-4")]
    [InlineData(4, "4+")]
    [InlineData(1000, "4+")]
    public void RatioBinsIncludeTheirLowerEdgeAndExcludeTheUpper(double ratio, string bin)
    {
        Assert.Equal(bin, RaidExposure.BinOf(ratio));
    }

    [Fact]
    public void TheEmbeddedCalibratedTableLoadsAndAnswersOnlyItsCalibratedCells()
    {
        var table = RaidExposure.Load();

        Assert.Equal(RaidExposure.Schema, table.Schema);
        Assert.All(table.Cells, c => Assert.True(c.Trials >= RaidExposure.MinTrials));

        // A small fight is calibrated too (#3880): lab-ranged-lone with lab-ranged-few for gunners, lab-melee-lone for melee.
        foreach (var (ratio, role) in new[] { (0.7, ExposureRoles.Gunner), (1.5, ExposureRoles.Gunner), (0.05, ExposureRoles.Gunner), (0.05, ExposureRoles.Melee) })
        {
            var reading = RaidExposure.HitsPerColonist(ratio, role);
            var range = Assert.IsType<ExposureReading.Calibrated>(reading).Range;
            Assert.True(range.Lo <= range.Mean && range.Mean <= range.Hi);
            Assert.Equal([RaidExposure.LayoutUnassessed], reading.Findings);
        }

        // A small fight hits a colonist less than a raid matched to the defense does.
        Assert.True(RaidExposure.HitsPerColonist(0.3, ExposureRoles.Gunner) is ExposureReading.Calibrated { Range.Hi: var hi } && hi < 0.5);

        // No committed sweep contains a violence-incapable trial yet (#3969).
        Assert.Equal("exposure_cell_uncalibrated:0-0.5/noncombatant", RaidExposure.HitsPerColonist(0.3, ExposureRoles.Noncombatant).Findings[0]);
    }

    [Fact]
    public void ATableFileWithAnUnknownFieldOrDuplicateCellIsRefused()
    {
        static ExposureTable Parse(string json) => RaidExposure.Parse(new MemoryStream(Encoding.UTF8.GetBytes(json)));

        Assert.ThrowsAny<System.Text.Json.JsonException>(() => Parse("{\"schema\":1,\"cells\":[],\"extra\":1}"));
        var cell = "{\"bin\":\"1-2\",\"role\":\"gunner\",\"trials\":1,\"meanHits\":0,\"hitsLo\":0,\"hitsHi\":0,\"downRate\":0,\"downLo\":0,\"downHi\":0}";
        Assert.Throws<DomainException>(() => Parse("{\"schema\":1,\"cells\":[" + cell + "," + cell + "]}"));
        Assert.Throws<DomainException>(() => Parse("{\"schema\":2,\"cells\":[]}"));
    }

    [Fact]
    public void TheTableIsBuiltFromRowsWithBootstrapAndWilsonRanges()
    {
        var rows = new List<ExposureRow>();
        for (var i = 0; i < 30; i++)
        {
            // Hits cycle 0..4 (mean 2); every fifth colonist ends down.
            rows.Add(new ExposureRow("lab-test", i, 1.5, ExposureRoles.Gunner, i % 5, Downed: i % 5 == 4, Dead: false));
        }

        rows.Add(new ExposureRow("lab-test", 99, null, ExposureRoles.Melee, 7, Downed: false, Dead: true));

        var table = SweepExposure.Build(rows);

        var gunner = Assert.Single(table.Cells, c => c.Role == ExposureRoles.Gunner);
        Assert.Equal(("1-2", 30, 2.0), (gunner.Bin, gunner.Trials, gunner.MeanHits));
        Assert.InRange(gunner.HitsLo, 1.4, 2.0);
        Assert.InRange(gunner.HitsHi, 2.0, 2.6);
        Assert.Equal(6.0 / 30, gunner.DownRate, 4);
        Assert.True(gunner.DownLo < gunner.DownRate && gunner.DownRate < gunner.DownHi);
        var melee = Assert.Single(table.Cells, c => c.Role == ExposureRoles.Melee);
        Assert.Equal(("4+", 1), (melee.Bin, melee.Trials));
        Assert.Equal(1.0, melee.DownRate);
        // A second build from the same rows draws the same bootstrap.
        Assert.Equal(gunner, SweepExposure.Build(rows).Cells.Single(c => c.Role == ExposureRoles.Gunner));

        // The report reads each cell back through the policy reader: the 30-trial cell answers, the 1-trial cell is uncalibrated.
        var report = SweepExposure.Report(table);
        Assert.Contains("1-2/gunner: 30 trials, hits per colonist 2.00", report, StringComparison.Ordinal);
        Assert.Contains("4+/melee: 1 trials, hits per colonist uncalibrated", report, StringComparison.Ordinal);
        Assert.Contains("exposure_cell_uncalibrated:4+/melee", report, StringComparison.Ordinal);
    }

    [Fact]
    public void ASweepYieldsOneRowPerSeedAndColonistWithItsRatio()
    {
        var metrics = SimMetrics.New("lab-test") with
        {
            Ticks = 100, FirstContactTick = 10, OrdersIssued = 1,
            RaidPoints = 60,
            DefenseCapacity = 30,
            Colonists = [new ColonistMetrics { Id = "a", Role = ExposureRoles.Gunner, Hits = 2, Downed = true }, new ColonistMetrics { Id = "b", Role = ExposureRoles.Melee }],
        };
        var sweep = Sweep.NewSweep("lab-test", 1, "", 0, [new SeedRow { Seed = 3, Metrics = metrics }, new SeedRow { Seed = 4, Error = "boom" }]);

        var rows = SweepExposure.Rows(sweep).ToArray();

        Assert.Equal(2, rows.Length);
        Assert.All(rows, r => Assert.Equal((2.0, 3), (r.Ratio, r.Seed)));
        Assert.Equal((ExposureRoles.Gunner, 2, true), (rows[0].Role, rows[0].Hits, rows[0].Downed));
    }

    [Theory]
    [InlineData(0, 0, 1, "")]
    [InlineData(100, -1, 1, "")]
    [InlineData(100, 10, 0, "")]
    [InlineData(100, 10, 1, "failed after scoring")]
    public void AnUnservedOrFailedSeedWritesNoExposureTrials(int ticks, int contact, int orders, string error)
    {
        var metrics = SimMetrics.New("lab-test") with
        {
            Ticks = ticks, FirstContactTick = contact, OrdersIssued = orders,
            Colonists = [new ColonistMetrics { Id = "a", Role = ExposureRoles.Gunner }],
        };
        var sweep = Sweep.NewSweep("lab-test", 1, "", 0, [new SeedRow { Seed = 3, Metrics = metrics, Error = error }]);
        Assert.Empty(SweepExposure.Rows(sweep));
    }
}
