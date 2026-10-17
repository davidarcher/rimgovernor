namespace RimGovernor.Brain.Policy;

/// <summary>
/// The cell census a site kind picks from (the square search is gone; every kind uses the rectangle picker).
/// (Go <c>FarmSiteRequest</c>.)
/// </summary>
public sealed record FarmSiteRequest
{
    public Cell Anchor { get; init; }

    public IReadOnlyList<SiteCell> Cells { get; init; } = [];

    /// <summary>Cells never planted: accepted footprints, walkways, entrances and reserved routes.</summary>
    public IReadOnlyList<Cell> Protected { get; init; } = [];

    /// <summary>When non-null, the only cells an outdoor candidate scores: the layout plan's field blocks.</summary>
    public IReadOnlySet<Cell>? Fields { get; init; }
}

public sealed record FarmSiteTerm(string Name, double Value);

/// <summary>Records why a patch scored as it did: its Terms. (Go <c>FarmSiteCandidate</c>.)</summary>
public sealed record FarmSiteCandidate
{
    public IReadOnlyList<FarmSiteTerm> Terms { get; init; } = [];
}

public sealed record FarmSitePlan
{
    public IReadOnlyList<Rectangle> Patches { get; init; } = [];

    public IReadOnlyList<FarmSiteCandidate> Selected { get; init; } = [];

    public int Cells { get; init; }
}

public static class FarmSiteRules
{
    /// <summary>Charges a picked cell per step from the anchor, as a fraction of one normal-soil cell's daily nutrition for the crop.</summary>
    internal const double SiteTravelWeight = 0.015;

    /// <summary>Bounds a new greenhouse's lamps.</summary>
    internal const int FarmSitePatchLimit = 32;
}
