using System.Globalization;
using System.Runtime.Versioning;
using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>The combat sections of one snapshot frame (Go <c>bridge.Combat</c> as the family reads it).</summary>
internal sealed record CombatState(JsonObject Frame)
{
    internal JsonObject? Context => Frame["context"].AsMap();

    /// <summary>The frame's <c>combatPawns</c> rows.</summary>
    internal IReadOnlyList<JsonObject> Pawns => [.. Frame["combatPawns"].AsSlice().Select(p => p.AsMap() ?? [])];

    /// <summary>The frame's <c>combatEvents</c> rows.</summary>
    internal IReadOnlyList<JsonObject> Events => [.. Frame["combatEvents"].AsSlice().Select(p => p.AsMap() ?? [])];

    /// <summary>The frame's tick.</summary>
    internal long Tick => Context is { } context ? (long)context["tick"].AsNumberOrZero() : 0;
}

/// <summary>
/// Reads the combat sections of the snapshot stream's frames: the case maps the ring the way the controller's bridge
/// does (Go <c>combatFrames</c>). Windows only, like the ring.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class CombatFrames : IDisposable
{
    /// <summary>Bounds the wait for a frame after a step.</summary>
    private static readonly TimeSpan FrameWait = TimeSpan.FromSeconds(10);

    private readonly JsonObject _identity;
    private readonly SnapshotRingReader _reader;
    private ulong _last;

    private CombatFrames(JsonObject identity, SnapshotRingReader reader)
    {
        _identity = identity;
        _reader = reader;
    }

    /// <summary>Opens the snapshot stream and maps its ring.</summary>
    internal static async Task<CombatFrames> OpenAsync(Harness h, JsonObject identity, CancellationToken cancellationToken)
    {
        var reply = await h.WireAsync("combat-open-stream", "observations_open_snapshot_stream", new JsonObject(), cancellationToken).ConfigureAwait(false);
        var name = reply["opened"].AsMap()?["name"].AsString() ?? "";
        if (name.Length == 0)
        {
            throw new AcceptanceException($"snapshot stream not opened: {reply.ToJson()}");
        }

        return new CombatFrames(identity, SnapshotRingReader.Open(name));
    }

    /// <summary>The combat state of the first frame published after the last one read and captured at or past <paramref name="tick"/>.</summary>
    internal async Task<CombatState> NextAsync(long tick, CancellationToken cancellationToken) =>
        new(await SnapshotAsync(tick, cancellationToken).ConfigureAwait(false));

    /// <summary>The first frame published after the last one read and captured at or past <paramref name="tick"/>.</summary>
    internal async Task<JsonObject> SnapshotAsync(long tick, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + FrameWait;
        while (true)
        {
            var frame = _reader.Latest();
            if (frame is not null && frame.Number > _last)
            {
                var v = JsonNode.Parse(frame.Payload)?.AsObject() ?? throw new AcceptanceException($"snapshot frame {frame.Number} is not a JSON object");
                if (new CombatState(v).Tick >= tick)
                {
                    _last = frame.Number;
                    var identity = v["context"].AsMap()?["identity"];
                    if (!Dyn.DeepEqual(identity, _identity))
                    {
                        throw new AcceptanceException($"frame {CaseSupport.Tag((long)frame.Number)} describes another world: {identity.ToJson()}");
                    }

                    return v;
                }
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"no snapshot frame at or past tick {tick} within {FrameWait} (last read {_last})"));
            }

            var after = frame is not null ? frame.Number : _last;
            _ = await _reader.WaitAsync(after, TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
        }
    }

    public void Dispose() => _reader.Dispose();
}
