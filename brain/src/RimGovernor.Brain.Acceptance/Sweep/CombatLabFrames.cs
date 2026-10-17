using System.Globalization;
using System.Text.Json.Nodes;
using RimGovernor.Contracts.Wire;
using Common = RimGovernor.Contracts.Wire.Common;
using Mirror = RimGovernor.Contracts.Wire.Mirror;
using Obs = RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Acceptance;

/// <summary>The combat sections of one snapshot frame.</summary>
public sealed record CombatState(Obs.BundleSnapshot Frame)
{
    /// <summary>The frame's <c>combatEvents</c> rows.</summary>
    public IReadOnlyList<Mirror.CombatEventRow> Events => Frame.CombatEvents;

    /// <summary>The frame's tick.</summary>
    public long Tick => Frame.Context?.Tick ?? 0;
}

/// <summary>
/// Reads the combat sections of the snapshot stream's frames over the tool channel: the case maps the frames the way
/// the controller's bridge does.
/// </summary>
public sealed class CombatFrames
{
    /// <summary>Bounds the wait for a frame after a step.</summary>
    private static readonly TimeSpan FrameWait = TimeSpan.FromSeconds(10);

    private readonly Common.Identity _identity;
    private readonly SnapshotPoller _poller;
    private ulong _last;
    private ulong _seen;

    private CombatFrames(Common.Identity identity, SnapshotPoller poller)
    {
        _identity = identity;
        _poller = poller;
    }

    /// <summary>Subscribes the snapshot stream.</summary>
    public static async Task<CombatFrames> OpenAsync(Harness h, JsonObject identity, CancellationToken cancellationToken)
    {
        var poller = new SnapshotPoller(h);
        _ = await poller.HeadAsync(cancellationToken).ConfigureAwait(false);
        return new CombatFrames(Harness.IdentityOf(identity), poller);
    }

    /// <summary>The combat state of the first frame published after the last one read and captured at or past <paramref name="tick"/>.</summary>
    public async Task<CombatState> NextAsync(long tick, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + FrameWait;
        while (true)
        {
            var frame = await _poller.LatestAsync(Math.Max(_last, _seen), cancellationToken).ConfigureAwait(false);
            if (frame is not null)
            {
                _seen = frame.Number;
                var state = new CombatState(frame.Frame);
                if (state.Tick >= tick)
                {
                    _last = frame.Number;
                    var identity = frame.Frame.Context?.Identity;
                    if (identity != _identity)
                    {
                        throw new AcceptanceException($"frame {SweepText.Tag((long)frame.Number)} describes another world: {(identity is null ? "null" : WireJson.Serialize(identity))}");
                    }

                    return state;
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
