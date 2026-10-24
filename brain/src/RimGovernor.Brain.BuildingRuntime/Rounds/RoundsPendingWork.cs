using RimGovernor.Brain.Policy;
namespace RimGovernor.Brain.BuildingRuntime;

internal static class RoundsPendingWork
{
    /// <summary>Open work on one action, counting an applied building whose blueprint or frame may still stand: only census-aware plan retirement settles it.</summary>
    internal static bool PendingWork(Progress progress) =>
        WorkProgress.Open([progress]) || BuildingWorkRules.AppliedBuildingOpen(progress, null);
}
