namespace RimGovernor.Brain.BuildingRuntime;

public sealed partial class RoundsBuildingPlanner
{
    /// <summary>
    /// Cuts a route door through natural rock: when no breach cell takes a door on open wall (previewRoutes found
    /// no space), the first breach cell the frame lists as rock or does not list (fogged mountain) is mined and
    /// the door built on it, through the shared rock step (AdmitRockStep), reached from the open ground beside
    /// it. A door is a floor-role cell: the rock there is dug, then built on. Not handled when no breach is rock.
    /// </summary>
    internal (RoundsBuildingResult Result, bool Handled) DigBreach(CancellationToken call, CancellationToken epoch, ExcavationStep s, IReadOnlyList<Cell> protectedCells, Action check)
    {
        if (Routes is not { Method: Pol.RoutesMethod.Build } routes)
        {
            return (new RoundsBuildingResult(), false);
        }
        foreach (var cell in routes.Breaches)
        {
            var planned = new[] { new Pol.RoleCell { Cell = cell, Role = Pol.RockRole.NeedsFloor } };
            if (protectedCells.Contains(cell) || Pol.RockStep(planned, s.Facts.Cells).Dig.Count == 0)
            {
                continue;
            }
            if (!Pol.RockAccess([cell], s.Facts.Cells, out var access))
            {
                continue;
            }
            var door = new Building(routes.Definition, cell, Rotation.North, routes.Stuff);
            var method = new MethodId(string.Create(CultureInfo.InvariantCulture, $"plan-dig-route-{cell.X}-{cell.Z}"));
            return AdmitRockStep(call, epoch, s, planned, access, method, [door], check);
        }
        return (new RoundsBuildingResult(), false);
    }
}
