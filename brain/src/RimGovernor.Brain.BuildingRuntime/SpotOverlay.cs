namespace RimGovernor.Brain.BuildingRuntime;

public sealed partial class Rounder
{
    /// <summary>
    /// Pushes the work-spot layer after a review, gated by the layout overlay flag like the others: the butcher
    /// spot, butcher table and crafting spot, which the layout plan does not hold, drawn from the construction
    /// census. Output only: a failure is dropped and the next review draws again.
    /// </summary>
    internal void DrawSpotOverlay(CancellationToken ct, GenerationSnapshot snapshot, Observation.ColonyProjection projection)
    {
        if (Native is not ILayoutOverlayNative native)
        {
            return;
        }
        var layer = new Pol.LayoutOverlay();
        if (projection.Facts.CurrentConstruction.TryGetValue(out var census) && LayoutOverlay)
        {
            layer = Pol.LayoutOverlay.SpotOverlay(census, projection.Bounds);
        }
        var tick = projection.Identity.Tick;
        if (layer.Layers.Count == 0)
        {
            if (!Spots.Cleared)
            {
                try
                {
                    native.DrawOverlay(ct, ControlIdentity(snapshot), Pol.LayoutOverlay.SpotLayer, new Pol.LayoutOverlay(), false);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    return;
                }
                Spots = new OverlayState { Cleared = true };
            }
            return;
        }
        if (OverlayCurrent(Spots, layer, tick))
        {
            return;
        }
        try
        {
            native.DrawOverlay(ct, ControlIdentity(snapshot), Pol.LayoutOverlay.SpotLayer, layer, true);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return;
        }
        Spots = new OverlayState { Layer = layer, Drawn = tick };
    }
}
