using RimGovernor.Contracts.Wire;
using System.Diagnostics;
using RimGovernor.Brain.Bridge;
using RimGovernor.Brain.Bridge.Contracts;
using Clock = RimGovernor.Contracts.Wire.Clock;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>smoke/clock-stall</c> (#3350): the runner's rate-based stall guard. A clock that keeps ticking, but far below the
/// case's floor, changes a tick-carrying signature on every probe, so the signature stall never fires; the rate guard
/// ends the wait within one stall budget and names the clock. The case injects the crawl: once the native clock runs
/// at Hyperfast it changes the Run in place to Normal (60 ticks/s), under a declared floor of twice that.
/// </summary>
internal static class SmokeClockStall
{
    // Twice Normal: the injected Normal clock is the crawl.
    private const double Floor = 120;

    internal static TypedCase Case() => new()
    {
        Name = "smoke/clock-stall",
        Scope = "Runner smoke: a native clock run slowed in place from Hyperfast to Normal, under a declared floor of 120 "
            + "ticks/s, fails its wait within one stall budget with the clock-rate failure naming the clock's state and "
            + "target speed, not at the tick budget or the hang guard. The guard is the runner's own path, which only a "
            + "ticking game exercises.",
        Ticks = 12_000,
        Tier = CaseTier.OnDemand,
        MinTicksPerSecond = Floor,
        Run = RunAsync,
    };

    private static async Task RunAsync(TypedSession s, CancellationToken cancellationToken)
    {
        var generation = await TypedAuthority.GrantAutoGenerationAsync(s, cancellationToken).ConfigureAwait(false);
        var request = new Clock.RunRequest
        {
            ExpectedGeneration = generation,
            Speed = Clock.Speed.Hyperfast,
            Policy = ClockExpectation.WithHazardThresholds(new Clock.WatchPolicy
            {
                Mode = Clock.WatchMode.Colony,
                HealthDropFraction = 0.5f,
                MinHealthFraction = 0.2f,
                HostileWithin = 40,
                InjuryStopCooldownMs = 0,
            }),
        };

        var slowed = false;
        var clock = Stopwatch.StartNew();
        AcceptanceException failure;
        try
        {
            var held = await s.RunClockUntilHeldAsync(
                "crawl",
                s.TicksLeft,
                request,
                async (running, token) =>
                {
                    if (slowed)
                    {
                        return false;
                    }

                    // In place: a Run while the clock runs changes its speed and keeps the segment (no hold).
                    var reply = await s.CallAsync(
                        "slow",
                        NativeOps.RunClock,
                        request with
                        {
                            Identity = s.Identity,
                            Speed = Clock.Speed.Normal,
                            PlannedHoldSerial = running.HoldSerial,
                            HoldAtTick = (ulong)(Math.Max(running.Context?.Tick ?? 0, 0) + s.TicksLeft),
                        },
                        token).ConfigureAwait(false);
                    var status = reply is { State: Clock.ClockState.Running, TargetSpeed: Clock.Speed.Normal }
                        ? reply
                        : throw new AcceptanceException($"slow: want a running clock at Normal, got {WireJson.Serialize(reply)}");
                    s.RecordWire("slowed", status);
                    slowed = true;
                    clock.Restart();
                    return false;
                },
                cancellationToken).ConfigureAwait(false);
            throw new AcceptanceException($"the crawling clock ended its wait without the rate guard: {held}");
        }
        catch (AcceptanceException e) when (slowed && e.Message.Contains("clock too slow", StringComparison.Ordinal))
        {
            failure = e;
        }

        var took = clock.Elapsed;
        s.Record("guard", new System.Text.Json.Nodes.JsonObject { { "error", failure.Message }, { "after_ms", (long)took.TotalMilliseconds } });

        // One window from the slow-down, plus the probe and status read that end it.
        if (took > Wait.StallBudget() * 1.5)
        {
            throw new AcceptanceException($"the rate guard fired {took.TotalSeconds:0}s after the slow-down, past one stall budget: {failure.Message}");
        }

        if (!failure.Message.Contains("clock Running", StringComparison.Ordinal) || !failure.Message.Contains("target Normal", StringComparison.Ordinal))
        {
            throw new AcceptanceException($"the rate failure does not name the running Normal clock: {failure.Message}");
        }
    }
}
