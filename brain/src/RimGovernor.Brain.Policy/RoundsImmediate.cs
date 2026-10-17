namespace RimGovernor.Brain.Policy;

public static partial class PolicyFunctions
{
    /// <summary>
    /// The immediate (safety-scope) review. It judges no Concern: every SeriousHarm Need is its family's to judge in
    /// the immediate pass (combat #3311, sheltering #3304). The review refreshes the dash latch, carries the disaster
    /// history forward and checks the declared method capabilities. Throws DomainException on invalid facts.
    /// </summary>
    public static RoundsFindings InspectImmediateRounds(RoundsFacts f, RoundsLatches previous, RoundsPolicy p)
    {
        p.Validate();
        foreach (var count in new[] { f.Hostiles, f.CriticalPatients, f.UrgentPatients })
        {
            if (count.TryGetValue(out var n) && (n < 0 || n > long.MaxValue / 10))
            {
                throw new DomainException("invalid immediate count");
            }
        }
        if (f.AvailableMethods.TryGetValue(out var methods))
        {
            var available = new HashSet<ConcernId>();
            foreach (var id in methods)
            {
                var (_, recognized) = InspectionFor(id);
                if (!recognized || !available.Add(id))
                {
                    throw new DomainException("invalid routine method capability " + id.Value);
                }
            }
        }
        return new RoundsFindings { Latches = previous with { Dash = DashLatch(f.Dash) }, Disaster = f.Disaster };
    }
}
