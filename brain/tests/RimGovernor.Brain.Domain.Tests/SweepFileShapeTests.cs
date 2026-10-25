using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RimGovernor.Brain.Acceptance;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Simulation.CombatMetrics;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// The committed sweep files and the checkpoint index keep their exact shape through the typed records (#3410): each
/// file reads into its record and writes back byte for byte.
/// </summary>
public sealed class SweepFileShapeTests
{
    [Fact]
    public void TheProductionExposureEstimatesUseEveryRecordedRaidFromDisjointEvidenceRounds()
    {
        var directory = Path.Combine(RepositoryRoot(), SweepCommand.CalibrationDir, "exposure");
        var training = SweepExposure.ReadRows(Path.Combine(directory, "rows-training.jsonl"));
        var validation = SweepExposure.ReadRows(Path.Combine(directory, "rows-validation.jsonl"));
        // These retain the historical fit/check partition. Both rounds now contribute to the
        // explicitly qualified empirical estimate; neither is claimed as a new untouched holdout.
        var fitted = SweepExposure.Build(training.Concat(validation).ToArray());
        Assert.Equal(fitted.Cells.ToArray(), RaidExposure.Load().Cells.ToArray());

        // A seed's pawns share one fight: no pawn from that fight may leak into the other set.
        var trainingFights = training.Select(r => (r.Fixture, r.Seed)).ToHashSet();
        Assert.DoesNotContain(validation.Select(r => (r.Fixture, r.Seed)).Distinct(), trainingFights.Contains);
        foreach (var cell in fitted.Cells)
        {
            Assert.True(cell.Raids >= RaidExposure.MinRaids);
            var binIndex = RaidExposure.Bins.ToList().IndexOf(cell.Bin);
            var ratio = binIndex == 0 ? 0 : RaidExposure.Edges[binIndex - 1];
            var reading = Assert.IsType<ExposureReading.Calibrated>(RaidExposure.HitsPerColonist(ratio, cell.Role));
            Assert.Contains(RaidExposure.EmpiricalEstimate, reading.Findings);
            Assert.Contains(RaidExposure.StabilityUnconfirmed, reading.Findings);
        }
    }

    public static TheoryData<string> SweepFiles()
    {
        var root = RepositoryRoot();
        var data = new TheoryData<string>();
        foreach (var file in Directory.GetFiles(Path.Combine(root, SweepCommand.BaselinesDir), "*.json")
            .Concat(Directory.GetFiles(Path.Combine(root, SweepCommand.CalibrationDir, "variants"), "*.json", SearchOption.AllDirectories))
            .Order(StringComparer.Ordinal))
        {
            data.Add(Path.GetRelativePath(root, file).Replace('\\', '/'));
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(SweepFiles))]
    public void ACommittedSweepFileRoundTripsUnchanged(string file)
    {
        var text = Normalized(File.ReadAllText(Path.Combine(RepositoryRoot(), file)));
        var sweep = Sweep.ParseSweep(text);

        // Indented output breaks lines with the platform's newline; the checkout normalizes them either way.
        Assert.Equal(text, Normalized(JsonSerializer.Serialize(sweep, SweepJsonContext.Default.Sweep) + "\n"));
    }

    [Fact]
    public void TheCommittedShotDatasetRestampsUnchanged()
    {
        var text = Normalized(File.ReadAllText(Path.Combine(RepositoryRoot(), SweepCommand.CalibrationDir, "shots", "lab-ranged.jsonl")));
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var seeds = lines.Select(l => JsonNode.Parse(l)!.AsObject().TryGetPropertyValue("seed", out var seed) ? seed!.GetValue<int>() : 0).ToList();
        Assert.NotEmpty(lines);

        // The dataset already carries each impact's dealt damage, so restamping with an empty ledger keeps every row.
        var restamped = new StringBuilder();
        for (var start = 0; start < lines.Length;)
        {
            var end = start;
            while (end < lines.Length && seeds[end] == seeds[start])
            {
                end++;
            }

            using var writer = new StringWriter(restamped) { NewLine = "\n" };
            CombatLabShots.StampShots(writer, seeds[start], string.Join('\n', lines[start..end]), []);
            start = end;
        }

        Assert.Equal(text, restamped.ToString());
    }

