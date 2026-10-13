using System.Globalization;
using System.Text;

namespace RimGovernor.Brain.Policy;

/// <summary>
/// The cell census a site kind picks from (the square search is gone; every kind uses the rectangle picker).
/// (Go <c>FarmSiteRequest</c>.)
/// </summary>
public sealed record FarmSiteRequest
{
    public Bounds Bounds { get; init; }

    public Cell Anchor { get; init; }

    public IReadOnlyList<SiteCell> Cells { get; init; } = [];

    /// <summary>Cells never planted: accepted footprints, walkways, entrances and reserved routes.</summary>
    public IReadOnlyList<Cell> Protected { get; init; } = [];

    /// <summary>When non-null, the only cells an outdoor candidate scores: the layout plan's field blocks.</summary>
    public IReadOnlySet<Cell>? Fields { get; init; }
}

public sealed record FarmSiteTerm(string Name, double Value);

/// <summary>Records why a patch scored as it did. Score is the sum of Terms; Density is Score per cell. (Go <c>FarmSiteCandidate</c>.)</summary>
public sealed record FarmSiteCandidate
{
    public Rectangle Patch { get; init; }

    public double Score { get; init; }

    public double Density { get; init; }

    public IReadOnlyList<FarmSiteTerm> Terms { get; init; } = [];
}

public sealed record FarmSitePlan
{
    public IReadOnlyList<Rectangle> Patches { get; init; } = [];

    public IReadOnlyList<FarmSiteCandidate> Selected { get; init; } = [];

    public int Cells { get; init; }

    public int Unplanted { get; init; }

    /// <summary>Go <c>Explain</c>: renders the selection for logs and acceptance evidence.</summary>
    public string Explain()
    {
        var inv = CultureInfo.InvariantCulture;
        var b = new StringBuilder();
        b.Append(inv, $"{Cells} cells in {Patches.Count} patches (unplanted={Unplanted})");
        foreach (var c in Selected)
        {
            b.Append(inv, $"\n  {c.Patch.Width}x{c.Patch.Height}@{c.Patch.X},{c.Patch.Z} score={c.Score:F4} density={c.Density:F4}");
            foreach (var t in c.Terms)
            {
                b.Append(inv, $" {t.Name}={t.Value:F4}");
            }
        }
        return b.ToString();
    }
}

public static partial class PolicyFunctions
{
    /// <summary>Charges a picked cell per step from the anchor, as a fraction of one normal-soil cell's daily nutrition for the crop.</summary>
    internal const double SiteTravelWeight = 0.015;

    /// <summary>Bounds a new greenhouse's lamps.</summary>
    internal const int FarmSitePatchLimit = 32;
}
