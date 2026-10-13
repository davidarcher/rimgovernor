namespace RimGovernor.Brain.Policy;

// Go: traffic_overlay.go.

public static partial class PolicyFunctions
{
    /// <summary>Every counted layer in overlay order: the heat layers draw as "heat.&lt;layer&gt;".</summary>
    public static readonly IReadOnlyList<TrafficLayer> TrafficLayers =
    [
        TrafficLayer.Colonist,
        TrafficLayer.Crossing,
        TrafficLayer.Animal,
        TrafficLayer.Visitor,
        TrafficLayer.Hostile,
    ];

    private static OverlayColor HeatColor(TrafficLayer layer) =>
        layer switch
        {
            TrafficLayer.Colonist => new OverlayColor(1, 0.55f, 0.1f, 0),
            TrafficLayer.Crossing => new OverlayColor(0.55f, 0.35f, 0.15f, 0),
            TrafficLayer.Animal => new OverlayColor(0.3f, 0.8f, 0.3f, 0),
            TrafficLayer.Visitor => new OverlayColor(0.3f, 0.6f, 1, 0),
            TrafficLayer.Hostile => new OverlayColor(1, 0.15f, 0.15f, 0),
            _ => default,
        };

    /// <summary>The number of opacity steps a heat layer scales over.</summary>
    private const int HeatBands = 4;

    /// <summary>Draws one layer's busiest cells as a heat map scaled to its busiest cell: HeatBands fills of rising opacity, row runs each.</summary>
    public static LayoutOverlay TrafficOverlay(IReadOnlyList<TrafficCell> cells, TrafficLayer layer, Bounds bounds)
    {
        uint peak = 0;
        foreach (var c in cells)
        {
            if (c.Layer == layer && c.Samples > peak)
            {
                peak = c.Samples;
            }
        }
        if (peak == 0)
        {
            return new LayoutOverlay();
        }
        var bands = new List<Cell>[HeatBands];
        for (var i = 0; i < bands.Length; i++)
        {
            bands[i] = [];
        }
        foreach (var c in cells)
        {
            if (c.Layer != layer || c.Samples == 0 || c.Cell.X < 0 || c.Cell.Z < 0 || c.Cell.X >= bounds.Width || c.Cell.Z >= bounds.Height)
            {
                continue;
            }
            var b = (int)((ulong)c.Samples * HeatBands / ((ulong)peak + 1));
            bands[b].Add(c.Cell);
        }
        var output = new LayoutOverlay();
        for (var b = 0; b < bands.Length; b++)
        {
            if (bands[b].Count == 0)
            {
                continue;
            }
            var color = HeatColor(layer) with { A = 0.15f + (0.5f * b / (HeatBands - 1)) };
            output.Layers.Add(
                new OverlayLayer
                {
                    Color = color,
                    Style = OverlayStyle.Fill,
                    Label = "heat " + Wire.Name(layer),
                    Runs = CellRuns(bands[b]),
                }
            );
        }
        return output;
    }
}
