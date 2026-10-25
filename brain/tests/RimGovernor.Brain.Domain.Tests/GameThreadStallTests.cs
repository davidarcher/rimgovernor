using RimGovernor.Native;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// #3576: a game thread lost inside a frame left five minutes of "hop queued" lines and no stack. The watchdog's stall
/// line now carries the thread's breadcrumb, and one stall per process writes a minidump; the formatting and the dump
/// decision are game-free and pinned here.
/// </summary>
public sealed class GameThreadStallTests
{
    [Fact]
    public void BreadcrumbNamesThePhaseAndItsAge() =>
        Assert.Equal("game thread last seen 12.3s ago in: snapshot capture after cf.upkeep.comfort",
            GameThreadTrace.Describe("snapshot capture after cf.upkeep.comfort", 12_300, 1_000));

    [Fact]
    public void BreadcrumbIsUnknownBeforeAnyMark() =>
        Assert.Equal("game thread phase unknown", GameThreadTrace.Describe(null, 5_000, 1_000));

    [Fact]
    public void DumpWaitsForBothTheQueueAndTheSilence()
    {
        var policy = new StallDumpPolicy();
        Assert.False(policy.Due(StallDumpPolicy.DumpAfterSeconds - 0.1, 60));
        Assert.False(policy.Due(60, StallDumpPolicy.DumpAfterSeconds - 0.1));
        Assert.False(policy.Fired);
        Assert.True(policy.Due(StallDumpPolicy.DumpAfterSeconds, StallDumpPolicy.DumpAfterSeconds));
        Assert.True(policy.Fired);
    }

    [Fact]
    public void DumpFiresOncePerProcess()
    {
        var policy = new StallDumpPolicy();
        Assert.True(policy.Due(30, 30));
        Assert.False(policy.Due(300, 300));
    }

    [Theory]
    [InlineData(null, false, true)]
    [InlineData("1", false, true)]
    [InlineData("on", false, true)]
    [InlineData("0", false, false)]
    [InlineData(" off ", false, false)]
    [InlineData("FALSE", false, false)]
    [InlineData("no", false, false)]
    [InlineData(null, true, false)]
    [InlineData("1", true, false)]
    public void DumpIsOnUnlessTurnedOffOrATestLaunch(string? variable, bool testLaunch, bool expected) =>
        Assert.Equal(expected, StallDump.Enabled(variable, testLaunch));

    [Fact]
    public void CommandWritesAHeapMinidumpOfThePidToTheNamedFile()
    {
        var path = Path.Combine("D:\\run", StallDump.FileName(62300, new DateTime(2026, 10, 18, 19, 55, 2, DateTimeKind.Utc)));
        Assert.Equal("D:\\run\\stall-62300-20261018-195502.dmp", path);
        Assert.Equal("--withheap --name \"D:\\run\\stall-62300-20261018-195502.dmp\" 62300", StallDump.Arguments(path, 62300));
    }

    [Fact]
    public void ToolSitsBesideTheGameExecutable()
    {
        Assert.Equal(Path.Combine("D:\\game", "createdump.exe"), StallDump.ToolPath("D:\\game\\RimWorld.exe"));
        Assert.Null(StallDump.ToolPath(null));
        Assert.Null(StallDump.ToolPath(""));
    }

    private static readonly string[] AcceleratedLaunch = { "RimWorld.exe", "-rimgovernor-inproc", "-rimgovernor-test-acceleration" };
    private static readonly string[] PlayLaunch = { "RimWorld.exe", "-rimgovernor-inproc" };

    [Fact]
    public void TestAccelerationLaunchIsReadFromTheCommandLine()
    {
        Assert.True(StallDump.TestAccelerationLaunch(AcceleratedLaunch));
        Assert.False(StallDump.TestAccelerationLaunch(PlayLaunch));
    }
}
