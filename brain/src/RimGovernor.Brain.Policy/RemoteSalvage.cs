namespace RimGovernor.Brain.Policy;

/// <summary>
/// Contains native output and route observations. Estimated yield never becomes inventory until ordinary
/// pawn work delivers it.
/// </summary>
public sealed record SalvageEvidence
{
    public Fact<bool> Safe { get; init; }

    public SupplyCandidate Candidate { get; init; } = new();

    /// <summary>How many game ticks old native's computation was at the read; served rows are cached until the refresher recomputes them.</summary>
    public int AgeTicks { get; init; }
}

public static partial class PolicyFunctions
{
    /// <summary>
    /// The one salvage yield pricing function: the native cost-list evidence as a CandidateSalvage row for the
    /// thing id. Optional resources keep only the yields of those resources with a known StockCap &gt; 0 (a
    /// yield with unknown price or cap prices nothing, never zero); the bool reports whether any yield
    /// remains. With no resources every yield is kept.
    /// </summary>
    public static (SupplyCandidate Candidate, bool Any) SalvagePriced(string id, SalvageEvidence e, params Resource[] resources)
    {
        var c = e.Candidate with { Id = id, Kind = CandidateKind.Salvage };
        if (resources.Length == 0)
        {
            return (c, c.Yields.Count > 0);
        }
        var yields = new List<SupplyYield>();
        foreach (var y in e.Candidate.Yields)
        {
            if (y.StockCap.TryGetValue(out var n) && n > 0 && resources.Contains(y.Good.Def))
            {
                yields.Add(y);
            }
        }
        c = c with { Yields = EquatableArray.From(yields) };
        return (c, yields.Count > 0);
    }
}
