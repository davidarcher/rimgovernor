namespace RimGovernor.Brain.Runtime;

/// <summary>
/// The last layer and tick a safety, spot or stock layer was sent, and whether it was cleared. The
/// layer is compared by value.
/// </summary>
internal sealed record OverlayState
{
    public Pol.LayoutOverlay? Layer { get; init; }

    public Tick Drawn { get; init; }

    public bool Cleared { get; init; }
}

partial class ColonyReview
{
    /// <summary>How often an unchanged safety or stock layer is resent (one game hour).</summary>
    internal static readonly Tick OverlayResendEvery = new(GameTime.TicksPerHour);

    /// <summary>True when an unchanged layer drawn at <paramref name="state"/> need not be resent at <paramref name="tick"/>.</summary>
    internal static bool OverlayCurrent(OverlayState state, Pol.LayoutOverlay layer, Tick tick) =>
        Equals(layer, state.Layer) && tick >= state.Drawn && tick.Value - state.Drawn.Value < OverlayResendEvery.Value;
}

public sealed partial class ColonyReview
{
    /// <summary>
    /// Pushes the safety layer after a review, gated by the layout overlay flag like the other layers: the
    /// holding threats' reach, the raid edge and the vetoed loot, sent when they change or an hour passed, and
    /// cleared once when nothing holds. Output only: a failure is dropped, never fatal.
    /// </summary>
    internal async Task DrawSafetyOverlayAsync(GenerationSnapshot snapshot, ColonyReading projection, Pol.EmergencyFacts emergency, CancellationToken ct)
    {
        if (Native is not GameClient native)
        {
            return;
        }
        var layer = new Pol.LayoutOverlay();
        if (LayoutOverlay)
        {
            layer = Pol.SafetyOverlayRules.SafetyOverlay(emergency.Threats, projection.Facts.EventLoot, projection.Facts.MapBounds);
        }
        var tick = projection.Identity.Tick;
        if (layer.Layers.Count == 0)
        {
            if (!Safety.Cleared)
            {
                try
                {
                    await native.DrawOverlayAsync(Control.ControlIdentity(snapshot), Pol.SafetyOverlayRules.SafetyLayer, new Pol.LayoutOverlay(), false, ct).ConfigureAwait(false);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    return;
                }
                Safety = new OverlayState { Cleared = true };
            }
            return;
        }
        if (OverlayCurrent(Safety, layer, tick))
        {
            return;
        }
        try
        {
            await native.DrawOverlayAsync(Control.ControlIdentity(snapshot), Pol.SafetyOverlayRules.SafetyLayer, layer, true, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return;
        }
        Safety = new OverlayState { Layer = layer, Drawn = tick };
    }
}
