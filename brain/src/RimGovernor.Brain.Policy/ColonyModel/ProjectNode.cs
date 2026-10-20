namespace RimGovernor.Brain.Policy;

// The tier-2 colony model's project graph nodes (#3530 of #3470): research, sowing, growth, harvest, crafting, construction, hunting (#3620) and practice (#3634).

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

    /// <summary>Hunting and butchering one race's animals of the wildlife census; the work is the hunt planner's pursuit work, the yields its meat and butchery products.</summary>
    Hunt,

    /// <summary>
    /// Practising a skill on a practice slot (#3634): one surgery at a time on a prisoner with a missing or material limb. It never
    /// completes; while the slot and its medicine last, the first free pawn able to do it works it with all its free hours and the
    /// recipe's learning callback pays that pawn's skill stock, as research keeps running in the baseline.
    /// </summary>
    Practice,
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

    /// <summary>Research: points remaining. Sow, Harvest, Craft, Construction, Hunt: work units. Practice: the work of one surgery. Grow: unused.</summary>
    public double Work { get; init; }

    /// <summary>Hunt: the meat's nutrition, which joins the nutrition stock at completion.</summary>
    public double Nutrition { get; init; }

    /// <summary>Sow, Grow and Harvest: the field (<see cref="ModelField.Id"/>) the crop stands on.</summary>
    public string Field { get; init; } = "";

    /// <summary>Sow, Grow and Harvest: the crop's name.</summary>
    public string Crop { get; init; } = "";

    /// <summary>Sow, Grow and Harvest: the cells sown, which is the harvested area.</summary>
    public int Cells { get; init; }

    /// <summary>Craft and Construction: the materials consumed.</summary>
    public EquatableArray<Amount> Ingredients { get; init; }

    /// <summary>Craft: the products. Hunt: the butchery products (leather), which join the materials stock at completion.</summary>
    public EquatableArray<Amount> Products { get; init; }

    /// <summary>Craft: true when the products go to the materials stock; false when they are apparel or another item the model holds no stock for.</summary>
    public bool ProductsToStock { get; init; }

    /// <summary>Practice: the skill the recipe trains.</summary>
    public string Skill { get; init; } = "";

    /// <summary>Practice: the recipe's raw XP per tick spent on it (<see cref="RimGovernor.Contracts.Wire.Observations.RecipeLearning"/>).</summary>
    public double RawXpPerWorkTick { get; init; }

    /// <summary>Practice: the stat the recipe's work speed is (empty for speed 1), read off the worker's <see cref="ModelPawn.SkillStatCurves"/> at its level.</summary>
    public string SpeedStat { get; init; } = "";

    /// <summary>Practice: the medicine one surgery uses up.</summary>
    public double Medicine { get; init; }
}
