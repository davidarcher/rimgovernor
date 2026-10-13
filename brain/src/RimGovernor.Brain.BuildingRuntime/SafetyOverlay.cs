namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>
/// The last layer and tick a safety, spot or stock layer was sent, and whether it was cleared. Go kept the
/// layer's printed form as a key; the layer itself is compared by value instead.
/// </summary>
internal sealed record OverlayState
{
    public Pol.LayoutOverlay? Layer { get; init; }

    public Tick Drawn { get; init; }

    public bool Cleared { get; init; }
}

internal static partial class BuildingRuntimePackage
{
    /// <summary>How often an unchanged safety or stock layer is resent (one game hour).</summary>
    internal static readonly Tick OverlayResendEvery = new(GameTime.TicksPerHour);

    /// <summary>True when an unchanged layer drawn at <paramref name="state"/> need not be resent at <paramref name="tick"/>.</summary>
    internal static bool OverlayCurrent(OverlayState state, Pol.LayoutOverlay layer, Tick tick) =>
        Equals(layer, state.Layer) && tick >= state.Drawn && tick.Value - state.Drawn.Value < OverlayResendEvery.Value;
}

public sealed partial class Rounder
{
    /// <summary>
    /// Pushes the safety layer after a review, gated by the layout overlay flag like the other layers: the
    /// holding threats' reach, the raid edge and the vetoed loot, sent when they change or an hour passed, and
    /// cleared once when nothing holds. Output only: a failure is dropped, never fatal.
    /// </summary>
    internal async Task DrawSafetyOverlayAsync(GenerationSnapshot snapshot, Observation.ColonyProjection projection, Pol.EmergencyFacts emergency, CancellationToken ct)
    {
        if (Native is not ILayoutOverlayNative native)
        {
            return;
        }
        var layer = new Pol.LayoutOverlay();
        if (LayoutOverlay)
        {
            projection.Facts.EventLoot.TryGetValue(out var loot);
            layer = Pol.PolicyFunctions.SafetyOverlay(emergency.Threats, loot, projection.Bounds);
        }
        var tick = projection.Identity.Tick;
        if (layer.Layers.Count == 0)
        {
            if (!Safety.Cleared)
            {
                try
                {
                    await native.DrawOverlayAsync(Control.ControlIdentity(snapshot), Pol.PolicyFunctions.SafetyLayer, new Pol.LayoutOverlay(), false, ct).ConfigureAwait(false);
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
            await native.DrawOverlayAsync(Control.ControlIdentity(snapshot), Pol.PolicyFunctions.SafetyLayer, layer, true, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return;
        }
        Safety = new OverlayState { Layer = layer, Drawn = tick };
    }
}
