namespace RimGovernor.Brain.Policy;

/// <summary>
/// The chance a hit lands on the parts that share one set of apparel body-part groups: the sum of those parts'
/// <c>BodyPartRecord.coverageAbs</c>, the weight <c>DamageWorker_AddInjury.ChooseHitPart</c> draws by. A garment covers the
/// parts when it shares any group with the set, as <c>ApparelProperties.CoversBodyPart</c> does.
/// </summary>
public sealed record CoverageShare
{
    public EquatableArray<string> Groups { get; init; }
    public double Probability { get; init; }
}

/// <summary>
/// Where a hit lands on a human body and which apparel layers wear over which: the shares of the parts that sit in some
/// apparel group, and the layer def names from innermost to outermost (<c>ApparelLayerDef.drawOrder</c>). The remainder
/// of the probability is parts in no apparel group, which no garment covers.
/// </summary>
public sealed record BodyCoverage
{
    public EquatableArray<CoverageShare> Shares { get; init; }

    /// <summary>Layer def names, innermost first; a garment's depth is its outermost layer's index.</summary>
    public EquatableArray<string> Layers { get; init; }

    public double Uncovered
    {
        get
        {
            var covered = 0.0;
            foreach (var share in Shares)
            {
                covered += share.Probability;
            }

            return Math.Max(0, 1 - covered);
        }
    }
}
