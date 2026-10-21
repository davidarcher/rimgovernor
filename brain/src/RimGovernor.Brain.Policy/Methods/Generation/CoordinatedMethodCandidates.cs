namespace RimGovernor.Brain.Policy.Methods;

internal sealed record CoordinatedMethodCandidates(ForecastActivityProblem? Problem, ForecastActivityOutcome[] Outcomes,
    IReadOnlyDictionary<(string Task, string Pawn), ActivityTaskCandidate> Bindings, bool Truncated)
{
    public EquatableArray<string> Findings { get; init; }
    public EquatableArray<ForecastCompositionOperation> Operations { get; init; }
    public EquatableArray<ForecastCompositionSupply> Observed { get; init; }
}

internal sealed record ActivityTaskCandidate(string Id, string Pawn, EquatableArray<string> Prerequisites,
    PawnActivityKind Kind, string Target, string WorkGiver, string Job, int Count, long Deadline, Cell Location)
{
    public string Resource { get; init; } = "";
    public Cell? Source { get; init; }
    public Cell? Destination { get; init; }
    public string Container { get; init; } = "";
    public string SiteDefinition { get; init; } = "";
    public string SupportDemand { get; init; } = "";
    public string Medicine { get; init; } = "";
    public bool CompletionMustBeObserved { get; init; }
    public int Capacity { get; init; }
    public RimGovernor.Contracts.Wire.Observations.FoodCookingEstimate? Cooking { get; init; }
    public string Ingredient { get; init; } = "";
    public PawnActivityRecipe? Recipe { get; init; }
    public PawnActivityPractice? Practice { get; init; }
    public string ProductStuff { get; init; } = "";
    public ClothingActivityRequirement? Clothing { get; init; }
    public EquatableArray<SelectedActivityInput> SelectedInputs { get; init; }
    /// <summary>A cook binding's chance its meal is poisoned (CoupledFoodConversion.FoodPoisonChance, #3167).</summary>
    public Fact<double> FoodPoisonChance { get; init; }
    /// <summary>A set step's members (#3613): the Work repeats over these thing ids, or over <see cref="Cells"/> (#3615); Count is their number.</summary>
    public EquatableArray<string> Targets { get; init; }
    public EquatableArray<Cell> Cells { get; init; }
    /// <summary>A roof-holder's Deconstruct (#3617): the roofed cells it leaves unsupported, which the binding removes the roof from
    /// first (a Work/RemoveRoof cell set step before the task's own step, in the same order).</summary>
    public EquatableArray<Cell> RoofCells { get; init; }
    /// <summary>A remote Supply (#3046): the step carries a full load, its stack then the <see cref="Collect"/> stacks.</summary>
    public bool FullLoad { get; init; }
    public EquatableArray<string> Collect { get; init; }
}
