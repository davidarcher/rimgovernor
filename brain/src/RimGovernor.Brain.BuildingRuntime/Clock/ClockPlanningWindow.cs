namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>The native read behind the planning window (the bridge client's ReadPlanningWindow); the scheduler requires it.</summary>
public interface IPlanningWindowNative
{
    Task<PlanningWindowRead> ReadPlanningWindowAsync(PbCommon.Identity identity, Rectangle region, CancellationToken ct);
}

/// <summary>
/// The step's refresher for the planning_cells section: the scheduler attaches one to each step and every
/// planning colony read in the step whose reply carries no cells asks it. Each ask cuts the window from the newest
/// snapshot frame's grid and files it; a failed read serves the held window of the same region.
/// </summary>
internal sealed class PlanningWindow(IPlanningWindowNative native, FactStore store) : IPlanExtentSource
{
    public FactScope Scope { get; set; }

    /// <summary>Reads the stored layout plan for the step's world (null before the step knows it); the window covers its extent.</summary>
    public Func<CancellationToken, Task<LayoutPlanRecord?>>? Layout { get; set; }

    /// <summary>The stored layout plan's extent, empty when there is none.</summary>
    public async Task<Rectangle> PlanExtentAsync(CancellationToken ct)
    {
        if (Layout is null || await Layout(ct).ConfigureAwait(false) is not { } record)
        {
            return default;
        }
        return record.Plan?.Extent() ?? default;
    }

    public async Task<Held<PlanningCells>> PlanningWindowAsync(PbCommon.Identity identity, Rectangle region, CancellationToken ct)
    {
        try
        {
            var window = await native.ReadPlanningWindowAsync(identity, region, ct).ConfigureAwait(false);
            return Put(window.Region, window.Cells, window.Context?.Tick ?? 0);
        }
        catch (Exception) when (FactStore.Read<PlanningCells>(store, FactSection.PlanningCells, out var held) && held.Value.Region == region)
        {
            return held;
        }
    }

    private Held<PlanningCells> Put(Rectangle region, IReadOnlyList<SiteCell> cells, long tick) =>
        File(new Held<PlanningCells> { Value = new PlanningCells { Region = region, Cells = [.. cells] }, AsOf = tick, Complete = true, Source = "rimgovernor/observations_get_cells", Region = ClockPlanning.RegionRect(region) });

    /// <summary>
    /// Puts the window in the store and publishes its rows as the planning_cells table, however it was read, so a
    /// recording holds the rows a review's cells are rebuilt from. An unchanged table is not republished.
    /// </summary>
    private Held<PlanningCells> File(Held<PlanningCells> output)
    {
        FactStore.Put(store, Scope, FactSection.PlanningCells, output);
        if (!StoreTables.TryGetTable<Cell, SiteCell>(store, Scope, Wire.Name(FactSection.PlanningCells), out var table)
            || table.AsOf != Watermark.At(output.AsOf) || !ClockPlanning.SameRows(table.Rows, output.Value.Cells))
        {
            StoreTables.PutTable(store, Scope, Wire.Name(FactSection.PlanningCells), ClockMirror.CellRows(output.Value.Cells), Watermark.At(output.AsOf));
        }
        return output;
    }
}

internal static class ClockPlanning
{
    public static bool SameRows(IReadOnlyDictionary<Cell, SiteCell> rows, IReadOnlyList<SiteCell> cells)
    {
        if (rows.Count != cells.Count)
        {
            return false;
        }
        foreach (var cell in cells)
        {
            if (!rows.TryGetValue(cell.Cell, out var row) || !row.Equals(cell))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>The inclusive cell bounds of a planning region, so an invalidation narrowed to a rectangle can leave a window it does not touch fresh.</summary>
    public static FactRect RegionRect(Rectangle region) =>
        region.Width <= 0 || region.Height <= 0
            ? default
            : new FactRect(region.X, region.Z, region.X + region.Width - 1, region.Z + region.Height - 1);

    /// <summary>
    /// Gives a planning read outside a scheduler step (a planner stepped on its own) a window read straight from
    /// source, when source serves one and no step refresher is attached.
    /// </summary>
    public static IDisposable? StandaloneWindow(object source) =>
        source is IPlanningWindowNative native && ObservationApi.PlanningWindowFrom() is null
            ? ObservationApi.WithPlanningWindow(new DirectWindow(native))
            : null;
}

/// <summary>Reads the planning window natively on every ask.</summary>
internal sealed class DirectWindow(IPlanningWindowNative native) : IPlanningWindowSource
{
    public async Task<Held<PlanningCells>> PlanningWindowAsync(PbCommon.Identity identity, Rectangle region, CancellationToken ct)
    {
        var window = await native.ReadPlanningWindowAsync(identity, region, ct).ConfigureAwait(false);
        return new Held<PlanningCells> { Value = new PlanningCells { Region = window.Region, Cells = [.. window.Cells] }, AsOf = window.Context?.Tick ?? 0, Complete = true, Source = "rimgovernor/observations_get_cells", Region = RegionRect(window.Region) };
    }

    private static FactRect RegionRect(Rectangle region) => ClockPlanning.RegionRect(region);
}
