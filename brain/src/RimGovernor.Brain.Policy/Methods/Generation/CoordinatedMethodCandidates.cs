namespace RimGovernor.Brain.Policy.Methods;

internal sealed record CoordinatedMethodCandidates(ForecastActivityProblem? Problem, ForecastActivityOutcome[] Outcomes,
    IReadOnlyDictionary<(string Task, string Pawn), ActivityTaskCandidate> Bindings, bool Truncated)
{
    public EquatableArray<string> Findings { get; init; }
    public EquatableArray<ForecastCompositionOperation> Operations { get; init; }
    public EquatableArray<ForecastCompositionSupply> Observed { get; init; }
    /// <summary>The clothing family's garment per outcome id (crafted or retained): the wearer's outfit change and comfort shift (#3330, #3621, #3816).</summary>
    public IReadOnlyDictionary<string, ClothingActivityRequirement> Garments { get; init; } = new Dictionary<string, ClothingActivityRequirement>(StringComparer.Ordinal);
}

internal record ActivityTaskCandidate(string Id, string Pawn, EquatableArray<string> Prerequisites,
    PawnActivityKind Kind, string Target, string WorkGiver, string Job, int Count, long Deadline, Cell Location)
{
    public string Resource { get; init; } = "";
    public Cell Source { get; init; } = Location;
    public string Container { get; init; } = "";
    public string SiteDefinition { get; init; } = "";
    public string SupportDemand { get; init; } = "";
    public string Medicine { get; init; } = "";
    public bool CompletionMustBeObserved { get; init; }
    public int Capacity { get; init; }

    public PawnActivityRecipe? Recipe { get; init; }
    public PawnActivityPractice? Practice { get; init; }
    public string ProductStuff { get; init; } = "";
    /// <summary>The products an ingredient delivery feeds (a cooking supply's recipe products): the
    /// <see cref="InterventionPurposeKind.ProductionStall"/> subjects that delivery answers. Empty on every other binding.</summary>
    public EquatableArray<string> Feeds { get; init; }
    public EquatableArray<SelectedActivityInput> SelectedInputs { get; init; }
    /// <summary>A set step's members (#3613): the Work repeats over these thing ids, or over <see cref="Cells"/> (#3615); Count is their number.</summary>
    public EquatableArray<string> Targets { get; init; }
    public EquatableArray<Cell> Cells { get; init; }
    /// <summary>A remote Supply (#3046): the step carries a full load, its stack then the <see cref="Collect"/> stacks.</summary>
    public bool FullLoad { get; init; }
    public EquatableArray<string> Collect { get; init; }
}

internal sealed record CellActivityTaskCandidate : ActivityTaskCandidate
{
    public CellActivityTaskCandidate(ActivityTaskCandidate task, Cell destination) : base(task) => Destination = destination;
    public Cell Destination { get; init; }
}
