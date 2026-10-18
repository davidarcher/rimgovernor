namespace RimGovernor.Brain.Policy;

/// <summary>A lump a project completion or a harvest adds to one stock at a given day; the project graph (T4) schedules these, the engine only applies them in order.</summary>
public sealed record ScheduledEffect
{
    public double Day { get; init; }

    /// <summary>Only <see cref="ModelEventKind.ProjectCompletion"/> and <see cref="ModelEventKind.Harvest"/> are scheduled.</summary>
    public ModelEventKind Kind { get; init; }

    public string Id { get; init; } = "";

    public ModelStock Stock { get; init; }

    public double Amount { get; init; }
}

/// <summary>The tier-2 engine's constants and hooks (#3529 of #3470).</summary>
public sealed record ColonyModelPolicy
{
    /// <summary>The simulated span in days: one game year.</summary>
    public double HorizonDays { get; init; } = CalendarRules.YearDays;

    /// <summary>The event budget; a run that reaches it is truncated. Events are counted, never timed.</summary>
    public int MaxEvents { get; init; } = 4_096;

    /// <summary>
    /// Medicine tended a day, as the colony is observed to tend. Unknown leaves medicine unconsumed and is reported, never defaulted.
    /// </summary>
    public Fact<double> MedicineTendsPerDay { get; init; }

    /// <summary>Constant production in stock units a day, indexed by <see cref="ModelStock"/> (a missing entry is zero). The project graph (T4) replaces it with real production.</summary>
    public EquatableArray<double> FixedProduction { get; init; }

    /// <summary>Project completions and harvests to apply, any order.</summary>
    public EquatableArray<ScheduledEffect> Scheduled { get; init; }
}
