namespace RimGovernor.Brain.Policy;

/// <summary>
/// Overlays current, same-world evidence on established history. Region observations use the input region
/// index; absent entries are unknown. Facilities is a complete census of current building and zone IDs. A
/// map-wide threat may conservatively hold every region. (Go <c>ExtentEligibilityRequest</c>.)
/// </summary>
public sealed record ExtentEligibilityRequest
{
    public Fact<ColonyExtent> Extent { get; init; }

    public Fact<IReadOnlyList<string>> Facilities { get; init; }

    public Fact<bool> Threat { get; init; }

    public IReadOnlyDictionary<int, ExtentRegionObservation> Regions { get; init; } = new Dictionary<int, ExtentRegionObservation>();
}

public sealed record ExtentRegionObservation
{
    public Fact<bool> Threat { get; init; }

    public Fact<bool> RouteObservedPassable { get; init; }
}

/// <summary>
/// Neither Home coverage nor resource-operation reach. It contains no write authority and never edits its
/// history or evidence inputs. (Go <c>ExtentEligibilityView</c>.)
/// </summary>
public sealed record ExtentEligibilityView
{
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

public static partial class PolicyFunctions
{
    /// <summary>Go <c>ExtentEligibility</c>.</summary>
    public static ExtentEligibilityView ExtentEligibility(ExtentEligibilityRequest r)
    {
        if (!r.Extent.TryGetValue(out var e))
        {
            return new ExtentEligibilityView { Known = false, Reason = "extent_unknown" };
        }
        var reason = e.Regions.Count == 0 ? "extent_empty" : "";
        var fk = r.Facilities.TryGetValue(out var facilities);
        var active = new HashSet<string>(facilities ?? [], StringComparer.Ordinal);
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
            var activeFacilities = fk ? ids.Where(active.Contains).ToList() : [];
            var holds = new List<string>();
            r.Regions.TryGetValue(i, out var obs);
            obs ??= new ExtentRegionObservation();
            var tk = obs.Threat.TryGetValue(out var threat);
            var gk = r.Threat.TryGetValue(out var global);
            if (gk && global)
            {
                threat = true;
                tk = true;
            }
            else if (!tk)
            {
                threat = global;
                tk = gk;
            }
            if (!tk)
            {
                holds.Add("threat_unknown");
            }
            else if (threat)
            {
                holds.Add("threat_present");
            }
            if (!fk)
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
            if (!obs.RouteObservedPassable.TryGetValue(out var route))
            {
                holds.Add("route_unknown");
            }
            else if (!route)
            {
                holds.Add("route_impassable");
            }
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
