namespace RimGovernor.Brain.Policy;
public static partial class ProductionRules
{
    /// <summary>A bench bill's repeat mode. Native reads only vanilla's three supported modes.</summary>
    public static BillRepeat BillRepeatOf(RimGovernor.Brain.GameFacts.Operations.RepeatMode mode)
    {
        switch (mode)
        {
            case RimGovernor.Brain.GameFacts.Operations.RepeatMode.Forever:
                return BillRepeat.Forever;
            case RimGovernor.Brain.GameFacts.Operations.RepeatMode.Count:
                return BillRepeat.Count;
            case RimGovernor.Brain.GameFacts.Operations.RepeatMode.Target:
                return BillRepeat.TargetCount;
        }
        throw new DomainException($"undefined repeat mode {(int)mode}");
    }

}
