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

/// <summary>
/// Work already done on one node at time zero (#3531): the perturbation the marginal value is measured by. In the node's
/// <see cref="ProjectNode.Work"/> units (research points; sow work). <see cref="Node"/> may instead be a medicine kind's key
/// (<see cref="ProjectGraph.MedicineKey"/>, units; #3634) or a skill stock's (<see cref="ProjectGraph.SkillKey"/>): that stock starts
/// one stored level higher, and <see cref="Work"/> is the XP the level takes (<see cref="SkillTrack.NextLevelXp"/>), which the run
/// does not read and the marginal value is divided by (#3344).
/// </summary>
public sealed record InjectedProgress(string Node, double Work);

/// <summary>The tier-2 engine's constants and hooks (#3529 of #3470).</summary>
public sealed record ColonyModelPolicy
{
    /// <summary>The simulated span in days: one game year.</summary>
    public double HorizonDays { get; init; } = CalendarRules.YearDays;

    /// <summary>The event budget; a run that reaches it is truncated. Events are counted, never timed.</summary>
    public int MaxEvents { get; init; } = 4_096;

    /// <summary>
    /// Medicine tended a day: the treatments the harm model's raid wounds imply (<see cref="ColonyValue.RaidTreatmentsPerDay"/>, #3634).
    /// Unknown leaves medicine unconsumed by tending and is reported, never defaulted.
    /// </summary>
    public Fact<double> MedicineTendsPerDay { get; init; }

    /// <summary>Constant production in stock units a day, indexed by <see cref="ModelStock"/> (a missing entry is zero). The project graph (T4) replaces it with real production.</summary>
    public EquatableArray<double> FixedProduction { get; init; }

    /// <summary>Project completions and harvests to apply, any order.</summary>
    public EquatableArray<ScheduledEffect> Scheduled { get; init; }

    /// <summary>The project graph (#3530): nodes in labor-priority order. Their completions and harvests are scheduled by the engine from the pawn cycles, not given.</summary>
    public ProjectGraph Projects { get; init; } = new();

    /// <summary>Progress injected at time zero on one node; null for the run as is.</summary>
    public InjectedProgress? Injected { get; init; }
}
