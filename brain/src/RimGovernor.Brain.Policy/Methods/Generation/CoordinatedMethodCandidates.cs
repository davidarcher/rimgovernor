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
}
