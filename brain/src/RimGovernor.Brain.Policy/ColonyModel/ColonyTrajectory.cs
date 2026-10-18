namespace RimGovernor.Brain.Policy;

/// <summary>Days one pawn spent with the nutrition stock at zero and consumption above production: its hunger rose at its fall rate. The malnutrition stage table prices them (#3519, #3520).</summary>
public sealed record PawnStarvation
{
    public string Pawn { get; init; } = "";

    public double Days { get; init; }
}

/// <summary>The tier-2 run's output: the events in order, the starvation days and the food runway.</summary>
public sealed record ColonyTrajectory
{
    /// <summary>The finding of a run that reached <see cref="ColonyModelPolicy.MaxEvents"/> before the horizon; the trajectory is the partial run.</summary>
    public const string TruncatedFinding = "colony_model_truncated";

    /// <summary>Mood over the year is not assessed in v1.</summary>
    public const string MoodUnassessedFinding = "colony_model_mood_unassessed";

    public EquatableArray<ModelEvent> Events { get; init; }

    public EquatableArray<PawnStarvation> Starvation { get; init; }

    /// <summary>The day nutrition falls to the stock that bridges the lead days at the consumption then (stock over consumption, less the lead); null when it does not within the run.</summary>
    public double? FoodRunwayDay { get; init; }

    /// <summary>Each gap or limit of the run, one finding each, sorted.</summary>
    public EquatableArray<string> Findings { get; init; }

    public bool Truncated => Findings.Contains(TruncatedFinding);
}
