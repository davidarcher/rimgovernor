using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance;

/// <summary>
/// <c>apply/deferred_snapshot</c> (Go <c>deferred_snapshot.go</c>): deferred snapshot capture (#1274). A burst of
/// defer_snapshot applies captures no frame per write, observations_flush_snapshot makes the next frame capture them,
/// and without a flush the 1 s safety net captures them. The capture schedule is native; a unit test cannot see it.
/// </summary>
internal static class ApplyDeferredSnapshot
{
    /// <summary>How many applies each burst sends.</summary>
    private const int DeferredBurst = 8;

    private static readonly TimeSpan FrameWait = TimeSpan.FromSeconds(3);

    internal static AcceptanceCase Case() => new()
    {
        Name = "apply/deferred_snapshot",
        Scope = "Deferred snapshot capture (#1274): a burst of defer_snapshot applies captures no frame per write, "
            + "observations_flush_snapshot makes the next frame capture them, and without a flush the 1 s safety net "
            + "captures them. The capture schedule is native; a Go test cannot see it.",
        Start = new LabStart(),
        Budget = TimeSpan.FromMinutes(3),
        Crew = new Crew { Size = 3 },
        Run = RunAsync,
    };

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new AcceptanceException("apply/deferred_snapshot reads the snapshot ring from Windows named shared memory");
        }

        await RunWindowsAsync(s, cancellationToken).ConfigureAwait(false);
    }

    [SupportedOSPlatform("windows")]
    private static async Task RunWindowsAsync(ISession s, CancellationToken cancellationToken)
    {
        var h = s.Harness;
        var report = s.Report;
        if (!Dyn.Contains(s.Names, "rimgovernor/observations_flush_snapshot"))
        {
            throw new AcceptanceException("missing rimgovernor/observations_flush_snapshot in discovery");
        }

        var opened = await h.WireAsync("open-stream", "observations_open_snapshot_stream", new JsonObject(), cancellationToken).ConfigureAwait(false);
        var name = opened["opened"].AsMap()?["name"].AsString() ?? "";
        if (name.Length == 0)
        {
            throw new AcceptanceException($"snapshot stream not opened: {opened.ToJson()}");
        }

        using var reader = SnapshotRingReader.Open(name);

        // Burst sends DeferredBurst refused applies (a refusal is still an applied write to the stream) and reports the
        // frames captured while it ran and the write count it ends at.
        async Task<(ulong Frames, long Writes)> Burst(string label, bool deferred)
        {
            if (!await WaitFrameAsync(reader, reader.Writes(), FrameWait, cancellationToken).ConfigureAwait(false))
            {
                throw new AcceptanceException($"{label}: no frame caught up before the burst");
            }

            var start = reader.Head();
            for (var i = 0; i < DeferredBurst; i++)
            {
                var request = new JsonObject
                {
                    ["identity"] = s.Identity.DeepClone(),
                    ["actions"] = new JsonArray(DeferredProbe(string.Create(CultureInfo.InvariantCulture, $"{label}-{i}"))),
                };
                if (deferred)
                {
                    request["deferSnapshot"] = true;
                }

                _ = await h.WireAsync(string.Create(CultureInfo.InvariantCulture, $"{label}-{i}"), "operations_apply", request, cancellationToken).ConfigureAwait(false);
            }

            return (reader.Head() - start, reader.Writes());
        }

        var (plain, _) = await Burst("plain", deferred: false).ConfigureAwait(false);
        var (deferredFrames, writes) = await Burst("deferred", deferred: true).ConfigureAwait(false);
        report["plain_burst_frames"] = plain;
        report["deferred_burst_frames"] = deferredFrames;

        // A paused game still captures once per wall second, so one periodic frame may land inside the burst; one per
        // write may not.
        if (deferredFrames > 2 || deferredFrames >= plain)
        {
            throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"deferred burst captured {deferredFrames} frames (plain burst {plain})"));
        }

        var flushedAt = Stopwatch.StartNew();
        var flushed = await h.WireAsync("flush", "observations_flush_snapshot", new JsonObject(), cancellationToken).ConfigureAwait(false);
        if (!flushed.ContainsKey("flushed"))
        {
            throw new AcceptanceException($"flush: {flushed.ToJson()}");
        }

        if (!await WaitFrameAsync(reader, writes, FrameWait, cancellationToken).ConfigureAwait(false))
        {
            throw new AcceptanceException("no frame captured the flushed writes");
        }

        report["flush_to_frame_ms"] = flushedAt.ElapsedMilliseconds;

        // Without a flush the safety net captures the deferred writes.
        (_, writes) = await Burst("unflushed", deferred: true).ConfigureAwait(false);
        var unflushedAt = Stopwatch.StartNew();
        if (!await WaitFrameAsync(reader, writes, FrameWait, cancellationToken).ConfigureAwait(false))
        {
            throw new AcceptanceException("the safety net never captured unflushed deferred writes");
        }

        report["unflushed_to_frame_ms"] = unflushedAt.ElapsedMilliseconds;
    }

    /// <summary>Waits up to <paramref name="limit"/> for a committed frame captured at or past <paramref name="writes"/>.</summary>
    [SupportedOSPlatform("windows")]
    private static async Task<bool> WaitFrameAsync(SnapshotRingReader reader, long writes, TimeSpan limit, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + limit;
        while (DateTime.UtcNow < deadline && !cancellationToken.IsCancellationRequested)
        {
            var frame = reader.Latest();
            if (frame is not null && frame.Writes >= writes)
            {
                return true;
            }

            _ = await reader.WaitAsync(frame?.Number ?? 0, TimeSpan.FromMilliseconds(50), cancellationToken).ConfigureAwait(false);
        }

        return false;
    }

    /// <summary>An apply native refuses: a trade with a trader that does not exist.</summary>
    private static JsonObject DeferredProbe(string key) => new()
    {
        ["key"] = key,
        ["trade"] = new JsonObject
        {
            ["target"] = new JsonObject { ["mapTrader"] = new JsonObject { ["traderId"] = "probe-trader" } },
            ["negotiatorId"] = "probe-negotiator",
            ["end"] = new JsonObject { ["kind"] = "END_TRADE_KIND_CANCEL" },
        },
    };
}
