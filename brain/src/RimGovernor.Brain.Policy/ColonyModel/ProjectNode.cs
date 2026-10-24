namespace RimGovernor.Brain.Policy;

// The tier-2 colony model's project graph nodes (#3530 of #3470): research, sowing, growth, harvest, crafting and hunting (#3620).

/// <summary>The kinds of project node.</summary>
public enum ProjectKind
{
    /// <summary>Research points to spend; the work is points remaining.</summary>
    Research,

    /// <summary>
    /// An Anomaly knowledge project (#3843): the work is knowledge points remaining, which only study gives. Study is not in the run
    /// (its yield is the containment credit's, #3811), so the node takes no labor and completes only once its points are given.
    /// </summary>
    Knowledge,

    /// <summary>Sowing the cells of a field; the work is cells times the crop's sow work.</summary>
    Sow,

    /// <summary>A crop maturing: no labor, the node completes when growth reaches the crop's harvestable growth.</summary>
    Grow,

    /// <summary>Harvesting the cells of a field; the work is cells times the crop's harvest work.</summary>
    Harvest,

    /// <summary>A recipe: the work, the ingredients it consumes and the products it makes.</summary>
    Craft,

    /// <summary>Hunting and butchering one race's animals of the wildlife census; the work is the hunt planner's pursuit work, the yields its meat and butchery products.</summary>
    Hunt,

    /// <summary>An aggregate boundary: completes when every constituent cohort completes, without labor or payoff.</summary>
    Milestone,
}

/// <summary>A knowledge category (#3843) and the id of the <see cref="ProjectKind.Knowledge"/> node its points fund; empty when it funds none.</summary>
public sealed record CategoryFunding(string Category, string Node);

/// <summary>
/// One node of the project graph. A node starts when every prerequisite is complete. The graph's order is the labor
/// allocation's priority order and is visible in the trajectory.
/// </summary>
public sealed record ProjectNode
{
    /// <summary>Native recipe identity retained independently of display/event ids.</summary>
    public string Recipe { get; init; } = "";

    /// <summary>A craft node's recipe workSpeedStat (#3868), the pawn stat its work goes at; "" works at 1.</summary>
    public string WorkSpeedStat { get; init; } = "";
    /// <summary>Unique within the graph; the id of the node's completion event.</summary>
    public string Id { get; init; } = "";

    public ProjectKind Kind { get; init; }

    /// <summary>Ids of the nodes that must complete first.</summary>
    public EquatableArray<string> Prerequisites { get; init; }

    /// <summary>Research: points remaining. Knowledge: knowledge points remaining. Sow, Harvest, Craft, Hunt: work units. Grow: unused.</summary>
    public double Work { get; init; }

    /// <summary>Hunt: the meat's nutrition, which joins the nutrition stock at completion.</summary>
    public double Nutrition { get; init; }

    /// <summary>Sow, Grow and Harvest: the field (<see cref="ModelField.Id"/>) the crop stands on.</summary>
    public string Field { get; init; } = "";

    /// <summary>Sow, Grow and Harvest: the crop's name.</summary>
    public string Crop { get; init; } = "";

    /// <summary>Sow, Grow and Harvest: the cells sown, which is the harvested area.</summary>
    public int Cells { get; init; }

    /// <summary>Craft: the materials consumed.</summary>
    public EquatableArray<Amount> Ingredients { get; init; }

    /// <summary>Craft: the products. Hunt: the butchery products (leather), which join the materials stock at completion.</summary>
    public EquatableArray<Amount> Products { get; init; }

    /// <summary>Craft: true when the products go to the materials stock; false when they are apparel or another item the model holds no stock for.</summary>
    public bool ProductsToStock { get; init; }
}
