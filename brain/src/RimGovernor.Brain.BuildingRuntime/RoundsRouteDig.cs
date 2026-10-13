namespace RimGovernor.Brain.BuildingRuntime;

public sealed partial class RoundsBuildingPlanner
{
    /// <summary>
    /// Cuts a route door through natural rock: when no breach cell takes a door on open wall (previewRoutes found
    /// no space), the first breach cell the frame lists as rock or does not list (fogged mountain) is mined and
    /// the door built on it, through the shared rock step (AdmitRockStep), reached from the open ground beside
    /// it. A door is a floor-role cell: the rock there is dug, then built on. Not handled when no breach is rock.
    /// </summary>
    internal async Task<(RoundsBuildingResult Result, bool Handled)> DigBreachAsync(ExcavationStep s, IReadOnlyList<Cell> protectedCells, Action check, CancellationToken call, CancellationToken epoch)
    {
        if (Routes is not { Method: Pol.RoutesMethod.Build } routes)
        {
            return (new RoundsBuildingResult(), false);
        }
        foreach (var cell in routes.Breaches)
        {
            var planned = new[] { new Pol.RoleCell { Cell = cell, Role = Pol.RockRole.NeedsFloor } };
            if (protectedCells.Contains(cell) || PolicyFunctions.RockStep(planned, s.Facts.Cells).Dig.Count == 0)
            {
                continue;
            }
            var (access, reachable) = PolicyFunctions.RockAccess([cell], s.Facts.Cells);
            if (!reachable)
            {
                continue;
            }
            var door = new Building(routes.Definition, cell, Rotation.North, routes.Stuff);
            var method = new MethodId(string.Create(CultureInfo.InvariantCulture, $"plan-dig-route-{cell.X}-{cell.Z}"));
            return await AdmitRockStepAsync(s, planned, access, method, [door], check, call, epoch).ConfigureAwait(false);
        }
        return (new RoundsBuildingResult(), false);
    }
}
