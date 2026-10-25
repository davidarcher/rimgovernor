namespace RimGovernor.Brain.Runtime;

/// <summary>
/// What one scheduler step did: the review, each planner's result (null when it did not run) and the proposals
/// the coordinator decided. <see cref="MergeFrom"/> copies each typed
/// result explicitly.
/// </summary>
public sealed class PlannerSchedulerResult
{
    public bool Immediate { get; set; }

    /// <summary>
    /// The hold serial the step's own clock read reported. The run driver's Run quotes it, never its own later read,
    /// so a hold native entered after the step planned refuses the resume.
    /// </summary>
    public ulong? HoldSerial { get; set; }

    /// <summary>Whether the clock was found running when the step read it (reported by the run driver).</summary>
    public bool Running { get; set; }

    /// <summary>The game tick of the clock status the run driver decided against; null when it read none.</summary>
    public long? ClockTick { get; set; }

    public ReviewResult? ReviewState { get; set; }

    public DefenseResult? Defense { get; set; }

    /// <summary>Whether the step deferred its run to the Worker's turn.</summary>
    public bool Deferred { get; set; }

    public TimeSpan Journal { get; set; }

    public List<Exception> PlannerFailures { get; } = [];

    public List<string> Planners { get; set; } = [];

    public List<string> Waiting { get; set; } = [];

    public List<string> MissedCutoff { get; } = [];

    /// <summary>Each family's run (#3250), by family id.</summary>
    public Dictionary<string, FamilyRun> Families { get; } = new(StringComparer.Ordinal);

    public List<string> HeldBy { get; } = [];

    public TimeSpan CriticalWave { get; set; }

    public Dictionary<string, double> PlannerMs { get; set; } = new(StringComparer.Ordinal);

    public StepReason Reason { get; set; } = new();

    /// <summary>The pace the step's clock status reported.</summary>
    public StepPacing Pacing { get; set; } = new();

    /// <summary>The step's stages (gate, read, select, refresh, rounds, extent, critical, optional, record), lapped as it runs; published on the clock_step row.</summary>
    public StepStages Stages { get; } = new();

    /// <summary>Copies every planner result <paramref name="other"/> holds into this one, leaving the ones it does not.</summary>
    internal void MergeFrom(PlannerSchedulerResult other)
    {
        Defense = other.Defense ?? Defense;
        foreach (var (family, run) in other.Families)
        {
            Families[family] = run;
        }
    }
}
