namespace RimGovernor.Brain.Policy;

/// <summary>
/// What the catalog's RecipeDef rows say about the recipes the planners treat specially. Names are the
/// recipes' defNames; every fact is derived from the rows, never from a name.
/// </summary>
public sealed record RecipeFacts
{
    /// <summary>The install recipes of parts made straight from a stuff (a peg leg from a log), by recipe.</summary>
    public IReadOnlyList<MaterialInstall> MaterialInstalls { get; init; } = [];

    /// <summary>
    /// The work type a bill on the recipe puts a pawn to, by recipe: that of the first bench (by name) whose
    /// DoBill giver serves it. A recipe no bench hosts has none.
    /// </summary>
    public IReadOnlyDictionary<string, WorkType> BillWork { get; init; } = new Dictionary<string, WorkType>(StringComparer.Ordinal);

    /// <summary>
    /// The paired vital organs a harvest takes, by body part defName (each spawns the item of the same
    /// defName): a vital part whose body carries two, so one survives the cut.
    /// </summary>
    public IReadOnlyList<string> HarvestOrgans { get; init; } = [];

    /// <summary>The hediff each recipe adds (<c>RecipeDef.addsHediff</c>), by recipe: what a health goal's recipe leaves on its pawn (#3508).</summary>
    public IReadOnlyDictionary<string, string> AddedHediffs { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>The body parts whose removal kills: a vital tag no other part of the body provides.</summary>
    public IReadOnlyDictionary<string, bool> VitalParts { get; init; } = new Dictionary<string, bool>(StringComparer.Ordinal);

    /// <summary>Reports whether the body part is a paired organ harvest takes.</summary>
    public bool HarvestOrgan(string part) => HarvestOrgans.Contains(part);

    /// <summary>
    /// Reports whether an added part on the body part stays on a prisoner: its removal kills (VitalParts) or
    /// is a KeptBodyParts judgment.
    /// </summary>
    internal bool KeptPart(string body) => VitalParts.GetValueOrDefault(body) || OrganHarvestRules.KeptBodyParts.Contains(body);

    /// <summary>The work type of a bill on the recipe, false when no bench the catalog knows hosts the recipe.</summary>
    public (WorkType Work, bool Known) BillWorkOf(string recipe) =>
        BillWork.TryGetValue(recipe, out var work) ? (work, true) : (default, false);

    /// <summary>The material install recipe that serves the body part, null when no stuff-made part fits it.</summary>
    public MaterialInstall? MaterialInstallFor(string body) => MaterialInstalls.FirstOrDefault(m => m.Bodies.Contains(body));

    /// <summary>The material install of the recipe.</summary>
    public MaterialInstall? MaterialInstallByRecipe(string recipe) => MaterialInstalls.FirstOrDefault(m => m.Recipe == recipe);

    /// <summary>The material install that adds the hediff.</summary>
    public MaterialInstall? MaterialInstallOfPart(string hediff) => MaterialInstalls.FirstOrDefault(m => m.Part == hediff);

    /// <summary>The material install a practice-cycle step belongs to (#3634, #3637): a restore by a material install recipe on a missing limb,
    /// or the removal of an installed material part. Null for every other operation.</summary>
    public MaterialInstall? PracticeCycleOf(SurgeryOperation op) =>
        op.Kind == SurgeryKind.Restore && op.Recipe.TryGetValue(out var recipe) ? MaterialInstallByRecipe(recipe)
        : op.Kind == SurgeryKind.Amputate && op.AddedPart.TryGetValue(out var part) ? MaterialInstallOfPart(part) : null;
}

/// <summary>
/// A surgery recipe that installs a part made straight from a stuff: the recipe, the body parts it applies
/// to, the hediff it adds, the recipe's work and the market value of the stuff it consumes.
/// </summary>
public sealed record MaterialInstall
{
    public string Recipe { get; init; } = "";

    public IReadOnlyList<string> Bodies { get; init; } = [];

    public string Part { get; init; } = "";

    public double Work { get; init; }

    /// <summary>The consumed stuff's market value times the count the recipe takes.</summary>
    public double Value { get; init; }

    /// <summary>The recipe's native learning callback (#3632); null when the catalog did not read it.</summary>
    public RimGovernor.Contracts.Wire.Observations.RecipeLearning? Learning { get; init; }

    /// <summary>The recipe's work speed stat (<c>RecipeDef.workSpeedStat</c>); empty for none, which works at speed 1.</summary>
    public string SpeedStat { get; init; } = "";

    /// <summary>The medicine the recipe uses up: the count of its ingredient slots that take only medicine.</summary>
    public double Medicine { get; init; }
}
