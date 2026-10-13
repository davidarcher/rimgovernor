namespace RimGovernor.Brain.Policy;

/// <summary>
/// The combined effect of a pawn's active genes on one stat: offsets add, factors multiply (the game's stat
/// pipeline order is base plus offsets, then factors). (Go <c>StatModifier</c>.)
/// </summary>
public readonly record struct StatModifier(double Offset, double Factor);

/// <summary>
/// What a pawn's active genes do, resolved from the catalog's typed gene rows (bridge.BiotechCatalog.GeneEffects);
/// policy never matches a gene by name. DisabledNeeds and EnabledNeeds are NeedDef names. (Go <c>GeneEffects</c>.)
/// </summary>
public sealed record GeneEffects
{
    public IReadOnlyDictionary<string, StatModifier> Stats { get; init; } = new Dictionary<string, StatModifier>();

    public IReadOnlySet<string> DisabledNeeds { get; init; } = new HashSet<string>();

    public IReadOnlySet<string> EnabledNeeds { get; init; } = new HashSet<string>();

    /// <summary>Go <c>GeneEffects.Stat</c>: the genes' combined modifier on a stat; the identity when no active gene touches it.</summary>
    public StatModifier Stat(string stat) => Stats.TryGetValue(stat, out var m) ? m : new StatModifier(0, 1);

    /// <summary>Go <c>GeneEffects.NeedDisabled</c>: whether an active gene removes the need and no active gene gives it back.</summary>
    public bool NeedDisabled(string need) => DisabledNeeds.Contains(need) && !EnabledNeeds.Contains(need);

    /// <summary>Go <c>GeneEffects.apply</c>: scales a stat whose other offsets sum to offset, returning the resulting offset from 1.</summary>
    internal double Apply(string stat, double offset)
    {
        var m = Stat(stat);
        return ((1 + offset + m.Offset) * m.Factor) - 1;
    }
}
