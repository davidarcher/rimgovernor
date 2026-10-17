namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>
/// What one scheduler step did: the review, each planner's result (null when it did not run) and the proposals
/// the coordinator decided. Go merged planner results by reflection; <see cref="MergeFrom"/> copies each typed
/// result explicitly.
/// </summary>
public sealed class ClockSchedulerResult
{
    public bool Immediate { get; set; }

    /// <summary>When the step began: everything the event poll had reviewed before it is what the step planned against.</summary>
    public DateTimeOffset StartedAt { get; set; }

    /// <summary>Whether the clock was found running when the step read it (reported by the run driver).</summary>
    public bool Running { get; set; }

    public RoundsResult? Rounds { get; set; }







    public RoundsBuildingResult? Routes { get; set; }

    public RoundsDefenseResult? Defense { get; set; }


    public RoundsRecoveryResult? Recovery { get; set; }





    public RoundsDefenseLayoutResult? DefenseLayout { get; set; }

    public RoundsMoodReliefResult? MoodRelief { get; set; }

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

    /// <summary>Copies every planner result <paramref name="other"/> holds into this one, leaving the ones it does not.</summary>
    internal void MergeFrom(ClockSchedulerResult other)
    {
        Routes = other.Routes ?? Routes;
        Defense = other.Defense ?? Defense;
        Recovery = other.Recovery ?? Recovery;
        DefenseLayout = other.DefenseLayout ?? DefenseLayout;
        MoodRelief = other.MoodRelief ?? MoodRelief;
        foreach (var (family, run) in other.Families)
        {
            Families[family] = run;
        }
    }
}
