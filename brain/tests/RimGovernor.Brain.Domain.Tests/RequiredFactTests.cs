using System.Reflection;
using RimGovernor.Brain.Acceptance;
using RimGovernor.Brain.BuildingRuntime;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Store;
using RimGovernor.Brain.Telemetry;
using RimGovernor.Brain.Telemetry.Otlp;
using Xunit;
using Decision = RimGovernor.Brain.Telemetry.Decision;
using Journal = RimGovernor.Brain.Store.Store;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// A missing fact fails loudly (#3753 D3, #3757): a planner raising <see cref="RequiredFactException"/> is skipped at the
/// wave's planner boundary with one error-level required_fact row while its peers run; the tests' sink throws on that
/// row; and an acceptance case whose log holds one fails naming the fact.
/// </summary>
public sealed class RequiredFactTests
{
    private static readonly GenerationSnapshot World = new(new("colony"), new(1), new("load"), new("review"), new(1), new(1));

    [Fact]
    public async Task APlannerMissingAFactIsSkippedWithOneRequiredFactRowAndItsPeersStillRun()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = Path.Combine(Path.GetTempPath(), "rg-required-fact-" + Guid.NewGuid().ToString("N") + ".db");
        using var journal = Journal.Open(path, ct);
        using var player = Player.Create(new PlayerConfig(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10)), journal, new Session(), new Worlds(), ct);
        var scheduler = new ClockScheduler(player, DispatchProxy.Create<IClockStepNative, Unreached>(), new ClockSchedulerConfig { MaxAge = TimeSpan.FromSeconds(30) }, new Clock());
        var ran = 0;
        var unread = new PlannerEntry { Name = "test-unread", Configured = _ => true, Run = (_, _, _, _, _, _) => throw new RequiredFactException("outdoor_curve") };
        var peer = new PlannerEntry
        {
            Name = "test-peer",
            Configured = _ => true,
            Run = (_, _, _, _, _, _) =>
            {
                Interlocked.Increment(ref ran);
                return Task.FromResult(Verdict.BuildingReasonNoDeficit);
            },
        };

        var seen = RequiredFactSink.Expect();
        using var wave = new PlannerWave(ct);
        var arbiter = new StepArbiter();
        wave.Queue(scheduler, arbiter, unread, ct);
        wave.Queue(scheduler, arbiter, peer, ct);
        Assert.Empty(await wave.Group.WaitUntilAsync(Task.Delay(TimeSpan.FromSeconds(30), ct)));

        Assert.Equal(["outdoor_curve"], seen);
        Assert.Equal(1, ran);
        Assert.Equal(["test-peer"], wave.FinishedNames());
        Assert.Empty(wave.Group.Failures());
    }

    [Fact]
    public void TheTestSinkThrowsOnARequiredFactRow()
    {
        var e = Assert.Throws<InvalidOperationException>(() => RequiredFactSink.Emitter().Decide(RequiredFactTelemetry.Skipped("clock-scheduler", "test-unread", "outdoor_curve")));
        Assert.Contains("outdoor_curve", e.Message, StringComparison.Ordinal);
        // It is the runtime's static emitter in every test, so a planner under test fails on the skip.
        Assert.Throws<InvalidOperationException>(() => Tel.Decide(RequiredFactTelemetry.Skipped("layout", TelemetryKinds.LayoutStationsKind, "resource demand")));
        // Every other row passes.
        RequiredFactSink.Emitter().Decide(new Decision { Kind = Decision.PlannerStepKind, Verdict = "skipped", Reason = "no_verdict" });
    }

#pragma warning disable CA1852 // DispatchProxy derives the proxy from its base, so the base cannot be sealed
    private class Unreached : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => throw new InvalidOperationException("unreached: " + targetMethod?.Name);
    }
#pragma warning restore CA1852

    private sealed class Session : IPlayerSession
    {
        public Task<GenerationSnapshot> AcquireAsync(GenerationSnapshot requested, CancellationToken ct, NativeGeneration? handoff = null) => Task.FromResult(World);
        public Task ManualAsync(CancellationToken ct) => Task.CompletedTask;
        public Task ManualForResumeAsync(CancellationToken ct) => Task.CompletedTask;
        public bool HoldsGrant(GenerationSnapshot scope) => true;
        public bool TargetsWorld(RimGovernor.Brain.Store.World world) => true;
        public void Disable()
        {
        }
        public ControlState State() => new(World, true, true);
        public Task CloseAsync(CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class Worlds : IWorldSource
    {
        public Task<RimGovernor.Brain.Store.World> ReadWorldAsync(CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class Clock : IClock
    {
        public DateTimeOffset Now() => DateTimeOffset.UnixEpoch;
    }
}

/// <summary>The acceptance runner's required-fact check over a case's OTLP log (#3757); it opens an OTLP writer, so it runs with the span writers.</summary>
[Collection(SpanWriters.Name)]
public sealed class RequiredFactCaseTests
{
    [Fact]
    public async Task ACaseWhoseLogHoldsARequiredFactRowFailsNamingTheFact()
    {
        var output = Path.Combine(Path.GetTempPath(), "rg-required-fact-case-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using (var writer = OtlpFileWriter.Start(new() { Dir = ServiceLogs.OtlpDir(output) }))
            {
                var emitter = new TelemetryEmitter(writer);
                emitter.Decide(new Decision { Kind = Decision.PlannerStepKind, Component = "clock-scheduler", Verdict = "ok", Reason = "no_verdict", Target = "work" });
                emitter.Decide(RequiredFactTelemetry.Skipped("layout", TelemetryKinds.LayoutStationsKind, "outdoor_curve"));
            }

            var failure = RequiredFactRows.Failure(ServiceLogs.ServiceLogsPath(output));
            Assert.NotNull(failure);
            // Only the required_fact row is named; the planner_step row beside it fails nothing.
            Assert.EndsWith("(required_fact): layout_stations: outdoor_curve", failure.Message, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(output))
            {
                Directory.Delete(output, true);
            }
        }
    }
}
