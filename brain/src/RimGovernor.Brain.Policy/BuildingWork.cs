namespace RimGovernor.Brain.Policy;

// Go: building_work.go

public static partial class PolicyFunctions
{
    /// <summary>An applied building intent whose blueprint or frame still stands (or whose census is unknown).</summary>
    public static bool AppliedBuildingOpen(Progress p, Fact<CurrentConstruction> census) =>
        p.Action.Kind == ActionKind.Building
        && p.Action.ConstructionTarget.Length == 0
        && p.View.Stage == Stage.Completed
        && BuildingWorkOpen(p, census) == BuildingWork.BuildingOpen;

    /// <summary>StandardWorkOpen plus applied buildings still under construction.</summary>
    public static bool PlanWorkOpen(IReadOnlyList<Progress> progress, Fact<CurrentConstruction> census)
    {
        if (Standard.WorkOpen(progress))
        {
            return true;
        }

        return progress.Any(p => AppliedBuildingOpen(p, census));
    }

    /// <summary>Every action of the plan completed, with each building standing built in the census.</summary>
    public static bool PlanBuilt(IReadOnlyList<Progress> progress, Fact<CurrentConstruction> census)
    {
        if (progress.Count == 0)
        {
            return false;
        }

        foreach (var p in progress)
        {
            var v = p.View;
            if (v.Stage != Stage.Completed || !v.Effect.TryGetValue(out var effect) || effect != Effect.Completed)
            {
                return false;
            }

            if (p.Action.Kind == ActionKind.Building && BuildingWorkOpen(p, census) != BuildingWork.BuildingDone)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>WorkOpen for a building progress row.</summary>
    private static BuildingWork BuildingWorkOpen(Progress p, Fact<CurrentConstruction> census)
    {
        var building = p.Action.Building;
        return WorkOpen(building!, census);
    }
}
