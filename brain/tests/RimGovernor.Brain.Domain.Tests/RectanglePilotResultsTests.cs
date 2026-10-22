using RimGovernor.Brain.Acceptance;
using RimGovernor.Brain.Telemetry;
using RimGovernor.Fixtures.Contracts;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class RectanglePilotResultsTests
{
    private static RectangleCaseResult Result(string run, string phase, bool success, string? error = null) => new()
    {
        RunId = run, Phase = phase, Success = success, Error = error, Evidence = new() { ["rectangle-create"] = "receipt" },
    };

    private static LogRow Row(RectangleCaseResult result) => new()
    {
        Kind = TelemetryKinds.AcceptanceCase.Name,
        Payload = new Dictionary<string, object?> { ["run_id"] = result.RunId, ["result"] = FixtureJson.Serialize(result) },
    };

    [Fact]
    public void InterruptedRunKeepsItsOwnLastEvidenceRatherThanAnOlderSuccess()
    {
        var old = Result("old", "completed", true);
        var started = Result("current", "started", false);
        var progress = Result("current", "rectangle-create", false);
        var result = RectanglePilotResults.Last([Row(old), Row(started), Row(progress), Row(old)], "current");
        Assert.NotNull(result);
        Assert.Equal("receipt", result.Evidence["rectangle-create"]);
        Assert.Equal("rectangle-create", result.Phase);
        Assert.Throws<AcceptanceException>(() => RectanglePilotResults.RequireSuccess("current", result));
        Assert.Null(RectanglePilotResults.Last([Row(old)], "missing"));
    }

    [Theory]
    [InlineData("other", "completed", true, null)]
    [InlineData("current", "started", true, null)]
    [InlineData("current", "completed", false, null)]
    [InlineData("current", "completed", true, "assertion failed")]
    public void InconsistentOrFailedRepliesCannotPass(string run, string phase, bool success, string? error)
    {
        Assert.Throws<AcceptanceException>(() => RectanglePilotResults.RequireSuccess("current", Result(run, phase, success, error)));
    }

    [Fact]
    public void CompletedReplyAndDurableFailureRetainTheirMeanings()
    {
        RectanglePilotResults.RequireSuccess("current", Result("current", "completed", true));
        var failed = Result("current", "rectangle-stockpile", false, "native refusal");
        var recovered = RectanglePilotResults.Last([Row(failed)], "current")!;
        Assert.Equal(failed.Error, recovered.Error);
        Assert.Contains("native refusal", Assert.Throws<AcceptanceException>(() => RectanglePilotResults.RequireSuccess("current", recovered)).Message);
    }
}
