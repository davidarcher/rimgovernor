using RimGovernor.Brain.Policy;
using RimGovernor.Brain.GameFacts.Common;
using RimGovernor.Brain.GameFacts.Observations;
using RimGovernor.Brain.GameOrders.Observations;
using Rectangle = RimGovernor.Brain.Policy.Rectangle;

namespace RimGovernor.Brain.GameAccess;

/// <summary>
/// The planning window's site cells inside <see cref="Region"/>, from the snapshot
/// stream's whole-map grid, with the context of the frame that carried it. Fogged cells are never listed;
/// <see cref="Filtered"/> counts them.
/// </summary>
public sealed record PlanningWindow(ObservationContext? Context, Rectangle Region, IReadOnlyList<SiteCell> Cells, ulong Filtered);

// ReadPlanningWindow (a Client read of the snapshot stream) is wire plumbing and not ported.
public static class PlanningWindowRules
{
    /// <summary>
    /// The planning window on a map of bounds is the whole map. The window was the
    /// colony centre +/- 22 cells widened to the layout plan's extent, and a plan first derived after the window
    /// was read left the planners that anchor on its core looking at cells round the landing only (a butcher spot
    /// placed 70 cells from the core). <paramref name="plan"/> is kept for the callers' signature; it does not
    /// narrow the read.
    /// </summary>
    public static Rectangle PlanningWindowRect(Bounds bounds, Rectangle plan) => new(0, 0, bounds.Width, bounds.Height);

    /// <summary>Orders site cells row-major (z, then x), the order a planning window lists them.</summary>
    public static void SortSiteCells(List<SiteCell> cells) =>
        cells.Sort(
            (left, right) =>
            {
                var a = left.Cell;
                var b = right.Cell;
                return a.Z != b.Z ? a.Z.CompareTo(b.Z) : a.X.CompareTo(b.X);
            }
        );
}
