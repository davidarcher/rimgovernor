namespace RimGovernor.Brain.Policy;

/// <summary>Whether the live census names the colony's buildings and stockpiles: the ids it holds, or no census read.</summary>
public abstract record FacilityCensus
{
    private protected FacilityCensus() { }

    /// <summary>The complete census of current building and zone ids.</summary>
    public sealed record Counted(EquatableArray<string> Ids) : FacilityCensus;

    /// <summary>No building or Home census was held.</summary>
    public sealed record Unread : FacilityCensus;
}

/// <summary>The map-wide hostile count read as a presence: hostiles on the map, none, or no count read.</summary>
public enum HostilePresence
{
    [WireName("present")]
    Present,

    [WireName("absent")]
    Absent,

    [WireName("unread")]
    Unread,
}

/// <summary>
/// Overlays current, same-world evidence on established history. Facilities is a complete census of current
/// building and zone IDs. A map-wide threat holds every region. No region route census exists, so every region
/// holds route_unknown.
/// </summary>
public sealed record ExtentEligibilityRequest
{
    public required ColonyExtent Extent { get; init; }

    public required FacilityCensus Facilities { get; init; }

    public required HostilePresence Threat { get; init; }
}

/// <summary>
/// Neither Home coverage nor resource-operation reach. It contains no write authority and never edits its
/// history or evidence inputs.
/// </summary>
public sealed record ExtentEligibilityView
{
    /// <summary>The view while no established history is held for the world.</summary>
    public static readonly ExtentEligibilityView NoHistory = new() { Known = false, Reason = "extent_unknown" };

    public bool Known { get; init; }

    public string Reason { get; init; } = "";

    public IReadOnlyList<ExtentRegionEligibility> Regions { get; init; } = [];
}

public sealed record ExtentRegionEligibility
{
    public int Region { get; init; }

    public string Stage { get; init; } = "";

    public int Cells { get; init; }

    public IReadOnlyList<ExtentOrigin> Origins { get; init; } = [];

    public IReadOnlyList<string> Facilities { get; init; } = [];

    public IReadOnlyList<string> ActiveFacilities { get; init; } = [];

    public bool Eligible { get; init; }

    public IReadOnlyList<string> HoldReasons { get; init; } = [];
}

public static class ExtentEligibilityRules
{
    public static ExtentEligibilityView ExtentEligibility(ExtentEligibilityRequest r)
    {
        var e = r.Extent;
        var reason = e.Regions.Count == 0 ? "extent_empty" : "";
        var census = r.Facilities as FacilityCensus.Counted;
        var active = new HashSet<string>(census?.Ids ?? [], StringComparer.Ordinal);
        var rows = new List<ExtentRegionEligibility>();
        for (var i = 0; i < e.Regions.Count; i++)
        {
            var region = e.Regions[i];
            var origins = new HashSet<ExtentOrigin>();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var cell in region.Cells)
            {
                foreach (var p in cell.Provenance)
                {
                    origins.Add(p.Origin);
                    if (p.Facility.Length != 0)
                    {
                        ids.Add(p.Facility);
                    }
                }
            }
            var activeFacilities = census is null ? [] : ids.Where(active.Contains).ToList();
            var holds = new List<string>();
            switch (r.Threat)
            {
                case HostilePresence.Present:
                    holds.Add("threat_present");
                    break;
                case HostilePresence.Unread:
                    holds.Add("threat_unknown");
                    break;
                case HostilePresence.Absent:
                    break;
            }
            if (census is null)
            {
                holds.Add("facilities_unknown");
            }
            else if (ids.Count == 0)
            {
                holds.Add("provenance_unknown");
            }
            else if (activeFacilities.Count != ids.Count)
            {
                holds.Add("facility_lost");
            }
            holds.Add("route_unknown");
            if (region.Cells.Count == 0)
            {
                holds.Add("extent_empty");
            }
            rows.Add(
                new ExtentRegionEligibility
                {
                    Region = i,
                    Stage = "established",
                    Cells = region.Cells.Count,
                    Origins = [.. origins.OrderBy(o => Wire.Name(o), StringComparer.Ordinal)],
                    Facilities = [.. ids.Order(StringComparer.Ordinal)],
                    ActiveFacilities = [.. activeFacilities.Order(StringComparer.Ordinal)],
                    Eligible = holds.Count == 0,
                    HoldReasons = holds,
                }
            );
        }
        return new ExtentEligibilityView { Known = true, Reason = reason, Regions = rows };
    }
}
