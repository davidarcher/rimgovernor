using System.Globalization;
using RimGovernor.Brain.Bridge;
using Common = RimGovernor.Contracts.Wire.Common;
using Observations = RimGovernor.Contracts.Wire.Observations;
using Operations = RimGovernor.Contracts.Wire.Operations;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>apply/deferred_snapshot</c> (Go <c>deferred_snapshot.go</c>): deferred snapshot capture (#1274). On the paused lab a
/// burst of defer_snapshot applies captures no frame per write where a plain burst does, observations_flush_snapshot
/// reports the uncaptured writes, and the next frame reflects them. The capture schedule is native; a unit test cannot see
/// it. The 1 s safety net is a wall-clock schedule (charter rule 4), so the case does not wait on it.
/// </summary>
internal static class ApplyDeferredSnapshot
{
    /// <summary>How many applies each burst sends.</summary>
    private const int Burst = 8;

    /// <summary>The ticks the flushed frame may take: the stream captures on the resume edge and every 60 ticks.</summary>
    private const int FrameTicks = 120;

    internal static TypedCase Case() => new()
    {
        Name = "apply/deferred_snapshot",
        Scope = "Deferred snapshot capture (#1274): on the paused lab a burst of defer_snapshot applies captures no frame per "
            + "write where a plain burst does, observations_flush_snapshot reports the uncaptured writes, and the next frame "
            + "reflects every write. The capture schedule is native; a unit test cannot see it.",
        Ticks = FrameTicks,
        Tier = CaseTier.OnDemand,
        Run = RunAsync,
    };

    /// <summary>The stream's counters, with the newest frame (and the writes it was captured at) when it is numbered past <paramref name="after"/>.</summary>
    private static async Task<Observations.SnapshotRead> ReadAsync(ITypedSession s, string label, ulong after, CancellationToken cancellationToken)
    {
        var reply = await s.CallAsync(label, NativeOps.ReadSnapshot, new Observations.SnapshotReadRequest { AfterNumber = after }, cancellationToken).ConfigureAwait(false);
        return reply.Outcome?.Read ?? throw new AcceptanceException($"{label}: {reply.Outcome}");
    }

    private static async Task RunAsync(ITypedSession s, CancellationToken cancellationToken)
    {
        // The first read subscribes the stream; each burst sends refused applies (a refusal is still an applied write).
        _ = await ReadAsync(s, "subscribe", ulong.MaxValue, cancellationToken).ConfigureAwait(false);

        async Task<(ulong Frames, long Writes)> BurstAsync(string label, bool deferred)
        {
            var start = await ReadAsync(s, label + "-start", ulong.MaxValue, cancellationToken).ConfigureAwait(false);
            for (var i = 0; i < Burst; i++)
            {
                var key = string.Create(CultureInfo.InvariantCulture, $"{label}-{i}");
                _ = await s.CallAsync(key, NativeOps.Apply, new Operations.ApplyRequest
                {
                    Identity = s.Identity,
                    DeferSnapshot = deferred ? true : null,
                    Actions = [new Operations.Action { Key = key, Intent = Probe() }],
                }, cancellationToken).ConfigureAwait(false);
            }

            var end = await ReadAsync(s, label + "-end", ulong.MaxValue, cancellationToken).ConfigureAwait(false);
            return (end.Head - start.Head, end.Writes);
        }

        var (plain, _) = await BurstAsync("plain", deferred: false).ConfigureAwait(false);
        var (deferredFrames, writes) = await BurstAsync("deferred", deferred: true).ConfigureAwait(false);
        s.Record("plain_burst_frames", plain);
        s.Record("deferred_burst_frames", deferredFrames);

        // A paused game still captures once per wall second, and the plain burst's last frame may publish late, so up to
        // two frames may land inside the deferred burst; one per write may not.
        if (deferredFrames > 2 || deferredFrames >= plain)
        {
            throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"deferred burst captured {deferredFrames} frames (plain burst {plain})"));
        }

        var flushed = await s.CallAsync("flush", NativeOps.FlushFrames, new Observations.FlushSnapshotRequest(), cancellationToken).ConfigureAwait(false);
        s.Record("flush_found_uncaptured", flushed.Flushed);
        var ticks = await s.RunUntilAsync("flushed-frame", FrameTicks, async ct =>
        {
            var read = await ReadAsync(s, "flushed-frame-read", 0, ct).ConfigureAwait(false);
            return (string.Create(CultureInfo.InvariantCulture, $"head {read.Head} captured {read.FrameWrites}"), read.FrameWrites >= writes);
        }, cancellationToken).ConfigureAwait(false);
        s.Record("flushed_frame_ticks", ticks);
    }

    /// <summary>An apply native refuses: a trade with a trader that does not exist.</summary>
    private static Operations.TradeIntent Probe() => new()
    {
        Target = new Common.TradeTarget { Kind = new Common.MapTradeTarget { TraderId = "probe-trader" } },
        NegotiatorId = "probe-negotiator",
        Step = new Operations.EndTrade { Kind = Operations.EndTradeKind.Cancel },
    };
}
