namespace RimGovernor.Brain.BuildingRuntime;

internal static partial class BuildingRuntimePackage
{
    /// <summary>How many cells of the incinerator interior hold stored items.</summary>
    internal static int IncineratorStored(ColonyProjection facts, Rectangle interior)
    {
        var inside = new HashSet<Cell>(PolicyFunctions.RectangleCells(interior));
        var stored = 0;
        foreach (var cell in facts.Cells)
        {
            if (inside.Contains(cell.Cell) && cell.StorageEmpty.TryGetValue(out var empty) && !empty)
            {
                stored++;
            }
        }

        return stored;
    }

    /// <summary>Whether a fire is burning; an unread fire census counts as burning.</summary>
    internal static bool FireBurning(ColonyProjection facts) => !facts.Facts.Upkeep.Fires.TryGetValue(out var fires) || fires.Count > 0;

}

public sealed partial class RoundsIncinerationPlanner
{
    /// <summary>Observe due labor without choosing a cleaner or burner outside the shared forecast.</summary>
    internal static Task<(RoundsIncinerationResult Result, bool Handled)> StageBurnAsync(ControlState state, Rounds review,
        StandardState goal, StepArbiter arbiter, RoundsReading reading, CancellationToken call, CancellationToken epoch)
    {
        var room = StandingIncinerator(reading.Projection);
        if (room is null) return Task.FromResult<(RoundsIncinerationResult, bool)>((default, false));
        reading.Projection.Facts.Upkeep.Filth.TryGetValue(out var filth);
        var cleaning = PolicyFunctions.IncineratorAsh(filth, room.Region.Bounds()).Count > 0 && !FireBurning(reading.Projection);
        var burningOwed = !FireBurning(reading.Projection)
            && IncineratorStored(reading.Projection, room.Region.Bounds()) >= PolicyFunctions.BurnStoredCells;
        if (!cleaning && !burningOwed) return Task.FromResult<(RoundsIncinerationResult, bool)>((default, false));
        return Task.FromResult((new RoundsIncinerationResult { Verdict = Verdict.AwaitingPlan(
            cleaning ? "unsupported_forecast_incinerator_cleaning" : "unsupported_forecast_incinerator_ignition",
            "Incinerator labor requires shared forecast occupancy, safety and outcome alternatives.") }, true));
    }
}