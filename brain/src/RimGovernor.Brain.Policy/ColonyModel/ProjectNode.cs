namespace RimGovernor.Brain.Policy;

// The tier-2 colony model's project graph nodes (#3530 of #3470): research, sowing, growth, harvest, crafting and construction.

/// <summary>The kinds of project node.</summary>
public enum ProjectKind
{
    /// <summary>Research points to spend; the work is points remaining.</summary>
    Research,

    /// <summary>Sowing the cells of a field; the work is cells times the crop's sow work.</summary>
    Sow,

    /// <summary>A crop maturing: no labor, the node completes when growth reaches the crop's harvestable growth.</summary>
    Grow,

    /// <summary>Harvesting the cells of a field; the work is cells times the crop's harvest work.</summary>
    Harvest,

    /// <summary>A recipe: the work, the ingredients it consumes and the products it makes.</summary>
    Craft,

    /// <summary>A building: the work and the materials it consumes.</summary>
    Construction,
}

/// <summary>
/// One node of the project graph. A node starts when every prerequisite is complete. The graph's order is the labor
/// allocation's priority order and is visible in the trajectory.
/// </summary>
public sealed record ProjectNode
{
    /// <summary>Unique within the graph; the id of the node's completion event.</summary>
    public string Id { get; init; } = "";

    public ProjectKind Kind { get; init; }

    /// <summary>Ids of the nodes that must complete first.</summary>
    public EquatableArray<string> Prerequisites { get; init; }

    /// <summary>Research: points remaining. Sow, Harvest, Craft, Construction: work units. Grow: unused.</summary>
    public double Work { get; init; }

    /// <summary>Sow, Grow and Harvest: the field (<see cref="ModelField.Id"/>) the crop stands on.</summary>
    public string Field { get; init; } = "";

    /// <summary>Sow, Grow and Harvest: the crop's name.</summary>
    public string Crop { get; init; } = "";

    /// <summary>Sow, Grow and Harvest: the cells sown, which is the harvested area.</summary>
    public int Cells { get; init; }

    /// <summary>Craft and Construction: the materials consumed.</summary>
    public EquatableArray<Amount> Ingredients { get; init; }

    /// <summary>Craft: the products.</summary>
    public EquatableArray<Amount> Products { get; init; }

    /// <summary>Craft: true when the products go to the materials stock; false when they are apparel or another item the model holds no stock for.</summary>
    public bool ProductsToStock { get; init; }
}
