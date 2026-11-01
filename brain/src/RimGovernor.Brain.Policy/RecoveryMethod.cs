using RimGovernor.Brain.Domain;

namespace RimGovernor.Brain.Policy;

public sealed record RecoverySafety
{
    public EquatableArray<RimGovernor.Brain.GameFacts.Observations.RecoveryRestriction> Restrictions { get; init; }
}

public static partial class RecoveryRules
{
    /// <summary>
    /// The disaster services Need's finding from the disaster history: met without one or once restored, unclear while unread,
    /// otherwise the latest service finding.
    /// </summary>
    public static Finding RecoveryNeed(DisasterHistory? h)
    {
        try
        {
            h?.Validate();
        }
        catch (DomainException)
        {
            return Finding.Unclear;
        }
        if (h is not null && h.Phase == DisasterPhase.Unread)
        {
            return Finding.Unclear;
        }
        if (h is null || h.Phase == DisasterPhase.Restored)
        {
            return Finding.Met;
        }
        return h.Services[h.Services.Count - 1].Finding;
    }
}
