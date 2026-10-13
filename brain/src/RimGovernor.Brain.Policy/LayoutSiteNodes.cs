namespace RimGovernor.Brain.Policy;

/// <summary>A region that belongs to a site node and is never an exclusive reservation. Cells are relative to the node's origin.</summary>
public sealed record SiteLane
{
    public string Id { get; init; } = "";
    public Region Region { get; init; } = new();
    public string Reason { get; init; } = "";

    /// <summary>Marks a turbine's wind path: the embedder prices it as the game does (windBlocked, WindOutput) and Blocked is the cells found blocked.</summary>
    public bool Wind { get; init; }

    public int Blocked { get; init; }

    /// <summary>The fraction of its rated output a wind lane leaves.</summary>
    public double WindOutput() => PolicyFunctions.WindOutput(Blocked);
}

/// <summary>A site element of the station graph.</summary>
public sealed record SiteNode
{
    public string Id { get; init; } = "";

    /// <summary>The stored node kind of a power site the plan keeps as a PlannedSite (PowerSitesOf); empty for a node the plan stores elsewhere.</summary>
    public NodeKind Kind { get; init; }

    /// <summary>The cells the node stands on, relative to its origin.</summary>
    public Region Footprint { get; init; } = new();

    /// <summary>The cell the origin must land on; null for a node that goes anywhere the envelope and its band allow.</summary>
    public Cell? Anchor { get; init; }

    public IReadOnlyList<SiteLane> Lanes { get; init; } = [];

    /// <summary>The planned room id this node supersedes; 0 for none.</summary>
    public int Replaces { get; init; }

    public string Reason { get; init; } = "";

    /// <summary>The way the node faces (a turbine's wind side); null where Go's empty rotation stood.</summary>
    public Rotation? Rotation { get; init; }

    /// <summary>The other shapes the same node may take, its turns: only Footprint, Lanes and Rotation are read from them. A free node takes the shape and origin of least price.</summary>
    public IReadOnlyList<SiteNode> Alts { get; init; } = [];
}

/// <summary>Where a site node landed. Lanes are in map cells, as the footprint is.</summary>
public sealed record SitePlacement
{
    public string Node { get; init; } = "";
    public Cell Origin { get; init; }
    public Region Footprint { get; init; } = new();
    public IReadOnlyList<SiteLane> Lanes { get; init; } = [];
    public Rotation? Rotation { get; init; }
    public bool Anchored { get; init; }
    public int Replaces { get; init; }
}

/// <summary>One site decision for the caller to journal.</summary>
public sealed record SiteDecision
{
    public string Node { get; init; } = "";
    public string Verdict { get; init; } = "";
    public string Reason { get; init; } = "";
}

/// <summary>The site nodes to place around an embedding of the rooms. Embed is the input the embedding was made from; Embedding is its result.</summary>
public sealed record SiteEmbedInput
{
    public EmbedInput Embed { get; init; } = new();
    public Embedding Embedding { get; init; } = new();
    public IReadOnlyList<SiteNode> Nodes { get; init; } = [];

    /// <summary>What a free node's origin is priced by; the zero value prices the ground and nothing else, and ties go to the lowest cell.</summary>
    public SitePrice Price { get; init; } = new();
}

/// <summary>The placement of the site nodes.</summary>
public sealed record SiteEmbedding
{
    public IReadOnlyList<SitePlacement> Sites { get; init; } = [];
    public IReadOnlyList<SiteDecision> Decisions { get; init; } = [];

    /// <summary>The placement of the node; null when absent.</summary>
    public SitePlacement? Site(string id) => Sites.FirstOrDefault(s => s.Node == id);
}

/// <summary>Marks a site node the embedder cannot read (Go <c>ErrSiteNode</c>).</summary>
public sealed class SiteNodeException(string message) : DomainException("site node: " + message);

/// <summary>Ports <c>layout_site_nodes.go</c>: the things the colony builds that have no walls and no door as nodes of the station graph.</summary>
public static partial class PolicyFunctions
{
    /// <summary>The site node was placed at its fixed anchor.</summary>
    public const string ReasonSiteAnchored = "site_placed_at_anchor";

    public const string ReasonSiteFree = "site_placed_in_envelope";
    public const string ReasonSiteNoGround = "site_no_ground";

    /// <summary>The anchor is fixed, so the node stands there although its footprint touches a room's walls or another footprint.</summary>
    public const string ReasonSiteAnchorBlocked = "site_anchor_blocked";

    /// <summary>Cells of the wind lane are roofed, off the map or held by a thing that blocks wind; the node stands, at the output the game leaves it.</summary>
    public const string ReasonSiteWindBlocked = "site_wind_lane_blocked";
}
