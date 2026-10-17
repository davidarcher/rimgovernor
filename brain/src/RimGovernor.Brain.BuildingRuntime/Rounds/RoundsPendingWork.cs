namespace RimGovernor.Brain.BuildingRuntime;

// Go: rounds_building_selection.go pendingWork.
internal static partial class BuildingRuntimePackage
{
    /// <summary>Open work on one action, counting an applied building whose blueprint or frame may still stand: only census-aware plan retirement settles it.</summary>
    internal static bool PendingWork(Progress progress) =>
        WorkProgress.Open([progress]) || PolicyFunctions.AppliedBuildingOpen(progress, Unknown<CurrentConstruction>());
}
