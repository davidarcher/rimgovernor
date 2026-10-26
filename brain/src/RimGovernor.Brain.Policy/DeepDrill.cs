namespace RimGovernor.Brain.Policy;

public static class DeepDrillRules
{

    public static long AvailableYield(RimGovernor.Brain.GameFacts.Observations.DeepResourceLump site, long deficit) =>
        Math.Min(site.Count, deficit);

    /// <summary>The deep resource lumps of the needed resources, nearest the colony centre first; empty without a scanner, the research or the drill.</summary>
    public static List<RimGovernor.Brain.GameFacts.Observations.DeepResourceLump> DeepDrillSites(ReviewFacts facts, RimGovernor.Brain.GameFacts.Observations.DeepResources deep, IReadOnlyList<ResourceRunway> runways, ItemFacts items, string drillDefinition, string scannerDefinition)
    {
        var available = ColonyPlanningRules.DefinitionAvailable(facts, drillDefinition);
        if (facts.LayoutPlan?.Center() is not { } center || ResearchRules.ResearchGate(["DeepDrilling", scannerDefinition], facts.Research) != "" || !available)
        {
            return [];
        }
        var scanner = false;
        foreach (var row in deep.GroundScanners)
        {
            scanner = scanner || row.Definition == scannerDefinition;
        }
        if (!scanner)
        {
            return [];
        }
        var needed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in runways)
        {
            if (row.InDeficit && row.Stock < row.Target && items.IsDeepResource(row.Resource))
            {
                needed.Add(row.Resource.Value);
            }
        }
        var sites = new List<RimGovernor.Brain.GameFacts.Observations.DeepResourceLump>();
        foreach (var lump in deep.Lumps)
        {
            if (needed.Contains(lump.Definition) && lump.Count > 0)
            {
                sites.Add(lump);
            }
        }
        long Distance(Cell c)
        {
            long x = (long)c.X - center.X;
            long z = (long)c.Z - center.Z;
            return (x * x) + (z * z);
        }

        sites.Sort((a, b) =>
        {
            var da = Distance(a.Centre);
            var db = Distance(b.Centre);
            if (da != db)
            {
                return da.CompareTo(db);
            }
            if (a.Centre.X != b.Centre.X)
            {
                return a.Centre.X.CompareTo(b.Centre.X);
            }
            return a.Centre.Z.CompareTo(b.Centre.Z);
        });
        return sites;
    }

    /// <summary>The drills with no valuable deposit left under them and no standing Deconstruct designation, by id.</summary>
    public static List<RimGovernor.Brain.GameFacts.Observations.DeepDrill> ExhaustedDrills(RimGovernor.Brain.GameFacts.Observations.DeepResources deep)
    {
        var output = new List<RimGovernor.Brain.GameFacts.Observations.DeepDrill>();
        foreach (var drill in deep.Drills)
        {
            if (drill.Deposit is null && !drill.Designated)
            {
                output.Add(drill);
            }
        }
        output.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
        return output;
    }

    /// <summary>Whether the drill footprint is all open, unroofed, walkable ground that includes the lump's anchor cell.</summary>
    public static bool DeepDrillFootprint(IReadOnlyList<SiteCell> cells, IReadOnlyList<Cell> footprint, Cell anchor)
    {
        if (footprint.Count == 0)
        {
            return false;
        }
        var seen = new Dictionary<Cell, SiteCell>();
        foreach (var row in cells)
        {
            seen[row.Cell] = row;
        }
        var onLump = false;
        foreach (var cell in footprint)
        {
            if (!seen.TryGetValue(cell, out var row) || row.Roofed || row.Occupied() || !row.Walkable)
            {
                return false;
            }
            onLump = onLump || cell == anchor;
        }
        return onLump;
    }

    public static double SiteDistance(Cell site, Cell center) =>
        Math.Sqrt(Math.Pow(site.X - center.X, 2) + Math.Pow(site.Z - center.Z, 2));

    /// <summary>
    /// Adds the extraction ladder only while a measured runway of a resource the catalog generates as a deep
    /// deposit is in deficit. Explicit operator research still takes precedence.
    /// </summary>
    public static IReadOnlyList<string> DeepDrillingResearch(IReadOnlyList<string> needs, IReadOnlyList<ResourceRunway> runways, ItemFacts items)
    {
        foreach (var row in runways)
        {
            if (!items.IsDeepResource(row.Resource))
            {
                continue;
            }
            if (row.InDeficit)
            {
                return [.. needs, "DeepDrilling", "GroundPenetratingScanner"];
            }
        }
        return needs;
    }
}
