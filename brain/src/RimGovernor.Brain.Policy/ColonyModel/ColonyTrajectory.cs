namespace RimGovernor.Brain.Policy;

/// <summary>A stretch of days with the nutrition stock at zero and consumption above production: every pawn's hunger rose at its fall rate. The malnutrition stage table prices it (#3519, #3520, #3531).</summary>
public sealed record StarvationSpan
{
    public double Start { get; init; }

    public double End { get; init; }
}

/// <summary>Apparel a craft node finished (#3531): the harm model prices what wearing it avoids from <see cref="Day"/> on.</summary>
public sealed record ApparelMade
{
    public double Day { get; init; }

    public string Node { get; init; } = "";

    public EquatableArray<Amount> Products { get; init; }

    public EquatableArray<Amount> Ingredients { get; init; }
}

/// <summary>A resource entering the run's per-resource ledger (#3622): a harvest's yield, a hunt's butchery products or a craft's products, on the day the node completes.</summary>
public sealed record ResourceArrival
{
    public double Day { get; init; }

    public string Node { get; init; } = "";

    public Resource Resource { get; init; }

    public double Count { get; init; }
}

/// <summary>The tier-2 run's output: the events in order, the starvation spans, the apparel made, the resource arrivals, the skill stocks and the food runway.</summary>
public sealed record ColonyTrajectory
{
    /// <summary>The finding of a run that reached <see cref="ColonyModelPolicy.MaxEvents"/> before the horizon; the trajectory is the partial run.</summary>
    public const string TruncatedFinding = "colony_model_truncated";

    /// <summary>Mood over the year is not assessed in v1.</summary>
    public const string MoodUnassessedFinding = "colony_model_mood_unassessed";

    public EquatableArray<ModelEvent> Events { get; init; }

    public EquatableArray<StarvationSpan> Starvation { get; init; }

    public EquatableArray<ApparelMade> Apparel { get; init; }

    /// <summary>Each addition to the per-resource ledger, in completion order: the day each stuff arrives and how much.</summary>
    public EquatableArray<ResourceArrival> Arrivals { get; init; }

    /// <summary>Each pawn's skill stocks whose mechanics are read (#3634): the levels they reach and when.</summary>
    public EquatableArray<SkillTrack> Skills { get; init; }

    /// <summary>The medicine kinds in the order they are used up (<see cref="ModelStocks.Medicine"/>); <see cref="ModelEvent.Medicine"/> is indexed by it.</summary>
    public EquatableArray<Resource> MedicineKinds { get; init; }

    /// <summary>The medicine on hand by kind at time zero, indexed by <see cref="MedicineKinds"/> (a perturbed kind's unit included).</summary>
    public EquatableArray<double> StartMedicine { get; init; }

    /// <summary>When nutrition falls to the stock that bridges the seasonal lead, or NotReached in this run.</summary>
    public ModelEventTime FoodRunwayDay { get; init; } = new ModelEventTime.NotReached();

    /// <summary>Each gap or limit of the run, one finding each, sorted.</summary>
    public EquatableArray<string> Findings { get; init; }

    public bool Truncated => Findings.Contains(TruncatedFinding);
}
