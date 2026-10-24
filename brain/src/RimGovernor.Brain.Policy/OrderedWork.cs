namespace RimGovernor.Brain.Policy;

public static partial class OrderedWorkRules
{
    /// <summary>
    /// Ranks the interruption of a pawn's whole job read without excluding it from Auto planning: a forced job
    /// costs one, a queued job one more. Active controller claims and native legality are checked independently.
    /// </summary>
    internal static int OrderedWorkCost(bool forced, uint queued) => (forced ? 1 : 0) + (queued > 0 ? 1 : 0);
}
