using RimGovernor.Brain.Host;
using RimGovernor.Brain.Telemetry;
using RimGovernor.Contracts.Host;
using Clock = RimGovernor.Contracts.Wire.Clock;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>authority/player-speed</c> (#3608): a player's time-control click while the bot runs the clock only asks the brain
/// for the speed, and the brain adopts it. The served brain runs the lab at Hyperfast; the case presses Superfast through
/// <c>rimgovernor/press_time_speed</c> (the handler a button or key runs) and the brain's next Run asks for Superfast
/// (<c>Status.TargetSpeed</c>) while the clock keeps running: no ExternalSpeedChanged hold, no ExternalPause hold. The
/// game launches under test acceleration, which pins the brain at Hyperfast, so the case unpins it with
/// <see cref="InProcEnvironment.UnpinTestSpeed"/>; native's test acceleration stays on.
/// </summary>
internal static class AuthorityPlayerSpeed
{
    /// <summary>Game time the clock must run under the adopted Superfast: it kept running, not merely took one Run.</summary>
    private const int RunAfter = 600;

    private const string Pressed = "Superfast";

    internal static TypedCase Case() => new()
    {
        Name = "authority/player-speed",
        Scope = "With the bot running the clock, a time-control press of Superfast (rimgovernor/press_time_speed) is adopted "
            + "by the served brain: its Run asks for Superfast (Status.TargetSpeed) and the clock keeps running with no "
            + "ExternalSpeedChanged or ExternalPause hold (#3608).",
        Ticks = 3000,
        Tier = CaseTier.OnDemand,

        // "work" keeps a family composed; the claim is the clock's, not a planner's.
        Serve = new ServeSpec
        {
            Families = ["work"],
            Env = [InProcEnvironment.UnpinTestSpeed + "=1"],
            ClockSpeed = nameof(Clock.Speed.Hyperfast),
            Prefix = "player-speed",
        },
        Run = RunAsync,
    };

    private static async Task RunAsync(TypedSession s, CancellationToken cancellationToken)
    {
        TimeSpeedReply? press = null;
        long pressedAt = 0;
        long? adoptedTick = null;
        _ = await s.ServeUntilAsync("adopted", s.TicksLeft, async ct =>
        {
            var status = await TypedAuthority.ClockStatusAsync(s, "clock-status", s.Identity, ct).ConfigureAwait(false);
            var tick = status.Context?.Tick ?? 0;
            if (press is null)
            {
                // The precondition: the brain's own Run at the case's Hyperfast, so a Superfast Run can only be the click's.
                if (status.State != Clock.ClockState.Running || status.TargetSpeed != Clock.Speed.Hyperfast)
                {
                    return ($"before {status.State} {status.TargetSpeed}", false);
                }

                s.RecordWire("clock_before", status);
                pressedAt = ClockRows.NowUnixMs();
                press = await s.PressTimeSpeedAsync("press", new TimeSpeedRequest { Speed = Pressed }, ct).ConfigureAwait(false);
                s.Record("press", ServedOps.Args(("success", press.Success), ("timeSpeed", press.TimeSpeed), ("paused", press.Paused)));
                // A click only asks the brain: the game's own speed is never set to Superfast by it.
                if (!press.Success || press.TimeSpeed == Pressed)
                {
                    throw new AcceptanceException($"press: want a request that leaves the game's speed alone, got success {press.Success} speed {press.TimeSpeed}");
                }

                return ("pressed", false);
            }

            if (status.State == Clock.ClockState.Held && status.Reason is Clock.HoldReason.ExternalSpeedChanged or Clock.HoldReason.ExternalPause)
            {
                s.RecordWire("clock_held", status);
                throw new AcceptanceException($"the click held the clock: {status.Reason} ({status.Detail})");
            }

            if (status.TargetSpeed == Clock.Speed.Superfast && status.State == Clock.ClockState.Running)
            {
                adoptedTick ??= tick;
            }

            var done = adoptedTick is { } adopted && tick >= adopted + RunAfter && status.State == Clock.ClockState.Running
                && status.TargetSpeed == Clock.Speed.Superfast;
            if (done)
            {
                s.RecordWire("clock_after", status);
            }

            return ($"{status.State} {status.TargetSpeed} {status.PlayerSpeedRequestSerial}", done);
        }, cancellationToken).ConfigureAwait(false);

        // A hold between two polls: none since the press was the click's.
        var external = s.Telemetry(TelemetryKinds.ClockHold)
            .Where(row => ClockRows.ObservedSince(row, pressedAt))
            .Select(row => ClockRows.Text(row, ColonyEventTelemetry.ReasonKey))
            .Where(reason => reason is nameof(Clock.HoldReason.ExternalSpeedChanged) or nameof(Clock.HoldReason.ExternalPause))
            .ToList();
        if (external.Count > 0)
        {
            throw new AcceptanceException($"the click held the clock: {string.Join(", ", external)}");
        }
    }

}
