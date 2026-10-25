using RimGovernor.Brain.GameAccess;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class ReadTallyTests
{
    [Fact]
    public void NestedTallyCountsItsOwnReadsAndForwardsThemToTheStep()
    {
        using var step = ReadTallyScope.WithReadTally();
        ReadTallyRules.CountNativeRead("ReadStatusAsync");
        ReadTally planner;
        using (var scope = ReadTallyScope.Nested())
        {
            planner = scope.Tally;
            ReadTallyRules.CountNativeRead("ListPawnsAsync");
            ReadTallyRules.CountHop("pawns", 3.0, 1.5, 4096);
            ReadTallyRules.CountHopPart("pawns/paths", 0.9);
        }
        ReadTallyRules.CountHop("status", 0.5, 0.25, 2048);

        var p = planner.Attrs();
        Assert.Equal(1UL, p["reads"]);
        Assert.Equal(3.0, p["hop_wait_ms"]);
        Assert.Equal(1.5, p["hop_run_ms"]);
        Assert.Equal(1UL, ((Dictionary<string, object?>)p["tools"]!)["ListPawnsAsync"]);
        Assert.Equal(4L, p["hop_alloc_kb"]);
        Assert.Equal(4L, ((Dictionary<string, object?>)p["hop_kb"]!)["pawns"]);
        Assert.Equal(1.5, ((Dictionary<string, object?>)p["hop_run"]!)["pawns"]);
        // A hop's part is listed beside the hops and summed into no total.
        Assert.Equal(0.9, ((Dictionary<string, object?>)p["hop_run"]!)["pawns/paths"]);
        Assert.False(((Dictionary<string, object?>)p["hop_kb"]!).ContainsKey("pawns/paths"));

        var s = step.Tally.Attrs();
        Assert.Equal(2UL, s["reads"]);
        Assert.Equal(3.5, s["hop_wait_ms"]);
        Assert.Equal(1.8, s["hop_run_ms"]);
        Assert.Equal(6L, s["hop_alloc_kb"]);
        Assert.Equal(0.9, ((Dictionary<string, object?>)s["hop_run"]!)["pawns/paths"]);
    }
}
