using RimGovernor.Contracts.Wire;
using System.Globalization;
using RimGovernor.Brain.Bridge;
using RimGovernor.Brain.Bridge.Contracts;
using Clock = RimGovernor.Contracts.Wire.Clock;
using Common = RimGovernor.Contracts.Wire.Common;
using Control = RimGovernor.Contracts.Wire.Authority;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>authority/warm</c> (#119): authority continuity when a controller dies with Auto granted and a clock run going.
/// One process is launched; the first controller loads the lab, grants Auto, starts a clock run and drops its session
/// with the run going. The reusable-game lifecycle then loads the lab again into the same process (a fresh load token):
/// the next controller must keep its own Auto grant through an hour of running game time. This catches native run state
/// retaining a disposed game. Nothing durable from the first run survives a kept process, by design.
/// </summary>
internal static class AuthorityWarm
{
    /// <summary>How long the second grant must stay active, in game time: an in-game hour the game actually runs.</summary>
    private const int HoldTicks = Wait.TicksPerHour;

    internal static TypedCase Case() => new()
    {
        Name = "authority/warm",
        Scope = "A process that hosted a controller dropped mid-run (authority Auto, a clock run going, session released) "
            + "reloads the lab under a fresh load token; the next controller's Auto grant reads active at the granted "
            + "generation on every read through an in-game hour of running time (#119).",
        Ticks = 2 * HoldTicks,
        Tier = CaseTier.OnDemand,
        Start = new OwnedStart(),
        ClaimsStart = new StartClaim { Launches = 1 },
        Run = RunAsync,
    };

    private static async Task RunAsync(TypedSession s, CancellationToken cancellationToken)
    {
        var reuse = await s.LaunchAsync("launch", cancellationToken).ConfigureAwait(false);
        try
        {
            // The first controller: the lab, Auto, a running clock; then its session drops with all three in place.
            var h = await reuse.SessionAsync(cancellationToken).ConfigureAwait(false);
            var labSave = await LabStarts.StartLabAsync(reuse.Config, SessionHost.Default, h, cancellationToken).ConfigureAwait(false);
            var (first, _, _) = await TypedLifecycle.IdentityAsync(s, "p1-identity", cancellationToken).ConfigureAwait(false);
            var expected = await TypedAuthority.ExpectedGenerationAsync(s, "p1-status", first, cancellationToken).ConfigureAwait(false);
            var generation = await TypedAuthority.GrantAsync(s, "p1-grant", first, expected, cancellationToken).ConfigureAwait(false);
            var before = await TypedAuthority.ClockStatusAsync(s, "p1-clock-status", first, cancellationToken).ConfigureAwait(false);
            var run = await s.CallAsync("p1-clock-run", NativeOps.RunClock, new Clock.RunRequest
            {
                Identity = first,
                ExpectedGeneration = generation,
                Speed = Clock.Speed.Superfast,
                Policy = ClockExpectation.WithHazardThresholds(new Clock.WatchPolicy { Mode = Clock.WatchMode.Colony, HealthDropFraction = 0.5f, MinHealthFraction = 0.2f, HostileWithin = 40, InjuryStopCooldownMs = 0 }),
                PlannedHoldSerial = before.HoldSerial,
                // A review a day out: the case runs an hour and drops the session before the clock reaches it.
                HoldAtTick = (ulong)(Math.Max(before.Context?.Tick ?? 0, 0) + 60_000),
            }, cancellationToken).ConfigureAwait(false);
            if (run is not { State: Clock.ClockState.Running } running)
            {
                throw new AcceptanceException($"p1-clock-run: want a running clock, got {WireJson.Serialize(run)}");
            }

            s.RecordWire("p1_clock", running);
            await reuse.ReleaseSessionAsync().ConfigureAwait(false);

            // The next controller: the same process, the lab loaded again.
            var c = await reuse.BeginCaseAsync("next-controller", labSave, Path.Combine(reuse.Config.Output, "next-controller"), cancellationToken).ConfigureAwait(false);
            var failed = true;
            try
            {
                var next = Harness.IdentityOf(c.Identity);
                if (next.LoadToken == first.LoadToken)
                {
                    throw new AcceptanceException($"the reload kept load token {first.LoadToken}");
                }

                await HoldAsync(s, next, cancellationToken).ConfigureAwait(false);
                failed = false;
            }
            finally
            {
                await reuse.EndCaseAsync(c, failed, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            using var retire = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            try
            {
                await reuse.RetireAsync("run complete", retire.Token).ConfigureAwait(false);
            }
            catch (AcceptanceException tolerated)
            {
                Tolerated.Note("retire", tolerated);
            }
            catch (OperationCanceledException tolerated)
            {
                Tolerated.Note("retire", tolerated);
            }
        }
    }

    /// <summary>Grants Auto and requires every status read through <see cref="HoldTicks"/> of running time to stay Active(Auto) at that generation; then revokes.</summary>
    private static async Task HoldAsync(TypedSession s, Common.Identity identity, CancellationToken cancellationToken)
    {
        var expected = await TypedAuthority.ExpectedGenerationAsync(s, "p2-status", identity, cancellationToken).ConfigureAwait(false);
        var generation = await TypedAuthority.GrantAsync(s, "p2-grant", identity, expected, cancellationToken).ConfigureAwait(false);
        long? start = null;
        var reads = 0;
        var held = await s.RunUntilAsync("p2-hold", s.TicksLeft, async ct =>
        {
            var status = await TypedAuthority.StatusAsync(s, string.Create(CultureInfo.InvariantCulture, $"p2-hold-{reads:D3}"), identity, ct).ConfigureAwait(false);
            reads++;
            if (status.State?.Active?.Mode != Control.Mode.Auto || status.Context?.NativeGeneration.IsAt(generation) != true)
            {
                s.RecordWire("p2_loss", status);
                s.RecordWire("p2_clock_at_loss", await TypedAuthority.ClockStatusAsync(s, "p2-clock-at-loss", identity, ct).ConfigureAwait(false));
                throw new AcceptanceException($"lost the Auto grant at generation {generation} after {reads} reads: {status}");
            }

            var tick = status.Context.Tick;
            start ??= tick;
            return (string.Empty, tick - start >= HoldTicks);
        }, cancellationToken).ConfigureAwait(false);
        s.Record("p2_held", ServedOps.Args(("reads", reads), ("ticks", held)));
        var release = await TypedAuthority.StatusAsync(s, "p2-release-status", identity, cancellationToken).ConfigureAwait(false);
        var revoke = await s.CallAsync("p2-release", NativeOps.ControlAuthority, new Control.ControlRequest
        {
            Operation = new Control.Revoke { Identity = identity, ExpectedGeneration = (release.Context ?? throw new AcceptanceException("p2-release: the status carried no context")).NativeGeneration.Claimed("p2-release: the status named no generation"), Reason = Control.RevocationReason.Manual },
        }, cancellationToken).ConfigureAwait(false);
        if (revoke.Outcome?.Revoked is null)
        {
            throw new AcceptanceException($"p2-release: want revoked, got {revoke.Outcome}");
        }
    }

}
