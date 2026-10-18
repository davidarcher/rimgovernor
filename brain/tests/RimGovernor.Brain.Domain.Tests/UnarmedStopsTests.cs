using System.Text;
using RimGovernor.Brain.Acceptance;
using Xunit;
using SimMetrics = RimGovernor.Brain.Simulation.CombatMetrics.Metrics;

namespace RimGovernor.Brain.Domain.Tests;

// #3551: a sweep seed whose combat watch stopped the clock while a colonist went down, with no stop on a combat event,
// played its fight with the stops unarmed; the sweep flags it.
public sealed class UnarmedStopsTests
{
    private static string Row(string kind, int tick, params (string Key, string Value)[] attrs)
    {
        var a = new StringBuilder();
        a.Append($$$"""{"key":"rimgovernor.kind","value":{"stringValue":"{{{kind}}}"}},{"key":"rimgovernor.row","value":{"stringValue":"event"}},{"key":"rimgovernor.tick","value":{"intValue":"{{{tick}}}"}}""");
        foreach (var (key, value) in attrs)
        {
            a.Append($$$""",{"key":"rimgovernor.attr.{{{key}}}","value":{{{value}}}}""");
        }

        return $$$"""{"resourceLogs":[{"resource":{"attributes":[]},"scopeLogs":[{"scope":{"name":"t"},"logRecords":[{"timeUnixNano":"{{{1_000_000_000L + tick}}}","severityNumber":9,"severityText":"INFO","body":{"stringValue":"r"},"attributes":[{{{a}}}],"eventName":"{{{kind}}}"}]}]}]}""";
    }

    private static string Stop(int tick, string kind) => Row("clock_stop", tick, ("event", $$$"""{"stringValue":"{{{kind}}}"}"""));

    private static string CombatRun(int tick) => Row("clock_run", tick, ("combat", """{"boolValue":true}"""));

    private static string? Flag(SimMetrics m, params string[] rows)
    {
        var path = Path.Combine(Path.GetTempPath(), "unarmed-stops-" + Guid.NewGuid().ToString("N") + ".jsonl");
        try
        {
            File.WriteAllText(path, string.Join("\n", rows) + "\n", new UTF8Encoding(false));
            return CombatLabBundle.UnarmedStops(m, path);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ADownedColonistWithOnlyReviewStopsIsFlaggedWithItsStops()
    {
        var flag = Flag(new SimMetrics { ColonistDowns = 1 }, CombatRun(10), Stop(600, "review"), Stop(1200, "review"), Stop(1300, "notification_batch"));

        Assert.NotNull(flag);
        Assert.Contains("#3551", flag, StringComparison.Ordinal);
        Assert.Contains("notification_batch 1, review 2", flag, StringComparison.Ordinal);
        Assert.Contains("1 combat clock_run rows, 0 combat_order rows", flag, StringComparison.Ordinal);
    }

    [Fact]
    public void AnArmedStopClearsTheSeed() =>
        Assert.Null(Flag(new SimMetrics { ColonistDowns = 1 }, Stop(100, "serious_injury"), Stop(600, "review")));

    [Fact]
    public void AFightThatHurtNoColonistOrNeverStoppedIsNotFlagged()
    {
        Assert.Null(Flag(new SimMetrics(), Stop(600, "review")));
        Assert.Null(Flag(new SimMetrics { ColonistDeaths = 1 }, CombatRun(10)));
    }
}
