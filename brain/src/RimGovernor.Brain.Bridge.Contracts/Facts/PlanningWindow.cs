using RimGovernor.Brain.Policy;
using RimGovernor.Contracts.Wire.Common;
using RimGovernor.Contracts.Wire.Observations;
using Rectangle = RimGovernor.Brain.Policy.Rectangle;

namespace RimGovernor.Brain.Bridge.Contracts;

/// <summary>
/// Go's <c>PlanningWindow</c>: the planning window's site cells inside <see cref="Region"/>, from the snapshot
/// stream's whole-map grid, with the context of the frame that carried it. Fogged cells are never listed;
/// <see cref="Filtered"/> counts them.
/// </summary>
public sealed record PlanningWindow(ObservationContext? Context, Rectangle Region, IReadOnlyList<SiteCell> Cells, ulong Filtered);

// Go planning_window.go. ReadPlanningWindow (a Client read of the snapshot stream) is wire plumbing and not ported.
public static partial class BridgeFunctions
{
    /// <summary>
    /// Go's <c>PlanningWindowRect</c>: the planning window on a map of bounds is the whole map. The window was the
    /// colony centre +/- 22 cells widened to the layout plan's extent, and a plan first derived after the window
    /// was read left the planners that anchor on its core looking at cells round the landing only (a butcher spot
    /// placed 70 cells from the core). <paramref name="plan"/> is kept for the callers' signature; it does not
    /// narrow the read.
    /// </summary>
    public static Rectangle PlanningWindowRect(Bounds bounds, Rectangle plan) => new(0, 0, bounds.Width, bounds.Height);

    /// <summary>Go's <c>SortSiteCells</c>: orders site cells row-major (z, then x), the order a planning window lists them.</summary>
    public static void SortSiteCells(List<SiteCell> cells) =>
        cells.Sort((left, right) =>
        {
            var a = left.Cell;
            var b = right.Cell;
            return a.Z != b.Z ? a.Z.CompareTo(b.Z) : a.X.CompareTo(b.X);
        });

    /// <summary>
    /// Go's <c>CellPresence</c>: whether a named cell feature is present. A value means present, a not-applicable
    /// issue or an applied field without a value means absent, and an unread field stays unknown.
    /// </summary>
    public static Fact<bool> CellPresence(string? value, IEnumerable<ReadIssue> issues, string field, bool applied)
    {
        if (value is not null)
        {
            return Known(true);
        }
        foreach (var issue in issues)
        {
            if ((issue.Field ?? "") == field)
            {
                return (issue.Unavailable?.Reason).GetValueOrDefault() == UnavailableReason.NotApplicable ? Known(false) : Unknown<bool>();
            }
        }
        return applied ? Known(false) : Unknown<bool>();
    }
}