    [Fact]
    public void AShotRowWithAFieldItsKindDoesNotDeclareIsRefused()
    {
        var e = Assert.Throws<AcceptanceException>(() =>
            CombatLabShots.StampShots(TextWriter.Null, 1, "{\"k\":\"draft\",\"tick\":0,\"pawn\":\"p\",\"drafted\":true,\"extra\":1}", []));

        Assert.Contains("line 1 (draft)", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnImpactIsStampedWithTheDamageItsShooterDealt()
    {
        var bundle = "{\"k\":\"shot\",\"tick\":5,\"id\":7,\"shooter\":\"a\"}\n{\"k\":\"impact\",\"tick\":9,\"id\":7,\"hit\":\"b\",\"dealt\":0}";
        var output = new StringWriter { NewLine = "\n" };

        CombatLabShots.StampShots(output, 3, bundle, [new MetricsHit(9, "b", "hostile", "a", "colonist", 12.5, false, false)]);

        Assert.Equal(
            "{\"seed\":3,\"k\":\"shot\",\"tick\":5,\"id\":7,\"shooter\":\"a\",\"side\":\"\",\"x\":0,\"z\":0,\"target\":\"\",\"tx\":0,\"tz\":0,\"dx\":0,\"dz\":0,\"burst\":0,\"of\":0,\"weapon\":\"\",\"proj\":\"\",\"tti\":0}\n"
            + "{\"seed\":3,\"k\":\"impact\",\"tick\":9,\"id\":7,\"hit\":\"b\",\"x\":0,\"z\":0,\"dealt\":12.5}\n",
            output.ToString());
    }

    [Fact]
    public void TheCheckpointIndexKeepsItsShape()
    {
        var dir = Directory.CreateTempSubdirectory("ring-shape-").FullName;
        try
        {
            var entry = new Checkpoint
            {
                Case = "c",
                Label = "t+7m",
                OffsetMs = 420000,
                Tick = 9000,
                Save = "c-t7m",
                Store = true,
                Identity = new JsonObject { { "colonyId", "a" } },
                Prepared = new JsonObject(),
                SourceRevision = "rev",
                Package = "pkg",
                Start = "start",
                Expansions = ["ludeon.rimworld.ideology"],
                Through = "service",
                At = "2026-10-17T00:00:00Z",
                WallMs = 12,
            };
            var ring = new Ring { Case = "c", Entries = [entry], FailedTick = 0, Next = "t+7m", SourceRevision = "rev" };

            ring.Write(dir);
            var text = Normalized(File.ReadAllText(Path.Combine(dir, Checkpoints.CheckpointRingFile)));
            var read = Ring.Read(dir)!;
            read.Write(dir);

            Assert.Equal(text, Normalized(File.ReadAllText(Path.Combine(dir, Checkpoints.CheckpointRingFile))));
            Assert.Equal(Path.Combine(dir, "t+7m"), read.Entries[0].Path);
            Assert.Equal(
                ["case", "entries", "failed_tick", "failed_offset_ms", "next", "source_revision"],
                JsonNode.Parse(text)!.AsObject().Select(p => p.Key));
            Assert.Equal(
                ["case", "label", "offset_ms", "tick", "save", "store", "identity", "source_revision", "package", "start", "expansions", "through", "at", "wall_ms"],
                JsonNode.Parse(entry.ToJsonText())!.AsObject().Select(p => p.Key));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static string Normalized(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal);

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "AGENTS.md")) && Directory.Exists(Path.Combine(directory.FullName, SweepCommand.BaselinesDir)))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException($"no repository root above {AppContext.BaseDirectory}");
    }
}
