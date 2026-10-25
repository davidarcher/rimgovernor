namespace RimGovernor.Brain.Policy;

public static partial class Rules
{
    /// <summary>
    /// The immediate (safety-scope) review. It judges no Concern: every SeriousHarm Need is its family's to judge in
    /// the immediate pass (combat #3311, sheltering #3304). The review refreshes the dash latch and carries the disaster
    /// history forward. Throws DomainException on invalid facts.
    /// </summary>
    public static Findings InspectImmediateReview(ReviewFacts f, Latches previous, PlannerSettings p)
    {
        p.Validate();
        foreach (var count in new[] { f.CriticalPatients, f.UrgentPatients })
        {
            if (count < 0 || count > long.MaxValue / 10)
            {
                throw new DomainException("invalid immediate count");
            }
        }
        return new Findings { Latches = previous with { Dash = f.Dash }, Disaster = f.Disaster };
    }
}
