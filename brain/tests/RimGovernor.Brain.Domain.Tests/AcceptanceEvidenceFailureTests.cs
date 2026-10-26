using System.Text.Json.Nodes;
using RimGovernor.Brain.Acceptance;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class AcceptanceEvidenceFailureTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rg-evidence-failure-" + Guid.NewGuid().ToString("N"));
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public AcceptanceEvidenceFailureTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, true);

    [Fact]
    public void UnwrittenEvidenceNamesArtifactWithoutReplacingNativeFailure()
    {
        var path = Path.Combine(_root, "call.json");
        File.WriteAllText(path, "earlier evidence");
        using var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
        using var diagnostics = new StringWriter();
        var primary = new AcceptanceException("native refusal");
        var observed = Assert.Throws<AcceptanceException>((Action)(() =>
        {
            try { throw primary; }
            finally { Assert.False(EvidenceBudget.WriteEvidence(path, new() { ["error"] = primary.Message }, diagnostics)); }
        }));
        Assert.Same(primary, observed);
        Assert.Contains(path, diagnostics.ToString(), StringComparison.Ordinal);
        Assert.Contains("artifact missing", diagnostics.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void HashFailureIsNamedAndOtherArtifactsStillHaveHashes()
    {
        var blocked = Path.Combine(_root, "blocked.json");
        File.WriteAllText(blocked, "{}");
        File.WriteAllText(Path.Combine(_root, "readable.json"), "{}");
        var report = new Report();
        report["error"] = "primary case failure";
        using var held = new FileStream(blocked, FileMode.Open, FileAccess.Read, FileShare.None);
        var hashes = EvidenceBudget.Artifacts(_root, report)!;
        Assert.True(hashes.ContainsKey("readable.json"));
        Assert.False(hashes.ContainsKey("blocked.json"));
        Assert.Equal("primary case failure", report.Data["error"]!.GetValue<string>());
        var error = Assert.Single(report.Data["evidence_errors"]!.AsArray());
        Assert.Equal(blocked, error!["path"]!.GetValue<string>());
    }

    [Fact]
    public void UnwrittenReportRetainsCaseVerdictAndReportsFailedPath()
    {
        var path = Path.Combine(_root, "result.json");
        File.WriteAllText(path, "{}");
        using var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
        var report = new Report();
        report["passed"] = false;
        report["error"] = "case failed first";
        report.Write(_root);
        Assert.False(report.Passed);
        Assert.Equal("case failed first", report.Data["error"]!.GetValue<string>());
        Assert.Equal(path, Assert.Single(report.Data["evidence_errors"]!.AsArray())!["path"]!.GetValue<string>());
    }

    [Fact]
    public void MalformedPostmortemAndMetricSeriesIdentifyTheUnreadEvidence()
    {
        var path = Path.Combine(_root, "result.json");
        File.WriteAllText(path, "invalid-json");
        var digest = Postmortems.Collect(_root, null, new CaseRows("", null), Ct);
        Assert.Contains(digest.Sections, section => section.Name == "report" && section.Note.Contains(path, StringComparison.Ordinal));
        var series = Path.Combine(_root, "series.jsonl");
        File.WriteAllText(series, "invalid-json");
        var failure = Assert.Throws<IOException>(() => MetricSeries.ReadSeries(series));
        Assert.Contains(series + " line 1", failure.Message, StringComparison.Ordinal);
    }

    private sealed class ManualTime : TimeProvider
    {
        internal long Timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Timestamp;
    }

    [Fact]
    public void UnreadSaveKeepsKnownSeedAndStatesWhyTheHashIsAbsent()
    {
        var config = new Config { Root = _root };
        var record = WorldRecords.Record(config, new SaveStart { Name = "missing" }, new() { Save = "missing", Seed = "known" });
        Assert.Equal("known", record.Seed);
        Assert.Empty(record.SaveSha256);
        Assert.Contains(config.ProfileSave("missing"), record.SaveReadError, StringComparison.Ordinal);
        Assert.Equal(record.SaveReadError, record.ToJson()["save_read_error"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CheckpointPollRecoversOrNamesLastFailureAtOriginalDeadline(bool timeout)
    {
        var config = new Config { Root = _root };
        var time = new ManualTime();
        var reads = 0;
        var resumed = false;
        var holds = new List<bool>();
        var transient = new AcceptanceException("state read failed");
        Task<ServiceApiReply> Api(string method, string path, JsonObject? body, string token, CancellationToken ct)
        {
            if (path == "/api/state")
            {
                reads++;
                if (reads == 1)
                {
                    if (timeout) { time.Timestamp += TimeSpan.FromSeconds(31).Ticks; }
                    throw transient;
                }
                return Task.FromResult(new ServiceApiReply(new() { ["mode"] = "manual" }, 200));
            }
            if (path == "/api/lifecycle/save")
            {
                var save = config.ProfileSave("checkpoint");
                Directory.CreateDirectory(Path.GetDirectoryName(save)!);
                File.WriteAllText(save, "saved");
                return Task.FromResult(new ServiceApiReply(new(), 201));
            }
            resumed |= path == "/api/player/control/resume";
            return Task.FromResult(new ServiceApiReply(new(), 200));
        }
        var saving = CheckpointRing.ServiceSaveAsync(config, "checkpoint", holds.Add, Api, null, "", "fault-test", Ct, time);
        if (timeout)
        {
            var failure = await Assert.ThrowsAsync<AcceptanceException>(() => saving);
            Assert.Same(transient, failure.InnerException);
            Assert.Contains(transient.Message, failure.Message, StringComparison.Ordinal);
            Assert.Equal(1, reads);
        }
        else
        {
            Assert.Equal(config.ProfileSave("checkpoint"), await saving);
            Assert.Equal(2, reads);
        }
        Assert.True(resumed);
        Assert.Equal([true, false], holds);
    }
}
