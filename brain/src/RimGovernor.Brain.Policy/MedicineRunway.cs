namespace RimGovernor.Brain.Policy;

/// <summary>
/// Ports <c>medicine_runway.go</c>. Medicine is spent where a colonist is tended; the realized-consumption ring
/// counts each dose and ForecastResourceRunway turns the observed rate and the stock into days left. Every catalog
/// medicine has a row, so a better medicine in use counts. The shortfall is the projector's Medicine domain; the
/// stock to hold is the runway's own Target.
/// </summary>
public static partial class PolicyFunctions
{
    /// <summary>
    /// Reads the catalog medicines' rows out of the resource runways. No catalog medicine, no row, or an unobserved
    /// use or stock leaves the projection unknown; a medicine observed unused has no shortfall.
    /// </summary>
    public static Fact<StockProjection> PlanMedicineRunway(IReadOnlyList<ResourceRunway> rows, ItemFacts items) => StockProjectionOf(rows, items.IsMedicine);
}
