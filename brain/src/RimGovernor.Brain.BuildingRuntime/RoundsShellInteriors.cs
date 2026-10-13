namespace RimGovernor.Brain.BuildingRuntime;

internal static partial class BuildingRuntimePackage
{
    /// <summary>
    /// Lists every cell inside the bounds of each wall ring a plan raises, built or not: the floor a shell
    /// encloses (or will enclose once its walls stand) belongs to the room's own furniture, and a planner that
    /// only avoids the walls' footprints would site over it (the first field patch was laid across the hut's
    /// interior while the shell still waited for wood, and the stockpile and sleeping spots then found the room
    /// already zoned). Completed claims alone are too late for that, so the rings come from the shelter goal's
    /// plans while it is open, and from the completed claims once it is served (the goal then leaves the review,
    /// and the next field batch was drawn inside the finished hut).
    /// </summary>
    internal static IReadOnlyList<Cell> ShellInteriors(IReadOnlyList<Store.PlanState> plans, IReadOnlyList<Pol.ConstructionClaim> claims)
    {
        var byPlan = new Dictionary<PlanId, ShellBounds>();

        void Extend(PlanId id, Building building)
        {
            var def = building.Definition;
            if (def != "Wall" && def != "Door")
            {
                return;
            }
            var cell = building.Cell;
            byPlan[id] = byPlan.TryGetValue(id, out var b)
                ? new ShellBounds(Math.Min(b.MinX, cell.X), Math.Min(b.MinZ, cell.Z), Math.Max(b.MaxX, cell.X), Math.Max(b.MaxZ, cell.Z))
                : new ShellBounds(cell.X, cell.Z, cell.X, cell.Z);
        }

        foreach (var plan in plans)
        {
            var id = plan.Spec.Id;
            foreach (var progress in plan.Progress)
            {
                if (progress.Action.Building is not { } building || progress.View.Stage == Stage.Cancelled)
                {
                    continue;
                }
                Extend(id, building);
            }
        }
        foreach (var claim in claims)
        {
            if (claim.Building is { } claimBuilding)
            {
                Extend(claim.Plan, claimBuilding);
            }
        }
        var cells = new List<Cell>();
        foreach (var id in byPlan.Keys.Order())
        {
            var b = byPlan[id];
            for (var x = b.MinX; x <= b.MaxX; x++)
            {
                for (var z = b.MinZ; z <= b.MaxZ; z++)
                {
                    cells.Add(new Cell(x, z));
                }
            }
        }
        return cells;
    }

    private readonly record struct ShellBounds(int MinX, int MinZ, int MaxX, int MaxZ);
}
