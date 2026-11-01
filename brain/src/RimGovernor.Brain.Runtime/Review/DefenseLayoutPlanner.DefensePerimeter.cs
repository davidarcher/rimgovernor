namespace RimGovernor.Brain.Runtime;

/// <summary>What the perimeter cut of one review holds: the layout plan, the bridge foundation and the power conduits a moisture pump wires to.</summary>
internal sealed class DefenseCut
{
    public LayoutPlan Plan = new();
    public string Bridge = "";
    public IReadOnlyList<Cell> Transmitters = [];
}

public sealed partial class DefenseLayoutPlanner
{
    internal const string DefenseMoisturePump = "MoisturePump";
    internal const string DefenseMoistureResearch = "MoisturePump";

    internal static (DefenseCut Cut, bool Known) DefensePerimeterCut(ReviewReading read)
    {
        var projection = read.Reading;
        if (projection.Facts.LayoutPlan is not { } plan)
        {
            return (new DefenseCut(), false);
        }
        var cut = new DefenseCut { Plan = plan, Bridge = DefensePerimeterBridge(projection) };
        if (DefenseResearched(projection, DefenseMoistureResearch))
        {
            cut.Transmitters = DefenseTurretRequest(read).Turret.Transmitters;
        }
        return (cut, true);
    }

    internal static bool DefenseResearched(ColonyReading projection, string project) =>
        projection.Facts.Research.Finished.Contains(new ResearchProjectId(project));

    internal static DefenseBuilding DefenseStored(Building b) =>
        new() { Definition = b.Definition, Cell = b.Cell, Rotation = b.Rotation, Stuff = b.Stuff };

    internal static (List<PerimeterSection> Ring, List<PerimeterSection> Fences, List<PerimeterSection> Pumps) DefenseNodeSections(LayoutPlan plan, string bridge, IReadOnlyList<Cell> transmitters)
    {
        var ring = LayoutPlan.PerimeterSections(plan, DefenseDefinitionNames.Wall, DefenseDefinitionNames.Door, bridge).ToList();
        var fences = LayoutPlan.FenceSections(plan, DefenseDefinitionNames.Fence, DefenseDefinitionNames.FenceStuff).ToList();
        var pumps = LayoutPlan.PerimeterPumps(plan, DefenseMoisturePump, DefenseConduitDefinition, transmitters).ToList();
        return (ring, fences, pumps);
    }

    internal static List<DefenseBuilding> DefenseResolve(DefenseTierRecord tier, IReadOnlyList<PerimeterSection> sections, ISet<Cell> taken)
    {
        var section = sections.FirstOrDefault(s => s.Name == tier.Name && s.Nodes.SequenceEqual(tier.Nodes));
        return section is null ? [] : [.. section.Buildings.Where(b => !taken.Contains(b.Cell)).Select(DefenseStored)];
    }

    internal static HashSet<Cell> DefenseTaken(IEnumerable<DefenseTierRecord> tiers)
    {
        var taken = new HashSet<Cell>();
        foreach (var t in tiers)
        {
            if (LayoutDefenseRules.IsPerimeterTier(t.Name))
            {
                continue;
            }
            foreach (var b in t.Buildings)
            {
                taken.Add(b.Cell);
            }
        }
        return taken;
    }

    /// <summary>
    /// The plan the tiers' site nodes resolve on: the whole plan, or, once a ring replacement retired the generation
    /// they name, that generation's own view (<see cref="LayoutPlan.SiteView"/>).
    /// </summary>
    internal static LayoutPlan DefenseNodeView(IEnumerable<DefenseTierRecord> tiers, LayoutPlan plan)
    {
        var ids = tiers.Where(t => t.NodeBacked).SelectMany(t => t.Nodes).Distinct(StringComparer.Ordinal).ToList();
        var retired = ids.Any(id => plan.Site(id) is { } s && plan.LiveSites(s.Kind).All(l => l.Node != id));
        return retired ? plan.SiteView(ids) : plan;
    }

    /// <summary>Fills the node-backed tiers of the record with the buildings the current cut resolves them to.</summary>
    internal static void DefenseHydrate(DefenseLayoutRecord record, DefenseCut cut)
    {
        var (ring, fences, pumps) = DefenseNodeSections(DefenseNodeView(record.Tiers, cut.Plan), cut.Bridge, cut.Transmitters);
        var sections = ring.Concat(fences).Concat(pumps).ToList();
        var taken = DefenseTaken(record.Tiers);
        foreach (var t in record.Tiers)
        {
            if (t.NodeBacked)
            {
                t.Buildings = DefenseResolve(t, sections, taken);
            }
        }
    }

    internal static bool DefenseRemovalGone(DefenseTierRecord tier, DefenseCensus census)
    {
        foreach (var b in tier.Buildings)
        {
            if (census.Standing(b))
            {
                return false;
            }
        }
        return true;
    }
}
