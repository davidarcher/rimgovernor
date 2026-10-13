namespace RimGovernor.Brain.Policy;

/// <summary>
/// Ports <c>material_runway.go</c>. Every runway row that is neither a drug nor a medicine (steel and components
/// among them) against the fabrication, construction and gear burn the realized-consumption ring counts. The
/// stock to hold is the runway's own Target; PlanSupply prices a bill's steel draw against the line the runway
/// protects.
/// </summary>
public static partial class PolicyFunctions
{
    /// <summary>
    /// Reads every runway row that is neither a drug nor a medicine out of the resource runways. A missing row or
    /// an unobserved use or stock leaves the projection unknown.
    /// </summary>
    public static Fact<StockProjection> PlanMaterialRunway(IReadOnlyList<ResourceRunway> rows, ItemFacts items) =>
        StockProjectionOf(rows, r => !items.Drugs.Any(d => d.Def == r) && !items.IsMedicine(r));
}
