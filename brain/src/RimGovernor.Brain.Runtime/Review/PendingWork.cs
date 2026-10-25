using RimGovernor.Brain.Policy;
namespace RimGovernor.Brain.Runtime;

internal static class PendingWork
{
    /// <summary>Open work on one action, counting an applied building whose blueprint or frame may still stand: only census-aware plan retirement settles it.</summary>
    internal static bool IsPending(Progress progress) =>
        WorkProgress.Open([progress]) || BuildingWorkRules.AppliedBuildingOpen(progress, null);
}
