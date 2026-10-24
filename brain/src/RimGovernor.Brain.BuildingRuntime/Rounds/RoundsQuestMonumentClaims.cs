namespace RimGovernor.Brain.BuildingRuntime;

internal static class RoundsQuestMonumentClaims
{
    /// <summary>Cells that open monument-install moves will occupy once the quest marker is placed.</summary>
    internal static IReadOnlyList<Cell> PendingMonumentCells(IReadOnlyList<PlanState> plans, EquatableArray<O.QuestState> rows)
    {
        var markers = new Dictionary<string, O.QuestMonument>();

        foreach (var offer in rows)
        {
            if (offer.State != PbReceipts.QuestStatus.Ongoing)
            {
                continue;
            }

            foreach (var objective in offer.Objectives)
            {
                if (objective.Monument is { State: not O.QuestMonumentState.Installed } m)
                {
                    markers[m.MarkerId] = m;
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
