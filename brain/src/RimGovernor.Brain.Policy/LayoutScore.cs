namespace RimGovernor.Brain.Policy;

/// <summary>The one weight table of plan scoring. The replay harness tunes it.</summary>
public static class PlanWeights
{
    // Hard tier: charged per unmet link.
    public const int BadRoutes = 100000;

    // Soil: per soilCost point under a room, and per rock cell (a dig cost). Rock is a dig, but its walls and floor come with it: a little dearer than the plain soil a built room covers.
    public const int Soil = 3;
    public const int Rock = 8;

    // Footprint per cell the ring would enclose; Wall per built wall cell.
    public const int Footprint = 1;
    public const int Wall = 10;

    // Edge: per step a room cell stands inside EdgeClear walking distance of an open map edge cell (raiders arrive there).
    public const int Edge = 3;
    public const int EdgeClear = 50;

    // Centre: per step a room cell stands inside CentreClear cells of the map border, rock or not. A mountain on the border saves a long wall and a dig's worth of soil, thousands of points; a room cell pays Centre per step inside a third of the map's short side, so tens of cells off the border outweigh it.
    public const int Centre = 1;
    public const int CentreClear = 83;

    // Walk: per step of a link's gap, the affinity-weighted walking distance between the rooms pawns travel.
    public const int Walk = 1;

    /// <summary>
    /// The score a replan's new embedding must beat the held arrangement of the plan's unbuilt, unpinned rooms by,
    /// both scored over the same survey and facts, before those rooms are sited again (EmbedRun.HoldPrior, #3740): below
    /// it every group that still fits its held room's site keeps the site, and so the room its id. About a room's
    /// worth of plain soil, so near-equal layouts never flicker. A room that stands also pays BuiltSwitch (SwitchThreshold).
    /// </summary>
    public const int ReplanGain = 100;

    /// <summary>The score a plan built on field ground must beat the best plan off it by before siting uses it.</summary>
    public const int FieldGain = 2000;

    /// <summary>BuiltSwitch and BuiltSwitchCell price moving a room that stands: what a relocation must gain beyond ReplanGain, per room and per floor cell (the walls, floor and furniture built twice).</summary>
    public const int BuiltSwitch = 200;

    public const int BuiltSwitchCell = 4;
}

/// <summary>The per-survey state every candidate's score shares.</summary>
internal sealed record PlanScorer(BuildGround G, MapSurvey S, SiteGround Ground)
{
    /// <summary>The scorer over a survey, its zones and the ground already reserved.</summary>
    public PlanScorer(IReadOnlyList<LayoutZone> zones, IReadOnlyList<LayoutReservation> reserved, MapSurvey s)
        : this(BuildGround.Create(zones, reserved).WithSoil(s), s, LayoutSiteGroundRules.NewSiteGround(s))
    {
    }

    /// <summary>The walking distance inside which a cell pays the edge term.</summary>
    internal int EdgeClear() => Math.Min(PlanWeights.EdgeClear, Math.Min(S.Bounds.Width, S.Bounds.Height) / 5);

    /// <summary>The edge term of one cell over ground: PlanWeights.Edge per step it stands inside clear of an open edge cell, 0 when no raider reaches it.</summary>
    internal static int EdgeCellCost(SiteGround ground, Cell c, int clear)
    {
        var d = ground.WalkDist(c);
        if (d < 0 || d >= clear)
        {
            return 0;
        }
        return PlanWeights.Edge * (clear - d);
    }

    /// <summary>The centre term of one room interior cell.</summary>
    internal int CentreCell(Cell c)
    {
        var b = S.Bounds;
        var clear = Math.Min(PlanWeights.CentreClear, Math.Min(b.Width, b.Height) / 3);
        var d = Math.Min(Math.Min(c.X, c.Z), Math.Min(b.Width - 1 - c.X, b.Height - 1 - c.Z));
        if (d >= clear)
        {
            return 0;
        }
        return PlanWeights.Centre * (clear - d);
    }
}

/// <summary>Plan scoring, one generator-independent score for a core plan, read by every siting search.</summary>
public static partial class LayoutScoreRules
{
    internal static PlanScorer NewPlanScorer(IReadOnlyList<LayoutZone> zones, IReadOnlyList<LayoutReservation> reserved, MapSurvey s) =>
        new(BuildGround.Create(zones, reserved).WithSoil(s), s, LayoutSiteGroundRules.NewSiteGround(s));
}
