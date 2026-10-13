namespace RimGovernor.Brain.BuildingRuntime;

internal static partial class BuildingRuntimePackage
{
    /// <summary>Cells that open monument-install moves will occupy once the quest marker is placed.</summary>
    internal static IReadOnlyList<Cell> PendingMonumentCells(IReadOnlyList<PlanState> plans, Fact<EquatableArray<JoinerOffer>> offers)
    {
        if (!offers.TryGetValue(out var rows))
        {
            return [];
        }

        var markers = new Dictionary<string, QuestMonument>();
        foreach (var offer in rows)
        {
            if (offer.State != "Ongoing")
            {
                continue;
            }

            foreach (var objective in offer.Objectives)
            {
                if (objective.Monument.TryGetValue(out var m))
                {
                    if (m.Installed.TryGetValue(out var installed) && installed)
                    {
                        continue;
                    }

                    markers[m.Marker] = m;
                }
            }
        }

        var cells = new List<Cell>();
        foreach (var plan in plans)
        {
            if (plan.Retired)
            {
                continue;
            }

            foreach (var progress in plan.Progress)
            {
                if (!StorePackage.ProgressOpen(plan, progress))
                {
                    continue;
                }

                if (progress.Action.MoveBuilding is not { } move || !markers.TryGetValue(move.Thing, out var marker))
                {
                    continue;
                }

                foreach (var piece in marker.Pieces)
                {
                    foreach (var offset in piece.Footprint)
                    {
                        cells.Add(new Cell(move.Cell.X + offset.X, move.Cell.Z + offset.Z));
                    }
                }
            }
        }

        return cells;
    }
}
