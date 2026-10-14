using System.Globalization;
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
/// Reads the combat sections of the snapshot stream's frames over the tool channel: the case maps the frames the way
/// the controller's bridge does (Go <c>combatFrames</c>).
/// </summary>
internal sealed class CombatFrames
{
    /// <summary>Bounds the wait for a frame after a step.</summary>
    private static readonly TimeSpan FrameWait = TimeSpan.FromSeconds(10);

    private readonly JsonObject _identity;
    private readonly SnapshotPoller _poller;
    private ulong _last;
    private ulong _seen;

    private CombatFrames(JsonObject identity, SnapshotPoller poller)
    {
        _identity = identity;
        _poller = poller;
    }

    /// <summary>Subscribes the snapshot stream.</summary>
    internal static async Task<CombatFrames> OpenAsync(Harness h, JsonObject identity, CancellationToken cancellationToken)
    {
        var poller = new SnapshotPoller(h);
        _ = await poller.HeadAsync(cancellationToken).ConfigureAwait(false);
        return new CombatFrames(identity, poller);
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
            var frame = await _poller.LatestAsync(Math.Max(_last, _seen), cancellationToken).ConfigureAwait(false);
            if (frame is not null)
            {
                _seen = frame.Number;
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

            _ = await _poller.WaitAsync(Math.Max(_last, _seen), TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
        }
    }
}
