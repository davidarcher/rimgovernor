namespace RimGovernor.Brain.Policy;

public static partial class PolicyFunctions
{
    /// <summary>The overlay layer the outdoor stand-in work spots draw on.</summary>
    public const string SpotLayer = "spots";

    /// <summary>
    /// Names the stand-ins the layout plan does not hold: the butcher spot and the crafting spot are placed by a
    /// search near the core, not planned ahead, so they are drawn from what stands or is blueprinted.
    /// </summary>
    private static readonly Dictionary<string, OverlayStyleOf> SpotLabels = new(StringComparer.Ordinal)
    {
        ["ButcherSpot"] = new(PlanBrown, "butcher spot"),
        ["CraftingSpot"] = new(PlanTan, "crafting spot"),
    };

    /// <summary>
    /// Outlines and labels every standing building and every blueprint of the stand-in work spots. Empty (the
    /// layer clears) when none stands or is blueprinted.
    /// </summary>
    public static LayoutOverlay SpotOverlay(CurrentConstruction construction, Bounds bounds)
    {
        var layers = new List<OverlayLayer>();
        var labels = new List<OverlayLabel>();

        void Draw(OverlayStyleOf style, string text, IReadOnlyList<Cell> cells)
        {
            if (cells.Count == 0)
            {
                return;
            }
            var kept = cells.Where(c => InBounds(c, bounds)).ToList();
            if (kept.Count == 0)
            {
                return;
            }
            var runs = CellRuns(kept);
            layers.Add(new OverlayLayer { Color = style.Hue.Fill(), Style = OverlayStyle.Fill, Label = text, Runs = runs });
            layers.Add(new OverlayLayer { Color = style.Hue.Outline(), Style = OverlayStyle.Outline, Label = text, Runs = runs });
            labels.Add(new OverlayLabel { Text = text, Cell = kept[0] });
        }

        foreach (var b in construction.Buildings)
        {
            if (SpotLabels.TryGetValue(b.Building!.Definition, out var style))
            {
                var cells = b.Cells;
                if (cells.Count == 0)
                {
                    cells = [b.Building.Cell];
                }
                Draw(style, style.Label, cells);
            }
        }
        foreach (var s in construction.Sites)
        {
            if (SpotLabels.TryGetValue(s.Building!.Definition, out var style))
            {
                Draw(style, style.Label + " (planned)", [s.Building.Cell]);
            }
        }
        return new LayoutOverlay { Layers = layers, Labels = labels };
    }
}
