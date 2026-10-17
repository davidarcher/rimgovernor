using System.Text.Json.Nodes;
using RimGovernor.Contracts.Wire;
using Obs = RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Acceptance;

/// <summary>One snapshot frame pulled over the tool channel: its number, the applied-write count it was captured at, and its JSON.</summary>
public sealed record SnapshotPulled(ulong Number, long Writes, byte[] Payload);

/// <summary>
/// Reads the native snapshot stream's frames over the tool channel (<c>observations_read_snapshot</c>): the newest
/// captured BundleSnapshot, the frame head and the applied-write counter. The first read subscribes the stream. A read
/// that names the number already seen returns the counters without the frame, so polling stays cheap.
/// </summary>
public sealed class SnapshotPoller
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);

    private readonly Harness _harness;

    /// <summary>Subscribes the native snapshot stream through <paramref name="harness"/>.</summary>
    public SnapshotPoller(Harness harness) => _harness = harness;

    /// <summary>The last committed frame number, 0 before the first.</summary>
    public async Task<ulong> HeadAsync(CancellationToken cancellationToken) =>
        (await ReadAsync(ulong.MaxValue, cancellationToken).ConfigureAwait(false)).Head;

    /// <summary>
    /// The native applied-write counter now: a frame captured at or past it reflects every write that returned before
    /// this call.
    /// </summary>
    public async Task<long> WritesAsync(CancellationToken cancellationToken) =>
        (await ReadAsync(ulong.MaxValue, cancellationToken).ConfigureAwait(false)).Writes;

    /// <summary>The newest committed frame when it is numbered past <paramref name="after"/>; null otherwise (or before the first).</summary>
    public async Task<SnapshotPulled?> LatestAsync(ulong after, CancellationToken cancellationToken)
    {
        var read = await ReadAsync(after, cancellationToken).ConfigureAwait(false);
        return read.Frame is { } frame ? new SnapshotPulled(read.Head, read.FrameWrites, WireJson.SerializeToUtf8Bytes(frame)) : null;
    }

    /// <summary>Waits until a frame past <paramref name="after"/> is committed, the token ends or <paramref name="timeout"/> passes; reports whether one was.</summary>
    public async Task<bool> WaitAsync(ulong after, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            if (await HeadAsync(cancellationToken).ConfigureAwait(false) > after)
            {
                return true;
            }

            var left = deadline - DateTime.UtcNow;
            if (left <= TimeSpan.Zero || cancellationToken.IsCancellationRequested)
            {
                return false;
            }

            try
            {
                await Task.Delay(left < PollInterval ? left : PollInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }
    }

    private async Task<Obs.SnapshotRead> ReadAsync(ulong after, CancellationToken cancellationToken)
    {
        var request = WireJson.Serialize(new Obs.SnapshotReadRequest { AfterNumber = after });
        var envelope = await _harness.CallAsync("snapshot-read", "rimgovernor/observations_read_snapshot", new JsonObject { ["request"] = request }, cancellationToken).ConfigureAwait(false);
        if (envelope["payload"] is not JsonValue value || !value.TryGetValue<string>(out var payload))
        {
            throw new AcceptanceException("observations_read_snapshot: reply did not preserve the JSON string envelope");
        }

        var reply = WireJson.Deserialize<Obs.SnapshotReadReply>(payload);
        return reply.Outcome?.Read
            ?? throw new AcceptanceException($"observations_read_snapshot: {(reply.Outcome?.Unavailable is { } unavailable ? "unavailable: " + unavailable.Detail : reply.Outcome?.Failure is { } failure ? "failure: " + failure.Detail : "no outcome")}");
    }
}
